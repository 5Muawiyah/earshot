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
                -Detail ($before.Disabled + ' of ' + $before.Targets + ' are disabled, state ' + $before.State + '.')

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
            -Detail ($after.Disabled + ' of ' + $after.Targets + ' are disabled, state ' + $after.State + '.')

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
        if ($startUpLines.Count -gt 0)
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
        Add-Finding -Run $run -Name 'taskbarCreatedSeen' -Value $themeLines.Count `
            -Detail 'restart Explorer from Task Manager to raise one, and the icon should come straight back'

        $focus = Read-Answer -Run $run -Question 'When the card appeared, did your keyboard focus stay where it was, in whatever you were typing in?'
        Add-Criterion -Run $run -Id 'card-no-focus' -Criterion 'The card never takes keyboard focus.' `
            -Outcome $(if ($focus -eq 'yes') { 'pass' } elseif ($focus -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $focus + '.')

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
