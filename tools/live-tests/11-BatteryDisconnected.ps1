<#
.SYNOPSIS
    The battery sweep with the AirPods disconnected, and again connected.

.DESCRIPTION
    Phase 0 asked whether Windows exposes any battery value for these AirPods. It
    was run with them connected to this PC and nothing came back, so v1 has no
    battery element at all. The one leg that was never run is the disconnected one,
    and this test runs it.

    The answer cannot bring the battery feature back into v1: the brief expected a
    value only while connected, and there was none. What it does is close the
    question honestly and record the evidence for a later version.

    Nothing in this test writes a number anywhere. If no property returns a value,
    the answer is that there is none, not a zero and not an estimate.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\11-BatteryDisconnected.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '11-battery-disconnected' -Title 'Battery sweep, disconnected and connected' `
    -Settles 'The leg of the phase 0 battery question that was never run: whether any battery property returns a value while the AirPods are disconnected from this PC.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Reads a battery sweep report: which watched keys returned a value, and the positive control.
function Read-SweepEvidence
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        $Evidence
    )

    $summary = [ordered]@{ Values = @(); Control = (Get-Field -Object $Evidence -Name 'containerIdReadOnAMatchedNode'); Matched = 0 }
    foreach ($node in (Get-Field -Object $Evidence -Name 'matched'))
    {
        $summary.Matched = $summary.Matched + 1
        Write-Line -Run $Run -Text ('  node ' + (Get-Field -Object $node -Name 'instanceId') +
            ' (' + (Get-Field -Object $node -Name 'friendlyName') + '), present ' + (Get-Field -Object $node -Name 'present') +
            ', property read ' + (Get-Field -Object $node -Name 'keys'))
    }

    foreach ($value in (Get-Field -Object $Evidence -Name 'watchedValues'))
    {
        $line = ($value | ConvertTo-Json -Depth 4 -Compress)
        $summary.Values = $summary.Values + @($line)
        Write-Line -Run $Run -Text ('  value: ' + $line)
    }

    Write-Line -Run $Run -Text ('  ' + $summary.Matched + ' matched nodes, ' + $summary.Values.Count +
        ' watched values returned, control (a container id was readable) ' + $summary.Control)
    return $summary
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up.',
        'The AirPods are paired with this PC.',
        'The AirPods Bluetooth nodes are allowed, not blocked. A blocked node answers nothing, which would not be an answer to this question.'
    ) -PhysicalActions @(
        'Start with the AirPods disconnected from this PC, on your phone or in the case.',
        'Halfway through you will be asked to connect them to this PC for the comparison leg.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Leg 1: disconnected'
        $audio = Get-AudioState -Run $run -Label 'audio-disconnected'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Render ' + $states.Render + ', capture ' + $states.Capture + ', derived ' + $states.Connection)
        if ($states.Render -eq 'Active')
        {
            Add-Criterion -Run $run -Id 'leg1-preconditions' -Criterion 'Leg 1 runs with the AirPods disconnected from this PC.' `
                -Outcome 'inconclusive' -Detail 'The render endpoint is ACTIVE, so they are connected. Disconnect them and run the test again.'
        }
        else
        {
            $evidence = Join-Path $run.Folder 'battery-disconnected.json'
            $probe = Invoke-Earshot -Run $run -Label 'probe-battery-disconnected' `
                -Command @('probe', 'battery', '--json', '--out', $evidence)
            if ($null -ne $probe)
            {
                Write-Line -Run $run -Text ('  has a source: ' + (Get-Field -Object $probe.json -Name 'hasSource') +
                    ', has a value: ' + (Get-Field -Object $probe.json -Name 'hasValue'))
            }

            $sweep = Invoke-Earshot -Run $run -Label 'battery-sweep-disconnected' -Command @('diag', 'battery-sweep') -Live `
                -Consequence 'Reads every device property Windows exposes for the AirPods and looks for a battery value. It only reads; it changes nothing.' `
                -TimeoutSeconds 300
            if ($null -eq $sweep)
            {
                Add-Criterion -Run $run -Id 'sweep-disconnected' -Criterion 'The disconnected sweep ran.' -Outcome 'inconclusive' -Detail 'It was skipped.'
            }
            else
            {
                $evidence = Get-DiagEvidence -Run $run
                $summary = Read-SweepEvidence -Run $run -Evidence $evidence
                Add-Criterion -Run $run -Id 'sweep-disconnected' -Criterion 'The sweep read the AirPods device properties while disconnected.' `
                    -Outcome $(if ($summary.Matched -gt 0 -and $summary.Control -eq $true) { 'pass' } else { 'inconclusive' }) `
                    -Detail ([string]$summary.Matched + ' nodes matched; the positive control (a container id could be read) was ' + $summary.Control + '.')
                Add-Criterion -Run $run -Id 'no-battery-disconnected' -Criterion 'No battery property returns a value while disconnected.' `
                    -Outcome $(if ($summary.Values.Count -eq 0) { 'pass' } else { 'fail' }) `
                    -Detail $(if ($summary.Values.Count -eq 0) { 'Nothing returned a value, which matches the phase 0 answer.' } else { 'Something did return a value: ' + ($summary.Values -join ' | ') })
                Add-Finding -Run $run -Name 'batteryValueWhileDisconnected' -Value $(if ($summary.Values.Count -eq 0) { 'none' } else { ($summary.Values -join ' | ') })
            }
        }

        Write-Section -Run $run -Title 'Leg 2: connected, for comparison'
        Wait-Owner -Run $run -Text 'Connect the AirPods to this PC now, from the tray icon or Windows Bluetooth settings, and wait until sound plays from this PC.'
        $audioConnected = Get-AudioState -Run $run -Label 'audio-connected'
        $statesConnected = Get-TargetEndpointStates -AudioJson $audioConnected
        Write-Line -Run $run -Text ('Render ' + $statesConnected.Render + ', capture ' + $statesConnected.Capture)

        if ($statesConnected.Render -ne 'Active')
        {
            Add-Criterion -Run $run -Id 'sweep-connected' -Criterion 'The connected sweep ran with the AirPods on this PC.' `
                -Outcome 'inconclusive' -Detail ('The render endpoint reads ' + $statesConnected.Render + ', so they are not connected.')
        }
        else
        {
            $sweepConnected = Invoke-Earshot -Run $run -Label 'battery-sweep-connected' -Command @('diag', 'battery-sweep') -Live `
                -Consequence 'The same read again, this time with the AirPods connected to this PC. It changes nothing.' `
                -TimeoutSeconds 300
            if ($null -ne $sweepConnected)
            {
                $evidence = Get-DiagEvidence -Run $run
                $summary = Read-SweepEvidence -Run $run -Evidence $evidence
                Add-Criterion -Run $run -Id 'sweep-connected' -Criterion 'The connected sweep confirms the phase 0 answer.' `
                    -Outcome $(if ($summary.Values.Count -eq 0) { 'pass' } else { 'fail' }) `
                    -Detail $(if ($summary.Values.Count -eq 0) { 'No battery property returned a value, which is what phase 0 found.' } else { 'A value came back: ' + ($summary.Values -join ' | ') + '. That would be new evidence worth recording in the README.' })
                Add-Finding -Run $run -Name 'batteryValueWhileConnected' -Value $(if ($summary.Values.Count -eq 0) { 'none' } else { ($summary.Values -join ' | ') })
            }
        }

        Write-Line -Run $run -Text ''
        Write-Line -Run $run -Text 'Whatever the answer, do not put a number in the interface that was not measured. The battery element'
        Write-Line -Run $run -Text 'is absent in v1 on purpose, and a placeholder would be worse than nothing.'
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
    Write-Host ('Test 11 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
