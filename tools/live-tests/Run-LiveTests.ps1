<#
.SYNOPSIS
    Lists the Earshot live device tests and runs one of them.

.DESCRIPTION
    The tests are ordered by risk, not by convenience: the first one settles the
    question most likely to sink the design, and the last ones tidy up the details.
    Test 08 is the acceptance test the whole application exists for; run it in the
    configuration Earshot ships in and treat its result as the one that counts.

    Read tools\live-tests\README.md before the first run. It explains the restore
    script, which you should know how to run before you start.

    This launcher only starts one test. Each test asks you before every step that
    changes anything.

.PARAMETER List
    Print the tests and stop.

.PARAMETER Test
    Which test to run: its number (1, 01, 8) or its script name.

.PARAMETER ExePath
    Earshot.exe: the installed copy (%ProgramFiles%\Earshot\Earshot.exe) or an
    unzipped release. Never a build output folder.

.PARAMETER RunRoot
    An existing evidence folder to write into, for the second half of a test that
    needs a restart. The first half prints the one to use.

.PARAMETER Resume
    Run the second half of a test, after a restart.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -List

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-LiveTests.ps1 -Test 01 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$List,
    [string]$Test = '',
    [string]$ExePath = '',
    [string]$RunRoot = '',
    [switch]$Resume
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Riskiest first. Each row names the script, what the test settles, and what it needs
# before it starts. A test marked "two halves" stops for a restart and prints the exact
# command to run afterwards.
$tests = @(
    [ordered]@{
        Number = '01'; Script = '01-A2dpOneShot.ps1'
        Title = 'One-shot reconnect on the A2DP filter, protection on then off'
        Settles = 'Whether connect works at all in the shipping default, and so whether the Handsfree assisted fallback is needed.'
        Needs = 'Set up, nodes allowed, AirPods on the phone, tray closed.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '02'; Script = '02-Disconnect.ps1'
        Title = 'One-shot disconnect per filter, and whether a block alone drops the link'
        Settles = 'The 12 s disconnect budget, whether one filter drops the whole link, and what a block does to a link in use.'
        Needs = 'Set up, nodes allowed, AirPods connected to this PC, tray closed.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '03'; Script = '03-AllowPages.ps1'
        Title = 'Whether an allow alone pages the AirPods'
        Settles = 'Whether turning Block at boot off can take the AirPods off the phone at that moment.'
        Needs = 'Set up, nodes blocked, AirPods playing on the phone, tray closed.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '04'; Script = '04-BlockAndReboot.ps1'
        Title = 'Block, restart, and read the nodes again'
        Settles = 'Whether the persistent disable really survives a restart, which the boot block depends on.'
        Needs = 'Set up, nodes present, able to restart.'
        Halves = 'two'
    },
    [ordered]@{
        Number = '05'; Script = '05-Allow.ps1'
        Title = 'Allow the nodes and watch the endpoints return'
        Settles = 'Whether the allow clears the disabled bit, and whether ten seconds is long enough for the endpoints to come back.'
        Needs = 'Set up, nodes blocked, tray closed.'
        Halves = 'two (the restart half is optional)'
    },
    [ordered]@{
        Number = '06'; Script = '06-Handsfree.ps1'
        Title = 'Handsfree protection, unelevated and through the gate'
        Settles = 'Whether a v1.1 unelevated fast path is possible, what Headset returns, and how long the SYSTEM call takes.'
        Needs = 'Set up, AirPods paired, a normal (not administrator) window, tray closed.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '07'; Script = '07-TaskRunEx.ps1'
        Title = 'Non-elevated RunEx of the SYSTEM tasks, and plan B'
        Settles = 'Whether the tray can start the elevated tasks at all, and whether the --principal user fallback is needed.'
        Needs = 'Set up, a normal (not administrator) window, tray closed.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '08'; Script = '08-AcceptancePowerCycle.ps1'
        Title = 'ACCEPTANCE: full power cycle in the shipping configuration'
        Settles = 'The single acceptance test for v1: after a full power cycle the AirPods stay connected to the phone.'
        Needs = 'Set up from a release build, Block at boot on, Protect audio on, AirPods playing on the phone, able to power down.'
        Halves = 'two'
    },
    [ordered]@{
        Number = '09'; Script = '09-ShutdownWhileConnected.ps1'
        Title = 'Shutdown while connected, then the next boot'
        Settles = 'Whether the v1.1 pre-shutdown service is needed.'
        Needs = 'Set up, tray running, Block at boot on, able to power down.'
        Halves = 'two'
    },
    [ordered]@{
        Number = '10'; Script = '10-ShutdownMessages.ps1'
        Title = 'End-session messages, one variant per run'
        Settles = 'Which end-session messages arrive for each kind of restart, and whether the queued block finishes.'
        Needs = 'Set up, tray running, AirPods connected. Run once per variant with -Variant 1 to 5; variant 5 signs out and back in.'
        Halves = 'two, per variant'
    },
    [ordered]@{
        Number = '11'; Script = '11-BatteryDisconnected.ps1'
        Title = 'Battery sweep, disconnected and connected'
        Settles = 'The disconnected leg of the phase 0 battery question, which was never run.'
        Needs = 'Set up, nodes allowed, AirPods disconnected from this PC to start.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '12'; Script = '12-CallbackThread.ps1'
        Title = 'Notification thread, apartment and event order'
        Settles = 'Which thread and apartment Core Audio calls back on, and how noisy one change is.'
        Needs = 'Set up, nodes allowed, AirPods disconnected from this PC to start, tray closed.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '13'; Script = '13-GraceWindow.ps1'
        Title = 'Tuning the idle grace window'
        Settles = 'Whether thirty seconds is the right wait before the nodes are blocked again.'
        Needs = 'Set up, tray running, Block at boot on, time to use the AirPods normally.'
        Halves = 'one, but it takes a while'
    },
    [ordered]@{
        Number = '14'; Script = '14-SetDeviceRefusal.ps1'
        Title = 'The gate refuses to pin a phone'
        Settles = 'That the pin can never move to a device with no A2DP sink, so a phone is never disabled.'
        Needs = 'Set up, the phone paired with this PC, tray closed.'
        Halves = 'one'
    },
    [ordered]@{
        Number = '15'; Script = '15-UninstallReversal.ps1'
        Title = 'Uninstall reverses everything, then install sets it up again'
        Settles = 'Whether uninstall restores the nodes, services, tasks and folders, and whether install passes its own read-back checks.'
        Needs = 'Set up, a release build to hand, tray closed, two or three administrator prompts to approve.'
        Halves = 'two'
    }
)

