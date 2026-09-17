<#
.SYNOPSIS
    Can the non-elevated tray start the SYSTEM task, and do its arguments arrive?

.DESCRIPTION
    Earshot never elevates itself. It asks Task Scheduler to start a task that
    already runs as SYSTEM, which is allowed because setup writes an explicit
    0x1200a9 entry for this user into the task's security descriptor. Whether that
    really lets a standard token call IRegisteredTask::RunEx, and whether the
    $(Arg0) to $(Arg2) placeholders reach the gate as separate arguments, is not
    documented anywhere. If it does not work, the tray cannot block or allow at all.

    The test reads the task security descriptor without elevation, starts the gate
    with the harmless status verb, and checks that the status file the gate wrote
    carries the same one-time number the tray generated. It also records whether
    LastTaskResult equals the gate's own exit code, which the completion poll uses.

    If that fails, it offers plan B: reinstall with --principal user, so the two
    tasks run elevated as you instead of as SYSTEM, and try again. Plan B is a
    fallback, not a preference: a task that runs with your own environment can be
    influenced by anything else running as you, so it is only used if SYSTEM does
    not work.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release. Plan B reinstalls, so a
    build output folder is refused.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.PARAMETER AllowPlanB
    Offer the reinstall with --principal user when the SYSTEM tasks do not work.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\07-TaskRunEx.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [switch]$AllowPlanB
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

$run = New-LiveTestRun -TestId '07-task-runex' -Title 'Non-elevated RunEx of the SYSTEM tasks, and plan B' `
    -Settles 'Whether the tray can start the elevated tasks at all, how Task Scheduler passes the arguments, and whether the --principal user plan B is needed.' `
    -ExePath $ExePath -RunRoot $RunRoot

function Show-Tasks
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $task = Get-TaskState -Run $Run -Label $Label
    $rows = @()
    foreach ($row in (Get-Field -Object $task -Name 'tasks'))
    {
        $line = [string](Get-Field -Object $row -Name 'path') + ': present ' + (Get-Field -Object $row -Name 'present') +
            ', principal ' + (Get-Field -Object $row -Name 'userId') +
            ', logon ' + (Get-Field -Object $row -Name 'logonType') +
            ', run level ' + (Get-Field -Object $row -Name 'runLevel') +
            ', this user may start it ' + (Get-Field -Object $row -Name 'trayMayRun') +
            ', mask ' + (Get-Field -Object $row -Name 'userMask') +
            ', last result ' + (Get-Field -Object $row -Name 'lastTaskResult')
        $rows = $rows + @($line)
        Write-Line -Run $Run -Text ('  ' + $line)
    }

    return [ordered]@{ SetUp = (Get-Field -Object $task -Name 'setUp'); Rows = $rows; Json = $task }
}

