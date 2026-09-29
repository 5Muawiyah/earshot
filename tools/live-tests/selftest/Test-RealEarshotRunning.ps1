<#
.SYNOPSIS
    Runs the real, unfaked Test-EarshotRunning against this machine's own process list.

.DESCRIPTION
    tools\live-tests\selftest replaces Test-EarshotRunning with a fake for every case it runs (see
    Run-OneHalf.ps1), the same reason Test-RealPowerEvents.ps1 exists for Get-PowerEvents: a helper the
    self-test never calls for real has never been proven against the process list on this machine.

    This script calls the real Test-EarshotRunning and checks that it answers yes or no (unknown is a
    failed read, and a failed read here is a problem) and that the answer is the one an independent read
    gives. The independent read is tasklist.exe with the session column, not Get-Process and not the
    helper's own expression: an Earshot process in a user session (session 0 is the hand-back service's,
    and never counts as the tray). It changes nothing and starts nothing: both reads are reads, and
    Earshot.exe is never launched.

    The yes answer is proved with a decoy: a copy of ping.exe named Earshot.exe, started hidden for a few
    seconds from a temp folder and stopped again, so a process of that name really is in a user session
    without the tray being started. The decoy is a program that pings the local machine and does nothing else.

    What this cannot prove is the session-0 branch, the one that skips the service's own process. It
    can only be exercised while an Earshot process is in session 0, which needs the hand-back service to
    be installed and running; the result says, in session0Branch, whether it was exercised on this run.
    On the machine this was written on no Earshot process has been in session 0, so the branch is
    unproved there.

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

$result = [ordered]@{
    ok = $false; problems = @(); running = $null; independentRunning = $null
    userSessionProcesses = 0; serviceSessionProcesses = 0; session0Branch = $null; decoyRunning = $null
}

try
{
    $answer = Test-EarshotRunning
    $result.running = $answer
    if ($answer -isnot [string] -or $answer -notin @('yes', 'no', 'unknown'))
    {
        $result.problems += ('Test-EarshotRunning returned ' + $(if ($null -eq $answer) { 'nothing' } else { [string]$answer + ' (' + $answer.GetType().Name + ')' }) + ', not yes, no or unknown.')
    }
    elseif ($answer -eq 'unknown')
    {
        $result.problems += 'Test-EarshotRunning could not read the process list here, so it answered unknown.'
    }

    # The independent read: tasklist.exe /v prints one CSV row per process with the session number in the fourth
    # column (Image Name, PID, Session Name, Session#, ...). The columns are read by position, not by name, because the
    # names follow the display language. With no match it prints an INFO line and no rows.
    # https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/tasklist
    $tasklist = Join-Path ([System.Environment]::GetFolderPath('System')) 'tasklist.exe'
    $lines = @(& $tasklist /v /fo csv /fi 'IMAGENAME eq Earshot.exe' 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0)
    {
        $result.problems += ('tasklist.exe exited ' + $LASTEXITCODE + ': ' + ($lines -join ' '))
    }

    $rows = @($lines | Where-Object { $_ -like '"*' } | Select-Object -Skip 1 |
            ForEach-Object { $_ | ConvertFrom-Csv -Header 'image', 'pid', 'sessionName', 'session', 'memory', 'status', 'user', 'cpu', 'window' })
    $user = @($rows | Where-Object { [int]$_.session -ne 0 })
    $service = @($rows | Where-Object { [int]$_.session -eq 0 })
    $result.userSessionProcesses = $user.Count
    $result.serviceSessionProcesses = $service.Count
    $result.independentRunning = $(if ($user.Count -gt 0) { 'yes' } else { 'no' })
    $result.session0Branch = $(if ($service.Count -gt 0) {
            'exercised: ' + $service.Count + ' Earshot process(es) in session 0 were present and skipped.'
        }
        else {
            'unproved: no Earshot process was in session 0 while this ran, so the branch that skips the hand-back service was not exercised.'
        })

    if ($answer -in @('yes', 'no') -and $answer -ne $result.independentRunning)
    {
        $result.problems += ('Test-EarshotRunning said ' + $answer + ', and tasklist.exe found ' + $user.Count +
            ' Earshot process(es) in a user session and ' + $service.Count + ' in session 0.')
    }

    # The yes branch, against a real process of the right name in a user session and an independent read that finds it by id.
    $decoyFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('earshot-decoy-' + [guid]::NewGuid().ToString('N'))
    $decoy = $null
    try
    {
        New-Item -ItemType Directory -Force -Path $decoyFolder | Out-Null
        $decoyPath = Join-Path $decoyFolder 'Earshot.exe'
        Copy-Item -LiteralPath (Join-Path ([System.Environment]::GetFolderPath('System')) 'PING.EXE') -Destination $decoyPath
        $decoy = Start-Process -FilePath $decoyPath -ArgumentList '-n', '30', '127.0.0.1' -WindowStyle Hidden -PassThru
        $seen = 'no'
        for ($i = 0; $i -lt 40 -and $seen -ne 'yes'; $i++)
        {
            Start-Sleep -Milliseconds 250
            $seen = Test-EarshotRunning
        }

        $result.decoyRunning = $seen
        $byId = @(& $tasklist /v /fo csv /fi ('PID eq ' + $decoy.Id) 2>&1 | ForEach-Object { [string]$_ } | Where-Object { $_ -like '"*' } | Select-Object -Skip 1 |
                ForEach-Object { $_ | ConvertFrom-Csv -Header 'image', 'pid', 'sessionName', 'session', 'memory', 'status', 'user', 'cpu', 'window' })
        if ($byId.Count -ne 1 -or [int]$byId[0].session -eq 0 -or $byId[0].image -ne 'Earshot.exe')
        {
            $result.problems += ('tasklist.exe did not show the decoy as Earshot.exe in a user session: ' + ($byId | Out-String).Trim())
        }
        elseif ($seen -ne 'yes')
        {
            $result.problems += ('A process named Earshot.exe is running in session ' + $byId[0].session + ', and Test-EarshotRunning said ' + $seen + '.')
        }
    }
    finally
    {
        if ($null -ne $decoy)
        {
            Stop-Process -Id $decoy.Id -Force -ErrorAction SilentlyContinue
            $decoy.WaitForExit(5000) | Out-Null
        }

        Remove-Item -LiteralPath $decoyFolder -Recurse -Force -ErrorAction SilentlyContinue
    }
}
catch
{
    $result.problems += ('Test-EarshotRunning threw: ' + ($_ | Out-String).Trim())
}

$result.ok = ($result.problems.Count -eq 0)
$result | ConvertTo-Json -Depth 4
if ($result.ok) { exit 0 } else { exit 1 }