function Show-Tests
{
    Write-Host ''
    Write-Host 'Earshot live device tests, riskiest first.'
    Write-Host 'Read README.md in this folder first. 00-Restore.ps1 puts the machine back at any point.'
    Write-Host ''
    foreach ($row in $tests)
    {
        Write-Host ([string]$row.Number + '  ' + $row.Title)
        Write-Host ('    settles: ' + $row.Settles)
        Write-Host ('    needs:   ' + $row.Needs)
        Write-Host ('    halves:  ' + $row.Halves + '    script: ' + $row.Script)
        Write-Host ''
    }

    Write-Host '00  Restore the machine to a known state (00-Restore.ps1). Run it whenever a test stops early.'
    Write-Host ''
}

if ($List -or [string]::IsNullOrEmpty($Test))
{
    Show-Tests
    if ([string]::IsNullOrEmpty($Test)) { Write-Host 'Choose one with -Test <number> -ExePath <path to Earshot.exe>.' }
    return
}

if ([string]::IsNullOrEmpty($ExePath))
{
    throw 'Which Earshot.exe? Pass -ExePath, the installed copy or an unzipped release.'
}

$wanted = $Test.Trim()
if ($wanted -eq '0' -or $wanted -eq '00' -or $wanted -match '^(?i)restore$')
{
    $script = Join-Path $PSScriptRoot '00-Restore.ps1'
}
else
{
    $match = $null
    foreach ($row in $tests)
    {
        if ($wanted -eq $row.Number -or $wanted -eq $row.Number.TrimStart('0') -or $wanted -eq $row.Script) { $match = $row }
    }

    if ($null -eq $match)
    {
        Show-Tests
        throw ('There is no test "' + $wanted + '". Use one of the numbers above.')
    }

    $script = Join-Path $PSScriptRoot $match.Script
    Write-Host ''
    Write-Host ([string]$match.Number + '  ' + $match.Title)
    Write-Host ('    settles: ' + $match.Settles)
    Write-Host ('    needs:   ' + $match.Needs)
    Write-Host ''
}

if (-not (Test-Path -LiteralPath $script -PathType Leaf))
{
    throw ('That test script is missing: ' + $script)
}

$forward = @{ ExePath = $ExePath }
if (-not [string]::IsNullOrEmpty($RunRoot)) { $forward['RunRoot'] = $RunRoot }
if ($Resume) { $forward['Resume'] = $true }

& $script @forward
