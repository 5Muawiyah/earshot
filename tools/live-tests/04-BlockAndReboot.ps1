<#
.SYNOPSIS
    Does the persistent disable hold across a restart?

.DESCRIPTION
    Blocks the AirPods Bluetooth nodes through the elevated task, reads every node
    back without elevation (problem 22 and the disabled bit in ConfigFlags), then
    stops and tells you exactly what to run after the machine has restarted. The
    second half reads the same nodes again and says whether the disable survived.

    This is the mechanism the whole boot block rests on: CM_Disable_DevNode with
    CM_DISABLE_PERSIST. Without the persist flag the disable is undone at the next
    boot, and the acceptance test would fail quietly.

    Run it once with Fast Startup as it is, and, if you ever turn Fast Startup on,
    once more with -Note 'fast startup on' so the evidence says which it was.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the restart.

.PARAMETER Note
    A short note kept in the evidence, for example 'fast startup on'.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\04-BlockAndReboot.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [switch]$Resume,
    [string]$Note = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

if ($Resume -and [string]::IsNullOrEmpty($RunRoot))
{
    throw 'The second half needs -RunRoot, the folder the first half printed. It is in resume.txt in that folder.'
}

$run = New-LiveTestRun -TestId '04-block-and-reboot' -Title 'Block, restart, and read the nodes again' `
    -Settles 'Whether CM_DISABLE_PERSIST really holds across a restart, which is what the boot block depends on.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Reads every target node and returns how many are disabled and how many are not.
function Measure-TargetNodes
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $nodes = Get-NodeState -Run $Run -Label $Label
    $counts = [ordered]@{ State = (Get-Field -Object $nodes -Name 'nodeState'); Targets = 0; Disabled = 0; Problem22 = 0; Present = 0; Rows = @() }
    foreach ($node in (Get-Field -Object $nodes -Name 'nodes'))
    {
        if ((Get-Field -Object $node -Name 'target') -ne $true) { continue }
        $counts.Targets = $counts.Targets + 1
        $disabledBit = Get-Field -Object $node -Name 'configFlagsDisabled'
        $problem = Get-Field -Object $node -Name 'problem'
        if ($disabledBit -eq $true) { $counts.Disabled = $counts.Disabled + 1 }
        if ($problem -eq 22) { $counts.Problem22 = $counts.Problem22 + 1 }
        if ((Get-Field -Object $node -Name 'present') -eq $true) { $counts.Present = $counts.Present + 1 }
        $line = (Get-Field -Object $node -Name 'instanceId') + ': present ' + (Get-Field -Object $node -Name 'present') +
            ', status ' + (Get-Field -Object $node -Name 'status') + ', problem ' + $problem + ', disabled bit ' + $disabledBit
        $counts.Rows = $counts.Rows + @($line)
        Write-Line -Run $Run -Text ('  ' + $line)
    }

    Write-Line -Run $Run -Text ('  state ' + $counts.State + ': ' + $counts.Targets + ' target nodes, ' + $counts.Disabled +
        ' with the disabled bit, ' + $counts.Problem22 + ' at problem 22, ' + $counts.Present + ' present')
    return $counts
}

try
{
    if (-not [string]::IsNullOrEmpty($Note)) { Add-Finding -Run $run -Name 'note' -Value $Note }

    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed and set up.',
            'The AirPods have been connected to this PC at least once, so their Bluetooth nodes exist.',
            'The AirPods are on your phone, not this PC.',
            'You are able to restart this machine now.'
        ) -PhysicalActions @(
            'Restart the machine when this half tells you to, then run the command it prints.'
        )

        if ($ready)
        {
            Write-Section -Run $run -Title 'Before the block'
            $before = Measure-TargetNodes -Run $run -Label 'nodes-before'
            if ($before.Targets -eq 0)
            {
                Add-Criterion -Run $run -Id 'nodes-present' -Criterion 'The pinned device has Bluetooth nodes to disable.' `
                    -Outcome 'inconclusive' -Detail 'No node matched. Connect the AirPods to this PC once from Windows Bluetooth settings, then run this test again.'
            }
            else
            {
                Write-Section -Run $run -Title 'Block'
                $block = Invoke-Earshot -Run $run -Label 'gate-block' -Command @('diag', 'gate', 'block') -Live `
                    -Consequence 'Disables the AirPods Bluetooth nodes through the elevated task, with the flag that makes the disable persist across restarts.'

                if ($null -eq $block)
                {
                    Add-Criterion -Run $run -Id 'block' -Criterion 'The block ran.' -Outcome 'inconclusive' -Detail 'It was skipped.'
                }
                else
                {
                    Add-Criterion -Run $run -Id 'block' -Criterion 'The block completes.' `
                        -Outcome $(if ($block.exitCode -eq 0) { 'pass' } else { 'fail' }) `
                        -Detail ('exit ' + $block.exitCode + ' after ' + $block.milliseconds + ' ms. The per-node results and timings are in the evidence file.')

                    $after = Measure-TargetNodes -Run $run -Label 'nodes-after-block'
                    Add-Criterion -Run $run -Id 'disabled-now' -Criterion 'Every target node reads disabled, at problem 22, with the disabled bit set.' `
                        -Outcome $(if ($after.Targets -gt 0 -and $after.Disabled -eq $after.Targets -and $after.Problem22 -eq $after.Targets) { 'pass' } else { 'fail' }) `
                        -Detail ($after.Disabled + ' of ' + $after.Targets + ' carry the bit, ' + $after.Problem22 + ' are at problem 22.')

                    Add-Criterion -Run $run -Id 'locatable' -Criterion 'A disabled node is still found by a non-elevated read, so the tray can report Blocked.' `
                        -Outcome $(if ($after.Present -gt 0) { 'pass' } else { 'fail' }) `
                        -Detail ($after.Present + ' of ' + $after.Targets + ' target nodes were still readable.')

                    Add-Finding -Run $run -Name 'nodesBlockedBeforeRestart' -Value ($after.Disabled.ToString() + '/' + $after.Targets.ToString())
                }
            }

            Save-EarshotLog -Run $run
            Write-Section -Run $run -Title 'Now restart'
            Write-Line -Run $run -Text 'Restart the machine yourself: Start menu, Power, Restart. This script does not restart anything.'
            Write-Line -Run $run -Text 'Leave the AirPods on your phone while it restarts.'
            Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
        }
    }
    else
    {
        Write-Section -Run $run -Title 'After the restart'
        Write-Line -Run $run -Text 'This half reads the same nodes again. Nothing is changed.'
        $after = Measure-TargetNodes -Run $run -Label 'nodes-after-restart'
        Add-Criterion -Run $run -Id 'persisted' -Criterion 'Every target node is still disabled after the restart.' `
            -Outcome $(if ($after.Targets -gt 0 -and $after.Disabled -eq $after.Targets -and $after.Problem22 -eq $after.Targets) { 'pass' } else { 'fail' }) `
            -Detail ($after.Disabled + ' of ' + $after.Targets + ' carry the disabled bit, ' + $after.Problem22 + ' are at problem 22, state ' + $after.State + '.')

        Add-Finding -Run $run -Name 'persistentDisableSurvivesRestart' -Value $(if ($after.Targets -gt 0 -and $after.Disabled -eq $after.Targets) { 'yes' } else { 'no' })

        $audio = Get-AudioState -Run $run -Label 'audio-after-restart'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Render ' + $states.Render + ', capture ' + $states.Capture + ', derived ' + $states.Connection)
        Add-Finding -Run $run -Name 'endpointStateWhileBlocked' -Value ('render ' + $states.Render + ', capture ' + $states.Capture) `
            -Detail 'what the audio endpoints look like while the nodes are disabled'
        Add-Finding -Run $run -Name 'resolutionWhileBlocked' -Value ('' + (Get-Field -Object $audio -Name 'resolution')) `
            -Detail 'whether the pinned device still resolves while its nodes are disabled'

        # Which step of the walk fails while the nodes are disabled is not documented anywhere.
        $topology = Get-TopologyState -Run $run -Label 'topology-while-blocked'
        $activated = 0
        foreach ($adapter in (Get-Field -Object $topology -Name 'adapters'))
        {
            if ((Get-Field -Object $adapter -Name 'ksControlActivated') -eq $true) { $activated = $activated + 1 }
        }

        Add-Finding -Run $run -Name 'filtersActivatedWhileBlocked' -Value $activated `
            -Detail 'the failing step is in the topology evidence file; nothing is sent to a filter either way'

        $stayed = Read-Answer -Run $run -Question 'Did the AirPods stay on your phone across the restart?'
        Add-Criterion -Run $run -Id 'stayed-on-phone' -Criterion 'The AirPods stayed on the phone across the restart.' `
            -Outcome $(if ($stayed -eq 'yes') { 'pass' } elseif ($stayed -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $stayed + '.')

        Save-EarshotLog -Run $run
        Write-Line -Run $run -Text ''
        Write-Line -Run $run -Text 'The nodes are still blocked. Run 05-Allow.ps1 when you want them back, or 00-Restore.ps1.'
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
    Write-Host ('Test 04 finished: ' + $overall)
}
