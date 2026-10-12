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
         -p:PublishSingleFile=false -p:PublishReadyToRun=true, in folder form, never single file;
      4. checks Earshot.files.json is there, that every file it lists is present with
         the SHA-256 it records, and that the folder holds nothing else. install reads
         the same manifest, so a release that fails here could not be installed;
      5. zips the folder, with Earshot as the folder inside the zip;
      6. writes the zip's SHA-256 beside it as Earshot-<version>-win-x64.zip.sha256, in the
         sha256sum form (hex, two spaces, file name), UTF-8 without a byte order mark,
         which is what the in-app update reads from the release;
      7. copies installer\earshot.ps1 to artifacts\earshot.ps1, byte for byte, after checking it is
         plain ASCII (Windows PowerShell 5.1 does not decode an octet-stream download as UTF-8, so any
         other byte would be mangled before it ran), and records its SHA-256, in lower-case hex, as
         script_sha256 beside zip_sha256, so the release notes can carry both;
      8. publishes src\Earshot.Setup as Native AOT with the zip embedded, reads the executable (never runs it) to check
         that it limits DLL loading to System32, imports nothing outside System32 and was built from this zip and this
         version, copies it to artifacts\Earshot-<version>-Setup.exe and writes Earshot-<version>-Setup.exe.sha256 beside
         it, in the same form as the zip's;
      9. writes artifacts\release-notes.md, which carries the SHA-256 of the zip, of Setup.exe and of earshot.ps1;
     10. prints the zip size and its SHA-256.

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
    publish_dir = ''; zip = ''; zip_bytes = 0; zip_sha256 = ''; checksum_file = ''
    script = ''; script_sha256 = ''
    log = (Join-Path $LogDir 'publish.log'); problems = @()
}

function Stop-Release {
    param([string]$Problem)
    $result.problems += $Problem
    $result.ok = $false
    $result | ConvertTo-Json -Depth 4
    exit 1
}

# What a 64-bit Windows executable says about itself, read from the file and never by running it: the DLLs it imports
# and the dependent load flag in its load configuration (what /DEPENDENTLOADFLAG sets).
# https://learn.microsoft.com/en-us/windows/win32/debug/pe-format
function Get-PeFacts {
    param([string]$Path)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $pe = $reader.ReadInt32()
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { throw "$Path is not a PE file." }
        $stream.Position = $pe + 6
        $sections = $reader.ReadUInt16()
        $stream.Position = $pe + 20
        $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24
        $stream.Position = $optional
        if ($reader.ReadUInt16() -ne 0x20B) { throw "$Path is not a 64-bit PE file." }
        $directories = $optional + 112
        $table = $optional + $optionalSize
        $map = @()
        for ($i = 0; $i -lt $sections; $i++) {
            $stream.Position = $table + 40 * $i + 8
            $virtualSize = $reader.ReadUInt32(); $virtualAddress = $reader.ReadUInt32(); $rawSize = $reader.ReadUInt32(); $rawPointer = $reader.ReadUInt32()
            $map += , @($virtualAddress, [Math]::Max($virtualSize, $rawSize), $rawPointer)
        }
        $toOffset = {
            param([uint32]$rva)
            foreach ($s in $map) { if ($rva -ge $s[0] -and $rva -lt ($s[0] + $s[1])) { return [long]($s[2] + ($rva - $s[0])) } }
            throw "RVA 0x$($rva.ToString('X')) is in no section of $Path."
        }
        $stream.Position = $directories + 8
        $importRva = $reader.ReadUInt32()
        $stream.Position = $directories + 80
        $configRva = $reader.ReadUInt32()
        $flags = -1
        if ($configRva -ne 0) {
            $stream.Position = (& $toOffset $configRva) + 78
            $flags = [int]$reader.ReadUInt16()
        }
        $imports = New-Object System.Collections.Generic.List[string]
        if ($importRva -ne 0) {
            $descriptor = & $toOffset $importRva
            while ($true) {
                $stream.Position = $descriptor
                $fields = 1..5 | ForEach-Object { $reader.ReadUInt32() }
                if (($fields | Measure-Object -Sum).Sum -eq 0) { break }
                $stream.Position = & $toOffset $fields[3]
                $bytes = New-Object System.Collections.Generic.List[byte]
                while (($b = $reader.ReadByte()) -ne 0) { $bytes.Add($b) }
                $imports.Add([System.Text.Encoding]::ASCII.GetString($bytes.ToArray()))
                $descriptor += 20
            }
        }
        return [pscustomobject]@{ DependentLoadFlags = $flags; Imports = @($imports) }
    }
    finally { $stream.Dispose() }
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
$checksumPath = $zipPath + '.sha256'
$result.checksum_file = $checksumPath
$scriptSource = Join-Path $Root 'installer\earshot.ps1'
$scriptPath = Join-Path $artifactsDir 'earshot.ps1'
$result.script = $scriptPath

