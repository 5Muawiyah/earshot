<#
.SYNOPSIS
    Pause when the AirPods leave this PC.

.DESCRIPTION
    While this PC is playing to the AirPods and they stop being this PC's output, Earshot now
    pauses what is playing, and never resumes it. This test connects, plays, and takes the AirPods
    away three ways: Earshot's own Disconnect (paused before the sound can jump to the speakers),
    the phone taking them while this PC plays, and the phone taking them while this PC plays
    nothing (which must not pause anything). It reads the owner's own account of what was heard,
    and the log line Earshot writes for every decision, with its reason.

    It settles whether Earshot pauses this PC's playback when the AirPods leave it, before its own
    disconnect and as soon as it sees the phone take them, pauses nothing when this PC was not
    playing to them, and never resumes anything by itself.

    Being taken by going out of range looks the same to this PC as being taken by the phone, and
    is not arranged here.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\21-PauseOnLeave.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '21-pause-on-leave' -Title 'Pause when the AirPods leave this PC' `
    -Settles 'Whether Earshot pauses this PC when the AirPods leave it while it plays to them, before its own disconnect and as soon as it sees the phone take them, pauses nothing when this PC was not playing, and never resumes anything.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Every leg ends with the AirPods off this PC (Earshot's Disconnect blocks the nodes itself, and the
# phone taking them leaves the idle rule to do it), so the closing step finds nothing to undo.
$atRestReason = ''

# Parses a number out of a log line with the given pattern (its first group). $null when the line is
# empty or the figure is not there to read, never a guessed 0.
function Get-MillisecondsFigure
{
    param([string]$Line, [string]$Pattern)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    if ($Line -match $Pattern) { return [int]$Matches[1] }
    return $null
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed from a release built from the current head, set up, and running in the tray.',
        'Block at boot is on.',
        'Pause when AirPods leave this PC is on (it is on unless you turned it off).',
        'The AirPods are paired with this PC and with your phone.',
        'Something that shows in Windows media controls plays on this PC: a music or video player in a browser, or a music app.'
    ) -PhysicalActions @(
        'Connect the AirPods to this PC by left-clicking the Earshot icon and clicking Connect on the card, and play something on this PC, three times.',
        'The first time, let Earshot let them go: left-click the Earshot icon and choose Disconnect.',
        'The second and third times, take them with your phone by choosing the AirPods as its output: once with music playing on this PC, once with nothing playing.',
        'Do not pause or resume anything on this PC yourself.'
    )

    if ($ready)
    {
        # A member the file does not hold reads as the default, which is on: an older settings file has none.
        $paths = Get-EarshotDataPaths
        $settings = Read-EarshotJsonFile -Run $run -Path $paths.SettingsFile
        $pauseSetting = Get-Field -Object $settings -Name 'PauseWhenAirPodsLeave'
        $pauseOn = ($null -eq $pauseSetting -or $pauseSetting -eq $true)
        Add-Criterion -Run $run -Id 'setting-on' -Criterion 'Pause when AirPods leave this PC is on, so there is something to test.' `
            -Outcome $(if ($pauseOn) { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -eq $pauseSetting) { 'settings.json holds no PauseWhenAirPodsLeave member, which reads as the default, on.' } else { 'PauseWhenAirPodsLeave is ' + $pauseSetting + ' in settings.json.' })
        Add-Finding -Run $run -Name 'blockAtBootAtStart' -Value (Get-BlockAtBootSetting -Run $run)

        # ---- Leg 1: Earshot lets the AirPods go itself ----
        Write-Section -Run $run -Title 'Leg 1: Earshot lets them go'
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon, then Connect on the card, to connect the AirPods to this PC, and start music playing on this PC. Do not pause it yourself.'
        $audio1 = Get-AudioState -Run $run -Label 'audio-leg1-connected'
        $states1 = Get-TargetEndpointStates -AudioJson $audio1
        $nodes1 = Get-NodeState -Run $run -Label 'nodes-leg1-connected'
        $nodeState1 = Get-Field -Object $nodes1 -Name 'nodeState'
        Add-Criterion -Run $run -Id 'connected-first' -Criterion 'The AirPods are connected to this PC and the nodes are enabled before the first leg.' `
            -Outcome $(if ($states1.Render -eq 'Active' -and $nodeState1 -eq 'Allowed') { 'pass' } else { 'inconclusive' }) `
            -Detail ('render ' + $states1.Render + ', nodes ' + $nodeState1 + '.')

        $leg1Start = (Get-Date).ToUniversalTime()
        Save-EarshotLog -Run $run
        Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC with Earshot: left-click the Earshot icon and choose Disconnect. Do not pause the music yourself. Then come back here.'

        $ownPaused = Read-Answer -Run $run -Question 'Did the music on this PC pause by itself before you heard it come out of the speakers?'
        Add-Criterion -Run $run -Id 'own-disconnect-paused' -Criterion 'The music paused by itself when Earshot let the AirPods go.' `
            -Outcome $(if ($ownPaused -eq 'yes') { 'pass' } elseif ($ownPaused -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $ownPaused + '.')

        $jumped = Read-Answer -Run $run -Question 'After the AirPods let go, did any of the music play out of the speakers?'
        Add-Criterion -Run $run -Id 'own-disconnect-not-jumped' -Criterion 'The sound did not jump to the speakers when Earshot let the AirPods go.' `
            -Outcome $(if ($jumped -eq 'no') { 'pass' } elseif ($jumped -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $jumped + '.')

        $own = Get-EarshotLogLines -Run $run -Pattern 'before Earshot lets the AirPods go (Disconnect), paused' -SinceUtc $leg1Start
        $ownLine = $(if (@($own).Count -gt 0) { @($own)[-1] } else { $null })
        Add-Criterion -Run $run -Id 'own-disconnect-logged' -Criterion 'Earshot logged that it paused before it let the AirPods go.' `
            -Outcome $(if ($null -ne $ownLine) { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -ne $ownLine) { $ownLine } else { 'no "paused" line for Earshot''s own Disconnect was logged since the leg began.' })
        Add-Finding -Run $run -Name 'ownPauseMs' -Value (Get-MillisecondsFigure -Line $ownLine -Pattern 'paused \S+ in (\d+) ms')

        # ---- Leg 2: the phone takes them while this PC plays ----
        Write-Section -Run $run -Title 'Leg 2: the phone takes them, this PC playing'
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon, then Connect on the card, to connect the AirPods to this PC again, and start music playing on this PC. Do not pause it yourself.'
        $leg2Start = (Get-Date).ToUniversalTime()
        Save-EarshotLog -Run $run
        Wait-Owner -Run $run -Text 'On your phone, choose the AirPods as the output and start playing there, so the phone takes the AirPods from this PC. When the sound is on the phone, come back here.'

        $phonePaused = Read-Answer -Run $run -Question 'Did the music on this PC pause by itself when the phone took the AirPods?'
        Add-Criterion -Run $run -Id 'phone-take-paused' -Criterion 'The music paused by itself when the phone took the AirPods.' `
            -Outcome $(if ($phonePaused -eq 'yes') { 'pass' } elseif ($phonePaused -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $phonePaused + '.')

        $resumed = Read-Answer -Run $run -Question 'After that, did anything on this PC start playing again by itself?'
        Add-Criterion -Run $run -Id 'phone-take-not-resumed' -Criterion 'Nothing on this PC resumed by itself.' `
            -Outcome $(if ($resumed -eq 'no') { 'pass' } elseif ($resumed -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $resumed + '.')

        $left2 = Get-EarshotLogLines -Run $run -Pattern 'Pause on leave: the AirPods left this PC' -SinceUtc $leg2Start
        $pausedLines2 = @($left2 | Where-Object { $_ -cmatch '\. Paused ' })
        $phoneLine = $(if ($pausedLines2.Count -gt 0) { $pausedLines2[-1] } else { $null })
        Add-Criterion -Run $run -Id 'phone-take-logged' -Criterion 'Earshot logged that it paused when it saw the phone take the AirPods, and how long after.' `
            -Outcome $(if ($null -ne $phoneLine) { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -ne $phoneLine) { $phoneLine } else { 'no "left this PC ... Paused" line was logged since the leg began. ' + [string]@($left2).Count + ' decision line(s) for a leave.' })
        Add-Finding -Run $run -Name 'phonePausedAfterChangeMs' -Value (Get-MillisecondsFigure -Line $phoneLine -Pattern 'Paused \S+ (\d+) ms after the change was seen')
        Add-Finding -Run $run -Name 'phoneReadingAgeMs' -Value (Get-MillisecondsFigure -Line $phoneLine -Pattern 'at the last reading, (\d+) ms before')

        # The count of pausing decisions so far, so leg 3 can show none was added.
        $pausedCountAfterLeg2 = $pausedLines2.Count

        # ---- Leg 3: the phone takes them while this PC plays nothing ----
        Write-Section -Run $run -Title 'Leg 3: the phone takes them, this PC playing nothing'
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon, then Connect on the card, to connect the AirPods to this PC again, then pause or close anything that is playing on this PC.'
        $leg3Start = (Get-Date).ToUniversalTime()
        Save-EarshotLog -Run $run
        Wait-Owner -Run $run -Text 'With nothing playing on this PC, use your phone to choose the AirPods as the output, so the phone takes the AirPods from this PC. When the phone has them, come back here.'

        $wasPlaying = Read-Answer -Run $run -Question 'Was anything playing on this PC when the phone took the AirPods?'
        $touched = Read-Answer -Run $run -Question 'When the phone took the AirPods, did anything on this PC pause, stop or start by itself?'

        $left3 = Get-EarshotLogLines -Run $run -Pattern 'Pause on leave: the AirPods left this PC' -SinceUtc $leg2Start
        $pausedCountAfterLeg3 = @($left3 | Where-Object { $_ -cmatch '\. Paused ' }).Count
        $notPaused = Get-EarshotLogLines -Run $run -Pattern 'Not paused: this PC was not playing to them' -SinceUtc $leg3Start
        $notPausedLine = $(if (@($notPaused).Count -gt 0) { @($notPaused)[-1] } else { $null })

        Add-Criterion -Run $run -Id 'idle-leave-not-paused' -Criterion 'Earshot paused nothing when this PC was not playing to the AirPods, and logged why.' `
            -Outcome $(if ($wasPlaying -ne 'no') { 'inconclusive' } elseif ($pausedCountAfterLeg3 -eq $pausedCountAfterLeg2 -and $null -ne $notPausedLine) { 'pass' } else { 'fail' }) `
            -Detail $(if ($wasPlaying -ne 'no') { 'You answered ' + $wasPlaying + ' to whether anything was playing, so this leg does not test what it is for.' } elseif ($null -ne $notPausedLine) { $notPausedLine } else { 'no "not paused: this PC was not playing to them" line was logged since the leg began.' })
        Add-Criterion -Run $run -Id 'idle-leave-untouched' -Criterion 'Nothing on this PC changed by itself when the phone took the AirPods.' `
            -Outcome $(if ($touched -eq 'no') { 'pass' } elseif ($touched -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $touched + '.')
        Add-Finding -Run $run -Name 'idleLeaveDecision' -Value $(if ($null -ne $notPausedLine -and $notPausedLine -match 'Not paused: (.+)$') { $Matches[1].Trim() } else { $null })

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
    Write-Host ('Test 21 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
