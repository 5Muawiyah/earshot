<#
.SYNOPSIS
    Does the one-shot disconnect work, and does one filter drop the whole link?

.DESCRIPTION
    With the AirPods connected to this PC, this sends KSPROPERTY_ONESHOT_DISCONNECT
    to the A2DP filter alone, then to the Handsfree filter alone, then to both, and
    records the HRESULT, the time to the render endpoint reaching UNPLUGGED and what
    happened to the capture endpoint each time.

    It then asks a second question the design never depends on but would like the
    answer to: does a Block alone, with no disconnect first, drop a link that is in
    use? Earshot never relies on that; if it turns out to be true it becomes a
    possible shortcut, and if it is false the current order is confirmed.

    It settles whether the 12 second disconnect budget is enough, whether one filter
    drops the whole link or only its own profile, and whether the capture endpoint
    always returns to UNPLUGGED with the render endpoint.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\02-Disconnect.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

# The disconnect budget the application waits for the render endpoint to leave ACTIVE.
$DisconnectBudgetMilliseconds = 12000

$run = New-LiveTestRun -TestId '02-disconnect' -Title 'One-shot disconnect per filter, and whether a block alone drops the link' `
    -Settles 'Whether the 12 s disconnect budget holds, whether one filter drops the whole link, and whether a block alone disconnects.' `
    -ExePath $ExePath -RunRoot $RunRoot

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
    Write-Line -Run $Run -Text ('  render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }) + ', derived ' + $states.Connection)
    return $states
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up.',
        'The AirPods are connected to this PC right now (the render endpoint is ACTIVE).',
        'The AirPods Bluetooth nodes are allowed, not blocked.',
        'The Earshot tray is closed, so nothing else connects or blocks during the test.'
    ) -PhysicalActions @(
        'Wear the AirPods and play something from this PC, so you can hear each disconnect.',
        'Be ready to reconnect them from Windows Bluetooth settings between the steps if asked.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Baseline'
        $before = Show-EndpointStates -Run $run -Label 'audio-before'
        $nodes = Get-NodeState -Run $run -Label 'nodes-before'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        Write-Line -Run $run -Text ('Nodes: ' + $nodeState + '.')

        if ($before.Render -ne 'Active')
        {
            Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The AirPods start connected to this PC.' `
                -Outcome 'inconclusive' -Detail ('The render endpoint reads ' + $before.Render + '. Connect them to this PC and run the test again.')
        }
        else
        {
            Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The AirPods start connected to this PC.' -Outcome 'pass' `
                -Detail ('render ' + $before.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($before.Capture)) { 'none' } else { $before.Capture }) + '.')

            Write-Section -Run $run -Title 'A2DP filter alone'
            $src = Invoke-KsStep -Run $run -Label 'ks-disconnect-src' -Action 'disconnect' -Filter 'src' `
                -Consequence 'Sends KSPROPERTY_ONESHOT_DISCONNECT to the A2DP filter only. The AirPods should leave this PC.'
            $afterSrc = Show-EndpointStates -Run $run -Label 'audio-after-src'
            if ($null -ne $src)
            {
                Add-Criterion -Run $run -Id 'disconnect-src' -Criterion 'The A2DP filter accepts ONESHOT_DISCONNECT.' `
                    -Outcome $(if ($src.AcceptedRoles.Count -gt 0) { 'pass' } else { 'fail' }) `
                    -Detail ('accepted: ' + (($src.AcceptedRoles + @('none')) -join ' ') + '.')
                Add-Criterion -Run $run -Id 'disconnect-src-unplugged' -Criterion 'The render endpoint leaves ACTIVE after the A2DP disconnect.' `
                    -Outcome $(if ($afterSrc.Render -ne 'Active') { 'pass' } else { 'fail' }) `
                    -Detail ('render now ' + $afterSrc.Render + ', capture now ' + $(if ([string]::IsNullOrEmpty($afterSrc.Capture)) { 'none' } else { $afterSrc.Capture }) + '.')
                Add-Finding -Run $run -Name 'a2dpDisconnectDropsCapture' -Value $(if ($afterSrc.Capture -eq 'Active') { 'no' } else { 'yes' }) `
                    -Detail 'whether the Handsfree capture endpoint left ACTIVE with the render endpoint'

                if ($null -ne $src.MillisToState)
                {
                    Add-Criterion -Run $run -Id 'disconnect-budget' -Criterion ('The render endpoint leaves ACTIVE within the ' + $DisconnectBudgetMilliseconds + ' ms disconnect budget.') `
                        -Outcome $(if ([int]$src.MillisToState -le $DisconnectBudgetMilliseconds) { 'pass' } else { 'fail' }) `
                        -Detail ([string]$src.MillisToState + ' ms.')
                    Add-Finding -Run $run -Name 'disconnectMilliseconds' -Value $src.MillisToState
                }
            }

            Write-Section -Run $run -Title 'Handsfree filter alone'
            Wait-Owner -Run $run -Text 'Connect the AirPods back to this PC from Windows Bluetooth settings, and wait until sound plays from this PC again.'
            $reconnected = Show-EndpointStates -Run $run -Label 'audio-before-wave'
            if ($reconnected.Render -eq 'Active')
            {
                $wave = Invoke-KsStep -Run $run -Label 'ks-disconnect-wave' -Action 'disconnect' -Filter 'wave' `
                    -Consequence 'Sends the disconnect to the Handsfree filter only. This shows whether one profile drops the whole link.'
                $afterWave = Show-EndpointStates -Run $run -Label 'audio-after-wave'
                if ($null -ne $wave)
                {
                    Add-Finding -Run $run -Name 'handsfreeDisconnectDropsWholeLink' -Value $(if ($afterWave.Render -ne 'Active') { 'yes' } else { 'no' }) `
                        -Detail ('render ' + $afterWave.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($afterWave.Capture)) { 'none' } else { $afterWave.Capture }))
                    Add-Criterion -Run $run -Id 'disconnect-wave' -Criterion 'The Handsfree filter answers the disconnect, one way or the other.' `
                        -Outcome $(if ($wave.Filters.Count -gt 0) { 'pass' } else { 'inconclusive' }) `
                        -Detail ('filters found: ' + $wave.Filters.Count + '. With protection on there is no Handsfree filter, which is not a failure.')
                }
            }
            else
            {
                Add-Criterion -Run $run -Id 'disconnect-wave' -Criterion 'The Handsfree filter answers the disconnect, one way or the other.' `
                    -Outcome 'inconclusive' -Detail 'The AirPods were not connected again, so this step did not run.'
            }

            Write-Section -Run $run -Title 'Every filter'
            Wait-Owner -Run $run -Text 'Connect the AirPods back to this PC again if they are not connected, and wait for sound.'
            if ((Show-EndpointStates -Run $run -Label 'audio-before-all').Render -eq 'Active')
            {
                $all = Invoke-KsStep -Run $run -Label 'ks-disconnect-all' -Action 'disconnect' -Filter 'all' `
                    -Consequence 'Sends the disconnect to every filter, which is what the application does on a left click.'
                $afterAll = Show-EndpointStates -Run $run -Label 'audio-after-all'
                if ($null -ne $all)
                {
                    Add-Criterion -Run $run -Id 'disconnect-all' -Criterion 'Sending to every filter disconnects the AirPods.' `
                        -Outcome $(if ($all.Reached -eq $true -and $afterAll.Render -ne 'Active') { 'pass' } else { 'fail' }) `
                        -Detail ('confirmation reached ' + $all.Reached + ', render now ' + $afterAll.Render + '.')
                }

                Write-Line -Run $run -Text 'For the record: what the drivers answer for a disconnect that is already satisfied.'
                $redundant = Invoke-KsStep -Run $run -Label 'ks-disconnect-all-while-unplugged' -Action 'disconnect' -Filter 'all' `
                    -Consequence 'Asks to disconnect while already disconnected, to record what the drivers return.'
                if ($null -ne $redundant)
                {
                    Add-Finding -Run $run -Name 'disconnectWhileUnplugged' -Value (($redundant.Filters | ForEach-Object { [string]$_.role + '=' + $_.hrName }) -join ' ')
                }
            }

            Write-Section -Run $run -Title 'Does a block alone drop an active link?'
            Write-Line -Run $run -Text 'Earshot never depends on this. It disconnects first and blocks afterwards. The answer only decides'
            Write-Line -Run $run -Text 'whether a shortcut is possible later, so a no here is a perfectly good result.'
            Wait-Owner -Run $run -Text 'Connect the AirPods back to this PC and wait until sound plays from this PC.'
            $beforeBlock = Show-EndpointStates -Run $run -Label 'audio-before-block'
            if ($beforeBlock.Render -eq 'Active')
            {
                $block = Invoke-Earshot -Run $run -Label 'gate-block-while-connected' -Command @('diag', 'gate', 'block') -Live `
                    -Consequence 'Disables the AirPods Bluetooth nodes through the elevated task while they are in use. It may cut the audio.'
                if ($null -ne $block)
                {
                    Wait-Seconds -Run $run -Seconds 10 -Reason 'letting the stack settle after the block'
                    $afterBlock = Show-EndpointStates -Run $run -Label 'audio-after-block'
                    $heard = Read-Answer -Run $run -Question 'Did the audio stop playing on this PC when the block ran?'
                    Add-Finding -Run $run -Name 'blockAloneDropsActiveLink' -Value $(if ($afterBlock.Render -ne 'Active') { 'yes' } else { 'no' }) `
                        -Detail ('render ' + $afterBlock.Render + ', you heard it stop: ' + $heard)
                    Add-Criterion -Run $run -Id 'block-recorded' -Criterion 'The effect of a block on a link in use is recorded.' `
                        -Outcome 'pass' -Detail ('render after the block: ' + $afterBlock.Render + '.')
                }

                Write-Line -Run $run -Text 'The nodes are blocked now. The next step allows them again so the machine is left usable.'
                $allow = Invoke-Earshot -Run $run -Label 'gate-allow' -Command @('diag', 'gate', 'allow') -Live `
                    -Consequence 'Enables the AirPods Bluetooth nodes again.'
                if ($null -ne $allow)
                {
                    $nodes = Get-NodeState -Run $run -Label 'nodes-final'
                    Add-Criterion -Run $run -Id 'left-allowed' -Criterion 'The nodes are left allowed.' `
                        -Outcome $(if ((Get-Field -Object $nodes -Name 'nodeState') -eq 'Allowed') { 'pass' } else { 'fail' }) `
                        -Detail ('They read ' + (Get-Field -Object $nodes -Name 'nodeState') + '. If they are blocked, run 00-Restore.ps1.')
                }
            }
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
    Write-Host ('Test 02 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
