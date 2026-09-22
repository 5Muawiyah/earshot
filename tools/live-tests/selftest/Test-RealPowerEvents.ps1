<#
.SYNOPSIS
    Runs the real, unfaked Get-PowerEvents against this machine's own System event log.

.DESCRIPTION
    tools\live-tests\selftest replaces Get-PowerEvents with a fake for every case it runs (see
    Run-OneHalf.ps1), the same reason Test-RealLauncher.ps1 exists for Invoke-Earshot: a helper
    the self-test never calls for real has never actually been proven against Get-WinEvent and
    this machine's own System log, FilterHashtable syntax included.

    This script calls the real, unfaked Get-PowerEvents from LiveTest.psm1 for the last 24 hours
    and checks that it returns a list (possibly empty, if nothing in that window matches) and
    that the read itself did not fail. It changes nothing: Get-WinEvent is a read of the System
    log, and no elevation is needed for the five event IDs Get-PowerEvents asks for.

.PARAMETER Root
    The repository root. Defaults to the folder three above this script.
#>

#Requires -Version 5.1

[CmdletBinding()]
param([string]$Root = '')

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Root)) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }

# The real module, not the self-test's shadowing stubs: nothing here installs Run-OneHalf.ps1's
# fakes, so this is Get-PowerEvents exactly as it ships.
Microsoft.PowerShell.Core\Import-Module (Join-Path $Root 'tools\live-tests\LiveTest.psm1') -Force

$result = [ordered]@{ ok = $false; problems = @(); eventCount = 0 }
$summaryPath = Join-Path $env:TEMP ('earshot-powerevents-selftest-' + [guid]::NewGuid().ToString('N') + '.txt')

# A minimal run: Get-PowerEvents only ever touches $Run.Errors (through Write-Failure, if the read
# itself throws for a reason other than "nothing matched") and $Run.SummaryPath (through
# Write-Failure's own call to Write-Line), so nothing else off New-LiveTestRun's shape is needed.
$run = [ordered]@{ Errors = (New-Object System.Collections.ArrayList); SummaryPath = $summaryPath }

try
{
    Set-Content -LiteralPath $summaryPath -Value '' -Encoding UTF8
    $events = Get-PowerEvents -Run $run -SinceUtc ((Get-Date).ToUniversalTime().AddHours(-24))
    $result.eventCount = @($events).Count

    foreach ($entry in $run.Errors)
    {
        $result.problems += ('Get-PowerEvents recorded a failure reading the real event log: ' + [string]$entry.message)
    }
}
catch
{
    $result.problems += ('Get-PowerEvents threw: ' + ($_ | Out-String).Trim())
}
finally
{
    if (Test-Path -LiteralPath $summaryPath) { Remove-Item -LiteralPath $summaryPath -Force -ErrorAction SilentlyContinue }
}

$result.ok = ($result.problems.Count -eq 0)
$result | ConvertTo-Json -Depth 4
if ($result.ok) { exit 0 } else { exit 1 }
