<#
.SYNOPSIS
    Runs the shipped Invoke-Earshot for real, against a harmless executable, and checks that
    the exit code it records is the one the process actually returned.

.DESCRIPTION
    tools\live-tests\selftest replaces Invoke-Earshot with a fake for every case it runs (see
    Run-OneHalf.ps1), so the real function, the one that calls
    Start-Process -FilePath ... -NoNewWindow -PassThru -RedirectStandardOutput, had never been
    executed by anything before the owner's first live sitting. There, every step came back
    with exitCode null: under Windows PowerShell 5.1, Start-Process -PassThru only fills in
    ExitCode for a process whose handle was read while it was still running. Commit ebb6d4c
    added "$null = $process.Handle" to fix that and a guard that reports an unreadable code
    instead of scoring it, but neither had a committed test.

    This script calls the real, unfaked Invoke-Earshot from LiveTest.psm1 with $Run.ExePath set
    to %SystemRoot%\System32\cmd.exe instead of Earshot.exe, once with arguments that exit 7 and
    once with arguments that exit 0, and checks the exitCode each run recorded. It changes
    nothing outside its own temporary folder under %TEMP%: cmd.exe /c exit <n> starts and exits
    immediately and touches no device, no registry key and no scheduled task.

.PARAMETER Root
    The repository root. Defaults to the folder three above this script.
#>

#Requires -Version 5.1

[CmdletBinding()]
param([string]$Root = '')

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Root)) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }

# The real cmdlet, not the self-test's shadowing function: nothing here installs the stubs from
# Run-OneHalf.ps1, so this is the module exactly as it ships.
Microsoft.PowerShell.Core\Import-Module (Join-Path $Root 'tools\live-tests\LiveTest.psm1') -Force

# A minimal run context built by hand rather than through New-LiveTestRun, because that
# function's Resolve-EarshotExe refuses any path whose file name is not Earshot.exe, and this
# script deliberately points at cmd.exe instead. Invoke-Earshot itself only reads $Run.ExePath,
# $Run.Folder, $Run.SummaryPath, $Run.StepIndex, $Run.Steps, $Run.Errors and $Run.AppLiveTest,
# so only those are given real values; the rest of the shape New-LiveTestRun would have built is
# not needed here.
function New-CmdRun
{
    param([Parameter(Mandatory = $true)][string]$Folder)

    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    return [ordered]@{
        ExePath        = (Join-Path $env:SystemRoot 'System32\cmd.exe')
        Folder         = $Folder
        SummaryPath    = (Join-Path $Folder 'summary.txt')
        AppLiveTest    = (Join-Path $Folder 'app-evidence-source')
        AppEvidence    = (Join-Path $Folder 'app-evidence')
        StepIndex      = 0
        Steps          = (New-Object System.Collections.ArrayList)
        Errors         = (New-Object System.Collections.ArrayList)
        CopiedEvidence = (New-Object System.Collections.ArrayList)
        LastCopied     = @()
    }
}

$result = [ordered]@{ ok = $false; problems = @(); exitCodes = [ordered]@{} }
$workRoot = Join-Path $env:TEMP ('earshot-launcher-selftest-' + [guid]::NewGuid().ToString('N'))

# Records what a step actually captured against what cmd.exe was told to exit with. Kept apart from the
# two calls below so each of those keeps a literal -Command array: LiveTestScriptTests.
# EveryArgumentIsEitherALiteralOrAKnownStandIn statically checks every Invoke-Earshot/Invoke-EarshotElevated
# call under tools\live-tests, this script included, and only accepts a literal or a variable from its own
# small stand-in table for -Command, not an arbitrary expression such as a loop variable's property.
function Confirm-ExitCode
{
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        $StepResult,
        [Parameter(Mandatory = $true)][int]$Want
    )

    $code = $null
    if ($null -ne $StepResult) { $code = $StepResult.exitCode }
    $result.exitCodes[$Label] = $code

    if ($code -ne $Want)
    {
        $shown = $(if ($null -eq $code) { 'null' } else { $code })
        $result.problems += ('cmd.exe /c exit ' + $Want + ' was recorded as exitCode ' + $shown + ' instead of ' + $Want + '.')
    }
}

try
{
    # Not -Live: this proves the exit code capture on the ordinary read-only path, the one every probe
    # step takes, without needing Confirm-Step answered.
    $run7 = New-CmdRun -Folder (Join-Path $workRoot 'exit7')
    $step7 = Invoke-Earshot -Run $run7 -Label 'exit7' -Command @('/c', 'exit', '7')
    Confirm-ExitCode -Label 'exit7' -StepResult $step7 -Want 7

    $run0 = New-CmdRun -Folder (Join-Path $workRoot 'exit0')
    $step0 = Invoke-Earshot -Run $run0 -Label 'exit0' -Command @('/c', 'exit', '0')
    Confirm-ExitCode -Label 'exit0' -StepResult $step0 -Want 0
}
finally
{
    if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

$result.ok = ($result.problems.Count -eq 0)
$result | ConvertTo-Json -Depth 4
if ($result.ok) { exit 0 } else { exit 1 }
