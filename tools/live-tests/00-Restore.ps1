<#
.SYNOPSIS
    Puts the machine back to a known state after a live test.

.DESCRIPTION
    Run this whenever a test stopped early, left the AirPods disconnected from
    everything, or you simply want the machine back as it was. It does four
    things, each only after you agree to it:

      1. reads the node state, and allows the nodes if they are blocked;
      2. reads the Bluetooth services, and puts Handsfree back the way you want it;
      3. reads everything again so the summary shows the state it finished in;
      4. offers to uninstall, which allows the nodes, turns the services back on,
         removes the tasks and the folders, and needs one administrator prompt.

    If the gate is not installed, allowing the nodes from here is not possible.
    The summary then tells you the manual way: Device Manager, View > Show hidden
    devices, then Enable device on each AirPods Bluetooth node.

.PARAMETER ExePath
    Earshot.exe. Use the installed copy (%ProgramFiles%\Earshot\Earshot.exe) or an
    unzipped release. Uninstall refuses anything else.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.PARAMETER OfferUninstall
    Also offer the uninstall step at the end.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\00-Restore.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [switch]$OfferUninstall
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

$run = New-LiveTestRun -TestId '00-restore' -Title 'Restore the machine to a known state' `
    -Settles 'Nothing. It puts the machine back so the next test starts from a known state.' `
    -ExePath $ExePath -RunRoot $RunRoot

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'You know which state you want to end in: nodes allowed, and Handsfree on or off.',
        'The Earshot tray is closed, so its idle rule does not block the nodes while this runs.'
    ) -PhysicalActions @(
        'Nothing physical, unless you choose to uninstall.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'The state now'
        $nodes = Get-NodeState -Run $run -Label 'nodes-before'
        $services = Get-ServiceState -Run $run -Label 'services-before'
        $task = Get-TaskState -Run $run -Label 'task-before'

        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $protection = Get-Field -Object $services -Name 'protection'
        $setUp = Get-Field -Object $task -Name 'setUp'
        Write-Line -Run $run -Text ('Nodes: ' + $nodeState + '. Protection: ' + $protection + '. Set up: ' + $setUp + '.')

        if ($setUp -ne $true)
        {
            Write-Line -Run $run -Text ''
            Write-Line -Run $run -Text 'The scheduled tasks are not installed, so the gate cannot be asked to allow or restore anything.'
            Write-Line -Run $run -Text 'If the nodes are disabled, enable them by hand: open Device Manager, turn on View > Show hidden'
            Write-Line -Run $run -Text 'devices, find the AirPods entries under Bluetooth, and choose Enable device on each one.'
            Add-Criterion -Run $run -Id 'setup' -Criterion 'The gate is installed, so restoring from here is possible.' `
                -Outcome 'inconclusive' -Detail 'probe task reports it is not set up.'
        }
        else
        {
            Write-Section -Run $run -Title 'Nodes'
            if ($nodeState -eq 'Blocked' -or $nodeState -eq 'Mixed')
            {
                $allow = Invoke-Earshot -Run $run -Label 'gate-allow' -Command @('diag', 'gate', 'allow') -Live `
                    -Consequence 'Enables the AirPods Bluetooth nodes through the elevated task. Windows may then page the AirPods, which can take them off your phone.'
                if ($null -ne $allow)
                {
                    $nodes = Get-NodeState -Run $run -Label 'nodes-after-allow'
                    $nodeState = Get-Field -Object $nodes -Name 'nodeState'
                }
            }
            else
            {
                Write-Line -Run $run -Text ('The nodes read ' + $nodeState + ', so nothing is allowed.')
            }

            Add-Criterion -Run $run -Id 'nodes' -Criterion 'The nodes end up Allowed.' `
                -Outcome $(if ($nodeState -eq 'Allowed') { 'pass' } else { 'fail' }) -Detail ('They read ' + $nodeState + '.')

            Write-Section -Run $run -Title 'Handsfree protection'
            Write-Line -Run $run -Text ('Protection reads ' + $protection + '. Earshot ships with it on.')
            $want = Read-Answer -Run $run -Question 'Which state do you want to finish in?' -Options @('on', 'off', 'leave')
            if ($want -eq 'on')
            {
                $applied = Invoke-Earshot -Run $run -Label 'gate-protect-on' -Command @('diag', 'gate', 'protect-on') -Live `
                    -Consequence 'Turns the Handsfree and Headset services off on the AirPods through the elevated task, which also turns off their microphone on this PC. It can take minutes.' `
                    -TimeoutSeconds 420
                [void]$applied
            }
            elseif ($want -eq 'off')
            {
                $applied = Invoke-Earshot -Run $run -Label 'gate-protect-off' -Command @('diag', 'gate', 'protect-off') -Live `
                    -Consequence 'Turns the Handsfree and Headset services back on, which reinstalls their profile drivers. It can take minutes.' `
                    -TimeoutSeconds 420
                [void]$applied
            }

            $services = Get-ServiceState -Run $run -Label 'services-after'
            $protection = Get-Field -Object $services -Name 'protection'
            $outcome = 'pass'
            if ($want -eq 'on' -and $protection -ne 'Protected') { $outcome = 'fail' }
            if ($want -eq 'off' -and $protection -ne 'NotProtected') { $outcome = 'fail' }
            Add-Criterion -Run $run -Id 'protection' -Criterion 'Handsfree ends in the state you asked for.' `
                -Outcome $outcome -Detail ('You asked for ' + $want + '; it reads ' + $protection + '.')
        }

        Write-Section -Run $run -Title 'The state it finished in'
        [void](Get-AudioState -Run $run -Label 'audio-after')
        [void](Get-NodeState -Run $run -Label 'nodes-final')
        Save-EarshotLog -Run $run

        if ($OfferUninstall)
        {
            Write-Section -Run $run -Title 'Uninstall'
            Write-Line -Run $run -Text 'Uninstall allows the nodes, turns the services back on, removes the tasks and removes both Earshot folders.'
            Write-Line -Run $run -Text 'It needs one administrator prompt, which only you can approve.'
            $exe = Resolve-EarshotExe -ExePath $ExePath -RequireRelease
            Write-Line -Run $run -Text ('It would run: ' + $exe + ' uninstall')
            $removed = Invoke-EarshotElevated -Run $run -Label 'uninstall' -Command @('uninstall') `
                -Consequence 'Removes Earshot from this machine and puts back everything it changed.'
            if ($null -ne $removed)
            {
                Add-Criterion -Run $run -Id 'uninstall' -Criterion 'Uninstall reports success.' `
                    -Outcome $(if ($removed.exitCode -eq 0) { 'pass' } else { 'fail' }) `
                    -Detail ('exit ' + $removed.exitCode + ' (' + $removed.exitName + ').')
            }
        }
    }
}
catch
{
    Write-Failure -Run $run -Message ('The restore stopped with an error: ' + ($_ | Out-String).Trim())
    Add-Criterion -Run $run -Id 'run' -Criterion 'The restore ran to the end.' -Outcome 'fail' -Detail 'See the error above.'
}
finally
{
    $overall = Complete-LiveTestRun -Run $run
    Write-Host ('Restore finished: ' + $overall)
}
