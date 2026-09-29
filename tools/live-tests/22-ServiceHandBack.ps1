<#
.SYNOPSIS
    Hand back by the service when Earshot is not running.

.DESCRIPTION
    Earshot registers a small service that runs all the time. When Windows starts to shut down, it
    blocks the AirPods if the Earshot tray icon did not already hand them back. This test is for the
    case the tray cannot cover: the tray icon is gone (ended from Task Manager here, the way a crash
    would end it) while the AirPods are still connected to this PC, and then the computer is shut
    down.

    It settles whether the always-on service blocks the AirPods at shut down when the tray icon is
    gone, and whether the next boot leaves them alone.

    Everything it records about the service is read from the status file the service writes in
    %ProgramData%\Earshot, from `probe service`, and from the System event log. The service's own
    log is in SYSTEM's profile, which a signed-in user cannot read, so nothing here reads it.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the power cycle.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\22-ServiceHandBack.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [switch]$Resume
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

if ($Resume -and [string]::IsNullOrEmpty($RunRoot))
{
    throw 'The second half needs -RunRoot, the folder the first half printed. It is in resume.txt in that folder.'
}

$run = New-LiveTestRun -TestId '22-service-handback' -Title 'Hand back by the service when Earshot is not running' `
    -Settles 'Whether the always-on service blocks the AirPods at shut down when the Earshot tray icon is gone, and whether the next boot leaves them alone.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Set below, in the first half only: this test shuts down deliberately with the AirPods connected,
# so the closing at-rest check must not offer to block them before that shutdown happens.
$atRestReason = ''

# The file the first half writes the shutdown's own start time to, so the second half reads the
# status files and the event log only from that point on, never from an earlier run's.
$startedFile = ''
if (-not [string]::IsNullOrEmpty($RunRoot)) { $startedFile = Join-Path $run.Folder 'shutdown-start.txt' }

# The newest test 17 result.json's overall outcome, from any earlier sitting, or 'not-run'. Recorded
# as a finding, never a reason to refuse: the owner may have run it under a different RunRoot.
function Get-Test17Result
{
    $paths = Get-EarshotDataPaths
    if (-not (Test-Path -LiteralPath $paths.LiveTestFolder)) { return 'not-run' }
    $newestPath = $null
    $newestWrite = [datetime]::MinValue
    foreach ($folder in (Get-ChildItem -LiteralPath $paths.LiveTestFolder -Directory -ErrorAction SilentlyContinue))
    {
        $candidate = Join-Path $folder.FullName '17-handback-on-shutdown\result.json'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $candidate
        if ($item.LastWriteTimeUtc -ge $newestWrite)
        {
            $newestPath = $candidate
            $newestWrite = $item.LastWriteTimeUtc
        }
    }

    if ($null -eq $newestPath) { return 'not-run' }
    try
    {
        $result = (Get-Content -LiteralPath $newestPath -Raw | ConvertFrom-Json)
        $overall = [string](Get-Field -Object $result -Name 'overall')
        if ([string]::IsNullOrEmpty($overall)) { return 'not-run' }
        return $overall
    }
    catch
    {
        return 'not-run'
    }
}

try
{
    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed from a release built from the current head, set up, and running in the tray.',
            'Block at boot is on.',
            '"Hand back on shut down, sleep and Exit" is ticked in the menu.',
            'The Earshot hand-back service is installed and running (setup installs it; this test reads it before the shutdown).',
            'The AirPods are paired with this PC and available to connect.'
        ) -PhysicalActions @(
            'Connect the AirPods to this PC by left-clicking the Earshot icon and clicking Connect on the card, and keep audio playing from this PC.',
            'Open Task Manager (Ctrl+Shift+Esc), select Earshot, and choose End task, so the tray icon is gone while the AirPods stay connected.',
            'Shut down from the Start menu straight away, while the AirPods are still connected and playing.',
            'Listen: notice whether the AirPods go back to your phone as the screen goes dark, or just after.',
            'Wait about ten seconds with the machine off, start it again, sign in, and run the command this half prints.'
        )

        if ($ready)
        {
            # Only now, with the owner committed to going ahead: nothing has shut down yet if they
            # said no above, so the closing check must still offer a block in that case.
            $atRestReason = 'This half deliberately shuts down with the Earshot tray icon gone and the AirPods still connected (the nodes ' +
                'enabled): that is the case the service exists to catch, inside the time Windows gives it.'

            Write-Section -Run $run -Title 'Connect and confirm'
            Wait-Owner -Run $run -Text 'Left-click the Earshot icon, then Connect on the card, to connect the AirPods to this PC, and play something so they stay in use.'
            $audio = Get-AudioState -Run $run -Label 'audio-connected'
            $states = Get-TargetEndpointStates -AudioJson $audio
            $nodes = Get-NodeState -Run $run -Label 'nodes-connected'
            Write-Line -Run $run -Text ('Render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }) + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState'))

            Add-Criterion -Run $run -Id 'connected-first' -Criterion 'The AirPods are connected to this PC and the nodes are enabled before the shutdown.' `
                -Outcome $(if ($states.Render -eq 'Active' -and (Get-Field -Object $nodes -Name 'nodeState') -eq 'Allowed') { 'pass' } else { 'inconclusive' }) `
                -Detail ('render ' + $states.Render + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState') + '. This is the state the shutdown has to be started from.')

            Write-Section -Run $run -Title 'The service, before the shutdown'
            $service = Get-HandBackServiceState -Run $run -Label 'service-before'
            $serviceState = Get-HandBackServiceSummary -ServiceJson $service
            Write-Line -Run $run -Text ('The hand-back service reads: ' + $serviceState)

            Write-Section -Run $run -Title 'End the Earshot tray icon'
            Wait-Owner -Run $run -Text 'Open Task Manager (Ctrl+Shift+Esc), select Earshot, and choose End task. Keep the AirPods connected and playing.'
            $audioAfterEnd = Get-AudioState -Run $run -Label 'audio-tray-gone'
            $statesAfterEnd = Get-TargetEndpointStates -AudioJson $audioAfterEnd
            $nodesAfterEnd = Get-NodeState -Run $run -Label 'nodes-tray-gone'
            $nodeStateAfterEnd = Get-Field -Object $nodesAfterEnd -Name 'nodeState'
            $trayRunning = Test-EarshotRunning
            Write-Line -Run $run -Text ('Tray running ' + $trayRunning + ', render ' + $statesAfterEnd.Render + ', nodes ' + $nodeStateAfterEnd)

            Add-Criterion -Run $run -Id 'tray-gone-still-connected' -Criterion 'The Earshot tray icon is gone and the AirPods are still connected with the nodes enabled.' `
                -Outcome $(if (-not $trayRunning -and $nodeStateAfterEnd -eq 'Allowed') { 'pass' } elseif ($trayRunning) { 'fail' } else { 'inconclusive' }) `
                -Detail ('tray running: ' + $trayRunning + '; render ' + $statesAfterEnd.Render + '; nodes ' + $nodeStateAfterEnd + '. The hand-back the tray does at shut down cannot run, so only the service can block.')

            Add-Finding -Run $run -Name 'blockAtBootAtShutdown' -Value (Get-BlockAtBootSetting -Run $run)
            Add-Finding -Run $run -Name 'fastStartupAtShutdown' -Value (Get-FastStartupSetting -Run $run) `
                -Detail 'HiberbootEnabled under HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power, read before the shutdown'

            $paths = Get-EarshotDataPaths
            $settings = Read-EarshotJsonFile -Run $run -Path $paths.SettingsFile
            Add-Finding -Run $run -Name 'handBackSettingAtShutdown' -Value (Get-Field -Object $settings -Name 'HandBackOnShutdownAndSleep') `
                -Detail 'HandBackOnShutdownAndSleep read from settings.json before the shutdown'
            $config = Read-EarshotJsonFile -Run $run -Path (Join-Path $run.MachineFolder 'config.json')
            Add-Finding -Run $run -Name 'handBackMirrorAtShutdown' -Value (Get-Field -Object $config -Name 'HandBackAtShutdown') `
                -Detail 'HandBackAtShutdown read from config.json, the copy of the setting the service reads'
            Add-Finding -Run $run -Name 'serviceStateAtShutdown' -Value $serviceState `
                -Detail 'running, stopped, missing, differs or unreadable, from probe service before the shutdown'
            Add-Finding -Run $run -Name 'serviceWorkingSetBytes' -Value (Get-HandBackServiceWorkingSet -ServiceJson $service) `
                -Detail 'the working set of the service process, read from Get-Process before the shutdown; not recorded when it cannot be told apart'
            Add-Finding -Run $run -Name 'test17Result' -Value (Get-Test17Result) -Detail 'the newest 17-HandBackOnShutdown result.json overall, from any earlier sitting'

            $shutdownStartUtc = (Get-Date).ToUniversalTime()
            $startedFile = Join-Path $run.Folder 'shutdown-start.txt'
            Set-Content -LiteralPath $startedFile -Value $shutdownStartUtc.ToString('o') -Encoding UTF8
            Save-EarshotLog -Run $run

            Write-Section -Run $run -Title 'Now shut down, straight away'
            Write-Line -Run $run -Text 'Shut down from the Start menu while the audio is still playing on this PC and the tray icon is gone.'
            Write-Line -Run $run -Text 'The service is the only part of Earshot that can hand the AirPods back now.'
            Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
        }
    }
    else
    {
        # Missing only when the first half never reached the point of shutting down (the owner said
        # no to "ready to start", so there is nothing to resume from): not a failure of this half.
        # Reading with no floor at all is the honest fallback: every status file and event the
        # folder and the log hold is read, not none of them.
        if ([string]::IsNullOrEmpty($startedFile) -or -not (Test-Path -LiteralPath $startedFile -PathType Leaf))
        {
            Write-Line -Run $run -Text 'shutdown-start.txt was not found; the first half likely never reached the shutdown. Reading with no time floor.'
            $shutdownStartUtc = [datetime]::MinValue
        }
        else
        {
            $startedText = ([string](Get-Content -LiteralPath $startedFile -Raw)).Trim()
            $shutdownStartUtc = [datetime]::Parse($startedText, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        }

        Write-Section -Run $run -Title 'After the boot'
        $nodes = Get-NodeState -Run $run -Label 'nodes-after-boot'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $audio = Get-AudioState -Run $run -Label 'audio-after-boot'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Nodes ' + $nodeState + ', render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }))

        Add-Criterion -Run $run -Id 'nodes-after-boot' -Criterion 'The nodes are blocked after the boot.' `
            -Outcome $(if ($nodeState -eq 'Blocked') { 'pass' } else { 'fail' }) `
            -Detail ('They read ' + $nodeState + '.')

        $paged = Read-Answer -Run $run -Question 'Did the AirPods connect to this PC by themselves after this boot?'
        Add-Criterion -Run $run -Id 'not-paged-at-boot' -Criterion 'Windows did not page the AirPods at the boot after this shutdown.' `
            -Outcome $(if ($paged -eq 'no' -and $states.Render -ne 'Active') { 'pass' } elseif ($paged -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $paged + '; the render endpoint reads ' + $states.Render + '.')

        $heard = Read-Answer -Run $run -Question 'As the screen went dark, or just after, did the AirPods go back to your phone?'
        Add-Criterion -Run $run -Id 'heard-handed-back' -Criterion 'The AirPods went back to the phone at the moment of shutting down.' `
            -Outcome $(if ($heard -eq 'yes') { 'pass' } elseif ($heard -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $heard + '.')

        $restarted = Read-Answer -Run $run -Question 'Did you restart the computer, rather than shut it down?'
        Add-Finding -Run $run -Name 'restartedNotShutDown' -Value $restarted -Detail 'the owner''s answer; a restart is recorded the same way, and the status file says whether it delivered the pre-shutdown control'

        Write-Section -Run $run -Title 'What the service recorded'
        $status = Get-PreshutdownStatus -Run $run -SinceUtc $shutdownStartUtc
        $serviceAfter = Get-HandBackServiceState -Run $run -Label 'service-after-boot'
        $serviceStateAfter = Get-HandBackServiceSummary -ServiceJson $serviceAfter

        $ranMs = $null
        $result = $null
        $reason = $null
        $stateRead = $null
        $vetoSeen = 'no-evidence'
        $retryTook = 'no-evidence'
        if ($null -ne $status)
        {
            $ranMs = $status.milliseconds
            $result = $status.result
            $reason = $status.reason
            $stateRead = $status.state
            $vetoSeen = $status.vetoSeen
            $retryTook = $status.retryTook
            Write-Line -Run $run -Text ('Status file ' + $status.file + ': ' + $result + ', state ' + $stateRead + ', ' + $ranMs + ' ms, "' + $reason + '".')
        }
        else
        {
            Write-Line -Run $run -Text 'No status file with the verb preshutdown was written since the shutdown started.'
        }

        Add-Criterion -Run $run -Id 'service-ran' -Criterion 'The service ran at shut down and wrote its status file.' `
            -Outcome $(if ($null -ne $status) { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -ne $status) { 'status file ' + $status.file } else { 'no status file with the verb preshutdown at or after the shutdown; the service did not run, or its folder check failed' })

        Add-Criterion -Run $run -Id 'service-blocked' -Criterion 'The service blocked the AirPods, or found them blocked.' `
            -Outcome $(if ($null -eq $status) { 'inconclusive' } elseif ($result -eq 'success' -and $stateRead -eq 'Blocked') { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -eq $status) { 'no status file to read' } elseif ($result -eq 'success' -and $stateRead -eq 'Blocked') { 'success, Blocked' } else { 'result ' + $result + ', state ' + $stateRead + '; failed steps: ' + $status.failedSteps })

        Add-Criterion -Run $run -Id 'service-in-budget' -Criterion 'The service finished inside its 8,000 ms budget.' `
            -Outcome $(if ($null -eq $ranMs) { 'inconclusive' } elseif ($ranMs -le 8000) { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -eq $ranMs) { 'no status file to read' } else { [string]$ranMs + ' ms from the control to the status file.' })

        $events = Get-PowerEvents -Run $run -SinceUtc $shutdownStartUtc
        $shutdownEvents = @($events | Where-Object { $_.id -eq 1074 })
        $dirtyEvents = @($events | Where-Object { $_.id -eq 41 })
        Add-Criterion -Run $run -Id 'shutdown-was-clean' -Criterion 'The shutdown itself was clean: a 1074 event and no dirty-boot 41 event after it.' `
            -Outcome $(if (@($shutdownEvents).Count -gt 0 -and @($dirtyEvents).Count -eq 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($shutdownEvents).Count + ' shutdown-request event(s), ' + @($dirtyEvents).Count + ' dirty-boot event(s).')

        $serviceEvents = @($events | Where-Object { ($_.id -eq 7023 -or $_.id -eq 7024) -and ($_.message -match 'EarshotHandBack|Earshot hand-back') })
        Add-Criterion -Run $run -Id 'service-no-error-event' -Criterion 'The service control manager recorded no error for the service (no 7023 or 7024 event).' `
            -Outcome $(if (@($serviceEvents).Count -eq 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($serviceEvents).Count + ' error event(s) naming the service.')

        $bootEvent = @($events | Where-Object { $_.id -eq 27 } | Select-Object -Last 1)
        $bootType = $(if (@($bootEvent).Count -gt 0) { @($bootEvent)[0].message } else { $null })
        $eventIds = @($events | Where-Object { $_.provider -eq 'Service Control Manager' } | ForEach-Object { [string]$_.id } | Sort-Object -Unique)

        Add-Finding -Run $run -Name 'servicePreshutdownMs' -Value $ranMs -Detail 'FinishedUtc minus StartedUtc of the status file; not recorded when there is none'
        Add-Finding -Run $run -Name 'serviceResult' -Value $result
        Add-Finding -Run $run -Name 'serviceState' -Value $serviceStateAfter -Detail 'how the service reads after the boot, from probe service'
        Add-Finding -Run $run -Name 'serviceReason' -Value $reason -Detail 'the Detail of the status file''s preshutdown step'
        Add-Finding -Run $run -Name 'serviceVetoSeen' -Value $vetoSeen -Detail 'CR_REMOVE_VETOED among the status file''s steps: yes, no or no-evidence'
        Add-Finding -Run $run -Name 'serviceRetryTook' -Value $retryTook -Detail 'a second cm-disable step for a vetoed node with CR_SUCCESS: yes, no or no-veto'
        Add-Finding -Run $run -Name 'bootType' -Value $bootType
        Add-Finding -Run $run -Name 'serviceEventIds' -Value $(if (@($eventIds).Count -gt 0) { ($eventIds -join ',') } else { $null }) -Detail 'the Service Control Manager event ids seen since the shutdown started'
        Add-Finding -Run $run -Name 'test17Result' -Value (Get-Test17Result) -Detail 'the newest 17-HandBackOnShutdown result.json overall, from any earlier sitting'

        Save-EarshotLog -Run $run
    }
}
catch
{
    Write-Failure -Run $run -Message ('The test stopped with an error: ' + ($_ | Out-String).Trim())
    Add-Criterion -Run $run -Id 'run' -Criterion 'The test ran to the end.' -Outcome 'fail' -Detail 'See the error above. Run 00-Restore.ps1 before the next test.'
}
finally
{
    $overall = Complete-LiveTestRun -Run $run -AtRestReason $atRestReason
    Write-Host ('Test 22 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
