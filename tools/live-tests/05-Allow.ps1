<#
.SYNOPSIS
    Does the allow clear the persistent disable and bring the endpoints back?

.DESCRIPTION
    From blocked nodes, this allows them through the elevated task and checks three
    things: the problem code clears, the disabled bit in ConfigFlags clears, and the
    audio endpoints come back within the ten second wait the connect path allows.

    The disabled bit matters on its own. If CM_Enable_DevNode cleared the problem
    but left the bit, the nodes would come back disabled at the next boot and a
    connect would look like it worked until the machine restarted.

    Optionally it restarts as well, to show the enable is as persistent as the
    disable was.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the restart.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\05-Allow.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

# The wait the connect path allows for a render endpoint to come back after an allow.
$EndpointWaitSeconds = 10

if ($Resume -and [string]::IsNullOrEmpty($RunRoot))
{
    throw 'The second half needs -RunRoot, the folder the first half printed. It is in resume.txt in that folder.'
}

$run = New-LiveTestRun -TestId '05-allow' -Title 'Allow the nodes and watch the endpoints return' `
    -Settles 'Whether the allow clears the persistent disable, and whether ten seconds is long enough for the render endpoint to come back.' `
    -ExePath $ExePath -RunRoot $RunRoot

function Measure-TargetNodes
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $nodes = Get-NodeState -Run $Run -Label $Label
    $counts = [ordered]@{ State = (Get-Field -Object $nodes -Name 'nodeState'); Targets = 0; Disabled = 0; Problem22 = 0 }
    foreach ($node in (Get-Field -Object $nodes -Name 'nodes'))
    {
        if ((Get-Field -Object $node -Name 'target') -ne $true) { continue }
        $counts.Targets = $counts.Targets + 1
        if ((Get-Field -Object $node -Name 'configFlagsDisabled') -eq $true) { $counts.Disabled = $counts.Disabled + 1 }
        if ((Get-Field -Object $node -Name 'problem') -eq 22) { $counts.Problem22 = $counts.Problem22 + 1 }
        Write-Line -Run $Run -Text ('  ' + (Get-Field -Object $node -Name 'instanceId') +
            ': problem ' + (Get-Field -Object $node -Name 'problem') +
            ', disabled bit ' + (Get-Field -Object $node -Name 'configFlagsDisabled'))
    }

    Write-Line -Run $Run -Text ('  state ' + $counts.State + ': ' + $counts.Targets + ' target nodes, ' +
        $counts.Disabled + ' with the disabled bit, ' + $counts.Problem22 + ' at problem 22')
    return $counts
}

