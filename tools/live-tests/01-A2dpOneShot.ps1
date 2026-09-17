<#
.SYNOPSIS
    Does the A2DP filter accept the one-shot reconnect while Handsfree protection is on?

.DESCRIPTION
    The riskiest unknown in the whole build. Earshot connects by sending
    KSPROPERTY_ONESHOT_RECONNECT through IKsControl on the Bluetooth audio filter.
    Microsoft documents that property for the Handsfree filter. Protect audio
    quality, which is on by default, removes the Handsfree filter, so the A2DP
    filter has to answer instead, and nothing says it will.

    The test sends the request to the A2DP filter alone, then to the Handsfree
    filter alone, then to both, first with protection on and then with it off, and
    records the HRESULT, the bytes returned and the time to the render endpoint
    reaching ACTIVE.

    It settles whether the Handsfree assisted fallback (protect off, reconnect,
    protect on) has to ship, and whether the 15 second connect budget is enough.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\01-A2dpOneShot.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

# The connect budget the application waits for the render endpoint to reach ACTIVE.
$ConnectBudgetMilliseconds = 15000

$run = New-LiveTestRun -TestId '01-a2dp-oneshot' -Title 'One-shot reconnect on the A2DP filter, protection on then off' `
    -Settles 'Whether the Handsfree assisted connect fallback is needed, and whether the 15 s connect budget holds.' `
    -ExePath $ExePath -RunRoot $RunRoot

# One reconnect or disconnect through diag ks, with its evidence read back.
function Invoke-KsStep
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][string]$Filter,
        [Parameter(Mandatory = $true)][string]$Consequence
    )

    $result = Invoke-Earshot -Run $Run -Label $Label -Command @('diag', 'ks', $Action, $Filter) -Live -Consequence $Consequence
    if ($null -eq $result) { return $null }
    $evidence = Get-DiagEvidence -Run $Run
    if ($null -eq $evidence) { return $null }
    return (Read-KsEvidence -Run $Run -Evidence $evidence)
}

