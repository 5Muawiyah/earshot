<#
.SYNOPSIS
    The AirPods widget: the taskbar gauge, its card, and the case-open card.

.DESCRIPTION
    Phase 0 has not run on this hardware yet: the decode table
    (Earshot.Widget.ProximityDecodeTable.Current) ships Unproved, so every reading that would depend
    on it (battery, charging, in-ear, the case-open card, the low battery alert, auto-pause) must
    honestly show nothing rather than a guessed figure until battery set-up has proved it. This test
    checks that it does.

    Battery set-up is how those readings get proved. "Set up battery" is on the card and in the tray
    menu. It has three steps: open your AirPods case next to this PC, say what your iPhone shows with
    three pickers, and a result, or "Couldn't find your AirPods" when nothing was heard. The picker
    values are evidence, never a displayed reading. Half B checks the item is there and available
    while Bluetooth is on. The last half runs the set-up once and asks what the card said, after
    every check that needs the card to show nothing has already been answered.

    Everything that does not depend on the claim or the decode table is exercised for real: the
    gauge's placement, its following the taskbar, its fallback under a full screen application, its
    re-attach after Explorer restarts, its redraw at a new DPI and in light and dark mode, its click
    behaviour, the card's "where" line (on this PC comes from Core Audio alone, never the
    advertisement), the watcher's own start and stop, the notification shortcut, and that nothing
    here ever connects the AirPods by itself.

    It settles whether the gauge and its cards work as built today, and whether every reading that
    is not yet provable honestly says so rather than showing a figure nobody measured.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\19-Widget.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '19-widget' -Title 'The AirPods widget: gauge, card and case-open card' `
    -Settles 'Whether the taskbar gauge and its cards work as built today, and whether every reading the decode table cannot yet prove honestly shows nothing rather than a guessed figure.' `
    -ExePath $ExePath -RunRoot $RunRoot

# The path Earshot.Infra.Paths.WidgetClaimFile computes: %LOCALAPPDATA%\Earshot\widget\claim.json,
# or under EARSHOT_DATA_ROOT when redirected. Read-only: this test never writes it.
function Get-WidgetClaimPath
{
    param([Parameter(Mandatory = $true)]$Run)

    $paths = Get-EarshotDataPaths
    return Join-Parts @($paths.LocalFolder, 'widget', 'claim.json')
}

