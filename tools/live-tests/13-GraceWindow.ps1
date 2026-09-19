<#
.SYNOPSIS
    Is the idle grace Earshot uses long enough, and never too short?

.DESCRIPTION
    Earshot blocks the nodes again once the AirPods stop being used: once the render
    endpoint has been away from ACTIVE for its idle grace with nothing in flight. That
    grace is a waiting budget somebody chose, not a measured figure. Too short and a
    pause between tracks, or the endpoint churn a protection change causes, would
    block the nodes while you are still listening. Too long and the machine sits with
    the nodes enabled for no reason.

    This test watches a real session with the tray running and reads the log for
    the two lines that matter: the block the idle rule issued, and every time it
    decided not to. It measures the gap between the render endpoint leaving ACTIVE
    and the block being issued, and it asks you whether anything blocked while you
    were still listening.

    It settles the value of the idle grace window.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.PARAMETER WatchMinutes
    How long to watch the log after you stop using the AirPods. 10 by default.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\13-GraceWindow.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [int]$WatchMinutes = 10
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

# There is no read-only surface that reports the idle grace before it has been used: no diag
# target carries BlockCoordinator.IdleGrace, and the application does not expose it as
# configuration. The one place it is observable is the log line the idle rule itself writes when
# it blocks ("...were not in use for <N> s..."), which is Earshot reporting a figure it used, not
# this script assuming one. Get-DelaySecondsAtBlock below reads that line.
#
# That figure is IdleDelay, not IdleGrace: IdleDelay => Doubled(IdleGrace, _idleFailures)
# (BlockCoordinator.cs:320), and NoteAutomaticBlock doubles it every time an automatic block fails
# to take (BlockCoordinator.cs:2436). It equals the grace only when nothing has doubled it, which
# this script can only rule out, never prove, by finding no record in the log of an automatic
# block failing (the line NoteAutomaticBlock writes, BlockCoordinator.cs:2439). Absent that
# record, or unable to tell, the grace-named finding stays null: a delay that might be doubled is
# not the grace, whatever this script calls it.
# src\Earshot\App\BlockCoordinator.cs:108 (IdleGrace), :320 (IdleDelay), :2385 (the block line),
# :2436-2439 (NoteAutomaticBlock, the failure line and what it increments)

# Pulls the seconds figure out of a "Blocking the nodes: ... were not in use for <N> s ..." log
# line: the delay Earshot used for that block, not necessarily the grace. $null when no such line
# is given, or its figure cannot be parsed, either of which the caller records as not measured
# rather than guessed.
function Get-DelaySecondsAtBlock
{
    param([string[]]$Lines)

    foreach ($line in @($Lines))
    {
        if ($line -match 'not in use for\s+([0-9]+(?:\.[0-9]+)?)\s*s\b')
        {
            return [double]$Matches[1]
        }
    }

    return $null
}

