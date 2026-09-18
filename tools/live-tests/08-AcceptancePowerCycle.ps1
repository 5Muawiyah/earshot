<#
.SYNOPSIS
    The acceptance test: after a full power cycle, the AirPods are still on the phone.

.DESCRIPTION
    This is the one test the whole application exists for, and it runs in the
    configuration Earshot ships in: Block at boot on, Protect audio quality on.

    The first half checks the configuration, blocks the nodes, and stops. You then
    power the machine right down, not restart, wait, and turn it back on. The second
    half reads the nodes, reads the endpoints, asks whether the AirPods stayed on
    your phone, and looks for a fresh enabled Handsfree node that protection may have
    re-added outside the disabled set. Finally it asks you to left-click the tray
    icon, so the allow and reconnect path is exercised once from the real user
    interface, with no administrator prompt.

    Every other test is secondary to this one.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the power cycle.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\08-AcceptancePowerCycle.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '08-acceptance-power-cycle' -Title 'Acceptance: full power cycle in the shipping configuration' `
    -Settles 'The single acceptance test for v1: after a full power cycle the AirPods stay connected to the phone.' `
    -ExePath $ExePath -RunRoot $RunRoot

function Measure-TargetNodes
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $nodes = Get-NodeState -Run $Run -Label $Label
    $counts = [ordered]@{
        State = (Get-Field -Object $nodes -Name 'nodeState'); Targets = 0; Disabled = 0
        HandsfreeEnabled = 0; HandsfreeIds = @()
    }

    foreach ($node in (Get-Field -Object $nodes -Name 'nodes'))
    {
        $instance = '' + (Get-Field -Object $node -Name 'instanceId')
        $disabled = (Get-Field -Object $node -Name 'configFlagsDisabled') -eq $true
        if ((Get-Field -Object $node -Name 'target') -eq $true)
        {
            $counts.Targets = $counts.Targets + 1
            if ($disabled) { $counts.Disabled = $counts.Disabled + 1 }
        }

        # Protection removes and re-adds the Handsfree service node, so a fresh one can appear
        # at boot outside the set that was persistently disabled.
        if ($instance -match '0000111E' -and -not $disabled)
        {
            $counts.HandsfreeEnabled = $counts.HandsfreeEnabled + 1
            $counts.HandsfreeIds = $counts.HandsfreeIds + @($instance)
        }

        Write-Line -Run $Run -Text ('  ' + $instance + ': target ' + (Get-Field -Object $node -Name 'target') +
            ', present ' + (Get-Field -Object $node -Name 'present') +
            ', problem ' + (Get-Field -Object $node -Name 'problem') + ', disabled bit ' + $disabled)
    }

    Write-Line -Run $Run -Text ('  state ' + $counts.State + ': ' + $counts.Disabled + ' of ' + $counts.Targets +
        ' target nodes disabled, ' + $counts.HandsfreeEnabled + ' enabled Handsfree nodes')
    return $counts
}

