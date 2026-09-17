<#
.SYNOPSIS
    Does uninstall put everything back, and does install set it up again cleanly?

.DESCRIPTION
    Uninstall has to undo every machine-wide change Earshot made: enable the nodes
    it disabled, turn the Bluetooth services back on that it turned off, remove the
    three scheduled tasks and the task folder, remove the hardened folder under
    ProgramData, and remove the program folder. Run from the installed copy it
    cannot delete its own files at once, so it schedules them for the next restart
    instead.

    This test blocks and protects first, so uninstall has something real to reverse,
    then uninstalls, then checks each of those, then installs again so the machine is
    left working. The install half exercises the copy from the release folder, the
    hash check against the publish manifest, the folder permissions and their
    read-back, and the task registration and its read-back.

    Both halves need an administrator prompt, which only you can approve.

.PARAMETER ExePath
    Earshot.exe. Install and uninstall only run from a release build: the installed
    copy under Program Files, or an unzipped release folder. A build output folder
    is refused here, as install itself would refuse it.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the restart, to check the delayed deletions happened.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\15-UninstallReversal.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

if ($Resume -and [string]::IsNullOrEmpty($RunRoot))
{
    throw 'The second half needs -RunRoot, the folder the first half printed. It is in resume.txt in that folder.'
}

$run = New-LiveTestRun -TestId '15-uninstall-reversal' -Title 'Uninstall reverses everything, then install sets it up again' `
    -Settles 'Whether uninstall really restores the nodes, the services, the tasks and the folders, and whether install from a release folder passes its own checks.' `
    -ExePath $ExePath -RunRoot $RunRoot -RequireRelease

function Show-Folders
{
    param([Parameter(Mandatory = $true)]$Run)

    $program = Join-Path $env:ProgramFiles 'Earshot'
    $data = $Run.MachineFolder
    Write-Line -Run $Run -Text ('  ' + $program + ' exists: ' + (Test-Path -LiteralPath $program))
    Write-Line -Run $Run -Text ('  ' + $data + ' exists: ' + (Test-Path -LiteralPath $data))
    return [ordered]@{ Program = $program; Data = $data; ProgramExists = (Test-Path -LiteralPath $program); DataExists = (Test-Path -LiteralPath $data) }
}

