<#
.SYNOPSIS
    Hand back on sleep, then wake.

.DESCRIPTION
    Earshot now disconnects and blocks inside the roughly two seconds Windows gives it before a
    sleep transition (the PBT_APMSUSPEND hold), in the same fixed order as at shut down, and runs
    a resume check once the machine wakes. This test connects, plays, puts the machine to sleep,
    wakes it, and reads whether the hand-back ran in time, whether the block finished before sleep
    or only after wake, and whether this computer took the AirPods back by itself on waking.

    It settles whether Earshot hands the AirPods back inside the two seconds Windows gives it at
    sleep, whether the block completes before sleep or after wake, and whether this computer takes
    the AirPods back when it wakes.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\18-HandBackOnSleep.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '18-handback-on-sleep' -Title 'Hand back on sleep, then wake' `
    -Settles 'Whether Earshot hands the AirPods back inside the two seconds Windows gives it at sleep, whether the block completes before sleep or after wake, and whether this computer takes the AirPods back when it wakes.' `
    -ExePath $ExePath -RunRoot $RunRoot

$atRestReason = ''

function Get-MillisecondsFigure
{
    param([string]$Line, [string]$Pattern)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    if ($Line -match $Pattern) { return [int]$Matches[1] }
    return $null
}

function Get-NewestGateBlockEvidence
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][datetime]$SinceUtc
    )

    if (-not (Test-Path -LiteralPath $Run.AppLiveTest)) { return $null }
    $newest = $null
    $newestStarted = [datetime]::MinValue
    foreach ($file in (Get-ChildItem -LiteralPath $Run.AppLiveTest -Filter '*-gate-block.json' -File))
    {
        try
        {
            $evidence = (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
            $text = [string](Get-Field -Object $evidence -Name 'startedUtc')
            if ([string]::IsNullOrEmpty($text)) { continue }
            $started = [datetime]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
            if ($started -ge $SinceUtc -and $started -ge $newestStarted)
            {
                $newest = $evidence
                $newestStarted = $started
            }
        }
        catch
        {
            continue
        }
    }

    return $newest
}

