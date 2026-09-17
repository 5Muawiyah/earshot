<#
.SYNOPSIS
    Handsfree protection: unelevated first, then through the gate, then restored.

.DESCRIPTION
    Protect audio quality turns the Handsfree and Headset services off with
    BluetoothSetServiceState, so Windows cannot drop the AirPods from A2DP to the
    8 or 16 kHz Handsfree profile. Nothing documents what that call needs: which
    privileges, how long it takes, or what it returns when the service is already
    in the requested state. Earshot runs it through a SYSTEM scheduled task because
    that is the safe assumption, not because it was measured.

    This test records the raw return code and duration twice: once from a normal,
    non-elevated prompt, and once through the elevated task. It then reads the
    services, the nodes and the endpoints each time, and finally restores.

    It settles whether a v1.1 unelevated fast path is possible, what the Headset
    service returns on these AirPods, and whether the Handsfree Bluetooth node and
    its capture endpoint disappear while protection is on.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\06-Handsfree.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

# The time limit on the Protect scheduled task, in seconds.
$ProtectTaskLimitSeconds = 300

$run = New-LiveTestRun -TestId '06-handsfree' -Title 'Handsfree protection, unelevated and through the gate' `
    -Settles 'Whether an unelevated fast path is possible in v1.1, what Headset returns on these AirPods, and how long the SYSTEM call takes against the five minute task limit.' `
    -ExePath $ExePath -RunRoot $RunRoot

function Show-Services
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $services = Get-ServiceState -Run $Run -Label $Label
    $listed = @()
    foreach ($service in (Get-Field -Object $services -Name 'installedServices'))
    {
        $listed = $listed + @([string](Get-Field -Object $service -Name 'label') + ' ' + (Get-Field -Object $service -Name 'guid'))
    }

    Write-Line -Run $Run -Text ('  protection ' + (Get-Field -Object $services -Name 'protection') +
        ', complete list ' + (Get-Field -Object $services -Name 'complete') +
        ', connected ' + (Get-Field -Object $services -Name 'connected'))
    foreach ($line in $listed) { Write-Line -Run $Run -Text ('    ' + $line) }
    return [ordered]@{ Protection = (Get-Field -Object $services -Name 'protection'); Services = $listed; Json = $services }
}

