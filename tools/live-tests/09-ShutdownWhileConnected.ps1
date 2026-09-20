<#
.SYNOPSIS
    Shut down while the AirPods are on this PC, and see what the next boot does.

.DESCRIPTION
    Earshot keeps the nodes enabled while the AirPods are in use, so a shutdown that
    happens at that moment leaves them enabled unless the session-end backstop gets
    its block through in time. Windows gives an application about five seconds after
    the end-session message, and a forced shutdown skips the query altogether.

    This test sets that case up deliberately: connect, shut down at once, power back
    on, and read whether the nodes came back enabled and whether Windows paged the
    AirPods off the phone at boot.

    It settles whether the pre-shutdown SYSTEM service, which is designed but held
    back for v1.1, is actually needed.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the power cycle.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\09-ShutdownWhileConnected.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '09-shutdown-while-connected' -Title 'Shutdown while connected, then the next boot' `
    -Settles 'Whether the v1.1 pre-shutdown service is needed, by showing what happens when the machine is shut down with the nodes still enabled.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Set below, in the first half only: this whole test is about shutting down with the nodes still
# enabled, so the closing at-rest check must not offer to block them before that shutdown happens.
$atRestReason = ''

try
{
    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed, set up, and running in the tray.',
            'Block at boot is on.',
            'The AirPods are paired with this PC and available to connect.'
        ) -PhysicalActions @(
            'Connect the AirPods to this PC with a left click on the tray icon and keep audio playing from this PC.',
            'Shut down at once, while they are still connected and playing. Do not wait for the icon to change back.',
            'Wait about ten seconds with the machine off, start it again, log in, and run the command this half prints.'
        )

        if ($ready)
        {
            # Only now, with the owner committed to going ahead: nothing has shut down yet if they
            # said no above, so the closing check must still offer a block in that case, not read
            # a shutdown as deliberately pending that is never actually going to happen.
            $atRestReason = 'This half deliberately shuts down with the AirPods still connected (the nodes enabled): that is ' +
                'the case it exists to catch, showing whether the session-end backstop or the boot task blocks them again.'

            Write-Section -Run $run -Title 'Connect and confirm'
            Wait-Owner -Run $run -Text 'Left-click the tray icon to connect the AirPods to this PC, and play something so they stay in use.'
            $audio = Get-AudioState -Run $run -Label 'audio-connected'
            $states = Get-TargetEndpointStates -AudioJson $audio
            $nodes = Get-NodeState -Run $run -Label 'nodes-connected'
            Write-Line -Run $run -Text ('Render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }) + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState'))

            Add-Criterion -Run $run -Id 'connected-first' -Criterion 'The AirPods are connected to this PC and the nodes are enabled before the shutdown.' `
                -Outcome $(if ($states.Render -eq 'Active' -and (Get-Field -Object $nodes -Name 'nodeState') -eq 'Allowed') { 'pass' } else { 'inconclusive' }) `
                -Detail ('render ' + $states.Render + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState') + '. This is the state the shutdown has to be started from.')

            Add-Finding -Run $run -Name 'blockAtBootAtShutdown' -Value (Get-BlockAtBootSetting -Run $run)

            # This half shuts the machine down rather than restarting it, so Fast Startup applies to it.
            # It is read, never changed, so the evidence says which kind of shutdown this run was.
            Add-Finding -Run $run -Name 'fastStartupAtShutdown' -Value (Get-FastStartupSetting -Run $run) `
                -Detail 'HiberbootEnabled under HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power, read before the shutdown'
            Save-EarshotLog -Run $run

            Write-Section -Run $run -Title 'Now shut down, straight away'
            Write-Line -Run $run -Text 'Shut down from the Start menu while the audio is still playing on this PC. Do not restart,'
            Write-Line -Run $run -Text 'and do not disconnect first. The point is to catch Earshot with the nodes enabled.'
            Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
        }
    }
    else
    {
        Write-Section -Run $run -Title 'After the boot'
        $nodes = Get-NodeState -Run $run -Label 'nodes-after-boot'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $audio = Get-AudioState -Run $run -Label 'audio-after-boot'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Nodes ' + $nodeState + ', render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }))

        $paged = Read-Answer -Run $run -Question 'After this boot, did the AirPods connect to this PC by themselves (taking them off your phone)?'
        Add-Criterion -Run $run -Id 'not-paged-at-boot' -Criterion 'Windows did not page the AirPods at the boot after a shutdown while connected.' `
            -Outcome $(if ($paged -eq 'no' -and $states.Render -ne 'Active') { 'pass' } elseif ($paged -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $paged + '; the render endpoint reads ' + $states.Render + '.')

        Add-Criterion -Run $run -Id 'nodes-after-boot' -Criterion 'The nodes are blocked again after the boot.' `
            -Outcome $(if ($nodeState -eq 'Blocked') { 'pass' } else { 'fail' }) `
            -Detail ('They read ' + $nodeState + '. Blocked means either the session-end backstop got through, or the boot task did.')

        Write-Section -Run $run -Title 'What the log says about the shutdown'
        $queued = Get-EarshotLogLines -Run $run -Pattern 'Session ending: block queued at'
        $endSession = Get-EarshotLogLines -Run $run -Pattern 'WM_ENDSESSION received'
        $querySession = Get-EarshotLogLines -Run $run -Pattern 'WM_QUERYENDSESSION received'
        foreach ($line in ($querySession | Select-Object -Last 3)) { Write-Line -Run $run -Text ('  ' + $line) }
        foreach ($line in ($endSession | Select-Object -Last 3)) { Write-Line -Run $run -Text ('  ' + $line) }
        foreach ($line in ($queued | Select-Object -Last 3)) { Write-Line -Run $run -Text ('  ' + $line) }

        Add-Criterion -Run $run -Id 'end-session-logged' -Criterion 'The end-session messages reached Earshot and were logged with their flags.' `
            -Outcome $(if (@($querySession).Count -gt 0 -or @($endSession).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($querySession).Count + ' query lines, ' + @($endSession).Count + ' end lines.')

        Add-Finding -Run $run -Name 'sessionEndBlockQueued' -Value $(if (@($queued).Count -gt 0) { 'yes' } else { 'no' })
        Add-Finding -Run $run -Name 'preShutdownServiceNeeded' `
            -Value $(if ($nodeState -eq 'Blocked' -and $paged -eq 'no') { 'no' } else { 'yes' }) `
            -Detail 'yes means the v1.1 pre-shutdown SYSTEM service should be built, because the best-effort hook did not hold'

        $boot = Get-EarshotLogLines -Run $run -Pattern 'boot'
        foreach ($line in ($boot | Select-Object -Last 8)) { Write-Line -Run $run -Text ('  ' + $line) }
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
    $overall = Complete-LiveTestRun -Run $run -AtRestReason $atRestReason
    Write-Host ('Test 09 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
