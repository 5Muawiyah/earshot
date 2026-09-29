<#
.SYNOPSIS
    Runs the real, unfaked Test-EarshotRunning against this machine's own process list.

.DESCRIPTION
    tools\live-tests\selftest replaces Test-EarshotRunning with a fake for every case it runs (see
    Run-OneHalf.ps1), the same reason Test-RealPowerEvents.ps1 exists for Get-PowerEvents: a helper the
    self-test never calls for real has never been proven against Get-Process on this machine.

    This script calls the real Test-EarshotRunning and checks that it returns a boolean and that the
    answer is the one an independent read of the process list gives: an Earshot process in a user
    session (session 0 is the hand-back service's, and never counts as the tray). It changes nothing
    and starts nothing: Get-Process is a read, and Earshot.exe is never launched.

.PARAMETER Root
    The repository root. Defaults to the folder three above this script.
#>

#Requires -Version 5.1

[CmdletBinding()]
param([string]$Root = '')

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Root)) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }

# The real module, not the self-test's shadowing stubs: nothing here installs Run-OneHalf.ps1's fakes, so this
# is Test-EarshotRunning exactly as it ships.
Microsoft.PowerShell.Core\Import-Module (Join-Path $Root 'tools\live-tests\LiveTest.psm1') -Force

$result = [ordered]@{ ok = $false; problems = @(); running = $null; userSessionProcesses = 0; serviceSessionProcesses = 0 }

try
{
    $answer = Test-EarshotRunning
    if ($answer -isnot [bool])
    {
        $result.problems += ('Test-EarshotRunning returned ' + $(if ($null -eq $answer) { 'nothing' } else { $answer.GetType().Name }) + ', not a boolean.')
    }

    $all = Get-Process -Name 'Earshot' -ErrorAction SilentlyContinue
    $user = @($all | Where-Object { $_.SessionId -ne 0 })
    $service = @($all | Where-Object { $_.SessionId -eq 0 })
    $result.userSessionProcesses = $user.Count
    $result.serviceSessionProcesses = $service.Count
    $result.running = $answer

    if ($answer -is [bool] -and $answer -ne ($user.Count -gt 0))
    {
        $result.problems += ('Test-EarshotRunning said ' + $answer + ', and an independent read found ' + $user.Count +
            ' Earshot process(es) in a user session and ' + $service.Count + ' in session 0.')
    }
}
catch
{
    $result.problems += ('Test-EarshotRunning threw: ' + ($_ | Out-String).Trim())
}

$result.ok = ($result.problems.Count -eq 0)
$result | ConvertTo-Json -Depth 4
if ($result.ok) { exit 0 } else { exit 1 }