# 'Available' sleep states, read from powercfg /a. Never a guess: a probe that throws, or a line
# this cannot parse, is recorded as 'unknown', not as S3 present or absent.
function Get-SleepStatesAvailable
{
    param([Parameter(Mandatory = $true)]$Run)

    try
    {
        $text = (& powercfg /a 2>&1 | Out-String)
        $lines = @($text -split "`r?`n" | Where-Object { $_ -match '^\s*(Standby|Hibernate|Fast Startup|Hybrid Sleep)' -and $_ -notmatch 'not (available|supported)' })
        $available = @($lines | ForEach-Object { $_.Trim() })
        return ($available -join '; ')
    }
    catch
    {
        Write-Failure -Run $Run -Message ('powercfg /a could not be read: ' + ($_ | Out-String).Trim())
        return 'unknown'
    }
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed from a release built from the current head, set up, and running in the tray.',
        'Block at boot is on.',
        '"Hand back at shut down and sleep" is ticked in the menu.',
        'The AirPods are paired with this PC and available to connect.',
        'This PC can sleep (this half records what it finds; it does not refuse on this alone).'
    ) -PhysicalActions @(
        'Connect the AirPods to this PC with a left click on the tray icon and play something so they stay in use.',
        'Choose Start, then Power, then Sleep.',
        'Wait at least thirty seconds, then wake this PC.',
        'Press Enter here once you have signed back in.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Connect and confirm'
        Wait-Owner -Run $run -Text 'Left-click the tray icon to connect the AirPods to this PC, and play something so they stay in use.'
        $audioBefore = Get-AudioState -Run $run -Label 'audio-connected'
        $statesBefore = Get-TargetEndpointStates -AudioJson $audioBefore
        Write-Line -Run $run -Text ('Render ' + $statesBefore.Render + ' before sleep.')

        Add-Finding -Run $run -Name 'sleepStatesAvailable' -Value (Get-SleepStatesAvailable -Run $run) -Detail 'powercfg /a, read before sleep'
        Save-EarshotLog -Run $run

        Write-Section -Run $run -Title 'Now sleep, then wake'
        $sleepStartUtc = (Get-Date).ToUniversalTime()
        Write-Line -Run $run -Text 'Choose Start, Power, Sleep. Wait at least thirty seconds, then wake this PC and sign back in.'
        Wait-Owner -Run $run -Text 'Put this computer to sleep (Start, Power, Sleep), wait at least thirty seconds, wake it, sign in, then come back here.'

        Write-Section -Run $run -Title 'After waking'
        $nodes = Get-NodeState -Run $run -Label 'nodes-after-wake'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $audioAfter = Get-AudioState -Run $run -Label 'audio-after-wake'
        $statesAfter = Get-TargetEndpointStates -AudioJson $audioAfter
        Write-Line -Run $run -Text ('Nodes ' + $nodeState + ', render ' + $statesAfter.Render + ' after waking.')

        $events = Get-PowerEvents -Run $run -SinceUtc $sleepStartUtc
        $sleepEvents = @($events | Where-Object { $_.id -eq 42 } | Sort-Object utc)
        $resumeEvents = @($events | Where-Object { $_.id -eq 107 } | Sort-Object utc)
        $sleepHappened = $false
        if (@($sleepEvents).Count -gt 0 -and @($resumeEvents).Count -gt 0)
        {
            $sleepAt = [datetime]::Parse(@($sleepEvents)[0].utc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
            $wakeAt = [datetime]::Parse(@($resumeEvents)[-1].utc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
            if ($wakeAt -gt $sleepAt) { $sleepHappened = $true }
        }

        Add-Criterion -Run $run -Id 'sleep-happened' -Criterion 'A Kernel-Power 42 (sleep) event was followed by a Kernel-Power 107 (wake) event.' `
            -Outcome $(if ($sleepHappened) { 'pass' } else { 'inconclusive' }) `
            -Detail ([string]@($sleepEvents).Count + ' sleep event(s), ' + @($resumeEvents).Count + ' wake event(s).')

        $suspendLogged = Get-EarshotLogLines -Run $run -Pattern 'WM_POWERBROADCAST received: Suspend' -SinceUtc $sleepStartUtc
        Add-Criterion -Run $run -Id 'suspend-logged' -Criterion 'PBT_APMSUSPEND reached Earshot and was logged.' `
            -Outcome $(if (@($suspendLogged).Count -eq 1) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($suspendLogged).Count + ' line(s).')

        $started = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): started at' -SinceUtc $sleepStartUtc
        Add-Criterion -Run $run -Id 'handback-started' -Criterion 'The sleep hand-back started.' `
            -Outcome $(if (@($started).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($started).Count + ' "started at" line(s).')

        $disconnect = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): disconnect' -SinceUtc $sleepStartUtc
        $disconnectLine = $(if (@($disconnect).Count -gt 0) { @($disconnect)[-1] } else { $null })
        Add-Criterion -Run $run -Id 'handback-disconnect-confirmed' -Criterion 'The disconnect confirmed before the block was sent.' `
            -Outcome $(if ($null -eq $disconnectLine) { 'inconclusive' } elseif ($disconnectLine -match 'confirmed after') { 'pass' } elseif ($disconnectLine -match 'not confirmed') { 'fail' } else { 'inconclusive' }) `
            -Detail $(if ($null -eq $disconnectLine) { 'no disconnect line was logged' } else { $disconnectLine })

        $blockSent = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): block sent at' -SinceUtc $sleepStartUtc
        Add-Criterion -Run $run -Id 'handback-block-sent' -Criterion 'The sleep hand-back sent the block.' `
            -Outcome $(if (@($blockSent).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($blockSent).Count + ' "block sent at" line(s).')

        $finished = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): finished in' -SinceUtc $sleepStartUtc
        $cutShort = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): cut short at' -SinceUtc $sleepStartUtc
        $cutShortLine = $(if (@($cutShort).Count -gt 0) { @($cutShort)[-1] } else { $null })
        $cutShortBlockNotSent = ($null -ne $cutShortLine -and $cutShortLine -match 'block was not sent')
        Add-Criterion -Run $run -Id 'handback-finished-or-sent' -Criterion 'The hand-back finished, or was cut short with the block already sent.' `
            -Outcome $(if (@($finished).Count -gt 0) { 'pass' } elseif ($null -ne $cutShortLine -and -not $cutShortBlockNotSent) { 'pass' } elseif ($cutShortBlockNotSent) { 'fail' } else { 'fail' }) `
            -Detail $(if (@($finished).Count -gt 0) { @($finished)[-1] } elseif ($null -ne $cutShortLine) { $cutShortLine } else { 'no "finished in" or "cut short" line was logged' })

        $resumeAutomatic = Get-EarshotLogLines -Run $run -Pattern 'WM_POWERBROADCAST received: ResumeAutomatic' -SinceUtc $sleepStartUtc
        $resumeCheck = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (resume):' -SinceUtc $sleepStartUtc
        Add-Criterion -Run $run -Id 'resume-logged' -Criterion 'PBT_APMRESUMEAUTOMATIC reached Earshot, and the resume check ran, after waking.' `
            -Outcome $(if (@($resumeAutomatic).Count -gt 0 -and @($resumeCheck).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($resumeAutomatic).Count + ' resume line(s), ' + @($resumeCheck).Count + ' resume-check line(s).')

        Add-Criterion -Run $run -Id 'nodes-after-wake' -Criterion 'The nodes read Blocked after waking.' `
            -Outcome $(if ($nodeState -eq 'Blocked') { 'pass' } else { 'fail' }) `
            -Detail ('They read ' + $nodeState + '.')

        $repaged = Read-Answer -Run $run -Question 'After this computer woke, did it take the AirPods back by itself?'
        Add-Criterion -Run $run -Id 'not-repaged-at-wake' -Criterion 'This computer did not take the AirPods back by itself on waking.' `
            -Outcome $(if ($repaged -eq 'no' -and $statesAfter.Render -ne 'Active') { 'pass' } elseif ($repaged -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $repaged + '; render reads ' + $statesAfter.Render + '.')

        $heard = Read-Answer -Run $run -Question 'At the moment this computer fell asleep, did the AirPods go back to your phone?'
        Add-Criterion -Run $run -Id 'heard-handed-back' -Criterion 'The AirPods went back to the phone at the moment of falling asleep.' `
            -Outcome $(if ($heard -eq 'yes') { 'pass' } elseif ($heard -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $heard + '.')

        Add-Finding -Run $run -Name 'handBackStartedMs' -Value $(if (@($started).Count -gt 0) { 0 } else { $null }) -Detail 'present only to mark that a started line exists; the moment itself is the log stamp'
        Add-Finding -Run $run -Name 'handBackFinishedMs' -Value (Get-MillisecondsFigure -Line $(if (@($finished).Count -gt 0) { @($finished)[-1] } else { $null }) -Pattern 'finished in (\d+) ms')
        Add-Finding -Run $run -Name 'handBackDisconnectMs' -Value (Get-MillisecondsFigure -Line $disconnectLine -Pattern '(?:confirmed after|not confirmed within) (\d+) ms')
        Add-Finding -Run $run -Name 'handBackCutShortStillRunning' -Value $(if ($null -ne $cutShortLine -and $cutShortLine -match 'still running:\s*([^;]*)') { $Matches[1].Trim() } else { $null })

        $resumeCheckAction = $null
        if (@($resumeCheck).Count -gt 0)
        {
            $last = @($resumeCheck)[-1]
            if ($last -match 'connected at resume') { $resumeCheckAction = 'connected-at-resume' }
            elseif ($last -match 'blocked now') { $resumeCheckAction = 'blocked' }
            elseif ($last -match 'nothing to do') { $resumeCheckAction = 'nothing-to-do' }
            else { $resumeCheckAction = 'other' }
        }

        Add-Finding -Run $run -Name 'resumeCheckAction' -Value $resumeCheckAction

        $sleepToWakeSeconds = $null
        if ($sleepHappened)
        {
            $sleepAt = [datetime]::Parse(@($sleepEvents)[0].utc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
            $wakeAt = [datetime]::Parse(@($resumeEvents)[-1].utc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
            $sleepToWakeSeconds = [int]([math]::Round((New-TimeSpan -Start $sleepAt -End $wakeAt).TotalSeconds))
        }

        Add-Finding -Run $run -Name 'sleepToWakeSeconds' -Value $sleepToWakeSeconds -Detail '107 minus 42, machine time'

        $gateEvidence = Get-NewestGateBlockEvidence -Run $run -SinceUtc $sleepStartUtc
        $blockCompletedBeforeSleep = 'no-evidence'
        if ($null -ne $gateEvidence -and @($sleepEvents).Count -gt 0)
        {
            $finishedText = [string](Get-Field -Object $gateEvidence -Name 'finishedUtc')
            if (-not [string]::IsNullOrEmpty($finishedText))
            {
                $gateFinishedAt = [datetime]::Parse($finishedText, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
                $sleepAt = [datetime]::Parse(@($sleepEvents)[0].utc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
                $blockCompletedBeforeSleep = $(if ($gateFinishedAt -lt $sleepAt) { 'yes' } else { 'no' })
            }
        }

        Add-Finding -Run $run -Name 'blockCompletedBeforeSleep' -Value $blockCompletedBeforeSleep -Detail 'the gate evidence''s own finishedUtc against the Kernel-Power 42 event'
        Add-Finding -Run $run -Name 'phoneHeardAfterReleaseOwnerObserved' -Value $(if ($heard -eq 'yes') { 'yes' } else { $null }) -Detail 'owner-observed; never merged with a machine figure'

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
    Write-Host ('Test 18 finished: ' + $overall)
}

exit (Get-LiveTestExitCode -Overall $overall)