try
{
    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed and set up.',
            'The AirPods Bluetooth nodes are blocked.',
            'The Earshot tray is closed, so its idle rule does not block them again mid-test.'
        ) -PhysicalActions @(
            'Keep the AirPods on your phone. Allowing the nodes may take them off it, which is itself worth noting.'
        )

        if ($ready)
        {
            Write-Section -Run $run -Title 'Before the allow'
            $before = Measure-TargetNodes -Run $run -Label 'nodes-before'
            $audioBefore = Get-AudioState -Run $run -Label 'audio-before'
            $statesBefore = Get-TargetEndpointStates -AudioJson $audioBefore
            Write-Line -Run $run -Text ('Render ' + $statesBefore.Render + ', capture ' + $statesBefore.Capture)

            if ($before.State -ne 'Blocked' -and $before.State -ne 'Mixed')
            {
                Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The nodes start blocked.' -Outcome 'inconclusive' `
                    -Detail ('They read ' + $before.State + '. Nothing to allow.')
            }
            else
            {
                Write-Section -Run $run -Title 'Allow'
                $allow = Invoke-Earshot -Run $run -Label 'gate-allow' -Command @('diag', 'gate', 'allow') -Live `
                    -Consequence 'Enables the AirPods Bluetooth nodes through the elevated task. Windows may page the AirPods afterwards, which can take them off your phone.'

                if ($null -eq $allow)
                {
                    Add-Criterion -Run $run -Id 'allow' -Criterion 'The allow ran.' -Outcome 'inconclusive' -Detail 'It was skipped.'
                }
                else
                {
                    Add-Criterion -Run $run -Id 'allow' -Criterion 'The allow completes.' `
                        -Outcome $(if ($allow.exitCode -eq 0) { 'pass' } else { 'fail' }) `
                        -Detail ('exit ' + $allow.exitCode + ' after ' + $allow.milliseconds + ' ms.')

                    $after = Measure-TargetNodes -Run $run -Label 'nodes-after-allow'
                    Add-Criterion -Run $run -Id 'problem-cleared' -Criterion 'No target node is left at problem 22.' `
                        -Outcome $(if ($after.Problem22 -eq 0) { 'pass' } else { 'fail' }) -Detail ($after.Problem22 + ' are still at problem 22.')
                    Add-Criterion -Run $run -Id 'bit-cleared' -Criterion 'The disabled bit in ConfigFlags is cleared on every target node.' `
                        -Outcome $(if ($after.Disabled -eq 0) { 'pass' } else { 'fail' }) `
                        -Detail ($after.Disabled + ' still carry it. A node that keeps the bit comes back disabled at the next boot.')
                    Add-Finding -Run $run -Name 'enableClearsConfigFlagsDisabled' -Value $(if ($after.Disabled -eq 0) { 'yes' } else { 'no' })

                    Write-Section -Run $run -Title 'Do the endpoints come back?'
                    $seenAfter = $null
                    $elapsed = 0
                    while ($elapsed -lt 60 -and $null -eq $seenAfter)
                    {
                        Wait-Seconds -Run $run -Seconds 5 -Reason 'waiting for the audio endpoints to come back'
                        $elapsed = $elapsed + 5
                        $audio = Get-AudioState -Run $run -Label ('audio-after-allow-' + $elapsed)
                        $states = Get-TargetEndpointStates -AudioJson $audio
                        Write-Line -Run $run -Text ('  after ' + $elapsed + ' s: render ' + $states.Render + ', capture ' + $states.Capture)
                        if ($null -ne $states.Render -and $states.Render -ne 'NotPresent') { $seenAfter = $elapsed }
                    }

                    if ($null -eq $seenAfter)
                    {
                        Add-Criterion -Run $run -Id 'endpoints-back' -Criterion ('A render endpoint comes back within the ' + $EndpointWaitSeconds + ' s the connect path waits.') `
                            -Outcome 'fail' -Detail 'No render endpoint was readable within 60 s of the allow.'
                    }
                    else
                    {
                        Add-Criterion -Run $run -Id 'endpoints-back' -Criterion ('A render endpoint comes back within the ' + $EndpointWaitSeconds + ' s the connect path waits.') `
                            -Outcome $(if ($seenAfter -le $EndpointWaitSeconds) { 'pass' } else { 'fail' }) `
                            -Detail ('It was readable about ' + $seenAfter + ' s after the allow. The wait is measured in five second steps, so treat it as a bound, not a measurement.')
                        Add-Finding -Run $run -Name 'secondsUntilRenderEndpointReturned' -Value $seenAfter
                    }
                }
            }

            Save-EarshotLog -Run $run
            Write-Section -Run $run -Title 'Optional: restart to check the enable persists'
            $wants = Read-Answer -Run $run -Question 'Do you want to restart now and check the nodes are still enabled afterwards?'
            if ($wants -eq 'yes')
            {
                Write-Line -Run $run -Text 'Restart the machine yourself: Start menu, Power, Restart. This script does not restart anything.'
                Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
            }
        }
    }
    else
    {
        Write-Section -Run $run -Title 'After the restart'
        $after = Measure-TargetNodes -Run $run -Label 'nodes-after-restart'
        Add-Criterion -Run $run -Id 'still-allowed' -Criterion 'The nodes are still enabled after the restart.' `
            -Outcome $(if ($after.Targets -gt 0 -and $after.Disabled -eq 0 -and $after.Problem22 -eq 0) { 'pass' } else { 'fail' }) `
            -Detail ('state ' + $after.State + ', ' + $after.Disabled + ' with the disabled bit, ' + $after.Problem22 + ' at problem 22.')
        [void](Get-AudioState -Run $run -Label 'audio-after-restart')
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
    Write-Host ('Test 05 finished: ' + $overall)
}
