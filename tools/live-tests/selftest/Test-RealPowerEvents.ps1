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
    that the read itself did not fail. It also checks the two Service Control Manager ids the
    hand-back service test reads (7023 and 7024) against an independent read of the same log over
    thirty days. It changes nothing: Get-WinEvent is a read of the System log, and no elevation is
    needed for the event IDs Get-PowerEvents asks for.

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

$result = [ordered]@{ ok = $false; problems = @(); eventCount = 0; serviceControlEvents = 0 }
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

    # The two Service Control Manager ids the hand-back service test reads (7023 and 7024). The real helper has to
    # return every one an independent read of the same log finds over the same window, so an id left out of its
    # filter shows up here as a shortfall on any machine whose log holds one, and as a match on one whose log does not.
    $since = (Get-Date).ToUniversalTime().AddDays(-30)
    $viaHelper = Get-PowerEvents -Run $run -SinceUtc $since
    $fromHelper = @($viaHelper | Where-Object { $_.provider -eq 'Service Control Manager' -and ($_.id -eq 7023 -or $_.id -eq 7024) })
    $independent = @()
    try
    {
        $independent = @(Get-WinEvent -FilterHashtable @{
                LogName      = 'System'
                ProviderName = 'Service Control Manager'
                Id           = @(7023, 7024)
                StartTime    = $since
            } -ErrorAction Stop)
    }
    catch
    {
        if (([string]($_.Exception.Message)) -notmatch 'No events were found')
        {
            $result.problems += ('The independent read of the Service Control Manager events failed: ' + ($_ | Out-String).Trim())
        }
    }

    $result.serviceControlEvents = $independent.Count
    if ($fromHelper.Count -ne $independent.Count)
    {
        $result.problems += ('Get-PowerEvents returned ' + $fromHelper.Count + ' Service Control Manager error event(s) since ' +
            $since.ToString('o') + ', and an independent read of the log found ' + $independent.Count + '.')
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