try
{
    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed and set up, from a release build.',
            'Block at boot is on and Protect audio quality is on, which is how Earshot ships.',
            'The AirPods have been connected to this PC at least once, so their nodes exist.',
            'The AirPods are connected to your phone and you are listening to something on it.',
            'You are able to power this machine right down and start it again.'
        ) -PhysicalActions @(
            'Wear the AirPods and keep listening on your phone through the whole power cycle.',
            'Shut down, not restart. Wait about ten seconds with the machine off, then start it again.',
            'After logging back in, run the command this half prints.'
        )

        if ($ready)
        {
            Write-Section -Run $run -Title 'The configuration this is being tested in'
            $blockAtBoot = Get-BlockAtBootSetting -Run $run
            $protectAudio = Get-ProtectAudioSetting -Run $run
            $tasks = Get-TaskState -Run $run -Label 'task-before'
            $services = Get-ServiceState -Run $run -Label 'services-before'
            $protection = Get-Field -Object $services -Name 'protection'
            Write-Line -Run $run -Text ('Block at boot: ' + $blockAtBoot + '. Protect audio quality (setting): ' + $protectAudio +
                '. Protection (device): ' + $protection + '. Set up: ' + (Get-Field -Object $tasks -Name 'setUp') + '.')
            Add-Finding -Run $run -Name 'configurationUnderTest' -Value ('BlockAtBoot=' + $blockAtBoot + ', ProtectAudioQuality=' + $protectAudio + ', protection=' + $protection)

            # Fast Startup only applies to a shutdown, never to a restart, so this test is one of the
            # two whose evidence can say anything about it. It is read, not changed: the setting stays
            # whatever the machine is set to, and the run records which that was.
            $fastStartup = Get-FastStartupSetting -Run $run
            Write-Line -Run $run -Text ('Fast Startup: ' + $fastStartup + '. This power cycle is the one that tests it.')
            Add-Finding -Run $run -Name 'fastStartupAtPowerDown' -Value $fastStartup `
                -Detail 'HiberbootEnabled under HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power, read before the shutdown'

            Add-Criterion -Run $run -Id 'default-config' -Criterion 'The test runs in the shipping configuration: Block at boot on, Protect audio quality on.' `
                -Outcome $(if ($blockAtBoot -eq $true -and $protectAudio -eq $true) { 'pass' } else { 'inconclusive' }) `
                -Detail ('Block at boot ' + $blockAtBoot + ', Protect audio quality ' + $protectAudio + '. Anything else is a different test.')

            Write-Section -Run $run -Title 'Block'
            $before = Measure-TargetNodes -Run $run -Label 'nodes-before'
            if ($before.State -ne 'Blocked')
            {
                $block = Invoke-Earshot -Run $run -Label 'gate-block' -Command @('diag', 'gate', 'block') -Live `
                    -Consequence 'Disables the AirPods Bluetooth nodes so Windows cannot page them at the next boot. This is the block the acceptance test is about.'
                if ($null -ne $block) { $before = Measure-TargetNodes -Run $run -Label 'nodes-after-block' }
            }
            else
            {
                Write-Line -Run $run -Text 'They are already blocked, so nothing is changed.'
            }

            Add-Criterion -Run $run -Id 'blocked-before-power-cycle' -Criterion 'Every target node is disabled before the machine is powered down.' `
                -Outcome $(if ($before.Targets -gt 0 -and $before.Disabled -eq $before.Targets) { 'pass' } else { 'fail' }) `
                -Detail ([string]$before.Disabled + ' of ' + $before.Targets + ' are disabled, state ' + $before.State + '.')

            Save-EarshotLog -Run $run
            Write-Section -Run $run -Title 'Now power the machine down'
            Write-Line -Run $run -Text 'Shut down from the Start menu. Do not restart: a full power cycle is the test.'
            Write-Line -Run $run -Text 'Keep listening on your phone the whole time. That is the thing being measured.'
            Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
        }
    }
    else
    {
        Write-Section -Run $run -Title 'After the power cycle'
        $stayed = Read-Answer -Run $run -Question 'Did the AirPods stay connected to your phone across the whole power cycle, with no break in the audio?'
        Add-Criterion -Run $run -Id 'ACCEPTANCE' -Criterion 'After a full power cycle the AirPods are still connected to the phone.' `
            -Outcome $(if ($stayed -eq 'yes') { 'pass' } elseif ($stayed -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $stayed + '. This is the acceptance test for v1.')

        $after = Measure-TargetNodes -Run $run -Label 'nodes-after-power-cycle'
        Add-Criterion -Run $run -Id 'still-blocked' -Criterion 'Every target node is still disabled after the power cycle.' `
            -Outcome $(if ($after.Targets -gt 0 -and $after.Disabled -eq $after.Targets) { 'pass' } else { 'fail' }) `
            -Detail ([string]$after.Disabled + ' of ' + $after.Targets + ' are disabled, state ' + $after.State + '.')

        Add-Criterion -Run $run -Id 'no-fresh-handsfree-node' -Criterion 'No enabled Handsfree node appeared outside the disabled set.' `
            -Outcome $(if ($after.HandsfreeEnabled -eq 0) { 'pass' } else { 'fail' }) `
            -Detail $(if ($after.HandsfreeEnabled -eq 0) { 'None found.' } else { 'Found: ' + ($after.HandsfreeIds -join ', ') + '. Protection removes and re-adds that node, so a fresh one can escape the block.' })
        Add-Finding -Run $run -Name 'freshHandsfreeNodeAfterBoot' -Value $after.HandsfreeEnabled

        $audio = Get-AudioState -Run $run -Label 'audio-after-power-cycle'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Render ' + $states.Render + ', capture ' + $states.Capture + ', derived ' + $states.Connection)
        Add-Criterion -Run $run -Id 'not-taken-by-pc' -Criterion 'The AirPods are not connected to this PC after the boot.' `
            -Outcome $(if ($states.Render -ne 'Active') { 'pass' } else { 'fail' }) -Detail ('The render endpoint reads ' + $states.Render + '.')

        Write-Section -Run $run -Title 'The boot task and the start-up check'
        $bootLines = Get-EarshotLogLines -Run $run -Pattern 'boot'
        foreach ($line in ($bootLines | Select-Object -Last 10)) { Write-Line -Run $run -Text ('  ' + $line) }
        $startUpLines = Get-EarshotLogLines -Run $run -Pattern 'connected at start-up'
        if (@($startUpLines).Count -gt 0)
        {
            foreach ($line in $startUpLines) { Write-Line -Run $run -Text ('  ' + $line) }
            Add-Finding -Run $run -Name 'connectedAtStartUp' -Value 'yes' -Detail 'the tray saw the AirPods already on this PC when it started, so the block did not hold'
        }
        else
        {
            Add-Finding -Run $run -Name 'connectedAtStartUp' -Value 'no'
        }

        Write-Section -Run $run -Title 'Left click, end to end'
        Write-Line -Run $run -Text 'The last part is the headline feature: one left click on the tray icon, from blocked nodes, with'
        Write-Line -Run $run -Text 'no administrator prompt. Earshot allows the nodes, sends the reconnect and confirms from the'
        Write-Line -Run $run -Text 'notifications before the icon changes.'
        Wait-Owner -Run $run -Text 'Start Earshot if it is not running, then left-click its tray icon once and watch what happens.'
        $connected = Read-Answer -Run $run -Question 'Did the AirPods connect to this PC after that single click?'
        $prompted = Read-Answer -Run $run -Question 'Did any administrator prompt appear during that click?'
        $cardText = Read-Note -Run $run -Question 'What did the card near the tray say, word for word?'

        Add-Criterion -Run $run -Id 'left-click-connects' -Criterion 'One left click connects the AirPods from blocked nodes.' `
            -Outcome $(if ($connected -eq 'yes') { 'pass' } elseif ($connected -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $connected + '. The card said: ' + $cardText)
        Add-Criterion -Run $run -Id 'no-admin-prompt' -Criterion 'No administrator prompt appears for a connect.' `
            -Outcome $(if ($prompted -eq 'no') { 'pass' } elseif ($prompted -eq 'yes') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $prompted + '.')

        $audioAfterClick = Get-AudioState -Run $run -Label 'audio-after-click'
        $statesAfterClick = Get-TargetEndpointStates -AudioJson $audioAfterClick
        $nodesAfterClick = Get-NodeState -Run $run -Label 'nodes-after-click'
        Write-Line -Run $run -Text ('Render ' + $statesAfterClick.Render + ', nodes ' + (Get-Field -Object $nodesAfterClick -Name 'nodeState'))
        Add-Criterion -Run $run -Id 'click-agrees-with-endpoints' -Criterion 'What the click claimed matches what the endpoints say.' `
            -Outcome $(if (($connected -eq 'yes' -and $statesAfterClick.Render -eq 'Active') -or ($connected -eq 'no' -and $statesAfterClick.Render -ne 'Active')) { 'pass' } else { 'fail' }) `
            -Detail ('You said connected: ' + $connected + '; the render endpoint reads ' + $statesAfterClick.Render + '.')

        Write-Section -Run $run -Title 'Left click again, to disconnect'
        Write-Line -Run $run -Text 'The click is a toggle, so the other half of it has to be asked as well: a second click hands the'
        Write-Line -Run $run -Text 'AirPods back. Earshot disconnects and then blocks the nodes straight away, without waiting for the'
        Write-Line -Run $run -Text 'idle rule, so the nodes should read Blocked again within a few seconds and still no prompt.'
        if ($statesAfterClick.Render -eq 'Active')
        {
            Wait-Owner -Run $run -Text 'Left-click the tray icon once more, and watch the AirPods go back to the phone.'
            $disconnected = Read-Answer -Run $run -Question 'Did the AirPods disconnect from this PC after that single click?'
            $promptedOff = Read-Answer -Run $run -Question 'Did any administrator prompt appear during that second click?'
            $offCardText = Read-Note -Run $run -Question 'What did the card say that time, word for word?'

            Add-Criterion -Run $run -Id 'left-click-disconnects' -Criterion 'A second left click disconnects the AirPods.' `
                -Outcome $(if ($disconnected -eq 'yes') { 'pass' } elseif ($disconnected -eq 'no') { 'fail' } else { 'inconclusive' }) `
                -Detail ('You answered ' + $disconnected + '. The card said: ' + $offCardText)
            Add-Criterion -Run $run -Id 'no-admin-prompt-disconnect' -Criterion 'No administrator prompt appears for a disconnect either.' `
                -Outcome $(if ($promptedOff -eq 'no') { 'pass' } elseif ($promptedOff -eq 'yes') { 'fail' } else { 'inconclusive' }) `
                -Detail ('You answered ' + $promptedOff + '.')

            Wait-Seconds -Run $run -Seconds 15 -Reason 'letting the disconnect and the block that follows it finish'
            $audioAfterOff = Get-AudioState -Run $run -Label 'audio-after-second-click'
            $statesAfterOff = Get-TargetEndpointStates -AudioJson $audioAfterOff
            $nodesAfterOff = Get-NodeState -Run $run -Label 'nodes-after-second-click'
            $stateAfterOff = Get-Field -Object $nodesAfterOff -Name 'nodeState'
            Write-Line -Run $run -Text ('Render ' + $statesAfterOff.Render + ', nodes ' + $stateAfterOff)

            Add-Criterion -Run $run -Id 'disconnect-agrees-with-endpoints' -Criterion 'What the second click claimed matches what the endpoints say.' `
                -Outcome $(if (($disconnected -eq 'yes' -and $statesAfterOff.Render -ne 'Active') -or ($disconnected -eq 'no' -and $statesAfterOff.Render -eq 'Active')) { 'pass' } else { 'fail' }) `
                -Detail ('You said disconnected: ' + $disconnected + '; the render endpoint reads ' + $statesAfterOff.Render + '.')
            Add-Criterion -Run $run -Id 'blocked-again-after-click' -Criterion 'The nodes go back to Blocked after the disconnect, without waiting for the idle rule.' `
                -Outcome $(if ($stateAfterOff -eq 'Blocked') { 'pass' } else { 'fail' }) `
                -Detail ('The nodes read ' + [string]$stateAfterOff + ' about 15 s after the click.')

            $backOnPhone = Read-Answer -Run $run -Question 'Are the AirPods playing from the phone again now?'
            Add-Finding -Run $run -Name 'secondClickReturnsThemToThePhone' -Value $backOnPhone
        }
        else
        {
            foreach ($id in @('left-click-disconnects', 'no-admin-prompt-disconnect', 'disconnect-agrees-with-endpoints', 'blocked-again-after-click'))
            {
                Add-Criterion -Run $run -Id $id -Criterion 'A second left click disconnects the AirPods, with no prompt, and blocks the nodes again.' `
                    -Outcome 'inconclusive' `
                    -Detail ('The first click did not connect them (the render endpoint reads ' + $statesAfterClick.Render + '), so there was nothing to disconnect.')
            }
        }

        Write-Section -Run $run -Title 'The tray icon and the card, on the real taskbar'
        Write-Line -Run $run -Text 'The glyph is drawn at run time so it scales, and the card must never take focus. Neither can be'
        Write-Line -Run $run -Text 'checked without looking at the screen. The probe renders the four states to files first, so the'
        Write-Line -Run $run -Text 'shapes can be compared against what the taskbar actually shows.'
        $iconFolder = Join-Path $run.Folder 'icons'
        New-Item -ItemType Directory -Force -Path $iconFolder | Out-Null
        [void](Invoke-Earshot -Run $run -Label 'probe-icon' -Command @('probe', 'icon', '--out', $iconFolder, '--json'))
        Write-Line -Run $run -Text ('The rendered glyphs are in ' + $iconFolder + '. Open them beside the taskbar to compare.')

        $sharp = Read-Answer -Run $run -Question 'Is the tray glyph sharp on the taskbar, with no pale fringe or darkened edges, at your normal scaling?'
        Add-Criterion -Run $run -Id 'icon-clean' -Criterion 'The tray glyph draws cleanly on the real taskbar.' `
            -Outcome $(if ($sharp -eq 'yes') { 'pass' } elseif ($sharp -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $sharp + '.')

        Wait-Owner -Run $run -Text 'Change the display scaling (Settings, System, Display, Scale) to 150 per cent and back, and watch the tray icon each time.'
        $scaled = Read-Answer -Run $run -Question 'Did the icon come back at the right size after each scaling change?'
        Add-Criterion -Run $run -Id 'icon-dpi' -Criterion 'The icon is rebuilt at the right size when the display scaling changes.' `
            -Outcome $(if ($scaled -eq 'yes') { 'pass' } elseif ($scaled -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $scaled + '.')

        Wait-Owner -Run $run -Text 'Switch Windows between light and dark mode (Settings, Personalisation, Colours) and watch the icon.'
        $themed = Read-Answer -Run $run -Question 'Did the glyph ink flip so it stays readable in both modes?'
        Add-Criterion -Run $run -Id 'icon-theme' -Criterion 'The glyph ink follows the Windows light and dark mode.' `
            -Outcome $(if ($themed -eq 'yes') { 'pass' } elseif ($themed -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $themed + '.')

        $themeLines = Get-EarshotLogLines -Run $run -Pattern 'TaskbarCreated received.'
        Add-Finding -Run $run -Name 'taskbarCreatedSeen' -Value @($themeLines).Count `
            -Detail 'restart Explorer from Task Manager to raise one, and the icon should come straight back'

        $focus = Read-Answer -Run $run -Question 'When the card appeared, did your keyboard focus stay where it was, in whatever you were typing in?'
        Add-Criterion -Run $run -Id 'card-no-focus' -Criterion 'The card never takes keyboard focus.' `
            -Outcome $(if ($focus -eq 'yes') { 'pass' } elseif ($focus -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $focus + '.')

        Write-Section -Run $run -Title 'Clicking, the menu and closing'
        Wait-Owner -Run $run -Text 'Click the tray icon a few ways: one click, a fast double click, then a right click. Watch what each one does.'
        $onceOnly = Read-Answer -Run $run -Question 'Did a fast double click still toggle only once, rather than connecting and disconnecting again?'
        Add-Criterion -Run $run -Id 'click-once' -Criterion 'A fast double click toggles once, not twice.' `
            -Outcome $(if ($onceOnly -eq 'yes') { 'pass' } elseif ($onceOnly -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $onceOnly + '.')

        $menuOk = Read-Answer -Run $run -Question 'Did the right click open the menu without toggling anything, with its ticks matching the real state?'
        Add-Criterion -Run $run -Id 'menu' -Criterion 'Right click opens the menu, never toggles, and its ticks are up to date.' `
            -Outcome $(if ($menuOk -eq 'yes') { 'pass' } elseif ($menuOk -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $menuOk + '.')

        $keyboard = Read-Answer -Run $run -Question 'From the keyboard (Windows+B, then arrow keys, then Enter or Shift+F10), could you reach the icon and open its menu?'
        Add-Finding -Run $run -Name 'keyboardReachesTheIcon' -Value $keyboard

        Write-Section -Run $run -Title 'Open on startup'
        Write-Line -Run $run -Text 'Earshot writes its own startup value in your registry hive. It is read here, never written.'
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $runValue = ''
        try
        {
            $entry = Get-ItemProperty -LiteralPath $runKey -Name 'Earshot'
            $runValue = '' + (Get-Field -Object $entry -Name 'Earshot')
        }
        catch
        {
            Write-Line -Run $run -Text ('There is no Earshot startup value: ' + ($_ | Out-String).Trim())
        }

        Write-Line -Run $run -Text ('Run value: ' + $runValue)
        Add-Criterion -Run $run -Id 'startup-value' -Criterion 'With Open on startup ticked, the Run value names this Earshot and passes --startup.' `
            -Outcome $(if (($runValue -like '*--startup*') -and ($runValue -like '*Earshot.exe*')) { 'pass' } else { 'inconclusive' }) `
            -Detail ('It reads: ' + $runValue + '. Inconclusive if Open on startup is not ticked, which is a setting, not a fault.')
        Add-Finding -Run $run -Name 'startupRunValue' -Value $runValue

        Wait-Owner -Run $run -Text 'In the Earshot menu, turn Open on startup off and on again, then open Task Manager, Startup apps, and turn Earshot off there.'
        $startupAgrees = Read-Answer -Run $run -Question 'After turning it off in Task Manager, did the Earshot menu show Open on startup unticked, and did clicking it show a card rather than silently re-enabling it?'
        Add-Criterion -Run $run -Id 'startup-agrees' -Criterion 'The menu agrees with what Task Manager says about startup, and never fights it.' `
            -Outcome $(if ($startupAgrees -eq 'yes') { 'pass' } elseif ($startupAgrees -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $startupAgrees + '.')

        Write-Section -Run $run -Title 'The card in awkward places'
        Write-Line -Run $run -Text 'These are all things only you can see. Answer unsure for any you cannot set up.'
        $secondCopy = Read-Answer -Run $run -Question 'Start Earshot a second time. Did the one already running show a card, instead of a second icon appearing?'
        Add-Criterion -Run $run -Id 'single-instance' -Criterion 'A second copy shows a card and leaves one icon.' `
            -Outcome $(if ($secondCopy -eq 'yes') { 'pass' } elseif ($secondCopy -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $secondCopy + '.')

        $underCursor = Read-Answer -Run $run -Question 'With a second display attached, did the card after a click appear on the display the cursor was on, sized for it?'
        Add-Finding -Run $run -Name 'cardFollowsTheCursorDisplay' -Value $underCursor

        $autoHide = Read-Answer -Run $run -Question 'With the taskbar set to hide itself, did the card stay clear of the taskbar when it slid back into view?'
        Add-Finding -Run $run -Name 'cardClearsAnAutoHidingTaskbar' -Value $autoHide

        $fullScreen = Read-Answer -Run $run -Question 'During a full-screen game or presentation, was the card correctly not shown at all?'
        Add-Finding -Run $run -Name 'cardSuppressedInFullScreen' -Value $fullScreen

        $contrast = Read-Answer -Run $run -Question 'In a contrast theme, was the card readable and drawn in the system colours?'
        Add-Finding -Run $run -Name 'cardReadableInContrastTheme' -Value $contrast

        Wait-Owner -Run $run -Text 'Now choose Exit from the Earshot menu and watch the notification area.'
        $ghost = Read-Answer -Run $run -Question 'Did the icon disappear cleanly, with no ghost left behind?'
        $stopped = Get-EarshotLogLines -Run $run -Pattern 'Tray stopped.'
        Add-Criterion -Run $run -Id 'clean-exit' -Criterion 'Exit removes the icon cleanly and the log ends with a clean stop.' `
            -Outcome $(if ($ghost -eq 'yes' -and @($stopped).Count -gt 0) { 'pass' } elseif ($ghost -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $ghost + '; the log holds ' + @($stopped).Count + ' clean stop line(s).')

        Save-EarshotLog -Run $run
        Write-Line -Run $run -Text ''
        Write-Line -Run $run -Text 'Leave the tray running. Its idle rule blocks the nodes again once the AirPods stop being used.'
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
    Write-Host ('Test 08 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive. Anything that starts a test can read the outcome without
# parsing result.json, and a test that recorded a failure is never read as a clean run.
exit (Get-LiveTestExitCode -Overall $overall)
