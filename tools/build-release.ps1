<#
.SYNOPSIS
    Publishes the self-contained win-x64 release, checks the publish manifest and
    zips the published folder into artifacts\Earshot-<version>-win-x64.zip.

.DESCRIPTION
    Runs, in order:
      1. reads the version from src\Earshot\Earshot.csproj and names the zip after it;
      2. empties publish\Earshot, because the publish step refuses to write its file
         manifest into a folder that already holds files it did not write;
      3. dotnet publish src\Earshot -c Release -r win-x64 --self-contained true
         -p:PublishSingleFile=false, in folder form, never single file;
      4. checks Earshot.files.json is there, that every file it lists is present with
         the SHA-256 it records, and that the folder holds nothing else. install reads
         the same manifest, so a release that fails here could not be installed;
      5. zips the folder, with Earshot as the folder inside the zip;
      6. prints the zip size and its SHA-256.

    Full output goes to the log folder. The summary is JSON on stdout. Any failure
    exits non-zero and nothing is left in artifacts.

.PARAMETER Root
    Repository root. Defaults to the folder above this script.

.PARAMETER LogDir
    Where the full publish log goes. Defaults to %TEMP%\earshot-release.
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$LogDir = (Join-Path $env:TEMP 'earshot-release')
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$Root = (Resolve-Path $Root).Path.TrimEnd('\')
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$result = [ordered]@{
    root = $Root; version = ''; published_files = 0
    publish_dir = ''; zip = ''; zip_bytes = 0; zip_sha256 = ''
    log = (Join-Path $LogDir 'publish.log'); problems = @()
}

function Stop-Release {
    param([string]$Problem)
    $result.problems += $Problem
    $result.ok = $false
    $result | ConvertTo-Json -Depth 4
    exit 1
}

$project = Join-Path $Root 'src\Earshot\Earshot.csproj'
if (-not (Test-Path $project)) { Stop-Release "Project not found: $project" }

# The zip is named from the one version in the project file, so the name and the binary can never
# disagree about which release this is.
$versionNode = ([xml](Get-Content $project -Raw)).SelectSingleNode('//Version')
if (-not $versionNode -or -not ($versionNode.InnerText -match '^\d+\.\d+\.\d+$')) {
    Stop-Release "Earshot.csproj has no <Version> of the form 1.0.0."
}
$version = $versionNode.InnerText
$result.version = $version

$publishDir = Join-Path $Root 'publish\Earshot'
$result.publish_dir = $publishDir
$artifactsDir = Join-Path $Root 'artifacts'
$zipPath = Join-Path $artifactsDir ("Earshot-$version-win-x64.zip")
$result.zip = $zipPath

# A leftover file from an earlier publish stops the manifest being written, and an old zip must never
# be mistaken for this run's.
try {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
}
catch { Stop-Release ("Could not clear the publish and artifacts folders: " + $_.Exception.Message) }

$publishLog = $result.log
# DebugType is left at the SDK default, so Earshot.pdb is published, listed in the manifest and
# installed with everything else. That is deliberate: FileLog writes ex.ToString() for a failure,
# and without the symbols beside the exe that stack trace carries no file or line. 281 KB against a
# 150 MB self-contained folder is a fair price for a log the owner can act on. (Measured on the v1.1
# audio streaming build. The folder was 125 MB before the target framework gained a Windows version:
# that brought in the WinRT projection, Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll, 25 MB.)
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publishDir *> $publishLog
$publishExit = $LASTEXITCODE
$publishText = Get-Content $publishLog -Raw
if ($null -eq $publishText) { $publishText = '' }
$warnLines = @(Select-String -Path $publishLog -Pattern ': warning [A-Z]+[0-9]+' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
$errLines = @(Select-String -Path $publishLog -Pattern ': error [A-Z]+[0-9]+|error MSB|error NETSDK' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
if ($warnLines.Count) { $result.problems += ($warnLines | Select-Object -First 25) }
if ($errLines.Count) { $result.problems += ($errLines | Select-Object -First 40) }
if ($publishExit -ne 0 -and -not $errLines.Count) {
    $result.problems += ($publishText -split "`r?`n" | Where-Object { $_ -match 'error|failed' } | Select-Object -First 20)
}
if ($publishExit -ne 0 -or $errLines.Count -or $warnLines.Count) {
    Stop-Release "dotnet publish did not finish clean. Full output: $publishLog"
}

# The manifest install reads. Without it, or with one file off by a byte, install would refuse the
# copy, so the release is not shippable and this run fails here instead.
$manifestName = 'Earshot.files.json'
$manifestPath = Join-Path $publishDir $manifestName
if (-not (Test-Path $manifestPath)) { Stop-Release "The publish wrote no $manifestName in $publishDir." }
try { $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json }
catch { Stop-Release ("$manifestName could not be read: " + $_.Exception.Message) }
if (-not $manifest.Files -or -not @($manifest.Files).Count) { Stop-Release "$manifestName lists no files." }

$listed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($manifest.Files)) {
    $relative = [string]$entry.Path
    if (-not $relative -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or [System.IO.Path]::IsPathRooted($relative)) {
        $result.problems += "$manifestName lists a path that is not inside the publish folder: $relative"
        continue
    }
    $full = [System.IO.Path]::GetFullPath((Join-Path $publishDir ($relative -replace '/', '\')))
    [void]$listed.Add($full)
    if (-not (Test-Path $full -PathType Leaf)) {
        $result.problems += "$manifestName lists a file that is not there: $relative"
        continue
    }
    $hash = (Get-FileHash -Algorithm SHA256 -Path $full).Hash
    if ($hash -ne [string]$entry.Sha256) {
        $result.problems += ("$relative does not match the hash $manifestName records: got $hash, expected " + $entry.Sha256)
    }
}

# Anything the manifest does not list would be copied by nothing and checked by nothing, so it has no
# place in the release.
foreach ($file in (Get-ChildItem -Path $publishDir -Recurse -File -Force)) {
    $full = [System.IO.Path]::GetFullPath($file.FullName)
    if ($full -eq [System.IO.Path]::GetFullPath($manifestPath)) { continue }
    if (-not $listed.Contains($full)) {
        $result.problems += ("The publish folder holds a file $manifestName does not list: " + $file.FullName.Substring($publishDir.Length).TrimStart('\'))
    }
}
$result.published_files = @($manifest.Files).Count
if ($result.problems.Count) { Stop-Release "The publish manifest and the published files do not agree." }

$exe = Join-Path $publishDir 'Earshot.exe'
if (-not (Test-Path $exe -PathType Leaf)) { Stop-Release "No Earshot.exe in $publishDir." }
$exeVersion = [string](Get-Item $exe).VersionInfo.ProductVersion
if (-not $exeVersion.StartsWith($version)) {
    Stop-Release "Earshot.exe reports version '$exeVersion', which is not the $version this release is named after."
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # The last argument keeps the Earshot folder inside the zip, so unzipping anywhere gives one folder
    # rather than loose files.
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}
catch {
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath -ErrorAction SilentlyContinue }
    Stop-Release ("The zip could not be written: " + $_.Exception.Message)
}

$result.zip_bytes = (Get-Item $zipPath).Length
$result.zip_sha256 = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash
$result.ok = $true
$result | ConvertTo-Json -Depth 4
exit 0