# The Start menu shortcut NotificationRegistration writes so a toast can name Earshot:
# %APPDATA%\Microsoft\Windows\Start Menu\Programs\Earshot.lnk.
function Get-NotificationShortcutPath
{
    if ([string]::IsNullOrEmpty($env:APPDATA)) { throw 'APPDATA is not set, so the notification shortcut cannot be found.' }
    return Join-Parts @($env:APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Earshot.lnk')
}

# One figure out of a "Widget counters: ... name=value ..." line (WidgetStatusService.FormatCountersLine),
# or $null when the line does not carry that name. The newest matching log line is used, never a guess.
function Get-CounterFigure
{
    param([string]$Line, [string]$Name)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    if ($Line -match ([string]$Name + '=(\d+)')) { return [int]$Matches[1] }
    return $null
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed from a release built from the current head, set up, and running in the tray.',
        '"Show on the taskbar" is ticked in the tray menu, so the gauge is on (or has fallen back to the tray icon).',
        'The AirPods are paired with this PC and available to connect.'
    ) -PhysicalActions @(
        'This is a long sitting. It watches the taskbar, restarts Explorer once, changes display scaling and light/dark mode and back, opens your AirPods case near the PC, and turns Bluetooth off and on. Nothing here is destructive, and every step says what it does before it asks.',
        'Several checks below expect the card to say "No reading", "Not seen yet" or show nothing at all. That is the correct, honest answer while phase 0 is outstanding: it is not a bug, and a check on this only fails if the card shows a figure nobody measured.'
    )

    if ($ready)
    {
        $paths = Get-EarshotDataPaths
        $claimPath = Get-WidgetClaimPath -Run $run
        $testStartUtc = (Get-Date).ToUniversalTime()

        Write-Section -Run $run -Title 'Starting facts'
        $settings = Read-EarshotJsonFile -Run $run -Path $paths.SettingsFile
        $showOnTaskbar = Get-FieldPath -Object $settings -Path @('Widget', 'ShowOnTaskbar')
        $leftClickConnects = Get-FieldPath -Object $settings -Path @('Widget', 'LeftClickConnects')
        $caseOpenCardSetting = Get-FieldPath -Object $settings -Path @('Widget', 'CaseOpenCard')
        $autoPauseSetting = Get-FieldPath -Object $settings -Path @('Widget', 'AutoPause')
        $lowBatteryAlertSetting = Get-FieldPath -Object $settings -Path @('Widget', 'LowBatteryAlert')
        Add-Finding -Run $run -Name 'showOnTaskbar' -Value $showOnTaskbar -Detail 'Widget.ShowOnTaskbar read from settings.json'
        Add-Finding -Run $run -Name 'leftClickConnects' -Value $leftClickConnects -Detail 'Widget.LeftClickConnects read from settings.json'
        Add-Finding -Run $run -Name 'caseOpenCardSetting' -Value $caseOpenCardSetting -Detail 'Widget.CaseOpenCard read from settings.json'
        Add-Finding -Run $run -Name 'autoPauseSetting' -Value $autoPauseSetting -Detail 'Widget.AutoPause read from settings.json'
        Add-Finding -Run $run -Name 'lowBatteryAlertSetting' -Value $lowBatteryAlertSetting -Detail 'Widget.LowBatteryAlert read from settings.json'

        $claimBefore = Read-EarshotJsonFile -Run $run -Path $claimPath
        Add-Finding -Run $run -Name 'claimFileExistsAtStart' -Value $(if ($null -eq $claimBefore) { 'no' } else { 'yes' }) -Detail $claimPath

        # =============================================================== the gauge and its cards

        Write-Section -Run $run -Title 'Gauge placement'
        Write-Line -Run $run -Text 'Look at the taskbar near the clock.'
        $placementAnswer = Read-Answer -Run $run -Question 'Does the gauge sit just to the right of the last taskbar button, with about 24 pixels of clear space and no button underneath it, and does clicking the free taskbar space beside it still do what it did before Earshot was installed?'
        Add-Criterion -Run $run -Id 'gauge-placement' -Criterion 'The gauge sits clear of every taskbar button and does not intercept a click meant for the taskbar.' `
            -Outcome $(if ($placementAnswer -eq 'yes') { 'pass' } elseif ($placementAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $placementAnswer + '.')

        Write-Section -Run $run -Title 'The gauge follows the taskbar buttons'
        Wait-Owner -Run $run -Text 'Open another app (anything pinned or running) so a new button appears on the taskbar, then close it again.'
        $followsButtonsAnswer = Read-Answer -Run $run -Question 'Did the gauge move right to make room for the new button, within about a second, and move back once you closed it?'
        Add-Criterion -Run $run -Id 'gauge-follows-buttons' -Criterion 'The gauge re-measures and moves when the taskbar buttons change.' `
            -Outcome $(if ($followsButtonsAnswer -eq 'yes') { 'pass' } elseif ($followsButtonsAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $followsButtonsAnswer + '.')

        Write-Section -Run $run -Title 'The gauge follows taskbar alignment'
        Wait-Owner -Run $run -Text 'Right-click an empty part of the desktop, click Personalise, click Taskbar (or Taskbar behaviors), set taskbar alignment to Left, then look at the taskbar.'
        $alignLeftAnswer = Read-Answer -Run $run -Question 'With alignment set to Left, does the gauge sit beside the taskbar buttons rather than out on its own?'
        Wait-Owner -Run $run -Text 'Set taskbar alignment back to Centre.'
        $alignCentreAnswer = Read-Answer -Run $run -Question 'Back at Centre, does the gauge follow the buttons again?'
        Add-Criterion -Run $run -Id 'gauge-follows-alignment' -Criterion 'The gauge follows the taskbar whether it is left-aligned or centred.' `
            -Outcome $(if ($alignLeftAnswer -eq 'yes' -and $alignCentreAnswer -eq 'yes') { 'pass' } elseif ($alignLeftAnswer -eq 'unsure' -or $alignCentreAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('Left: ' + $alignLeftAnswer + '. Centre: ' + $alignCentreAnswer + '.')

        Write-Section -Run $run -Title 'The gauge follows auto-hide'
        Wait-Owner -Run $run -Text 'In the same Taskbar settings, turn on "Automatically hide the taskbar", then move the mouse away from the bottom of the screen so it slides away, then move it back down.'
        $autoHideAnswer = Read-Answer -Run $run -Question 'Did the gauge slide away with the taskbar, and come back when you brought the taskbar back?'
        Wait-Owner -Run $run -Text 'Turn "Automatically hide the taskbar" back off.'
        Add-Criterion -Run $run -Id 'gauge-follows-autohide' -Criterion 'The gauge hides and returns with an auto-hiding taskbar.' `
            -Outcome $(if ($autoHideAnswer -eq 'yes') { 'pass' } elseif ($autoHideAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $autoHideAnswer + '.')

        Write-Section -Run $run -Title 'The gauge under a full-screen application'
        Wait-Owner -Run $run -Text 'Start a full-screen game or video, watch the taskbar area for a moment, then leave full screen again.'
        $fullScreenAnswer = Read-Answer -Run $run -Question 'While it was full-screen, was the gauge gone, and was it back once you left full screen?'
        Add-Criterion -Run $run -Id 'gauge-hidden-fullscreen' -Criterion 'The gauge hides under a full-screen application and returns afterwards.' `
            -Outcome $(if ($fullScreenAnswer -eq 'yes') { 'pass' } elseif ($fullScreenAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $fullScreenAnswer + '.')

        Write-Section -Run $run -Title 'Explorer restarting'
        $explorerRestartUtc = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Right-click the Start button, click Task Manager, click the Processes tab, find Windows Explorer, right-click it and choose Restart. Wait for the taskbar to come back.'
        $explorerAnswer = Read-Answer -Run $run -Question 'After Explorer restarted, did the tray icon come back first, and then the gauge?'
        $taskbarCreatedLines = Get-EarshotLogLines -Run $run -Pattern 'TaskbarCreated received.' -SinceUtc $explorerRestartUtc
        $abmNewLines = Get-EarshotLogLines -Run $run -Pattern 'sh-app-bar-message:abm-new' -SinceUtc $explorerRestartUtc
        Write-Line -Run $run -Text ('  ' + @($taskbarCreatedLines).Count + ' "TaskbarCreated received." line(s), ' + @($abmNewLines).Count + ' ABM_NEW re-registration line(s).')
        Add-Criterion -Run $run -Id 'explorer-restart-reattach' -Criterion 'The gauge re-attaches after Explorer restarts: TaskbarCreated is logged, the appbar is re-registered, and the icon and gauge come back.' `
            -Outcome $(if ($explorerAnswer -eq 'yes' -and @($taskbarCreatedLines).Count -gt 0 -and @($abmNewLines).Count -gt 0) { 'pass' } elseif ($explorerAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $explorerAnswer + '; ' + @($taskbarCreatedLines).Count + ' TaskbarCreated line(s), ' + @($abmNewLines).Count + ' ABM_NEW line(s).')

        Write-Section -Run $run -Title 'Display scaling'
        Wait-Owner -Run $run -Text 'Right-click an empty part of the desktop, click Display settings, set Scale to 150%, then set it back to what it was.'
        $dpiAnswer = Read-Answer -Run $run -Question 'At 150 per cent and back to normal, did the gauge and the card both redraw crisply at the new size, with nothing left blurred or the wrong size?'
        Add-Criterion -Run $run -Id 'gauge-card-dpi' -Criterion 'The gauge and the card redraw for a new display scale.' `
            -Outcome $(if ($dpiAnswer -eq 'yes') { 'pass' } elseif ($dpiAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $dpiAnswer + '.')

        Write-Section -Run $run -Title 'Light and dark mode'
        Wait-Owner -Run $run -Text 'Right-click an empty part of the desktop, click Personalise, click Colours, and change "Choose your mode" to the opposite of what it is now.'
        $themeAnswer = Read-Answer -Run $run -Question 'Did the ink on the gauge flip so it stays readable, and is every line on the card readable against the new background?'
        Wait-Owner -Run $run -Text 'Change "Choose your mode" back to what it was.'
        Add-Criterion -Run $run -Id 'gauge-card-theme' -Criterion 'The gauge and the card follow light and dark mode.' `
            -Outcome $(if ($themeAnswer -eq 'yes') { 'pass' } elseif ($themeAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $themeAnswer + '.')

        Write-Section -Run $run -Title 'Clicking the gauge'
        $clickAnswer = Read-Answer -Run $run -Question 'Right-click the gauge: does the same menu the tray icon shows appear? Left-click the gauge: does the card open above it, does Escape close it, does clicking elsewhere close it, and does a second click on the gauge close it without reopening it?'
        Add-Criterion -Run $run -Id 'gauge-click-behaviour' -Criterion 'Right click opens the tray menu; left click opens, and a second click, Escape or clicking elsewhere all close, the card.' `
            -Outcome $(if ($clickAnswer -eq 'yes') { 'pass' } elseif ($clickAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $clickAnswer + '.')

        # ============================================================ the advertisement, claim and alert

        Write-Section -Run $run -Title 'Half A: the positive control'
        Write-Line -Run $run -Text 'The watcher listens passively for every nearby Apple advertisement, not only the owner''s own AirPods, so this counts up on its own with nobody handling a device.'
        Write-Line -Run $run -Text 'The counters are logged about once a minute while anything changed, so this waits in short steps rather than one long sleep, and stops as soon as a line appears.'
        $counterLines = @()
        for ($step = 0; $step -lt 5 -and @($counterLines).Count -eq 0; $step++)
        {
            Wait-Seconds -Run $run -Seconds 15 -Reason 'waiting for the widget''s once-a-minute counters line'
            $counterLines = Get-EarshotLogLines -Run $run -Pattern 'Widget counters:' -SinceUtc $testStartUtc
        }
        $newestCounterLine = $(if (@($counterLines).Count -gt 0) { @($counterLines)[-1] } else { $null })
        $allSections = Get-CounterFigure -Line $newestCounterLine -Name 'allSections'
        $appleSections = Get-CounterFigure -Line $newestCounterLine -Name 'apple'
        $proximityItems = Get-CounterFigure -Line $newestCounterLine -Name 'items'
        Write-Line -Run $run -Text ('  newest counters line: ' + $(if ($null -eq $newestCounterLine) { '(none yet)' } else { $newestCounterLine }))
        Add-Finding -Run $run -Name 'widgetAllSectionsSeen' -Value $allSections
        Add-Finding -Run $run -Name 'widgetAppleSectionsSeen' -Value $appleSections
        Add-Finding -Run $run -Name 'widgetProximityItemsSeen' -Value $proximityItems
        Add-Criterion -Run $run -Id 'positive-control-counters' -Criterion 'The watcher''s own counters show Apple advertisements and 0x07 proximity items, so the watcher and the parser are both alive.' `
            -Outcome $(if ($null -eq $newestCounterLine) { 'inconclusive' } elseif ($appleSections -gt 0 -and $proximityItems -gt 0) { 'pass' } else { 'fail' }) `
            -Detail $(if ($null -eq $newestCounterLine) { 'No "Widget counters:" line was logged in the wait; either nothing Apple was nearby or the watcher is not running.' } else { 'allSections=' + $allSections + ' apple=' + $appleSections + ' items=' + $proximityItems + '.' })

        Write-Section -Run $run -Title 'Half B: battery set-up is reachable'
        $setupInMenuAnswer = Read-Answer -Run $run -Question 'Right-click the Earshot tray icon: is there a "Set up battery" item in the menu?'
        $setupAvailableAnswer = if ($setupInMenuAnswer -eq 'yes') { Read-Answer -Run $run -Question 'With Bluetooth on, is that item available to click, not greyed out and with no reason in brackets after its name?' } else { 'unsure' }
        $claimNow = Read-EarshotJsonFile -Run $run -Path $claimPath
        Add-Finding -Run $run -Name 'batterySetupInMenu' -Value $(if ($setupInMenuAnswer -eq 'yes') { 'yes' } else { 'no' }) `
            -Detail 'The tray menu holds "Set up battery". It reads "Set up battery (Bluetooth is off)" and is greyed out while the watcher is not running, and is available otherwise.'
        Add-Finding -Run $run -Name 'claimFileExistsAfterCheck' -Value $(if ($null -eq $claimNow) { 'no' } else { 'yes' }) -Detail $claimPath
        Add-Criterion -Run $run -Id 'battery-setup-reachable' -Criterion 'Battery set-up is in the running application, and is available while Bluetooth is on.' `
            -Outcome $(if ($setupInMenuAnswer -ne 'yes') { 'fail' } elseif ($setupAvailableAnswer -eq 'yes') { 'pass' } elseif ($setupAvailableAnswer -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail $(
                if ($setupInMenuAnswer -ne 'yes') { 'You answered ' + $setupInMenuAnswer + ': "Set up battery" should always be in the tray menu. A missing item is a real defect.' }
                elseif ($setupAvailableAnswer -eq 'yes') { 'Found and available: battery set-up can be started.' }
                elseif ($setupAvailableAnswer -eq 'no') { 'The item is greyed out although Bluetooth is on: it should be available. This is a real defect, not an inconclusive result.' }
                else { 'You answered ' + $setupAvailableAnswer + ' on whether it is available.' })

        Write-Section -Run $run -Title 'Half C: battery, honestly'
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon or the gauge to open the card.'
        $batteryAnswer = Read-Answer -Run $run -Question 'Does the card say "No reading" for the battery, never a percentage, and show nothing at all for charging or in-ear state?'
        Add-Criterion -Run $run -Id 'battery-honestly-not-shown' -Criterion 'Before battery set-up, with an unproved decode table, the card never shows a battery figure, charging state or in-ear state it has not actually read.' `
            -Outcome $(if ($batteryAnswer -eq 'yes') { 'pass' } elseif ($batteryAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $batteryAnswer + '. A "yes" here is the correct, honest state before battery set-up has proved anything; a "no" (a figure was shown) would mean something invented a reading, which is a real defect.')

        Write-Section -Run $run -Title 'Half E: where, connected'
        Wait-Owner -Run $run -Text 'Connect the AirPods to this PC: left-click the Earshot icon or the gauge, then click Connect on the card.'
        $whereThisPcAnswer = Read-Answer -Run $run -Question 'With the AirPods connected to this PC, does the "where" line on the card say "On this PC"?'
        Add-Criterion -Run $run -Id 'where-this-pc' -Criterion 'The card reads "on this PC" from Core Audio alone, which needs no set-up and no decode table.' `
            -Outcome $(if ($whereThisPcAnswer -eq 'yes') { 'pass' } elseif ($whereThisPcAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $whereThisPcAnswer + '.')

        Write-Section -Run $run -Title 'Half H: auto-pause, while it can still be exercised'
        Write-Line -Run $run -Text 'The AirPods are already connected and playing from the connect step above.'
        Wait-Owner -Run $run -Text 'Make sure something is playing from this PC through the AirPods, then take one bud out of your ear.'
        $autoPauseAnswer = Read-Answer -Run $run -Question 'Did the audio keep playing on this PC (auto-pause must not act yet, because phase 0 has not proved the broadcast keeps arriving while playing from this PC)?'
        Add-Criterion -Run $run -Id 'auto-pause-inert' -Criterion 'Auto-pause never acts while its own phase 0 gate is unproved.' `
            -Outcome $(if ($autoPauseAnswer -eq 'yes') { 'pass' } elseif ($autoPauseAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $autoPauseAnswer + '. "Yes" (it kept playing) is the correct, honest state before phase 0.')

        Write-Section -Run $run -Title 'Half E: where, not connected'
        Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC again: left-click the Earshot icon or the gauge, then click Disconnect on the card.'
        $whereNotConnectedAnswer = Read-Answer -Run $run -Question 'With the AirPods not connected to this PC, does the card say "Not seen yet" rather than guessing whether they are on your phone or in the case?'
        Add-Criterion -Run $run -Id 'where-not-connected-honest' -Criterion 'Before battery set-up, the card never guesses "on your phone" or "in the case": it says "Not seen yet".' `
            -Outcome $(if ($whereNotConnectedAnswer -eq 'yes') { 'pass' } elseif ($whereNotConnectedAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $whereNotConnectedAnswer + '.')

        Write-Section -Run $run -Title 'Half D: the case-open card'
        $caseOpenUtc = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Open your AirPods case next to this computer, without touching anything in Earshot.'
        $caseCardAnswer = Read-Answer -Run $run -Question 'Did any small card appear near the taskbar by itself, without you clicking anything?'
        $toggleLines = Get-EarshotLogLines -Run $run -Pattern 'connect: ' -SinceUtc $caseOpenUtc
        Add-Finding -Run $run -Name 'caseOpenToggleLinesSeen' -Value @($toggleLines).Count
        Add-Criterion -Run $run -Id 'case-open-card-honestly-not-shown' -Criterion 'Without a proved reading (no battery set-up, and no proved lid signal), the case-open card correctly stays silent rather than showing an unproved reading.' `
            -Outcome $(if ($caseCardAnswer -eq 'no') { 'pass' } elseif ($caseCardAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $caseCardAnswer + '. "No" is the correct, honest state before battery set-up and phase 0 prove the lid signal; "yes" would mean a card appeared for a set of AirPods this build cannot yet confirm are the owner''s.')
        Add-Criterion -Run $run -Id 'case-open-no-auto-connect' -Criterion 'Opening the case never connects the AirPods by itself, whether or not a card appeared.' `
            -Outcome $(if (@($toggleLines).Count -eq 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($toggleLines).Count + ' connect or disconnect line(s) logged since the case was opened; there should be none.')

        Write-Section -Run $run -Title 'Half F: Bluetooth off, then on'
        $bluetoothOffUtc = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Turn Bluetooth off (Windows Settings, Bluetooth and devices, or the Quick Settings icon), and wait a few seconds.'
        $stoppedLines = Get-EarshotLogLines -Run $run -Pattern 'Widget watcher stopped:' -SinceUtc $bluetoothOffUtc
        $stoppedLine = $(if (@($stoppedLines).Count -gt 0) { @($stoppedLines)[-1] } else { $null })
        Write-Line -Run $run -Text ('  ' + $(if ($null -eq $stoppedLine) { '(no stopped line yet)' } else { $stoppedLine }))
        $bluetoothOnUtc = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Turn Bluetooth back on.'
        $restartedLines = Get-EarshotLogLines -Run $run -Pattern 'Widget watcher start:' -SinceUtc $bluetoothOnUtc
        Add-Finding -Run $run -Name 'widgetWatcherStoppedLine' -Value $(if ($null -eq $stoppedLine) { $null } else { [string]$stoppedLine })
        Add-Criterion -Run $run -Id 'bluetooth-off-then-on' -Criterion 'The widget watcher reports being stopped when Bluetooth goes off, and starts again once it is back, inside its retry budget.' `
            -Outcome $(if (@($stoppedLines).Count -gt 0 -and @($restartedLines).Count -gt 0) { 'pass' } elseif (@($stoppedLines).Count -eq 0 -and @($restartedLines).Count -eq 0) { 'inconclusive' } else { 'fail' }) `
            -Detail ([string]@($stoppedLines).Count + ' "stopped" line(s), ' + @($restartedLines).Count + ' "start" line(s) after turning it back on.')

        Write-Section -Run $run -Title 'Half G: the low battery alert'
        $shortcutPath = Get-NotificationShortcutPath
        $shortcutExists = Test-Path -LiteralPath $shortcutPath -PathType Leaf
        Write-Line -Run $run -Text ('  shortcut: ' + $shortcutPath + ' exists=' + $shortcutExists)
        Add-Finding -Run $run -Name 'notificationShortcutPath' -Value $shortcutPath
        Add-Criterion -Run $run -Id 'notification-shortcut-exists' -Criterion 'The Start menu shortcut a toast needs to name Earshot has been written.' `
            -Outcome $(if ($shortcutExists) { 'pass' } else { 'fail' }) `
            -Detail $(if ($shortcutExists) { 'Found at ' + $shortcutPath + '.' } else { 'Not found at ' + $shortcutPath + '.' })
        Add-Criterion -Run $run -Id 'low-battery-alert-fires' -Criterion 'The alert fires once a part with a proved reading first reads at or below the threshold, and does not repeat.' `
            -Outcome 'inconclusive' `
            -Detail 'The latch is only ever fed a reading Earshot has confirmed is the owner''s own AirPods and has proved, and this test does not prove one, so this cannot be exercised for real yet.'

        Write-Section -Run $run -Title 'Half I: battery set-up, run once'
        Wait-Owner -Run $run -Text 'Click the Earshot icon to open the card, then click Set up battery. When the card asks you to open your AirPods case, open it next to this computer.'
        Wait-Owner -Run $run -Text 'The card now asks what your iPhone shows. Pick the nearest 10 for each part (if it ends in 5, pick the lower), then click Save.'
        $setupStepsAnswer = Read-Answer -Run $run -Question 'Did the card go through the three steps: open the case, pick what your iPhone shows, then a result? Answer no if it stopped early and said it could not find your AirPods.'
        Add-Criterion -Run $run -Id 'battery-setup-flow' -Criterion 'Battery set-up goes through its three steps and ends on a result.' `
            -Outcome $(if ($setupStepsAnswer -eq 'yes') { 'pass' } else { 'inconclusive' }) `
            -Detail ('You answered ' + $setupStepsAnswer + '. A "no" is not a defect on its own: the set-up says it could not find your AirPods when it heard none, which depends on the case being open next to this computer.')
        $setupHonestAnswer = Read-Answer -Run $run -Question 'If the card now shows a battery percentage, is it only for a part the set-up said it had set up, with nothing shown for a part it could not read?'
        Add-Criterion -Run $run -Id 'battery-setup-honest-result' -Criterion 'After battery set-up, the card shows a battery figure only for a part the set-up proved, and nothing for a part it could not read.' `
            -Outcome $(if ($setupHonestAnswer -eq 'yes') { 'pass' } elseif ($setupHonestAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $setupHonestAnswer + '. A "no" would mean a figure was shown that the set-up had not proved, which is a real defect.')

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
    Write-Host ('Test 19 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