function Show-EndpointStates
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $audio = Get-AudioState -Run $Run -Label $Label
    $states = Get-TargetEndpointStates -AudioJson $audio
    Write-Line -Run $Run -Text ('  render ' + $states.Render + ', capture ' + $states.Capture + ', derived ' + $states.Connection)
    return $states
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up (the \Earshot tasks exist).',
        'The AirPods are paired with this PC and are connected to your phone, not to this PC.',
        'The AirPods Bluetooth nodes are allowed, not blocked. If they are blocked, run 05-Allow.ps1 first.',
        'The Earshot tray is closed, so its idle rule and its reconnect logic do not act during the test.'
    ) -PhysicalActions @(
        'Wear the AirPods and play something from your phone, so you can hear the moment this PC takes them.',
        'After each step, say whether the audio moved to this PC.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Baseline'
        $task = Get-TaskState -Run $run -Label 'task-before'
        $nodes = Get-NodeState -Run $run -Label 'nodes-before'
        $services = Get-ServiceState -Run $run -Label 'services-before'
        $before = Show-EndpointStates -Run $run -Label 'audio-before'

        # Read-only proof that the connect path reaches IKsControl before anything is sent to it.
        $topologyBefore = Get-TopologyState -Run $run -Label 'topology-before'
        $activatedBefore = 0
        foreach ($adapter in (Get-Field -Object $topologyBefore -Name 'adapters'))
        {
            if ((Get-Field -Object $adapter -Name 'ksControlActivated') -eq $true) { $activatedBefore = $activatedBefore + 1 }
        }

        Add-Criterion -Run $run -Id 'topology-reachable' -Criterion 'The walk reaches IKsControl on at least one filter before anything is sent.' `
            -Outcome $(if ($activatedBefore -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]$activatedBefore + ' filter(s) activated IKsControl while disconnected.')

        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $protection = Get-Field -Object $services -Name 'protection'
        Write-Line -Run $run -Text ('Set up: ' + (Get-Field -Object $task -Name 'setUp') + '. Nodes: ' + $nodeState + '. Protection: ' + $protection + '.')

        if ($nodeState -eq 'Blocked' -or $nodeState -eq 'Mixed')
        {
            Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The nodes are allowed before the test starts.' `
                -Outcome 'inconclusive' -Detail ('They read ' + $nodeState + '. Run 05-Allow.ps1, then this test again.')
        }
        elseif ($before.Render -eq 'Active')
        {
            Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The AirPods start disconnected from this PC.' `
                -Outcome 'inconclusive' -Detail 'The render endpoint is already ACTIVE. Disconnect them from this PC and run the test again.'
        }
        else
        {
            Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The nodes are allowed and the AirPods start off this PC.' `
                -Outcome 'pass' -Detail ('nodes ' + $nodeState + ', render ' + $before.Render + '.')

            # ---------------------------------------------------------- protection on
            Write-Section -Run $run -Title 'Leg A: protection on (the shipping default, no Handsfree filter)'
            if ($protection -ne 'Protected')
            {
                $applied = Invoke-Earshot -Run $run -Label 'gate-protect-on' -Command @('diag', 'gate', 'protect-on') -Live `
                    -Consequence 'Turns the Handsfree and Headset services off through the elevated task, which removes the Handsfree filter and the AirPods microphone on this PC. It can take minutes.' `
                    -TimeoutSeconds 420
                [void]$applied
                $services = Get-ServiceState -Run $run -Label 'services-protected'
                $protection = Get-Field -Object $services -Name 'protection'
            }

            Add-Criterion -Run $run -Id 'protection-on' -Criterion 'Protection is on for leg A.' `
                -Outcome $(if ($protection -eq 'Protected') { 'pass' } else { 'inconclusive' }) -Detail ('It reads ' + $protection + '.')

            $srcOn = Invoke-KsStep -Run $run -Label 'ks-reconnect-src-protected' -Action 'reconnect' -Filter 'src' `
                -Consequence 'Sends KSPROPERTY_ONESHOT_RECONNECT to the A2DP filter only. If it works, the AirPods leave your phone and join this PC.'
            $afterSrcOn = Show-EndpointStates -Run $run -Label 'audio-after-src-protected'
            [void](Get-TopologyState -Run $run -Label 'topology-after-src-protected')
            $heardOn = Read-Answer -Run $run -Question 'Did the audio move from your phone to this PC?'

            if ($null -eq $srcOn)
            {
                Add-Criterion -Run $run -Id 'K1-src-protected' -Criterion 'The A2DP filter accepts ONESHOT_RECONNECT with protection on.' `
                    -Outcome 'inconclusive' -Detail 'The step was skipped or wrote no evidence.'
            }
            else
            {
                $accepted = ($srcOn.AcceptedRoles -contains 'A2DP') -or ($srcOn.AcceptedRoles.Count -gt 0)
                Add-Criterion -Run $run -Id 'K1-src-protected' -Criterion 'The A2DP filter accepts ONESHOT_RECONNECT with protection on.' `
                    -Outcome $(if ($accepted) { 'pass' } else { 'fail' }) `
                    -Detail ('accepted roles: ' + (($srcOn.AcceptedRoles + @('none')) -join ' ') + '; rejected: ' + (($srcOn.RejectedRoles + @('none')) -join ' ') + '.')

                Add-Criterion -Run $run -Id 'K1-src-active' -Criterion 'The render endpoint reaches ACTIVE after that request.' `
                    -Outcome $(if ($srcOn.Reached -eq $true -and $afterSrcOn.Render -eq 'Active') { 'pass' } else { 'fail' }) `
                    -Detail ('confirmation reached ' + $srcOn.Reached + ', render now ' + $afterSrcOn.Render + ', you heard it move: ' + $heardOn + '.')

                if ($null -ne $srcOn.MillisToState)
                {
                    Add-Criterion -Run $run -Id 'K1-budget' -Criterion ('The render endpoint reaches ACTIVE within the ' + $ConnectBudgetMilliseconds + ' ms connect budget.') `
                        -Outcome $(if ([int]$srcOn.MillisToState -le $ConnectBudgetMilliseconds) { 'pass' } else { 'fail' }) `
                        -Detail ([string]$srcOn.MillisToState + ' ms.')
                    Add-Finding -Run $run -Name 'a2dpReconnectMilliseconds' -Value $srcOn.MillisToState -Detail 'protection on'
                }

                Add-Finding -Run $run -Name 'hfpAssistedFallbackNeeded' -Value $(if ($accepted) { 'no' } else { 'yes' }) `
                    -Detail 'yes means D3, the Handsfree assisted connect, has to ship because the A2DP filter refuses the request with protection on.'

                # A filter that answered with a buffer complaint is retried with the four byte buffer.
                $bufferComplaint = $false
                foreach ($row in $srcOn.Filters)
                {
                    if ($row.sent -eq $true -and $row.accepted -ne $true -and
                        ($row.hrName -match 'BUFFER' -or $row.hrName -match 'INSUFFICIENT' -or $row.hr -eq '0x8007007A'))
                    {
                        $bufferComplaint = $true
                    }
                }

                if ($bufferComplaint)
                {
                    Write-Line -Run $run -Text 'A filter answered the documented request with a buffer error, so the four byte variant is worth a try.'
                    $buffered = Invoke-Earshot -Run $run -Label 'ks-reconnect-src-buffer4' -Command @('diag', 'ks', 'reconnect', 'src', 'buffer4') -Live `
                        -Consequence 'Sends the same request again with a four byte zeroed buffer, to see whether the filter wanted one.'
                    if ($null -ne $buffered)
                    {
                        $bufferedEvidence = Get-DiagEvidence -Run $run
                        if ($null -ne $bufferedEvidence)
                        {
                            $bufferedSummary = Read-KsEvidence -Run $run -Evidence $bufferedEvidence
                            Add-Finding -Run $run -Name 'buffer4Accepted' -Value $(if ($bufferedSummary.AcceptedRoles.Count -gt 0) { 'yes' } else { 'no' })
                        }
                    }
                }
            }

            # Put the AirPods back on the phone before the next request.
            if ($afterSrcOn.Render -eq 'Active')
            {
                $back = Invoke-KsStep -Run $run -Label 'ks-disconnect-all-protected' -Action 'disconnect' -Filter 'all' `
                    -Consequence 'Sends KSPROPERTY_ONESHOT_DISCONNECT to every filter, so the AirPods leave this PC and can return to your phone.'
                [void]$back
                [void](Show-EndpointStates -Run $run -Label 'audio-after-disconnect-protected')
            }

            $waveOn = Invoke-KsStep -Run $run -Label 'ks-reconnect-wave-protected' -Action 'reconnect' -Filter 'wave' `
                -Consequence 'Sends the reconnect to the Handsfree filter only. With protection on there should be no Handsfree filter to send to.'
            if ($null -ne $waveOn)
            {
                Add-Criterion -Run $run -Id 'K1-wave-absent' -Criterion 'With protection on, there is no Handsfree filter to send to.' `
                    -Outcome $(if ($waveOn.Filters.Count -eq 0) { 'pass' } else { 'inconclusive' }) `
                    -Detail ('filters found: ' + $waveOn.Filters.Count + '.')
                [void](Show-EndpointStates -Run $run -Label 'audio-after-wave-protected')
            }

            $allOn = Invoke-KsStep -Run $run -Label 'ks-reconnect-all-protected' -Action 'reconnect' -Filter 'all' `
                -Consequence 'Sends the reconnect to every filter that is there, which is what the application does on a left click.'
            if ($null -ne $allOn)
            {
                Add-Criterion -Run $run -Id 'K1-all-protected' -Criterion 'Sending to every filter connects with protection on.' `
                    -Outcome $(if ($allOn.Reached -eq $true) { 'pass' } else { 'fail' }) `
                    -Detail ('accepted: ' + (($allOn.AcceptedRoles + @('none')) -join ' ') + ', confirmation reached ' + $allOn.Reached + '.')
                [void](Show-EndpointStates -Run $run -Label 'audio-after-all-protected')
            }

            # --------------------------------------------------------- protection off
            Write-Section -Run $run -Title 'Leg B: protection off (both filters present)'
            Write-Line -Run $run -Text 'This leg is the comparison. It shows whether a refusal in leg A was about the missing Handsfree filter.'
            $off = Invoke-Earshot -Run $run -Label 'gate-protect-off' -Command @('diag', 'gate', 'protect-off') -Live `
                -Consequence 'Turns the Handsfree and Headset services back on, which reinstalls their profile drivers and brings the Handsfree filter back. It can take minutes.' `
                -TimeoutSeconds 420
            if ($null -ne $off)
            {
                $services = Get-ServiceState -Run $run -Label 'services-unprotected'
                Write-Line -Run $run -Text ('Protection now reads ' + (Get-Field -Object $services -Name 'protection') + '.')

                if ((Show-EndpointStates -Run $run -Label 'audio-before-src-unprotected').Render -eq 'Active')
                {
                    $back = Invoke-KsStep -Run $run -Label 'ks-disconnect-all-unprotected' -Action 'disconnect' -Filter 'all' `
                        -Consequence 'Disconnects again, so the reconnect below starts from the same place as leg A.'
                    [void]$back
                }

                $srcOff = Invoke-KsStep -Run $run -Label 'ks-reconnect-src-unprotected' -Action 'reconnect' -Filter 'src' `
                    -Consequence 'Sends the reconnect to the A2DP filter only, this time with the Handsfree filter present.'
                $afterSrcOff = Show-EndpointStates -Run $run -Label 'audio-after-src-unprotected'
                if ($null -ne $srcOff)
                {
                    Add-Criterion -Run $run -Id 'K1-src-unprotected' -Criterion 'The A2DP filter accepts ONESHOT_RECONNECT with protection off.' `
                        -Outcome $(if ($srcOff.AcceptedRoles.Count -gt 0) { 'pass' } else { 'fail' }) `
                        -Detail ('accepted: ' + (($srcOff.AcceptedRoles + @('none')) -join ' ') + ', render now ' + $afterSrcOff.Render + '.')
                }

                if ($afterSrcOff.Render -eq 'Active')
                {
                    [void](Invoke-KsStep -Run $run -Label 'ks-disconnect-all-unprotected-2' -Action 'disconnect' -Filter 'all' `
                        -Consequence 'Disconnects again before the Handsfree only request.')
                }

                $waveOff = Invoke-KsStep -Run $run -Label 'ks-reconnect-wave-unprotected' -Action 'reconnect' -Filter 'wave' `
                    -Consequence 'Sends the reconnect to the Handsfree filter only, to see whether it brings A2DP up on its own.'
                $afterWaveOff = Show-EndpointStates -Run $run -Label 'audio-after-wave-unprotected'
                if ($null -ne $waveOff)
                {
                    Add-Criterion -Run $run -Id 'K1-wave-brings-a2dp' -Criterion 'The Handsfree filter alone brings the A2DP render endpoint up.' `
                        -Outcome $(if ($afterWaveOff.Render -eq 'Active') { 'pass' } else { 'fail' }) `
                        -Detail ('accepted: ' + (($waveOff.AcceptedRoles + @('none')) -join ' ') + ', render now ' + $afterWaveOff.Render + '.')
                    Add-Finding -Run $run -Name 'handsfreeAloneBringsA2dp' -Value $(if ($afterWaveOff.Render -eq 'Active') { 'yes' } else { 'no' })
                }
            }

            Write-Section -Run $run -Title 'Wrong state requests'
            Write-Line -Run $run -Text 'What the drivers answer for a request that is already satisfied is worth recording once.'
            if ((Show-EndpointStates -Run $run -Label 'audio-before-redundant').Render -eq 'Active')
            {
                $redundant = Invoke-KsStep -Run $run -Label 'ks-reconnect-all-while-active' -Action 'reconnect' -Filter 'all' `
                    -Consequence 'Asks to connect while already connected, to record what the drivers return.'
                if ($null -ne $redundant)
                {
                    Add-Finding -Run $run -Name 'reconnectWhileActive' -Value (($redundant.Filters | ForEach-Object { [string]$_.role + '=' + $_.hrName }) -join ' ')
                }
            }

            Write-Section -Run $run -Title 'Leaving the machine as the default'
            Write-Line -Run $run -Text 'Earshot ships with Protect audio quality on, so the last step puts it back on.'
            $restore = Invoke-Earshot -Run $run -Label 'gate-protect-on-restore' -Command @('diag', 'gate', 'protect-on') -Live `
                -Consequence 'Turns the Handsfree and Headset services off again, returning the machine to the shipping default. It can take minutes.' `
                -TimeoutSeconds 420
            [void]$restore
            $services = Get-ServiceState -Run $run -Label 'services-final'
            Add-Criterion -Run $run -Id 'restored' -Criterion 'The machine is left with protection on, as it ships.' `
                -Outcome $(if ((Get-Field -Object $services -Name 'protection') -eq 'Protected') { 'pass' } else { 'fail' }) `
                -Detail ('It reads ' + (Get-Field -Object $services -Name 'protection') + '. If it is not Protected, run 00-Restore.ps1.')
        }

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
    $overall = Complete-LiveTestRun -Run $run
    Write-Host ('Test 01 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
