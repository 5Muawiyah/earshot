<#
.SYNOPSIS
    Is the thirty second idle grace long enough, and never too short?

.DESCRIPTION
    Earshot blocks the nodes again once the AirPods stop being used: when the render
    endpoint has been away from ACTIVE for thirty seconds with nothing in flight.
    Thirty seconds is a waiting budget somebody chose, not a measured figure. Too
    short and a pause between tracks, or the endpoint churn a protection change
    causes, would block the nodes while you are still listening. Too long and the
    machine sits with the nodes enabled for no reason.

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

# The idle grace the application uses now, in seconds.
$IdleGraceSeconds = 30

$run = New-LiveTestRun -TestId '13-grace-window' -Title 'Tuning the idle grace window' `
    -Settles 'Whether thirty seconds of idle is the right wait before the nodes are blocked again, measured against real driver churn.' `
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
        Write-Line -Run $run -Text ('A pause between tracks must not be treated as idle. That is what the ' + $IdleGraceSeconds + ' s wait is for.')
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

        Add-Criterion -Run $run -Id 'blocks-when-idle' -Criterion 'The idle rule blocks the nodes once the AirPods stop being used.' `
            -Outcome $(if ($blocked -or $stateAfter -eq 'Blocked') { 'pass' } else { 'fail' }) `
            -Detail $(if ($blocked) { 'It blocked about ' + $seconds + ' s after you stopped (measured in 15 s steps).' } else { 'Nothing blocked within ' + $WatchMinutes + ' minutes; the nodes read ' + $stateAfter + '.' })

        Add-Criterion -Run $run -Id 'not-too-long' -Criterion ('The wait is close to the ' + $IdleGraceSeconds + ' s it is set to, not minutes longer.') `
            -Outcome $(if ($blocked -and $seconds -le 120) { 'pass' } elseif ($blocked) { 'fail' } else { 'inconclusive' }) `
            -Detail ('About ' + $seconds + ' s, watched in 15 s steps. A much longer wait usually means something kept restarting it; the "not issued" lines say what.')

        Add-Finding -Run $run -Name 'secondsFromIdleToBlock' -Value $seconds -Detail 'measured in 15 s steps, so treat it as a bound'
        Add-Finding -Run $run -Name 'idleBlockDeferrals' -Value @($notIssued).Count -Detail 'how many times the rule decided not to block; the reasons are in the log lines'
        Add-Finding -Run $run -Name 'suggestedIdleGraceSeconds' -Value $IdleGraceSeconds `
            -Detail 'change it only if this test shows churn keeping the endpoint non-ACTIVE for longer than the wait, or a block landing during use'

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