$run = New-LiveTestRun -TestId '13-grace-window' -Title 'Tuning the idle grace window' `
    -Settles 'Whether the idle grace Earshot uses is the right wait before the nodes are blocked again, measured against real driver churn.' `
    -ExePath $ExePath -RunRoot $RunRoot

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed, set up and running in the tray.',
        'Block at boot is on, so the idle rule is active.',
        'The AirPods are paired with this PC.'
    ) -PhysicalActions @(
        'Connect the AirPods to this PC and use them normally for a few minutes: play, pause between tracks, take a call if you can.',
        'Then stop using them, and leave everything alone while this test watches.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Normal use'
        Write-Line -Run $run -Text 'A pause between tracks must not be treated as idle. That is what the idle grace wait is for.'
        Write-Line -Run $run -Text 'This test reads the grace period from the log line Earshot writes when it blocks, later on; nothing here assumes a figure up front.'
        Wait-Owner -Run $run -Text 'Connect the AirPods to this PC and use them for a few minutes, with at least one pause between tracks longer than ten seconds.'

        $duringUse = Get-EarshotLogLines -Run $run -Pattern 'Blocking the nodes: the AirPods were not in use for'
        $blockedDuringUse = @($duringUse).Count
        $nodes = Get-NodeState -Run $run -Label 'nodes-during-use'
        $audio = Get-AudioState -Run $run -Label 'audio-during-use'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Render ' + $states.Render + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState') +
            ', idle blocks in the log so far: ' + $blockedDuringUse)

        $interrupted = Read-Answer -Run $run -Question 'While you were using them, did Earshot ever cut the connection or block the nodes?'
        Add-Criterion -Run $run -Id 'not-too-short' -Criterion 'Nothing blocked the nodes while the AirPods were in use.' `
            -Outcome $(if ($interrupted -eq 'no') { 'pass' } elseif ($interrupted -eq 'yes') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $interrupted + '. The nodes read ' + (Get-Field -Object $nodes -Name 'nodeState') + ' while in use.')

        Write-Section -Run $run -Title 'Now stop, and watch'
        $stoppedAt = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Stop using the AirPods now: pause everything, or take them out. Then leave the machine alone.'
        $stoppedAt = (Get-Date).ToUniversalTime()
        Write-Line -Run $run -Text ('Stopped at ' + $stoppedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + '. Watching for up to ' + $WatchMinutes + ' minutes.')

        $blocked = $false
        $seconds = 0
        $limit = $WatchMinutes * 60
        while ($seconds -lt $limit -and -not $blocked)
        {
            Wait-Seconds -Run $run -Seconds 15 -Reason 'watching for the idle rule to block the nodes'
            $seconds = $seconds + 15
            $lines = Get-EarshotLogLines -Run $run -Pattern 'Blocking the nodes: the AirPods were not in use for' -SinceUtc $stoppedAt
            if (@($lines).Count -gt 0)
            {
                $blocked = $true
                foreach ($line in $lines) { Write-Line -Run $run -Text ('  ' + $line) }
            }
        }

        $notIssued = Get-EarshotLogLines -Run $run -Pattern 'Idle block not issued' -SinceUtc $stoppedAt
        foreach ($line in ($notIssued | Select-Object -Last 10)) { Write-Line -Run $run -Text ('  ' + $line) }

        $nodesAfter = Get-NodeState -Run $run -Label 'nodes-after-idle'
        $stateAfter = Get-Field -Object $nodesAfter -Name 'nodeState'
        Write-Line -Run $run -Text ('Nodes after the watch: ' + $stateAfter)

        # The delay reported in the block line itself, when there was one. Nothing is measured
        # when nothing blocked, so this stays $null rather than the watch limit, and it stays
        # $null rather than a guess when the line was there but its figure could not be parsed.
        $delaySecondsAtBlock = $(if ($blocked) { Get-DelaySecondsAtBlock -Lines $lines } else { $null })

        # A failed automatic block anywhere in the log since Earshot started (see the header
        # comment for the line and what it means) is the log's own evidence that the delay at
        # this block might have been doubled away from the grace. Read over the whole log, not
        # just since the watch started: a failure earlier in the same run still leaves the
        # doubling in force until something re-arms the rule, and this script has no reliable way
        # to tell whether that happened in between. Present, or this script cannot tell, and the
        # grace-named finding stays null.
        $automaticBlockFailures = Get-EarshotLogLines -Run $run -Pattern 'the nodes are still enabled ('
        $anyAutomaticBlockFailure = (@($automaticBlockFailures).Count -gt 0)
        $doublingRuledOut = ($blocked -and ($null -ne $delaySecondsAtBlock) -and -not $anyAutomaticBlockFailure)
        $suggestedGraceSeconds = $(if ($doublingRuledOut) { $delaySecondsAtBlock } else { $null })

        # A nodes reading of Blocked with no log line proving the idle rule did it is not proof
        # the idle rule blocked them: they may have started Blocked, or been blocked for another
        # reason entirely. That is inconclusive, not a pass, and its detail must say so rather
        # than contradict the outcome.
        Add-Criterion -Run $run -Id 'blocks-when-idle' -Criterion 'The idle rule blocks the nodes once the AirPods stop being used.' `
            -Outcome $(if ($blocked) { 'pass' } elseif ($stateAfter -eq 'Blocked') { 'inconclusive' } else { 'fail' }) `
            -Detail $(
                if ($blocked) { 'It blocked about ' + $seconds + ' s after you stopped (measured in 15 s steps).' }
                elseif ($stateAfter -eq 'Blocked') { 'The nodes read Blocked, but no log line shows the idle rule did it within ' + $WatchMinutes + ' minutes.' }
                else { 'Nothing blocked within ' + $WatchMinutes + ' minutes; the nodes read ' + $stateAfter + '.' }
            )

        # "Close to the grace" cannot be checked honestly: the block line reports the delay in
        # force, which is only the grace when nothing doubled it (see suggestedIdleGraceSeconds
        # below), and this criterion should not silently go quiet just because it was. It compares
        # the measured wait against the delay instead, with one 15 s sampling step of slack, which
        # is the only tolerance this script has grounds for: the loop that produced $seconds polls
        # in 15 s steps, so a wait it reports can be up to 14 s later than the real one without
        # anything having gone wrong.
        Add-Criterion -Run $run -Id 'not-too-long' -Criterion 'The measured wait is within one 15 s sampling step of the delay Earshot reported for that block, not longer.' `
            -Outcome $(
                if (-not $blocked) { 'inconclusive' }
                elseif ($null -eq $delaySecondsAtBlock) { 'inconclusive' }
                elseif ($seconds -le ($delaySecondsAtBlock + 15)) { 'pass' }
                else { 'fail' }
            ) `
            -Detail $(
                if (-not $blocked) { 'Nothing blocked within the watch window, so there is no wait to compare.' }
                elseif ($null -eq $delaySecondsAtBlock) { 'The block line did not carry a figure this could parse, so there is nothing to compare the wait against.' }
                else {
                    'About ' + $seconds + ' s, watched in 15 s steps, against a delay of ' + $delaySecondsAtBlock +
                    ' s reported in the block line. Pass is within one 15 s sampling step over that; a much longer wait ' +
                    'usually means something kept restarting it, and the "not issued" lines say what.'
                }
            )

        Add-Finding -Run $run -Name 'secondsFromIdleToBlock' -Value $(if ($blocked) { $seconds } else { $null }) `
            -Detail $(if ($blocked) { 'measured in 15 s steps, so treat it as a bound' } else { 'not measured: nothing blocked within the watch window' })
        Add-Finding -Run $run -Name 'idleBlockDeferrals' -Value @($notIssued).Count -Detail 'how many times the rule decided not to block; the reasons are in the log lines'

        Add-Finding -Run $run -Name 'idleDelaySecondsAtBlock' -Value $delaySecondsAtBlock `
            -Detail $(
                if (-not $blocked) { 'not measured: nothing blocked within the watch window' }
                elseif ($null -eq $delaySecondsAtBlock) { 'the block line did not carry a figure this could parse, so this is not recorded rather than guessed' }
                else { 'read from the log line Earshot wrote when it blocked: the delay in force at that block, which is the grace only when nothing doubled it (see suggestedIdleGraceSeconds)' }
            )

        Add-Finding -Run $run -Name 'suggestedIdleGraceSeconds' -Value $suggestedGraceSeconds `
            -Detail $(
                if ($doublingRuledOut) { 'equals idleDelaySecondsAtBlock: the log shows no automatic block failing in this run, so nothing doubled the delay away from the grace' }
                elseif (-not $blocked) { 'not measured: nothing blocked within the watch window' }
                elseif ($null -eq $delaySecondsAtBlock) { 'the block line did not carry a figure this could parse, so this is not recorded rather than guessed' }
                else { 'an automatic block failed somewhere in this run (' + @($automaticBlockFailures).Count + ' line(s)), so the delay this block reported may be doubled rather than the grace; not recorded as the grace' }
            )

        Write-Section -Run $run -Title 'Protection churn'
        Write-Line -Run $run -Text 'A protection change adds and removes endpoints, and each one restarts the wait. If the churn from a'
        Write-Line -Run $run -Text 'single change is longer than the grace window, the rule can never settle.'
        $churn = Get-EarshotLogLines -Run $run -Pattern 'Idle rule re-armed'
        foreach ($line in ($churn | Select-Object -Last 10)) { Write-Line -Run $run -Text ('  ' + $line) }
        Add-Finding -Run $run -Name 'idleRuleReArmed' -Value @($churn).Count

        Save-EarshotLog -Run $run
    }
}
catch
{
    Write-Failure -Run $run -Message ('The test stopped with an error: ' + ($_ | Out-String).Trim())
    Add-Criterion -Run $run -Id 'run' -Criterion 'The test ran to the end.' -Outcome 'fail' -Detail 'See the error above.'
}
finally
{
    $overall = Complete-LiveTestRun -Run $run
    Write-Host ('Test 13 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
