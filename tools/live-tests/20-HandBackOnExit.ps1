<#
.SYNOPSIS
    Hand back on Exit.

.DESCRIPTION
    Choosing Exit from the tray menu while the AirPods are connected to this PC now hands them
    back the way a shut down does: it lets go of the connection, waits for the sound to leave,
    then blocks the nodes, inside a short cap, and says so if the block did not take. This test
    connects, plays, chooses Exit, and reads whether the AirPods were let go and blocked, in that
    order, with nothing left enabled and nothing said that should not have been. It also reads
    whether the music was paused just before the AirPods let go.

    It settles whether Exit lets the AirPods go and blocks them, inside its cap, and leaves the
    nodes blocked.

    Nothing is shut down: this needs no restart. It leaves Earshot closed, so start it again from
    the Start menu afterwards.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\20-HandBackOnExit.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

$run = New-LiveTestRun -TestId '20-handback-on-exit' -Title 'Hand back on Exit' `
    -Settles 'Whether choosing Exit while the AirPods are connected lets them go and blocks them, inside its cap, and leaves the nodes blocked.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Exit is the ordinary way out, and hands the AirPods back and blocks them, so this half ends the way
# every other does: through the closing step, with the nodes expected blocked already. No reason is
# given for leaving them enabled.
$atRestReason = ''

# Parses "<label> <number> ms" out of a log line. $null when the line is empty or the figure is
# not there to read, never a guessed 0.
function Get-MillisecondsFigure
{
    param([string]$Line, [string]$Pattern)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    if ($Line -match $Pattern) { return [int]$Matches[1] }
    return $null
}

# The first UTC stamp (yyyy-MM-ddTHH:mm:ss.fffZ) inside a log line's own text, after the line's
# leading stamp, or $null. Both the "started at" and the "block sent at" lines carry one.
function Get-TextStampUtc
{
    param([string]$Line)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    $found = [regex]::Matches($Line, '\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z')
    if ($found.Count -lt 2) { return $null }
    return [datetime]::Parse($found[1].Value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed from a release built from the current head, set up, and running in the tray.',
        'Block at boot is on.',
        'The "Hand back" item in the tray menu is turned on: it is off by default, so tick it first.',
        'The AirPods are paired with this PC and available to connect.',
        'Something that shows in Windows media controls plays on this PC: a music or video player in a browser, or a music app.'
    ) -PhysicalActions @(
        'Connect the AirPods to this PC by left-clicking the Earshot icon and clicking Connect on the card, and play something so they stay in use.',
        'Right-click the Earshot icon and choose Exit while they are still connected and playing.',
        'Listen: notice whether the sound goes back to your phone as Earshot closes.',
        'Start Earshot again from the Start menu when this test says it is done.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Connect and confirm'
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon, then Connect on the card, to connect the AirPods to this PC, and play something so they stay in use. Do not pause it yourself.'
        $audioBefore = Get-AudioState -Run $run -Label 'audio-connected'
        $statesBefore = Get-TargetEndpointStates -AudioJson $audioBefore
        $nodesBefore = Get-NodeState -Run $run -Label 'nodes-connected'
        $nodeStateBefore = Get-Field -Object $nodesBefore -Name 'nodeState'
        Write-Line -Run $run -Text ('Render ' + $statesBefore.Render + ', nodes ' + $nodeStateBefore + ' before Exit.')

        Add-Criterion -Run $run -Id 'connected-first' -Criterion 'The AirPods are connected to this PC and the nodes are enabled before Exit.' `
            -Outcome $(if ($statesBefore.Render -eq 'Active' -and $nodeStateBefore -eq 'Allowed') { 'pass' } else { 'inconclusive' }) `
            -Detail ('render ' + $statesBefore.Render + ', nodes ' + $nodeStateBefore + '. This is the state Exit has to be chosen from.')

        Add-Finding -Run $run -Name 'blockAtBootAtExit' -Value (Get-BlockAtBootSetting -Run $run)

        # A member the file does not hold reads as the default, which is off: a new settings file has none.
        $paths = Get-EarshotDataPaths
        $settings = Read-EarshotJsonFile -Run $run -Path $paths.SettingsFile
        $handBackSetting = Get-Field -Object $settings -Name 'HandBackOnShutdownAndSleep'
        $handBackOn = ($handBackSetting -eq $true)
        Add-Criterion -Run $run -Id 'hand-back-on' -Criterion 'The hand-back setting is on, so Exit has something to run.' `
            -Outcome $(if ($handBackOn) { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -eq $handBackSetting) { 'settings.json holds no HandBackOnShutdownAndSleep member, which reads as the default, off: tick the menu item first.' } else { 'HandBackOnShutdownAndSleep is ' + $handBackSetting + ' in settings.json.' })

        # Read now, so a run with the pause setting off is not blamed for a pause it was told not to make.
        $pauseSetting = Get-Field -Object $settings -Name 'PauseWhenAirPodsLeave'
        $pauseOn = ($null -eq $pauseSetting -or $pauseSetting -eq $true)
        Add-Finding -Run $run -Name 'pauseSettingAtExit' -Value $(if ($pauseOn) { 'on' } else { 'off' }) -Detail 'PauseWhenAirPodsLeave in settings.json; a member the file does not hold reads as the default, on'

        $exitStartUtc = (Get-Date).ToUniversalTime()
        Save-EarshotLog -Run $run

        Write-Section -Run $run -Title 'Now choose Exit'
        Wait-Owner -Run $run -Text 'While the AirPods are connected and playing, right-click the Earshot icon and choose Exit. Wait until the icon is gone and Earshot has closed, then come back here.'

        Write-Section -Run $run -Title 'After Exit'
        $nodesAfter = Get-NodeState -Run $run -Label 'nodes-after-exit'
        $nodeStateAfter = Get-Field -Object $nodesAfter -Name 'nodeState'
        $audioAfter = Get-AudioState -Run $run -Label 'audio-after-exit'
        $statesAfter = Get-TargetEndpointStates -AudioJson $audioAfter
        Write-Line -Run $run -Text ('Nodes ' + $nodeStateAfter + ', render ' + $statesAfter.Render + ' after Exit.')

        Add-Criterion -Run $run -Id 'nodes-after-exit' -Criterion 'The nodes read Blocked after Exit.' `
            -Outcome $(if ($nodeStateAfter -eq 'Blocked') { 'pass' } else { 'fail' }) `
            -Detail ('They read ' + $nodeStateAfter + '.')

        Add-Criterion -Run $run -Id 'released-after-exit' -Criterion 'The AirPods are no longer connected to this PC after Exit.' `
            -Outcome $(if ($statesAfter.Render -ne 'Active') { 'pass' } else { 'fail' }) `
            -Detail ('The render endpoint reads ' + $statesAfter.Render + '.')

        $heard = Read-Answer -Run $run -Question 'As Earshot closed, did the sound go back to your phone or stop coming out of the AirPods on this PC?'
        Add-Criterion -Run $run -Id 'heard-handed-back' -Criterion 'The sound left this PC as Exit ran.' `
            -Outcome $(if ($heard -eq 'yes') { 'pass' } elseif ($heard -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $heard + '.')

        $cardSeen = Read-Answer -Run $run -Question 'Did a card say the AirPods were being handed back while Exit ran?'
        Add-Finding -Run $run -Name 'handBackCardSeenOwnerObserved' -Value $cardSeen -Detail 'owner-observed; the card can be gone in well under a second'

        Write-Section -Run $run -Title 'What the log says about the hand-back'
        $started = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): started at' -SinceUtc $exitStartUtc
        $disconnect = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): disconnect' -SinceUtc $exitStartUtc
        $blockSent = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): block sent at' -SinceUtc $exitStartUtc
        $finished = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): finished in' -SinceUtc $exitStartUtc
        $cutShort = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): cut short at' -SinceUtc $exitStartUtc
        $exitSays = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): Exit will say' -SinceUtc $exitStartUtc
        $paused = Get-EarshotLogLines -Run $run -Pattern 'before Earshot lets the AirPods go (hand-back on Exit), paused' -SinceUtc $exitStartUtc
        $trayStopped = Get-EarshotLogLines -Run $run -Pattern 'Tray stopped.' -SinceUtc $exitStartUtc

        foreach ($line in (@($started) + @($disconnect) + @($blockSent) + @($finished) + @($cutShort) + @($exitSays) + @($paused))) { Write-Line -Run $run -Text ('  ' + $line) }

        Add-Criterion -Run $run -Id 'exit-handback-started' -Criterion 'The hand-back started exactly once, at Exit.' `
            -Outcome $(if (@($started).Count -eq 1) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($started).Count + ' "started at" line(s).')

        $disconnectLine = $(if (@($disconnect).Count -gt 0) { @($disconnect)[-1] } else { $null })
        Add-Criterion -Run $run -Id 'exit-disconnect-confirmed' -Criterion 'The disconnect confirmed before the block was sent.' `
            -Outcome $(if ($null -eq $disconnectLine) { 'inconclusive' } elseif ($disconnectLine -match 'confirmed after') { 'pass' } elseif ($disconnectLine -match 'not confirmed') { 'fail' } else { 'inconclusive' }) `
            -Detail $(if ($null -eq $disconnectLine) { 'no disconnect line was logged' } else { $disconnectLine })

        Add-Criterion -Run $run -Id 'exit-block-sent' -Criterion 'The hand-back sent the block.' `
            -Outcome $(if (@($blockSent).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($blockSent).Count + ' "block sent at" line(s).')

        $stillRunning = $null
        if (@($cutShort).Count -gt 0 -and (@($cutShort)[-1]) -match 'still running:\s*([^;]*)') { $stillRunning = $Matches[1].Trim() }
        Add-Criterion -Run $run -Id 'exit-finished' -Criterion 'The hand-back finished inside its own cap.' `
            -Outcome $(if (@($finished).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail $(if (@($finished).Count -gt 0) { @($finished)[-1] } elseif (@($cutShort).Count -gt 0) { @($cutShort)[-1] } else { 'no "finished in" or "cut short" line was logged' })

        # No warning line is only good news once the hand-back is known to have finished: on a log with no hand-back
        # in it, "nothing said" would hold for the wrong reason.
        Add-Criterion -Run $run -Id 'exit-nothing-said' -Criterion 'Exit had nothing to warn about: the block took and the AirPods disconnected.' `
            -Outcome $(if (@($exitSays).Count -gt 0) { 'fail' } elseif (@($finished).Count -gt 0) { 'pass' } else { 'inconclusive' }) `
            -Detail $(if (@($exitSays).Count -gt 0) { @($exitSays)[-1] } elseif (@($finished).Count -gt 0) { 'the hand-back finished and no "Exit will say" line was logged.' } else { 'no "finished in" line was logged, so a missing warning proves nothing.' })

        Add-Criterion -Run $run -Id 'exit-paused-first' -Criterion 'The music was paused just before the AirPods let go.' `
            -Outcome $(if (@($paused).Count -gt 0) { 'pass' } elseif (-not $pauseOn) { 'inconclusive' } else { 'fail' }) `
            -Detail ([string]@($paused).Count + ' "paused" line(s) for the hand-back on Exit.' + $(if ($pauseOn) { ' Something has to be playing for it to pause.' } else { ' Pause when AirPods leave this PC is off in settings.json, so none is expected.' }))

        Add-Criterion -Run $run -Id 'exit-closed' -Criterion 'Earshot closed after the hand-back.' `
            -Outcome $(if (@($trayStopped).Count -gt 0) { 'pass' } else { 'inconclusive' }) `
            -Detail ([string]@($trayStopped).Count + ' "Tray stopped." line(s).')

        $startedStamp = Get-TextStampUtc -Line $(if (@($started).Count -gt 0) { @($started)[0] } else { $null })
        $sentStamp = Get-TextStampUtc -Line $(if (@($blockSent).Count -gt 0) { @($blockSent)[-1] } else { $null })
        Add-Finding -Run $run -Name 'exitHandBackFinishedMs' -Value (Get-MillisecondsFigure -Line $(if (@($finished).Count -gt 0) { @($finished)[-1] } else { $null }) -Pattern 'finished in (\d+) ms')
        Add-Finding -Run $run -Name 'exitDisconnectMs' -Value (Get-MillisecondsFigure -Line $disconnectLine -Pattern '(?:confirmed after|not confirmed within) (\d+) ms')
        Add-Finding -Run $run -Name 'exitBlockSentAfterMs' -Value $(if ($null -ne $startedStamp -and $null -ne $sentStamp) { [int]([math]::Round((New-TimeSpan -Start $startedStamp -End $sentStamp).TotalMilliseconds)) } else { $null })
        Add-Finding -Run $run -Name 'exitCutShortStillRunning' -Value $stillRunning

        Write-Line -Run $run -Text ''
        Write-Line -Run $run -Text 'Earshot is closed. Start it again from the Start menu.'
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
    Write-Host ('Test 20 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