# The install script is attached to the release as it is in the repository. It is checked first, so a script that
# would be mangled in transit stops the release before any time is spent publishing.
if (-not (Test-Path $scriptSource -PathType Leaf)) { Stop-Release "The install script was not found: $scriptSource" }
$scriptBytes = [System.IO.File]::ReadAllBytes($scriptSource)
$nonAscii = @($scriptBytes | Where-Object { $_ -ge 0x80 }).Count
if ($nonAscii -gt 0) { Stop-Release "installer\earshot.ps1 holds $nonAscii byte(s) that are not plain ASCII, so a download of it would be mangled before it ran." }

# A leftover file from an earlier publish stops the manifest being written, and an old zip must never
# be mistaken for this run's.
try {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    if (Test-Path $checksumPath) { Remove-Item -Force $checksumPath }
    if (Test-Path $scriptPath) { Remove-Item -Force $scriptPath }
}
catch { Stop-Release ("Could not clear the publish and artifacts folders: " + $_.Exception.Message) }

$publishLog = $result.log
# DebugType is left at the SDK default, so Earshot.pdb is published, listed in the manifest and
# installed with everything else. That is deliberate: FileLog writes ex.ToString() for a failure,
# and without the symbols beside the exe that stack trace carries no file or line. 281 KB against a
# 150 MB self-contained folder is a fair price for a log the owner can act on. (Measured on the v1.1
# audio streaming build. The folder was 125 MB before the target framework gained a Windows version:
# that brought in the WinRT projection, Microsoft.Windows.SDK.NET.dll and WinRT.Runtime.dll, 25 MB.)
# PublishReadyToRun precompiles the managed code to native code beside the IL, so the tray starts without
# the JIT compiling its start-up path. It is set here, on the command line with -r win-x64, and not in the
# project file: ReadyToRun needs a runtime identifier, which the project leaves unset (see Earshot.csproj).
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $publishDir *> $publishLog
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
# Windows PowerShell 5.1 writes UTF-16 by default, which the update's parser refuses as malformed, so
# the file is written through .NET with an explicit encoding.
try {
    $line = $result.zip_sha256.ToLowerInvariant() + '  ' + (Split-Path -Leaf $zipPath) + "`n"
    [System.IO.File]::WriteAllText($checksumPath, $line, (New-Object System.Text.UTF8Encoding($false)))
}
catch {
    Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
    Stop-Release ("The checksum file could not be written: " + $_.Exception.Message)
}

# The install script, byte for byte, with its own SHA-256 for the release notes.
try {
    [System.IO.File]::WriteAllBytes($scriptPath, $scriptBytes)
    $result.script_sha256 = (Get-FileHash -Algorithm SHA256 -Path $scriptPath).Hash.ToLowerInvariant()
}
catch {
    Remove-Item -Force $zipPath, $checksumPath, $scriptPath -ErrorAction SilentlyContinue
    Stop-Release ("The install script could not be copied: " + $_.Exception.Message)
}

# Setup.exe: the zip just written, embedded in a Native AOT executable that is elevated itself. The project is built here, once
# the zip exists, with the zip and its version on the command line; it needs a runtime identifier and the AOT compiler package,
# so they are given here and not in the project file. The linker finds Visual Studio through vswhere, which is not on the PATH
# of every shell, so the Installer folder is added for this run.
$setupProject = Join-Path $Root 'src\Earshot.Setup\Earshot.Setup.csproj'
$setupPublish = Join-Path $Root 'publish\Setup'
$setupPath = Join-Path $artifactsDir ("Earshot-$version-Setup.exe")
$setupChecksumPath = $setupPath + '.sha256'
$notesPath = Join-Path $artifactsDir 'release-notes.md'
$result.setup = $setupPath; $result.setup_bytes = 0; $result.setup_sha256 = ''; $result.setup_checksum_file = $setupChecksumPath
$result.notes = $notesPath
$result.setup_log = (Join-Path $LogDir 'setup-publish.log')
$result.setup_checks = @()

function Stop-Setup {
    param([string]$Problem)
    Remove-Item -Force $zipPath, $checksumPath, $scriptPath, $setupPath, $setupChecksumPath, $notesPath -ErrorAction SilentlyContinue
    Stop-Release $Problem
}