function Test-MayRunBoth
{
    param($TaskJson)

    $gate = $false
    $protect = $false
    foreach ($row in (Get-Field -Object $TaskJson -Name 'tasks'))
    {
        $path = Get-Field -Object $row -Name 'path'
        if ($path -match 'Gate$' -and (Get-Field -Object $row -Name 'trayMayRun') -eq $true) { $gate = $true }
        if ($path -match 'Protect$' -and (Get-Field -Object $row -Name 'trayMayRun') -eq $true) { $protect = $true }
    }

    return ($gate -and $protect)
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up.',
        'This window is a normal window, not an administrator one. The whole point is what a standard token can do.',
        'The Earshot tray is closed, so nothing else starts the tasks while this runs.'
    ) -PhysicalActions @(
        'Nothing physical unless plan B runs, which needs you to approve two administrator prompts.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'What the tasks look like from here'
        $tasks = Show-Tasks -Run $run -Label 'task-before'
        $mayRun = Test-MayRunBoth -TaskJson $tasks.Json
        Add-Criterion -Run $run -Id 'setup' -Criterion 'The three tasks are registered and pass their own checks.' `
            -Outcome $(if ($tasks.SetUp -eq $true) { 'pass' } else { 'fail' }) -Detail ('probe task reports set up: ' + $tasks.SetUp + '.')
        Add-Criterion -Run $run -Id 'ace-present' -Criterion 'The security descriptor says this user may start the Gate and Protect tasks.' `
            -Outcome $(if ($mayRun) { 'pass' } else { 'fail' }) -Detail 'This is the read-only half. The next step is whether it actually works.'

        Write-Section -Run $run -Title 'Starting the Gate task with the status verb'
        Write-Line -Run $run -Text 'status changes nothing. It is the safest verb to prove the round trip with.'
        $status = Invoke-Earshot -Run $run -Label 'gate-status' -Command @('diag', 'gate', 'status') -Live `
            -Consequence 'Starts the SYSTEM Gate task with the status verb. It reads the node state and writes a status file. It changes no device.'

        if ($null -eq $status)
        {
            Add-Criterion -Run $run -Id 'runex' -Criterion 'A non-elevated RunEx starts the SYSTEM Gate task.' `
                -Outcome 'inconclusive' -Detail 'The step was skipped.'
        }
        else
        {
            $evidence = Get-DiagEvidence -Run $run
            $outcome = Get-Field -Object $evidence -Name 'outcome'
            $nonce = Get-Field -Object $evidence -Name 'nonce'
            $statusFile = Get-Field -Object $evidence -Name 'statusFile'
            $lastResult = Get-Field -Object $evidence -Name 'lastTaskResultCode'
            $statusExit = Get-Field -Object $statusFile -Name 'exitCode'
            Write-Line -Run $run -Text ('  outcome ' + $outcome + ', nonce ' + $nonce + ', ran for ' + (Get-Field -Object $evidence -Name 'runMilliseconds') + ' ms')
            Write-Line -Run $run -Text ('  status file: result ' + (Get-Field -Object $statusFile -Name 'result') + ', exit ' + $statusExit)
            Write-Line -Run $run -Text ('  LastTaskResult from the scheduler: ' + $lastResult)

            Add-Criterion -Run $run -Id 'runex' -Criterion 'A non-elevated RunEx starts the SYSTEM Gate task.' `
                -Outcome $(if ($outcome -eq 'Completed') { 'pass' } else { 'fail' }) -Detail ('outcome ' + $outcome + '.')

            Add-Criterion -Run $run -Id 'arguments-arrive' -Criterion 'The verb and the one-time number reach the gate as separate arguments.' `
                -Outcome $(if ($null -ne $statusFile) { 'pass' } else { 'fail' }) `
                -Detail 'The gate writes its status file under the number the tray generated, so a file at that name proves both arguments arrived.'

            if ($null -ne $lastResult -and $null -ne $statusExit)
            {
                Add-Criterion -Run $run -Id 'lasttaskresult' -Criterion 'LastTaskResult equals the exit code the gate recorded.' `
                    -Outcome $(if ([int]$lastResult -eq [int]$statusExit) { 'pass' } else { 'fail' }) `
                    -Detail ('LastTaskResult ' + $lastResult + ', gate exit ' + $statusExit + '. Earshot never trusts it alone, but the completion poll uses it.')
                Add-Finding -Run $run -Name 'lastTaskResultMatchesGateExit' -Value $(if ([int]$lastResult -eq [int]$statusExit) { 'yes' } else { 'no' })
            }

            Write-Section -Run $run -Title 'The third argument'
            Write-Line -Run $run -Text 'Only set-device carries an address. For every other verb Task Scheduler is left with nothing to put'
            Write-Line -Run $run -Text 'in $(Arg2), and what it does then is undocumented: it may pass the literal placeholder, an empty'
            Write-Line -Run $run -Text 'argument, or nothing at all. Earshot treats the literal placeholder as empty either way.'
            Write-Line -Run $run -Text 'The gate log for the run above shows which of the three happened. Look for the rejected or accepted'
            Write-Line -Run $run -Text 'line naming the arguments it received.'
            $gateLines = Get-EarshotLogLines -Run $run -Pattern 'args:'
            foreach ($line in ($gateLines | Select-Object -Last 5)) { Write-Line -Run $run -Text ('  ' + $line) }
            $argNote = Read-Note -Run $run -Question 'From the gate log, how did the unsupplied third argument arrive (literal placeholder, empty, or not at all)?'
            if (-not [string]::IsNullOrEmpty($argNote)) { Add-Finding -Run $run -Name 'unsuppliedThirdArgument' -Value $argNote }

            Write-Section -Run $run -Title 'A verb that writes: setboot'
            Write-Line -Run $run -Text 'status only reads. setboot-off and setboot-on write the SYSTEM-owned config file, so they show'
            Write-Line -Run $run -Text 'that a verb started this way really reaches the gate and takes effect. No device node is touched.'
            $blockAtBootBefore = Get-BlockAtBootSetting -Run $run
            Write-Line -Run $run -Text ('Block at boot reads ' + $blockAtBootBefore + ' before the change.')
            $off = Invoke-Earshot -Run $run -Label 'gate-setboot-off' -Command @('diag', 'gate', 'setboot-off') -Live `
                -Consequence 'Turns Block at boot off in the machine configuration. No device node is changed by this verb, and the next step turns it back on.'
            if ($null -ne $off)
            {
                $afterOff = Get-BlockAtBootSetting -Run $run
                Write-Line -Run $run -Text ('Block at boot now reads ' + $afterOff + '.')
                Add-Criterion -Run $run -Id 'setboot-round-trip' -Criterion 'A verb sent through RunEx changes the machine configuration and the change can be read back.' `
                    -Outcome $(if ($afterOff -eq $false) { 'pass' } else { 'fail' }) -Detail ('It reads ' + $afterOff + ' after setboot-off.')

                $on = Invoke-Earshot -Run $run -Label 'gate-setboot-on' -Command @('diag', 'gate', 'setboot-on') -Live `
                    -Consequence 'Turns Block at boot back on, returning the machine to how it ships.'
                if ($null -ne $on)
                {
                    $afterOn = Get-BlockAtBootSetting -Run $run
                    Add-Criterion -Run $run -Id 'setboot-restored' -Criterion 'Block at boot is left on, as Earshot ships.' `
                        -Outcome $(if ($afterOn -eq $true) { 'pass' } else { 'fail' }) -Detail ('It reads ' + $afterOn + '.')
                }
            }

            if (-not $mayRun -or $outcome -ne 'Completed')
            {
                Write-Section -Run $run -Title 'Plan B'
                Write-Line -Run $run -Text 'The SYSTEM tasks did not work from this token, so plan B is what the design kept in reserve:'
                Write-Line -Run $run -Text 'register Gate and Protect for your own account, elevated, keeping the same security descriptor.'
                Write-Line -Run $run -Text 'The boot task stays SYSTEM either way.'
                Write-Line -Run $run -Text 'The cost is real: those tasks would then run with your own environment, which anything running as'
                Write-Line -Run $run -Text 'you can change, so only use it if SYSTEM is genuinely unusable.'
                Add-Finding -Run $run -Name 'planBNeeded' -Value 'yes' -Detail 'the SYSTEM tasks could not be started from the non-elevated token'

                if ($AllowPlanB)
                {
                    $nodes = Get-NodeState -Run $run -Label 'nodes-for-install'
                    $Address = ('' + (Get-Field -Object $nodes -Name 'address')).ToUpperInvariant()
                    $Container = '' + (Get-Field -Object $nodes -Name 'container')
                    $Sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
                    Write-Line -Run $run -Text ('Account ' + $Sid + ', device ' + $Address + ', container ' + $Container)

                    if ([string]::IsNullOrEmpty($Address) -or [string]::IsNullOrEmpty($Container))
                    {
                        Add-Criterion -Run $run -Id 'planb' -Criterion 'Plan B registers the tasks for this user.' -Outcome 'inconclusive' `
                            -Detail 'The pinned device address or container could not be read, so install cannot be called.'
                    }
                    else
                    {
                        [void](Invoke-EarshotElevated -Run $run -Label 'uninstall-before-planb' -Command @('uninstall') `
                            -Consequence 'Removes the current SYSTEM tasks and folders before the tasks are registered the other way.')
                        $installed = Invoke-EarshotElevated -Run $run -Label 'install-principal-user' `
                            -Command @('install', $Sid, $Address, $Container, '--principal', 'user') `
                            -Consequence 'Installs again and registers the Gate and Protect tasks to run elevated as you rather than as SYSTEM.'
                        if ($null -ne $installed)
                        {
                            $tasksAfter = Show-Tasks -Run $run -Label 'task-after-planb'
                            $statusB = Invoke-Earshot -Run $run -Label 'gate-status-planb' -Command @('diag', 'gate', 'status') -Live `
                                -Consequence 'Starts the Gate task again, now registered for your account, with the status verb. It changes no device.'
                            $outcomeB = 'skipped'
                            if ($null -ne $statusB)
                            {
                                $evidenceB = Get-DiagEvidence -Run $run
                                $outcomeB = '' + (Get-Field -Object $evidenceB -Name 'outcome')
                            }

                            Add-Criterion -Run $run -Id 'planb' -Criterion 'Plan B lets the tray start the tasks.' `
                                -Outcome $(if ($outcomeB -eq 'Completed') { 'pass' } else { 'fail' }) `
                                -Detail ('install exit ' + $installed.exitCode + ' (' + $installed.exitName + '), gate outcome ' + $outcomeB +
                                    ', set up ' + $tasksAfter.SetUp + '.')
                            Add-Finding -Run $run -Name 'planBWorks' -Value $(if ($outcomeB -eq 'Completed') { 'yes' } else { 'no' })

                            Write-Section -Run $run -Title 'Put the shipping registration back'
                            Write-Line -Run $run -Text 'The Gate and Protect tasks now run as you, which is not how Earshot ships, and they stay that way until'
                            Write-Line -Run $run -Text 'they are registered again. Before any other test, uninstall and install again without --principal user:'
                            Write-Line -Run $run -Text ('  "' + $run.ExePath + '" uninstall')
                            Write-Line -Run $run -Text ('  "' + $run.ExePath + '" install ' + $Sid + ' ' + $Address + ' ' + $Container)
                            Write-Line -Run $run -Text 'Both need an administrator window. 00-Restore.ps1 does not undo a plan B install.'
                        }
                    }
                }
                else
                {
                    Add-Criterion -Run $run -Id 'planb' -Criterion 'Plan B was tried.' -Outcome 'inconclusive' `
                        -Detail 'Run this test again with -AllowPlanB to try the reinstall with --principal user.'
                }
            }
            else
            {
                Add-Finding -Run $run -Name 'planBNeeded' -Value 'no' -Detail 'the SYSTEM tasks started from the non-elevated token'
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
    Write-Host ('Test 07 finished: ' + $overall)
}