# Reads the unelevated diag evidence: the raw return code per service GUID.
function Read-UnelevatedEvidence
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        $Evidence
    )

    $rows = @()
    foreach ($step in (Get-Field -Object $Evidence -Name 'steps'))
    {
        $line = [string](Get-Field -Object $step -Name 'step') + ': ' + (Get-Field -Object $step -Name 'codeName') +
            ' (' + (Get-Field -Object $step -Name 'code') + ') ' + (Get-Field -Object $step -Name 'detail')
        $rows = $rows + @($line)
        Write-Line -Run $Run -Text ('  ' + $line)
    }

    return $rows
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up.',
        'The AirPods are paired with this PC. Connected or not is fine; note which, because it may change the answer.',
        'The Earshot tray is closed, so it does not re-apply protection during the test.',
        'This window is a normal window, not an administrator one. The first leg is about what an unelevated caller can do.'
    ) -PhysicalActions @(
        'Nothing physical, but expect Windows to install and remove Bluetooth profile drivers, which can take minutes and may show notifications.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'Baseline'
        $baseline = Show-Services -Run $run -Label 'services-before'
        $nodesBefore = Get-NodeState -Run $run -Label 'nodes-before'
        $audioBefore = Get-AudioState -Run $run -Label 'audio-before'
        $statesBefore = Get-TargetEndpointStates -AudioJson $audioBefore
        Write-Line -Run $run -Text ('Nodes ' + (Get-Field -Object $nodesBefore -Name 'nodeState') +
            ', render ' + $statesBefore.Render + ', capture ' + $statesBefore.Capture)
        Add-Finding -Run $run -Name 'servicesAtBaseline' -Value ($baseline.Services -join '; ')

        Write-Section -Run $run -Title 'Leg 1: unelevated'
        Write-Line -Run $run -Text 'This is the leg that decides whether v1.1 could skip the scheduled task. A refusal here is a'
        Write-Line -Run $run -Text 'perfectly good answer: it means the elevated task the build already uses is required.'

        $unelevatedOn = Invoke-Earshot -Run $run -Label 'protect-unelevated-on' -Command @('diag', 'protect-unelevated', 'on') -Live `
            -Consequence 'Calls BluetoothSetServiceState directly from this non-elevated process to turn Handsfree and Headset off, and records exactly what it returns.' `
            -TimeoutSeconds 420
        if ($null -ne $unelevatedOn)
        {
            $evidence = Get-DiagEvidence -Run $run
            $rows = @()
            if ($null -ne $evidence) { $rows = Read-UnelevatedEvidence -Run $run -Evidence $evidence }
            Add-Finding -Run $run -Name 'unelevatedProtectOnReturns' -Value ($rows -join '; ') `
                -Detail 'the raw BluetoothSetServiceState results from a non-elevated process'
            $afterUnelevated = Show-Services -Run $run -Label 'services-after-unelevated-on'
            Add-Criterion -Run $run -Id 'unelevated-recorded' -Criterion 'The unelevated return codes are recorded.' `
                -Outcome $(if ($rows.Count -gt 0) { 'pass' } else { 'inconclusive' }) `
                -Detail ('protection now reads ' + $afterUnelevated.Protection + '.')
            Add-Finding -Run $run -Name 'unelevatedProtectWorks' -Value $(if ($afterUnelevated.Protection -eq 'Protected') { 'yes' } else { 'no' }) `
                -Detail 'yes would mean a v1.1 fast path is possible without the scheduled task'

            if ($afterUnelevated.Protection -eq 'Protected')
            {
                $unelevatedOff = Invoke-Earshot -Run $run -Label 'protect-unelevated-off' -Command @('diag', 'protect-unelevated', 'off') -Live `
                    -Consequence 'Turns the services back on from this non-elevated process, undoing what the previous step did.' `
                    -TimeoutSeconds 420
                if ($null -ne $unelevatedOff)
                {
                    $offEvidence = Get-DiagEvidence -Run $run
                    if ($null -ne $offEvidence) { [void](Read-UnelevatedEvidence -Run $run -Evidence $offEvidence) }
                    [void](Show-Services -Run $run -Label 'services-after-unelevated-off')
                }
            }
        }

        Write-Section -Run $run -Title 'Leg 2: through the elevated task'
        $gateOn = Invoke-Earshot -Run $run -Label 'gate-protect-on' -Command @('diag', 'gate', 'protect-on') -Live `
            -Consequence 'Starts the SYSTEM Protect task, which turns Handsfree and Headset off. Windows removes their profile drivers, so the AirPods microphone stops working on this PC. It can take minutes.' `
            -TimeoutSeconds 420
        if ($null -eq $gateOn)
        {
            Add-Criterion -Run $run -Id 'gate-protect-on' -Criterion 'Protection applies through the elevated task.' `
                -Outcome 'inconclusive' -Detail 'The step was skipped.'
        }
        else
        {
            $gateEvidence = Get-DiagEvidence -Run $run
            $runMs = Get-Field -Object $gateEvidence -Name 'runMilliseconds'
            $lastResult = Get-Field -Object $gateEvidence -Name 'lastTaskResult'
            Write-Line -Run $run -Text ('  the task ran for ' + $runMs + ' ms, LastTaskResult ' + $lastResult)
            $protectedNow = Show-Services -Run $run -Label 'services-protected'

            Add-Criterion -Run $run -Id 'gate-protect-on' -Criterion 'Protection applies through the elevated task.' `
                -Outcome $(if ($protectedNow.Protection -eq 'Protected') { 'pass' } else { 'fail' }) `
                -Detail ('protection reads ' + $protectedNow.Protection + ', exit ' + $gateOn.exitCode + '.')

            if ($null -ne $runMs)
            {
                Add-Criterion -Run $run -Id 'within-task-limit' -Criterion ('The call finishes inside the ' + $ProtectTaskLimitSeconds + ' s limit on the Protect task.') `
                    -Outcome $(if ([int]$runMs -le ($ProtectTaskLimitSeconds * 1000)) { 'pass' } else { 'fail' }) `
                    -Detail ([string]$runMs + ' ms.')
                Add-Finding -Run $run -Name 'protectOnMilliseconds' -Value $runMs
            }

            Add-Finding -Run $run -Name 'lastTaskResultMatchesExit' -Value ($lastResult) `
                -Detail 'LastTaskResult as the scheduler reported it, next to the gate exit code in the evidence file'

            $nodesProtected = Get-NodeState -Run $run -Label 'nodes-protected'
            $handsfreeNodes = 0
            foreach ($node in (Get-Field -Object $nodesProtected -Name 'nodes'))
            {
                if ((Get-Field -Object $node -Name 'instanceId') -match '0000111E') { $handsfreeNodes = $handsfreeNodes + 1 }
            }

            Add-Finding -Run $run -Name 'handsfreeNodesWhileProtected' -Value $handsfreeNodes `
                -Detail 'how many BTHENUM nodes for the Handsfree service GUID are still listed while protection is on'

            $audioProtected = Get-AudioState -Run $run -Label 'audio-protected'
            $statesProtected = Get-TargetEndpointStates -AudioJson $audioProtected
            Write-Line -Run $run -Text ('Render ' + $statesProtected.Render + ', capture ' + $statesProtected.Capture)
            Add-Finding -Run $run -Name 'captureEndpointWhileProtected' -Value $(if ($null -eq $statesProtected.Capture) { 'gone' } else { $statesProtected.Capture })

            Write-Section -Run $run -Title 'Does protection last?'
            Write-Line -Run $run -Text 'Community reports say Windows turns Handsfree back on when the device reconnects. Nothing in the'
            Write-Line -Run $run -Text 'documentation says so, which is why Earshot re-checks after every connect.'
            Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC and connect them again from Windows Bluetooth settings.'
            $afterReconnect = Show-Services -Run $run -Label 'services-after-reconnect'
            Add-Criterion -Run $run -Id 'protection-survives-reconnect' -Criterion 'Protection is still on after a reconnect.' `
                -Outcome $(if ($afterReconnect.Protection -eq 'Protected') { 'pass' } else { 'fail' }) `
                -Detail ('It reads ' + $afterReconnect.Protection + '. A fail here is what the re-check after each connect exists for.')
            Add-Finding -Run $run -Name 'handsfreeReturnsAfterReconnect' -Value $(if ($afterReconnect.Protection -eq 'Protected') { 'no' } else { 'yes' })

            Write-Section -Run $run -Title 'Protection while the nodes are blocked'
            Write-Line -Run $run -Text 'The tray reads the services while the nodes may be disabled. Whether that read is trustworthy'
            Write-Line -Run $run -Text 'decides how protection is reported in the menu while Block at boot is holding.'
            $block = Invoke-Earshot -Run $run -Label 'gate-block' -Command @('diag', 'gate', 'block') -Live `
                -Consequence 'Disables the AirPods Bluetooth nodes, so the service read can be compared against the same read with the nodes enabled.'
            if ($null -ne $block)
            {
                $blockedServices = Show-Services -Run $run -Label 'services-while-blocked'
                Add-Finding -Run $run -Name 'servicesListedWhileBlocked' -Value ($blockedServices.Services -join '; ') `
                    -Detail 'compare with servicesAtBaseline; the same list means the read is trustworthy while blocked'
                Add-Criterion -Run $run -Id 'services-readable-while-blocked' -Criterion 'The installed services are still readable while the nodes are disabled.' `
                    -Outcome $(if ($blockedServices.Services.Count -gt 0) { 'pass' } else { 'fail' }) `
                    -Detail ([string]$blockedServices.Services.Count + ' services were listed, protection read ' + $blockedServices.Protection + '.')

                $allow = Invoke-Earshot -Run $run -Label 'gate-allow' -Command @('diag', 'gate', 'allow') -Live `
                    -Consequence 'Enables the nodes again.'
                [void]$allow
            }

            Write-Section -Run $run -Title 'Restore'
            $restoreTo = Read-Answer -Run $run -Question 'Leave protection on, as Earshot ships, or turn it off?' -Options @('on', 'off')
            if ($restoreTo -eq 'off')
            {
                $gateOff = Invoke-Earshot -Run $run -Label 'gate-protect-off' -Command @('diag', 'gate', 'protect-off') -Live `
                    -Consequence 'Turns the Handsfree and Headset services back on through the elevated task, which reinstalls their profile drivers. It can take minutes.' `
                    -TimeoutSeconds 420
                if ($null -ne $gateOff)
                {
                    $offEvidence = Get-DiagEvidence -Run $run
                    Write-Line -Run $run -Text ('  the task ran for ' + (Get-Field -Object $offEvidence -Name 'runMilliseconds') + ' ms')
                    $final = Show-Services -Run $run -Label 'services-final'
                    Add-Criterion -Run $run -Id 'gate-protect-off' -Criterion 'Turning protection off puts the services back.' `
                        -Outcome $(if ($final.Protection -eq 'NotProtected') { 'pass' } else { 'fail' }) `
                        -Detail ('It reads ' + $final.Protection + '.')
                }
            }
            else
            {
                $final = Show-Services -Run $run -Label 'services-final'
                Add-Criterion -Run $run -Id 'left-as-asked' -Criterion 'The machine is left with protection in the state you asked for.' `
                    -Outcome $(if ($final.Protection -eq 'Protected') { 'pass' } else { 'fail' }) -Detail ('It reads ' + $final.Protection + '.')
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
    Write-Host ('Test 06 finished: ' + $overall)
}