if (-not (Test-Path $setupProject)) { Stop-Setup "Project not found: $setupProject" }
try {
    if (Test-Path $setupPublish) { Remove-Item -Recurse -Force $setupPublish }
    New-Item -ItemType Directory -Force -Path $setupPublish | Out-Null
}
catch { Stop-Setup ("Could not clear the Setup publish folder: " + $_.Exception.Message) }

$installerTools = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $installerTools) -and ($env:PATH -notlike "*$installerTools*")) { $env:PATH = $installerTools + ';' + $env:PATH }
& dotnet publish $setupProject -c Release -r win-x64 -p:PublishAot=true -p:IsPublishable=true "-p:EarshotPayload=$zipPath" "-p:EarshotPayloadVersion=$version" -o $setupPublish *> $result.setup_log
$setupExit = $LASTEXITCODE
$setupWarn = @(Select-String -Path $result.setup_log -Pattern ': warning [A-Z]+[0-9]+' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
$setupErr = @(Select-String -Path $result.setup_log -Pattern ': error [A-Z]+[0-9]+|error MSB|error NETSDK' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
if ($setupWarn.Count) { $result.problems += ($setupWarn | Select-Object -First 25) }
if ($setupErr.Count) { $result.problems += ($setupErr | Select-Object -First 40) }
if ($setupExit -ne 0 -or $setupErr.Count -or $setupWarn.Count) { Stop-Setup "dotnet publish of Setup did not finish clean. Full output: $($result.setup_log)" }
$builtSetup = Join-Path $setupPublish 'Earshot.Setup.exe'
if (-not (Test-Path $builtSetup -PathType Leaf)) { Stop-Setup "The Setup publish wrote no Earshot.Setup.exe in $setupPublish." }

# The executable is read, never run: its imports and its load flag are read from the file, and the hash it will check at run
# time is read from the constants file the build wrote beside it.
$facts = Get-PeFacts -Path $builtSetup
if ($facts.DependentLoadFlags -ne 0x800) {
    Stop-Setup ("Setup.exe does not limit DLL loading to System32: its dependent load flag is 0x{0:X}, not 0x800." -f $facts.DependentLoadFlags)
}
$system32 = [Environment]::SystemDirectory
$foreign = @($facts.Imports | Where-Object { $_ -notlike 'api-ms-win-*' -and $_ -notlike 'ext-ms-win-*' -and -not (Test-Path (Join-Path $system32 $_)) })
if ($foreign.Count) { Stop-Setup ("Setup.exe imports DLLs that are not in System32: " + ($foreign -join ', ')) }
$result.setup_checks = @("dependent load flag 0x800", ("imports: " + ($facts.Imports -join ', ')))

$constants = Get-ChildItem -Path (Join-Path $Root 'src\Earshot.Setup\obj') -Recurse -Filter 'PayloadConstants.g.cs' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$constantsText = if ($constants) { Get-Content $constants.FullName -Raw } else { '' }
if ($constantsText -notmatch ('Sha256 = "' + $result.zip_sha256.ToLowerInvariant() + '"')) {
    Stop-Setup "Setup.exe was built with a payload hash that is not the hash of $zipPath."
}
if ($constantsText -notmatch ('Version = "' + [regex]::Escape($version) + '"')) {
    Stop-Setup "Setup.exe was built for a version that is not $version."
}
if ((Get-Item $builtSetup).Length -lt $result.zip_bytes) { Stop-Setup "Setup.exe is smaller than the zip it should carry." }

try {
    Copy-Item -Path $builtSetup -Destination $setupPath -Force
    $result.setup_bytes = (Get-Item $setupPath).Length
    $result.setup_sha256 = (Get-FileHash -Algorithm SHA256 -Path $setupPath).Hash.ToLowerInvariant()
    $setupLine = $result.setup_sha256 + '  ' + (Split-Path -Leaf $setupPath) + "`n"
    [System.IO.File]::WriteAllText($setupChecksumPath, $setupLine, (New-Object System.Text.UTF8Encoding($false)))

    # The notes carry all three hashes, in lower-case hex, for the release page.
    $notes = @(
        "Earshot $version"
        ''
        'SHA-256'
        ''
        ('- ' + (Split-Path -Leaf $zipPath) + ': ' + $result.zip_sha256.ToLowerInvariant())
        ('- ' + (Split-Path -Leaf $setupPath) + ': ' + $result.setup_sha256)
        ('- earshot.ps1: ' + $result.script_sha256)
        ''
        'A hash shows a download arrived whole. It does not show the file is the one published, so check it against this page.'
    )
    [System.IO.File]::WriteAllText($notesPath, (($notes -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
}
catch { Stop-Setup ("Setup.exe could not be copied or its checksum written: " + $_.Exception.Message) }

$result.ok = $true
$result | ConvertTo-Json -Depth 4
exit 0