try
{
    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed and set up.',
            'You have the release folder or the installed copy to hand, because install is run again at the end.',
            'The Earshot tray is closed.',
            'The AirPods are paired with this PC.'
        ) -PhysicalActions @(
            'Approve two or three administrator prompts when they appear. Nothing else is physical.',
            'Keep the AirPods on your phone; the nodes are blocked and then allowed during this test.'
        )

        if ($ready)
        {
            Write-Section -Run $run -Title 'Give uninstall something to reverse'
            $nodes = Get-NodeState -Run $run -Label 'nodes-start'
            $Address = ('' + (Get-Field -Object $nodes -Name 'address')).ToUpperInvariant()
            $Container = '' + (Get-Field -Object $nodes -Name 'container')
            $Sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
            Write-Line -Run $run -Text ('Account ' + $Sid + ', device ' + $Address + ', container ' + $Container)
            Set-Content -LiteralPath (Join-Path $run.Folder 'identity.txt') -Value ([string]$Sid + "`r`n" + $Address + "`r`n" + $Container) -Encoding UTF8

            if ((Get-Field -Object $nodes -Name 'nodeState') -ne 'Blocked')
            {
                [void](Invoke-Earshot -Run $run -Label 'gate-block' -Command @('diag', 'gate', 'block') -Live `
                    -Consequence 'Disables the AirPods Bluetooth nodes, so uninstall has nodes to enable again.')
            }

            $services = Get-ServiceState -Run $run -Label 'services-start'
            if ((Get-Field -Object $services -Name 'protection') -ne 'Protected')
            {
                [void](Invoke-Earshot -Run $run -Label 'gate-protect-on' -Command @('diag', 'gate', 'protect-on') -Live `
                    -Consequence 'Turns the Handsfree and Headset services off, so uninstall has services to turn back on. It can take minutes.' `
                    -TimeoutSeconds 420)
            }

            Write-Section -Run $run -Title 'Before uninstall'
            $nodesBefore = Get-NodeState -Run $run -Label 'nodes-before-uninstall'
            $servicesBefore = Get-ServiceState -Run $run -Label 'services-before-uninstall'
            $tasksBefore = Get-TaskState -Run $run -Label 'task-before-uninstall'
            $foldersBefore = Show-Folders -Run $run
            Write-Line -Run $run -Text ('Nodes ' + (Get-Field -Object $nodesBefore -Name 'nodeState') +
                ', protection ' + (Get-Field -Object $servicesBefore -Name 'protection') +
                ', set up ' + (Get-Field -Object $tasksBefore -Name 'setUp'))

            $fromInstalled = $run.ExePath.ToLowerInvariant().StartsWith($foldersBefore.Program.ToLowerInvariant())
            Add-Finding -Run $run -Name 'uninstallRunFrom' -Value $(if ($fromInstalled) { 'the installed copy' } else { 'a release folder outside the install folder' }) `
                -Detail 'run from the installed copy, the files are scheduled for deletion at the next restart instead of removed at once'

            Write-Section -Run $run -Title 'Uninstall'
            $removed = Invoke-EarshotElevated -Run $run -Label 'uninstall' -Command @('uninstall') `
                -Consequence 'Enables the AirPods nodes, turns the Bluetooth services back on, removes the three scheduled tasks and both Earshot folders.'

            if ($null -eq $removed)
            {
                Add-Criterion -Run $run -Id 'uninstall' -Criterion 'Uninstall ran.' -Outcome 'inconclusive' -Detail 'It was skipped.'
            }
            else
            {
                Add-Criterion -Run $run -Id 'uninstall' -Criterion 'Uninstall reports success.' `
                    -Outcome $(if ($removed.exitCode -eq 0) { 'pass' } else { 'fail' }) `
                    -Detail ('exit ' + $removed.exitCode + ' (' + $removed.exitName + ').')

                $nodesAfter = Get-NodeState -Run $run -Label 'nodes-after-uninstall'
                $servicesAfter = Get-ServiceState -Run $run -Label 'services-after-uninstall'
                $tasksAfter = Get-TaskState -Run $run -Label 'task-after-uninstall'
                $foldersAfter = Show-Folders -Run $run

                Add-Criterion -Run $run -Id 'nodes-restored' -Criterion 'The Bluetooth nodes are enabled again.' `
                    -Outcome $(if ((Get-Field -Object $nodesAfter -Name 'nodeState') -eq 'Allowed') { 'pass' } else { 'fail' }) `
                    -Detail ('They read ' + (Get-Field -Object $nodesAfter -Name 'nodeState') + '.')

                Add-Criterion -Run $run -Id 'services-restored' -Criterion 'The Handsfree and Headset services are turned back on.' `
                    -Outcome $(if ((Get-Field -Object $servicesAfter -Name 'protection') -eq 'NotProtected') { 'pass' } else { 'fail' }) `
                    -Detail ('Protection reads ' + (Get-Field -Object $servicesAfter -Name 'protection') + '.')

                Add-Criterion -Run $run -Id 'tasks-removed' -Criterion 'The scheduled tasks and the task folder are gone.' `
                    -Outcome $(if ((Get-Field -Object $tasksAfter -Name 'setUp') -ne $true) { 'pass' } else { 'fail' }) `
                    -Detail ('probe task reports set up: ' + (Get-Field -Object $tasksAfter -Name 'setUp') + '.')

                Add-Criterion -Run $run -Id 'data-folder-removed' -Criterion 'The ProgramData folder is gone.' `
                    -Outcome $(if (-not $foldersAfter.DataExists) { 'pass' } else { 'fail' }) `
                    -Detail ([string]$foldersAfter.Data + ' exists: ' + $foldersAfter.DataExists + '.')

                if ($fromInstalled)
                {
                    Add-Criterion -Run $run -Id 'program-folder' -Criterion 'The program folder is gone, or scheduled to go at the next restart.' `
                        -Outcome 'inconclusive' `
                        -Detail ('Uninstall ran from the installed copy, so the files are scheduled for deletion at the next restart. ' +
                            $foldersAfter.Program + ' exists: ' + $foldersAfter.ProgramExists + '. The second half checks it after the restart.')
                }
                else
                {
                    Add-Criterion -Run $run -Id 'program-folder' -Criterion 'The program folder is gone.' `
                        -Outcome $(if (-not $foldersAfter.ProgramExists) { 'pass' } else { 'fail' }) `
                        -Detail ([string]$foldersAfter.Program + ' exists: ' + $foldersAfter.ProgramExists + '.')
                }
            }

            Write-Section -Run $run -Title 'Install again, so the machine is left working'
            Write-Line -Run $run -Text 'Install copies only the files the publish manifest lists, checks each copy against its recorded'
            Write-Line -Run $run -Text 'hash, creates the ProgramData folder with its permissions and reads them back, then registers the'
            Write-Line -Run $run -Text 'three tasks and reads each one back. Any mismatch stops it.'
            if ([string]::IsNullOrEmpty($Address) -or [string]::IsNullOrEmpty($Container))
            {
                Add-Criterion -Run $run -Id 'install-again' -Criterion 'Install sets the machine up again.' -Outcome 'inconclusive' `
                    -Detail 'The device address or container could not be read before the uninstall, so install cannot be called from here. Start Earshot and use its setup instead.'
            }
            else
            {
                $installed = Invoke-EarshotElevated -Run $run -Label 'install' -Command @('install', $Sid, $Address, $Container) `
                    -Consequence 'Installs Earshot again for this account and this device, and registers the three scheduled tasks.'
                if ($null -ne $installed)
                {
                    $tasksNow = Get-TaskState -Run $run -Label 'task-after-install'
                    $foldersNow = Show-Folders -Run $run
                    Add-Criterion -Run $run -Id 'install-again' -Criterion 'Install sets the machine up again and its own read-back checks pass.' `
                        -Outcome $(if ($installed.exitCode -eq 0 -and (Get-Field -Object $tasksNow -Name 'setUp') -eq $true) { 'pass' } else { 'fail' }) `
                        -Detail ('exit ' + $installed.exitCode + ' (' + $installed.exitName + '), set up ' + (Get-Field -Object $tasksNow -Name 'setUp') +
                            ', program folder ' + $foldersNow.ProgramExists + ', data folder ' + $foldersNow.DataExists + '.')
                    Add-Finding -Run $run -Name 'installExit' -Value ($installed.exitCode.ToString() + ' (' + $installed.exitName + ')')
                }
            }

            Save-EarshotLog -Run $run
            Write-Section -Run $run -Title 'If the files were scheduled for deletion'
            Write-Line -Run $run -Text 'Restart the machine yourself and run the command below, to confirm the delayed deletion happened.'
            Write-Line -Run $run -Text 'If uninstall ran from a release folder outside Program Files, there is nothing left to check.'
            Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
        }
    }
    else
    {
        Write-Section -Run $run -Title 'After the restart'
        $folders = Show-Folders -Run $run
        $tasks = Get-TaskState -Run $run -Label 'task-after-restart'
        Write-Line -Run $run -Text ('Set up: ' + (Get-Field -Object $tasks -Name 'setUp'))
        Write-Line -Run $run -Text 'If Earshot was installed again at the end of the first half, both folders exist and that is right.'
        Write-Line -Run $run -Text 'If it was not, neither folder should be left.'
        $reinstalled = Read-Answer -Run $run -Question 'Did the first half install Earshot again at the end?'
        if ($reinstalled -eq 'no')
        {
            Add-Criterion -Run $run -Id 'delayed-deletion' -Criterion 'The files scheduled for deletion are gone after the restart.' `
                -Outcome $(if (-not $folders.ProgramExists -and -not $folders.DataExists) { 'pass' } else { 'fail' }) `
                -Detail ('program folder exists ' + $folders.ProgramExists + ', data folder exists ' + $folders.DataExists + '.')
        }
        else
        {
            Add-Criterion -Run $run -Id 'delayed-deletion' -Criterion 'The install that followed the uninstall survived the restart.' `
                -Outcome $(if ($folders.ProgramExists -and $folders.DataExists -and (Get-Field -Object $tasks -Name 'setUp') -eq $true) { 'pass' } else { 'fail' }) `
                -Detail ('program folder exists ' + $folders.ProgramExists + ', data folder exists ' + $folders.DataExists +
                    ', set up ' + (Get-Field -Object $tasks -Name 'setUp') + '. A missing file here would mean the delayed deletion removed what install had just written.')
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
    Write-Host ('Test 15 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
