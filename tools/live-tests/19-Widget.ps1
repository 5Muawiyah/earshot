<#
.SYNOPSIS
    The AirPods widget: the taskbar gauge, its card, and the case-open card.

.DESCRIPTION
    Earshot shows the battery of the AirPods it is paired with from what those AirPods broadcast, with no
    set-up step: the two buds, and the case when its lid is open or the buds are out of it. A figure is current
    for 30 seconds and is then greyed with the time it was read, and a bud's figure leaves the gauge after an
    hour. Nothing is shown unless the AirPods are connected to this PC, so a step that shuts the AirPods in the
    case expects the figures to be greyed (still connected) or gone (disconnected), never current. The card has a refresh icon beside the gear and the tray menu has "Refresh battery": each restarts the
    listening and waits up to twelve seconds for the chosen AirPods to be heard again, or says nothing was heard.
    The broadcast cannot say whether a bud is in the ear or whether the case lid is open, so auto-pause and the
    case-open card stay off. This test checks all of that, and that any figure shown agrees with the iPhone.

    Everything that does not depend on the battery is exercised for real: the
    gauge's two positions (at the right end, 8 pixels left of the notification area, by default; next
    to the apps, 4 pixels after the last button), its following the taskbar, its staying visible and on
    top when Start, a flyout or a taskbar click comes and goes, its fallback under a full screen
    application, its re-attach after Explorer restarts, its redraw at a new DPI and in light and dark
    mode, its click behaviour, the card's "where" line (on this PC comes from Core Audio alone, never
    the advertisement), the watcher's own start and stop, the notification shortcut, and that nothing
    here ever connects the AirPods by itself.

    It settles whether the gauge and its cards work as built today, whether the battery shown agrees with the
    iPhone and greys when it is old, and whether refresh reads it again or says nothing was heard.

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
    -Settles 'Whether the taskbar gauge and its cards work as built today, whether the battery they show agrees with the iPhone and greys when it is old, and whether refresh reads it again or says nothing was heard.' `
    -ExePath $ExePath -RunRoot $RunRoot

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
        'The battery figures on the card come from what your AirPods broadcast, which they only do while they are out of the case or the case lid is open, and the card shows a figure only while the AirPods are connected to this PC. Some steps below ask you to shut them in the case: that is on purpose, to see the figures grey or go, and the refresh say nothing was heard.'
    )

    if ($ready)
    {
        $paths = Get-EarshotDataPaths
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

        # =============================================================== the gauge and its cards

        Write-Section -Run $run -Title 'Gauge placement'
        $gaugePositionAtStart = Get-FieldPath -Object $settings -Path @('Widget', 'GaugePosition')
        Add-Finding -Run $run -Name 'gaugePositionAtStart' -Value $gaugePositionAtStart -Detail 'Widget.GaugePosition read from settings.json'
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon or the gauge to open the card, click the gear, and check that "Gauge position" is set to "Right end". Then close the card.'
        Write-Line -Run $run -Text 'Look at the taskbar near the clock.'
        $placementAnswer = Read-Answer -Run $run -Question 'Is the gauge at the right end of the taskbar, just left of the notification area (the small arrow and icons beside the clock), with a small gap of about 8 pixels and no icon underneath it, and does clicking the free taskbar space beside it still do what it did before Earshot was installed?'
        Add-Criterion -Run $run -Id 'gauge-placement' -Criterion 'By default the gauge sits at the right end of the taskbar, 8 pixels left of the notification area, clear of every taskbar item, and does not intercept a click meant for the taskbar.' `
            -Outcome $(if ($placementAnswer -eq 'yes') { 'pass' } elseif ($placementAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $placementAnswer + '.')

        Write-Section -Run $run -Title 'Next to apps'
        Wait-Owner -Run $run -Text 'Open the card, click the gear, set "Gauge position" to "Next to apps", then close the card.'
        $nextToAppsAnswer = Read-Answer -Run $run -Question 'Is the gauge now just after the last taskbar button, with a small gap of about 4 pixels and no button underneath it?'

        Write-Section -Run $run -Title 'The gauge follows the taskbar buttons'
        Wait-Owner -Run $run -Text 'Open another app (anything pinned or running) so a new button appears on the taskbar, then close it again.'
        $followsButtonsAnswer = Read-Answer -Run $run -Question 'Did the gauge move right to make room for the new button, within about a second, and move back once you closed it?'
        Add-Criterion -Run $run -Id 'gauge-follows-buttons' -Criterion 'With the gauge next to the apps, it re-measures and moves when the taskbar buttons change.' `
            -Outcome $(if ($followsButtonsAnswer -eq 'yes') { 'pass' } elseif ($followsButtonsAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $followsButtonsAnswer + '.')

        Write-Section -Run $run -Title 'The gauge follows taskbar alignment'
        Wait-Owner -Run $run -Text 'Right-click an empty part of the desktop, click Personalise, click Taskbar (or Taskbar behaviors), set taskbar alignment to Left, then look at the taskbar.'
        $alignLeftAnswer = Read-Answer -Run $run -Question 'With alignment set to Left, does the gauge sit beside the taskbar buttons rather than out on its own?'
        Wait-Owner -Run $run -Text 'Set taskbar alignment back to Centre.'
        $alignCentreAnswer = Read-Answer -Run $run -Question 'Back at Centre, does the gauge follow the buttons again?'
        Add-Criterion -Run $run -Id 'gauge-follows-alignment' -Criterion 'With the gauge next to the apps, it follows the taskbar whether it is left-aligned or centred.' `
            -Outcome $(if ($alignLeftAnswer -eq 'yes' -and $alignCentreAnswer -eq 'yes') { 'pass' } elseif ($alignLeftAnswer -eq 'unsure' -or $alignCentreAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('Left: ' + $alignLeftAnswer + '. Centre: ' + $alignCentreAnswer + '.')

        Wait-Owner -Run $run -Text 'Open the card, click the gear, set "Gauge position" back to "Right end", then close the card.'
        $rightEndAgainAnswer = Read-Answer -Run $run -Question 'Back at Right end, is the gauge at the right end of the taskbar again, just left of the notification area?'
        Add-Criterion -Run $run -Id 'gauge-next-to-apps' -Criterion 'The Gauge position setting moves the gauge next to the apps, 4 pixels after the last button, and back to the right end.' `
            -Outcome $(if ($nextToAppsAnswer -eq 'yes' -and $rightEndAgainAnswer -eq 'yes') { 'pass' } elseif ($nextToAppsAnswer -eq 'unsure' -or $rightEndAgainAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('Next to apps: ' + $nextToAppsAnswer + '. Back at the right end: ' + $rightEndAgainAnswer + '.')

        Write-Section -Run $run -Title 'The gauge stays on top'
        $onTopStartUtc = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Open the Start menu and close it. Click the clock to open its flyout and close it. Click an empty part of the taskbar. Do each of the three a couple of times.'
        $onTopAnswer = Read-Answer -Run $run -Question 'Through all of that, did the gauge stay visible and on top of the taskbar, or come straight back, with no time where it was gone and stayed gone?'
        $raisedLines = Get-EarshotLogLines -Run $run -Pattern 'Gauge raised:' -SinceUtc $onTopStartUtc
        $leftUnderLines = Get-EarshotLogLines -Run $run -Pattern 'Gauge left under' -SinceUtc $onTopStartUtc
        # The gauge is the taskbar's owned window, so the system keeps it above the bar and there may be nothing to raise: the
        # line saying it was owned is what shows that was how it stayed on top.
        $ownedLines = Get-EarshotLogLines -Run $run -Pattern 'Gauge owned by the taskbar' -SinceUtc $testStartUtc
        Write-Line -Run $run -Text ('  ' + @($raisedLines).Count + ' "Gauge raised" line(s), ' + @($leftUnderLines).Count + ' "Gauge left under" line(s) in the app log during that step.')
        Add-Finding -Run $run -Name 'gaugeRaisedLines' -Value @($raisedLines).Count -Detail 'Log lines saying the gauge was put back on top while Start, a flyout and taskbar clicks came and went'
        Add-Finding -Run $run -Name 'gaugeLeftUnderLines' -Value @($leftUnderLines).Count -Detail 'Log lines saying another window was over the gauge and it was left there'
        Add-Criterion -Run $run -Id 'gauge-stays-on-top' -Criterion 'The gauge stays visible and on top when Start, a flyout or a taskbar click comes and goes, and the app log shows what covered it and when it was raised.' `
            -Outcome $(if ($onTopAnswer -eq 'no') { 'fail' } elseif ($onTopAnswer -eq 'unsure') { 'inconclusive' } elseif (@($raisedLines).Count -gt 0 -or @($leftUnderLines).Count -gt 0 -or @($ownedLines).Count -gt 0) { 'pass' } else { 'inconclusive' }) `
            -Detail $(
                if ($onTopAnswer -eq 'no') { 'You answered no: the gauge was gone and stayed gone, which is the fault this check exists to catch.' }
                elseif ($onTopAnswer -eq 'unsure') { 'You answered unsure.' }
                elseif (@($raisedLines).Count -gt 0 -or @($leftUnderLines).Count -gt 0) { 'You answered yes; the log shows ' + @($raisedLines).Count + ' raise(s) and ' + @($leftUnderLines).Count + ' cover(s) left alone.' }
                elseif (@($ownedLines).Count -gt 0) { 'You answered yes; nothing needed raising, and the log says the gauge is the taskbar''s owned window, which is how the system kept it on top.' }
                else { 'You answered yes, but the log shows neither a raise nor the gauge being owned by the taskbar, so how it stayed on top is not shown. Try again and open Start with the gauge in view.' })

        Write-Section -Run $run -Title 'The shell stays responsive'
        Wait-Owner -Run $run -Text 'Open the Start menu again, type a few letters into its search box, then press Escape. Do this twice, then click the clock and close its flyout.'
        $responsiveAnswer = Read-Answer -Run $run -Question 'Did Start, what you typed in it and the flyout from the clock answer at once each time, with no pause and no letter going missing?'
        Add-Criterion -Run $run -Id 'shell-stays-responsive' -Criterion 'With the gauge owned by the taskbar, Start, typing into it and the clock''s flyout answer as quickly as without the gauge: no pause and no lost typing.' `
            -Outcome $(if ($responsiveAnswer -eq 'yes') { 'pass' } elseif ($responsiveAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $responsiveAnswer + '. A "no" would mean the gauge is still tied to the shell''s input, which it must not be.')

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

        # ============================================================ the advertisement, the battery and the alert

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

        Write-Section -Run $run -Title 'Half B: refresh is reachable'
        $refreshInMenuAnswer = Read-Answer -Run $run -Question 'Right-click the Earshot tray icon: is there a "Refresh battery" item in the menu?'
        Add-Finding -Run $run -Name 'refreshBatteryInMenu' -Value $(if ($refreshInMenuAnswer -eq 'yes') { 'yes' } else { 'no' }) `
            -Detail 'The tray menu holds "Refresh battery" while "Show on the taskbar" is on.'
        Add-Criterion -Run $run -Id 'refresh-battery-reachable' -Criterion 'Refresh battery is in the tray menu while the gauge is on.' `
            -Outcome $(if ($refreshInMenuAnswer -eq 'yes') { 'pass' } elseif ($refreshInMenuAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $refreshInMenuAnswer + '. A missing item while the gauge is on is a real defect.')

        Write-Section -Run $run -Title 'Half C: the battery, from the broadcast'
        $batteryStartUtc = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Connect the AirPods to this PC (left-click the Earshot icon or the gauge, then Connect). Then put both AirPods in the case, open the lid next to this computer and leave it open, and open the card again. If the card no longer says "On this PC", connect them again with the lid still open.'
        # A set that is linked stays linked while it is heard (and for two minutes after), so one linked earlier in this
        # sitting (or just before it) is not linked again and logs no new line here. Either a linked line anywhere since
        # the sitting began, or the counters' chosen figure rising since the first counters line of the sitting, shows the
        # widget has a linked set and is hearing it.
        $firstChosen = $(if (@($counterLines).Count -gt 0) { Get-CounterFigure -Line (@($counterLines)[0]) -Name 'chosen' } else { $null })
        $lastChosen = $null
        $chosenRose = $false
        $pickedLines = @()
        for ($step = 0; $step -lt 7 -and @($pickedLines).Count -eq 0 -and -not $chosenRose; $step++)
        {
            Wait-Seconds -Run $run -Seconds 10 -Reason 'waiting for the widget to link your AirPods'
            $pickedLines = Get-EarshotLogLines -Run $run -Pattern 'Widget: linked to the AirPods whose case was opened' -SinceUtc $testStartUtc
            $sittingCounterLines = Get-EarshotLogLines -Run $run -Pattern 'Widget counters:' -SinceUtc $testStartUtc
            if (@($sittingCounterLines).Count -gt 0)
            {
                $firstChosen = Get-CounterFigure -Line (@($sittingCounterLines)[0]) -Name 'chosen'
                $lastChosen = Get-CounterFigure -Line (@($sittingCounterLines)[-1]) -Name 'chosen'
                $chosenRose = ($null -ne $firstChosen -and $null -ne $lastChosen -and $lastChosen -gt $firstChosen)
            }
        }

        Add-Finding -Run $run -Name 'airPodsPickedOutLines' -Value @($pickedLines).Count -Detail 'Log lines saying the widget linked the AirPods whose case was opened, since the sitting began'
        Add-Finding -Run $run -Name 'chosenCounterRose' -Value $(if ($chosenRose) { 'yes' } else { 'no' }) -Detail 'Whether the counters'' chosen figure was higher in the newest counters line of the sitting than in its first'
        Add-Criterion -Run $run -Id 'airpods-picked-out' -Criterion 'With the case open next to this PC, the widget links the AirPods from what it hears: it linked them during the sitting, or its chosen counter is rising.' `
            -Outcome $(if (@($pickedLines).Count -gt 0 -or $chosenRose) { 'pass' } else { 'inconclusive' }) `
            -Detail $(if (@($pickedLines).Count -gt 0 -or $chosenRose) { [string]@($pickedLines).Count + ' linked line(s) since the sitting began; the chosen counter ' + $(if ($chosenRose) { 'rose from ' + $firstChosen + ' to ' + $lastChosen + '.' } else { 'did not rise.' }) } else { 'Nothing was linked since the sitting began and the chosen counter did not rise in about a minute: the case was not open with a bud in it near this PC, or the watcher is not running. A set linked before this sitting keeps being shown without a new line, so its counter is the thing to look at.' })
        $batteryAnswer = Read-Answer -Run $run -Question 'Compare the card with what your iPhone shows. Does every battery figure the card shows agree with the iPhone (give or take one step of 10), with nothing shown for a part the card has no figure for? If the card shows no battery figure at all, answer "not sure".'
        Add-Criterion -Run $run -Id 'battery-matches-iphone' -Criterion 'Every battery figure the card shows agrees with the iPhone, and nothing is shown for a part with no figure.' `
            -Outcome $(if ($batteryAnswer -eq 'yes') { 'pass' } elseif ($batteryAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $batteryAnswer + '. A "no" would mean a figure was shown that disagrees with the iPhone, or one for a part that was not heard, which is a real defect. "Not sure" is the answer when the card showed no figure at all (the AirPods were not connected to this PC, or no pair was linked), and it leaves this inconclusive: nothing was compared.')

        Write-Section -Run $run -Title 'The battery when it is old'
        Wait-Owner -Run $run -Text 'Close the case lid with the AirPods inside, and leave the card open for a minute.'
        $greyAnswer = Read-Answer -Run $run -Question 'A minute later, is every battery figure greyed, with how long ago it was read beside it? The figures stay whether or not closing the lid disconnected the AirPods. None may look current.'
        Add-Criterion -Run $run -Id 'battery-greys-when-old' -Criterion 'A battery figure older than 30 seconds is greyed and the card says how long ago it was read, whether or not the AirPods are connected to this PC; it is never shown as current.' `
            -Outcome $(if ($greyAnswer -eq 'yes') { 'pass' } elseif ($greyAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $greyAnswer + '. A "no" would mean an old figure was shown as current, which is a real defect.')

        Write-Section -Run $run -Title 'Refresh'
        Wait-Owner -Run $run -Text 'With the AirPods still shut in the case, click the circular arrow beside the gear on the card, and wait about twelve seconds.'
        $nothingHeardAnswer = Read-Answer -Run $run -Question 'After about twelve seconds, does the card say "Nothing heard. Open the case", with every figure still greyed and none looking current?'
        Add-Criterion -Run $run -Id 'refresh-says-nothing-heard' -Criterion 'A refresh with the AirPods shut in the case says nothing was heard and leaves any old figures greyed, never current.' `
            -Outcome $(if ($nothingHeardAnswer -eq 'yes') { 'pass' } elseif ($nothingHeardAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $nothingHeardAnswer + '.')
        Wait-Owner -Run $run -Text 'Open the case lid next to this computer and connect the AirPods to this PC (left-click the Earshot icon or the gauge, then Connect). Then click the circular arrow beside the gear and wait a few seconds.'
        $refreshedAnswer = Read-Answer -Run $run -Question 'Did the arrow turn while the card said "Reading the battery", and did the figures come back no longer greyed?'
        Add-Criterion -Run $run -Id 'refresh-reads-again' -Criterion 'A refresh with the case open turns the icon while it reads, then shows fresh figures.' `
            -Outcome $(if ($refreshedAnswer -eq 'yes') { 'pass' } elseif ($refreshedAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $refreshedAnswer + '.')
        $endedLines = Get-EarshotLogLines -Run $run -Pattern 'Widget: battery refresh ended:' -SinceUtc $batteryStartUtc
        Add-Finding -Run $run -Name 'refreshEndedLines' -Value @($endedLines).Count -Detail 'Log lines saying a battery refresh ended, since the battery steps began'
        # Each click the owner reports (the arrow turned, or the card said nothing was heard) ran one refresh, and each
        # refresh logs its own end: two clicks need two lines, so a single line cannot stand for both.
        $refreshesSeen = @($nothingHeardAnswer, $refreshedAnswer | Where-Object { $_ -eq 'yes' }).Count
        Add-Criterion -Run $run -Id 'refresh-logged' -Criterion 'Each refresh the card started is on the log with how it ended.' `
            -Outcome $(if ($refreshesSeen -eq 0) { 'inconclusive' } elseif (@($endedLines).Count -ge $refreshesSeen) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($endedLines).Count + ' "battery refresh ended" line(s) logged for the ' + $refreshesSeen + ' refresh(es) you reported; there should be one for each click.')

        Write-Section -Run $run -Title 'Half E: where, connected'
        Wait-Owner -Run $run -Text 'Connect the AirPods to this PC: left-click the Earshot icon or the gauge, then click Connect on the card.'
        $whereThisPcAnswer = Read-Answer -Run $run -Question 'With the AirPods connected to this PC, does the "where" line on the card say "On this PC"?'
        Add-Criterion -Run $run -Id 'where-this-pc' -Criterion 'The card reads "on this PC" from Core Audio alone.' `
            -Outcome $(if ($whereThisPcAnswer -eq 'yes') { 'pass' } elseif ($whereThisPcAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $whereThisPcAnswer + '.')

        Write-Section -Run $run -Title 'Half H: auto-pause, while it can still be exercised'
        Write-Line -Run $run -Text 'The AirPods are already connected and playing from the connect step above.'
        Wait-Owner -Run $run -Text 'Make sure something is playing from this PC through the AirPods, then take one bud out of your ear.'
        $autoPauseAnswer = Read-Answer -Run $run -Question 'Did the audio keep playing on this PC (auto-pause must not act, because Earshot cannot yet tell when a bud is out of your ear)?'
        Add-Criterion -Run $run -Id 'auto-pause-inert' -Criterion 'Auto-pause never acts while nothing can tell when a bud is out of the ear.' `
            -Outcome $(if ($autoPauseAnswer -eq 'yes') { 'pass' } elseif ($autoPauseAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $autoPauseAnswer + '. "Yes" (it kept playing) is the correct, honest state: Earshot cannot yet tell when a bud is in the ear.')

        Write-Section -Run $run -Title 'Half E: where, not connected'
        Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC again: left-click the Earshot icon or the gauge, then click Disconnect on the card.'
        $whereNotConnectedAnswer = Read-Answer -Run $run -Question 'With the AirPods not connected to this PC, does the card say "Not seen yet" rather than guessing whether they are on your phone or in the case?'
        Add-Criterion -Run $run -Id 'where-not-connected-honest' -Criterion 'When the AirPods are not connected to this PC and nothing recent is known, the card never guesses "on your phone" or "in the case": it says "Not seen yet".' `
            -Outcome $(if ($whereNotConnectedAnswer -eq 'yes') { 'pass' } elseif ($whereNotConnectedAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $whereNotConnectedAnswer + '.')

        Write-Section -Run $run -Title 'Half D: the case-open card'
        $caseOpenUtc = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Open your AirPods case next to this computer, without touching anything in Earshot.'
        $caseCardAnswer = Read-Answer -Run $run -Question 'Did any small card appear near the taskbar by itself, without you clicking anything?'
        $toggleLines = Get-EarshotLogLines -Run $run -Pattern 'connect: ' -SinceUtc $caseOpenUtc
        Add-Finding -Run $run -Name 'caseOpenToggleLinesSeen' -Value @($toggleLines).Count
        Add-Criterion -Run $run -Id 'case-open-card-honestly-not-shown' -Criterion 'While nothing can tell when the case lid is open, the case-open card correctly stays silent rather than showing an unproved reading.' `
            -Outcome $(if ($caseCardAnswer -eq 'no') { 'pass' } elseif ($caseCardAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $caseCardAnswer + '. "No" is the correct, honest state: Earshot cannot yet tell when the case lid is open; "yes" would mean a card appeared for a set of AirPods this build cannot yet confirm are the owner''s.')
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
        Add-Criterion -Run $run -Id 'low-battery-alert-fires' -Criterion 'The alert fires once a part whose figure is shown first reads at or below the threshold, and does not repeat.' `
            -Outcome 'inconclusive' `
            -Detail 'The alert acts on the figure the card shows, and this test does not set a threshold above your AirPods'' battery, so it does not exercise it.'

        Write-Section -Run $run -Title 'The ring and its colour'
        $ringAnswer = Read-Answer -Run $run -Question 'Is there a ring round the earbud mark on the gauge? (It appears only while a bud has a reading from the last hour, so no ring is a fine answer when the AirPods have been quiet.)'
        $accentAnswer = 'unsure'
        if ($ringAnswer -eq 'yes')
        {
            Wait-Owner -Run $run -Text 'Open Windows Settings, click Personalisation, click Colours, choose a different accent colour, then look at the gauge. Choose your usual accent colour again afterwards.'
            $accentAnswer = Read-Answer -Run $run -Question 'Was the ring filled in your Windows accent colour, and did it change to the new one when you changed it?'
        }

        Add-Finding -Run $run -Name 'gaugeRingShown' -Value $ringAnswer -Detail 'Whether the gauge showed a ring'
        Add-Criterion -Run $run -Id 'gauge-ring-accent' -Criterion 'The gauge ring is filled in the Windows accent colour and follows it when it changes.' `
            -Outcome $(if ($ringAnswer -ne 'yes') { 'inconclusive' } elseif ($accentAnswer -eq 'yes') { 'pass' } elseif ($accentAnswer -eq 'no') { 'fail' } else { 'inconclusive' }) `
            -Detail $(
                if ($ringAnswer -ne 'yes') { 'There was no ring (you answered ' + $ringAnswer + '), so nothing here could be looked at. A bud needs a reading from the broadcast first, which needs the AirPods out of the case or the case open.' }
                else { 'You answered ' + $accentAnswer + ' about the accent colour.' })

        Write-Section -Run $run -Title 'A reading older than an hour'
        Wait-Owner -Run $run -Text 'Put the AirPods in the case and close it, or take them away from this computer, and leave them for over an hour. Then hover over the gauge. If you cannot wait, answer "not sure" below.'
        $staleAnswer = Read-Answer -Run $run -Question 'More than an hour after the AirPods were last heard, does the gauge still show the last figure in grey (the case mark and the case figure if they disconnected, the earbud mark and the lower bud if they stayed connected), and does hovering say "Last read" or "Estimated" with how long ago?'
        Add-Criterion -Run $run -Id 'gauge-reading-older-than-an-hour' -Criterion 'A battery reading older than one hour stays on the gauge in grey, as a last reading or an estimate grown from it, and the tooltip says which and how old: the case figure with the case mark when the AirPods are not on this PC, the lower bud when they are.' `
            -Outcome $(if ($staleAnswer -eq 'yes') { 'pass' } elseif ($staleAnswer -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $staleAnswer + '. "Not sure" is the honest answer when you did not wait the hour.')

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
