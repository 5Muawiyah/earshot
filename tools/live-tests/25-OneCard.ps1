<#
.SYNOPSIS
    One card.

.DESCRIPTION
    Earshot has one popup, the main card. With the card closed, Connect and Disconnect show on the gauge
    (the ring spins, then a tick or a cross). Opening the case opens the main card, and shutting it closes the
    card. A case left shut shows nothing. When the iPhone takes the AirPods, a compact card says "Moved to"
    with a Take back icon; Disconnect in Earshot, and walking out of range, show nothing.

    The test walks those pictures with you. Each step records what you saw, and the Card, Case, Move and
    Battery lines Earshot logged while it happened, and holds each criterion to both: a picture you saw that
    the log does not show is a fail, and so is a log line for something you did not see.

    Step 4 matters most. The false show of the case card came about a minute after a real close, so a case
    merely left shut would pass on the old build. The test opens and shuts the lid once, then waits five
    minutes with the lid shut.

    The log lines are written without names or addresses, and the test asks you for no free text. Step 6, the
    sides and steps, is optional.

    What stays unproved: the rounds or floors of a step, which bud is which when a figure moves by one step,
    the charging bits, what a shut case that is charging or moving sends, what out of range looks like on the
    air, and whether "Moved to" is true when nothing was playing.

    It settles whether the gauge says Connect and Disconnect with the card closed, whether the failed Connect
    ends in a cross and then a card with a reason, whether the case opens and closes the card without taking
    focus, whether a shut case shows nothing for five minutes, and whether Moved to shows when the iPhone takes
    the AirPods and not otherwise.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\25-OneCard.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '25-one-card' -Title 'One card' `
    -Settles 'Whether the gauge says Connect and Disconnect with the card closed, whether the failed Connect ends in a cross and then a card with a reason, whether the case opens and closes the card without taking focus, whether a shut case shows nothing for five minutes, and whether Moved to shows when the iPhone takes the AirPods and not otherwise.' `
    -ExePath $ExePath -RunRoot $RunRoot

# The figure a Connect was built to: playing within 20 s of being asked for.
$BudgetMs = 20000

# How long the shut case is left alone. The false show came about a minute after a real close, so five minutes covers it with room.
$ShutCaseSeconds = 300

# The lines that show what the card, the case, a move and the battery did. Every one is read through Remove-Identifiers.
$StepPatterns = @('Card: ', 'Case: ', 'Move: ', 'Battery: ', 'Switch to-pc: ', 'Switch to-phone: ')

# The Connect and the Disconnect at the end leave the nodes as Earshot's own Disconnect does; the closing step blocks whatever is left.
$atRestReason = ''

# A log line with anything that could name a device or an address taken out: a Bluetooth address in either spelling, and a quoted name.
function Remove-Identifiers
{
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $clean = $Text -replace '\b[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}\b', '<address>'
    $clean = $clean -replace '\b[0-9A-Fa-f]{12}\b', '<address>'
    $clean = $clean -replace '"[^"]*"', '"<name>"'
    return $clean
}

# How many log lines since the moment match the pattern and the regular expression.
function Get-LogMatches
{
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Regex,
        [Parameter(Mandatory = $true)][datetime]$Since
    )

    $all = Get-EarshotLogLines -Run $run -Pattern $Pattern -SinceUtc $Since
    return ,@(@($all) | Where-Object { $_ -cmatch $Regex })
}

# Every Card, Case, Move, Battery and Switch line since the moment, in log order, with identifiers taken out and at most the cap kept.
function Get-StepLines
{
    param(
        [Parameter(Mandatory = $true)][datetime]$Since,
        [int]$Cap = 12
    )

    $found = @()
    foreach ($pattern in $StepPatterns)
    {
        $read = Get-EarshotLogLines -Run $run -Pattern $pattern -SinceUtc $Since
        $found = $found + @($read)
    }

    $sorted = @($found | Sort-Object)
    $kept = @()
    foreach ($line in $sorted)
    {
        if ($kept.Count -ge $Cap) { break }
        $kept = $kept + @(Remove-Identifiers -Text ([string]$line))
    }

    return ,$kept
}

# Records what the step logged as one finding, and says how many lines there were in all.
function Add-StepFinding
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][datetime]$Since
    )

    $lines = Get-StepLines -Since $Since
    Add-Finding -Run $run -Name $Name -Value ([string]@($lines).Count + ' lines: ' + (@($lines) -join ' | '))
}

# A picture that should have happened: what you saw, and whether the log shows it. $Logged is $true, $false, or $null when the log cannot say.
function Get-SeenVerdict
{
    param(
        [Parameter(Mandatory = $true)][string]$Seen,
        $Logged,
        [Parameter(Mandatory = $true)][string]$LoggedWords
    )

    if ($Seen -eq 'no') { return [ordered]@{ Outcome = 'fail'; Why = 'you said you did not see it' } }
    if ($Seen -eq 'unsure') { return [ordered]@{ Outcome = 'inconclusive'; Why = 'you were not sure what you saw' } }
    if ($null -eq $Logged) { return [ordered]@{ Outcome = 'inconclusive'; Why = 'you saw it, but the log could not say: ' + $LoggedWords } }
    if (-not $Logged) { return [ordered]@{ Outcome = 'fail'; Why = 'you saw it, but the log does not show it: ' + $LoggedWords } }
    return [ordered]@{ Outcome = 'pass'; Why = 'you saw it and the log shows it' }
}

# A picture that should not have happened: what you saw, how many log lines say it did, and how many show the step itself happened. With
# no such line nothing shows the step was run at all, so an empty log and a "no" do not pass.
function Get-QuietVerdict
{
    param(
        [Parameter(Mandatory = $true)][string]$Seen,
        [Parameter(Mandatory = $true)][int]$LogLines,
        [Parameter(Mandatory = $true)][int]$ControlLines,
        [Parameter(Mandatory = $true)][string]$ControlWords
    )

    if ($LogLines -gt 0) { return [ordered]@{ Outcome = 'fail'; Why = ([string]$LogLines + ' log line(s) say it showed') } }
    if ($Seen -eq 'yes') { return [ordered]@{ Outcome = 'fail'; Why = 'you saw it, though the log shows nothing' } }
    if ($Seen -eq 'unsure') { return [ordered]@{ Outcome = 'inconclusive'; Why = 'the log shows nothing; you were not sure what you saw' } }
    if ($ControlLines -eq 0) { return [ordered]@{ Outcome = 'inconclusive'; Why = 'you saw nothing, but ' + $ControlWords } }
    return [ordered]@{ Outcome = 'pass'; Why = 'you saw nothing, the log shows nothing and it shows the step happened' }
}

# The one switch line a shortcut left since the moment, as test 16 reads it. $true when exactly one line is a good reading, $false when
# the reading is bad, and $null when the log has none or several so it is not clear which was this one.
function Get-ShortcutSwitch
{
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Regex,
        [Parameter(Mandatory = $true)][datetime]$Since
    )

    $lines = Get-LogMatches -Pattern $Pattern -Regex $Regex -Since $Since
    if (@($lines).Count -eq 0) { return [ordered]@{ Good = $false; Ms = $null; Words = 'no line for the shortcut was logged' } }
    if (@($lines).Count -gt 1) { return [ordered]@{ Good = $null; Ms = $null; Words = ([string]@($lines).Count + ' lines for the shortcut were logged, so it is not clear which was this one') } }

    $figure = ''
    if (@($lines)[0] -cmatch $Regex) { $figure = $Matches[1] }
    if ($figure -cnotmatch '^[0-9]+$') { return [ordered]@{ Good = $null; Ms = $null; Words = 'the time was not measured' } }
    $ms = [int]$figure
    if ($ms -le $BudgetMs) { return [ordered]@{ Good = $true; Ms = $ms; Words = ('done after ' + $ms + ' ms') } }
    return [ordered]@{ Good = $false; Ms = $ms; Words = ('done only after ' + $ms + ' ms, more than ' + $BudgetMs + ' ms') }
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot 1.7.0 or later is installed, set up, and running in the tray.',
        'Block at boot is on.',
        'The AirPods are paired with this PC and with your iPhone, and your iPhone and the AirPods case are with you.',
        'The Earshot card is closed, and the shortcuts are the ones Earshot ships with: Ctrl+Alt+Shift+A to connect and Ctrl+Alt+Shift+D to disconnect.',
        'The Moved to switch under More in Earshot settings is on.'
    ) -PhysicalActions @(
        'Connect and disconnect the AirPods with the shortcuts while the card is closed.',
        'Put the AirPods in the shut case and try to connect them, then open and shut the case lid.',
        'Leave the case shut for five minutes without touching it.',
        'Play something on this PC and then on your iPhone so it takes the AirPods, then take them back from the card.'
    )

    if ($ready)
    {
        Add-Finding -Run $run -Name 'blockAtBootAtStart' -Value (Get-BlockAtBootSetting -Run $run)
        $testStart = (Get-Date).ToUniversalTime()

        # ---- 1. Connect and Disconnect by shortcut, card closed ----
        Write-Section -Run $run -Title 'Connect and Disconnect with the card closed'
        $connectStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Close the Earshot card if it is open. Put both AirPods in your ears, then press Ctrl+Alt+Shift+A to connect them. Watch the gauge: the ring spins, then a tick, then the reading of the AirPods. Come back here when it settles.'
        $connectSeen = Read-Answer -Run $run -Question 'With the card closed, did the ring on the gauge spin, then show a tick, then show the reading of the AirPods, with no card or popup opening?' -Options @('yes', 'no', 'unsure')
        $connectSwitch = Get-ShortcutSwitch -Pattern 'Switch to-pc: ' -Regex 'Switch to-pc: active after ([0-9]+|-) ms \(trigger shortcut-to-pc,' -Since $connectStart
        Add-StepFinding -Name 'connectShortcutLines' -Since $connectStart
        Add-Finding -Run $run -Name 'connectShortcutMs' -Value $connectSwitch.Ms
        $v = Get-SeenVerdict -Seen $connectSeen -Logged $connectSwitch.Good -LoggedWords $connectSwitch.Words
        Add-Criterion -Run $run -Id 'shortcut-connect-card-closed' -Criterion 'With the card closed, the connect shortcut spins the ring on the gauge, then shows a tick and the reading, and connects within 20 seconds.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + $connectSwitch.Words + '.')

        $disconnectStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC with Ctrl+Alt+Shift+D, with the card still closed. Watch the gauge: the ring spins, then the gauge says the AirPods are not on this PC. Come back here when it settles.'
        $disconnectSeen = Read-Answer -Run $run -Question 'With the card closed, did the ring spin and then the gauge say the AirPods are not on this PC, with no card or popup opening?' -Options @('yes', 'no', 'unsure')
        $disconnectSwitch = Get-ShortcutSwitch -Pattern 'Switch to-phone: ' -Regex 'Switch to-phone: released after ([0-9]+|-) ms, at rest after [0-9-]+ ms \(trigger shortcut-to-phone,' -Since $disconnectStart
        Add-StepFinding -Name 'disconnectShortcutLines' -Since $disconnectStart
        $v = Get-SeenVerdict -Seen $disconnectSeen -Logged $disconnectSwitch.Good -LoggedWords $disconnectSwitch.Words
        Add-Criterion -Run $run -Id 'shortcut-disconnect-card-closed' -Criterion 'With the card closed, the disconnect shortcut spins the ring and then shows the AirPods are not on this PC.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + $disconnectSwitch.Words + '.')

        # ---- 2. A failed Connect, the AirPods in the shut case ----
        Write-Section -Run $run -Title 'A failed Connect'
        $failStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Take the AirPods out of your ears, put them in the case and shut the lid. Then press Ctrl+Alt+Shift+A to connect them. Watch the gauge: the ring spins, then a cross, then a card tells you why it failed. Come back here when the card has shown.'
        $failSeen = Read-Answer -Run $run -Question 'Did the ring spin, then show a cross, and then did a card tell you why the connection failed?' -Options @('yes', 'no', 'unsure')
        $failedCard = Get-LogMatches -Pattern 'Card: ' -Regex 'Card: (shown unasked \(connect failed\), |connect failed, said in the open card)' -Since $failStart
        $notActive = Get-LogMatches -Pattern 'Switch to-pc: ' -Regex 'Switch to-pc: not active \(outcome [A-Za-z]+, trigger shortcut-to-pc,' -Since $failStart
        Add-StepFinding -Name 'failedConnectLines' -Since $failStart
        Add-Finding -Run $run -Name 'failedConnectNotActiveLines' -Value @($notActive).Count
        $v = Get-SeenVerdict -Seen $failSeen -Logged (@($failedCard).Count -gt 0) -LoggedWords 'no line says the failure card was shown'
        Add-Criterion -Run $run -Id 'failed-connect-cross-then-reason' -Criterion 'A Connect that fails with the AirPods in the shut case ends in a cross on the gauge, and then a card that gives the reason.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($failedCard).Count + ' failure card line(s), ' + @($notActive).Count + ' not-active switch line(s).')

        # ---- 3. The case opens the card and shutting it closes the card ----
        Write-Section -Run $run -Title 'The case opens the card'
        $openStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Close the failed card if it is still open. Start typing in any other window, then open the case lid next to this computer. Leave the lid open and do not touch Earshot.'
        $openSeen = Read-Answer -Run $run -Question 'Within ten seconds, did the Earshot card open by itself while the window you were typing in kept the keyboard focus?' -Options @('yes', 'no', 'unsure')
        $caseOpened = Get-LogMatches -Pattern 'Case: open by' -Regex 'Case: open by (link|burst): ' -Since $openStart
        $caseCardShown = Get-LogMatches -Pattern 'Card: shown unasked' -Regex 'Card: shown unasked \(case\), ' -Since $openStart
        Add-StepFinding -Name 'caseOpenLines' -Since $openStart
        $v = Get-SeenVerdict -Seen $openSeen -Logged (@($caseOpened).Count -gt 0 -and @($caseCardShown).Count -gt 0) -LoggedWords 'the log needs both an open case and a shown case card'
        Add-Criterion -Run $run -Id 'case-opens-the-card' -Criterion 'Opening the case next to this PC opens the main card by itself, without taking the keyboard focus.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($caseOpened).Count + ' open case line(s), ' + @($caseCardShown).Count + ' shown card line(s).')

        $shutStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Shut the case lid, and wait about fifteen seconds.'
        $shutSeen = Read-Answer -Run $run -Question 'Did the card close by itself after the lid was shut?' -Options @('yes', 'no', 'unsure')
        $caseClosedCard = Get-LogMatches -Pattern 'Card: closed' -Regex 'Card: closed \(case\), ' -Since $shutStart
        Add-StepFinding -Name 'caseShutLines' -Since $shutStart
        $v = Get-SeenVerdict -Seen $shutSeen -Logged (@($caseClosedCard).Count -gt 0) -LoggedWords 'no line says the case card closed'
        Add-Criterion -Run $run -Id 'case-shut-closes-the-card' -Criterion 'Shutting the case closes the card the case opened.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($caseClosedCard).Count + ' closed card line(s).')

        # ---- 4. The shut case shows nothing ----
        Write-Section -Run $run -Title 'The shut case'
        $shutStepStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Open the case lid next to this computer, then shut it. Leave it shut, and do not touch the case, the AirPods or Earshot for the next five minutes. Come back here once the lid is shut.'
        $quietStart = (Get-Date).ToUniversalTime()
        Wait-Seconds -Run $run -Seconds $ShutCaseSeconds -Reason 'leaving the shut case alone for five minutes'
        $quietSeen = Read-Answer -Run $run -Question 'While the case stayed shut, did any card open by itself?' -Options @('yes', 'no', 'unsure')
        $quietCards = Get-LogMatches -Pattern 'Card: shown unasked' -Regex 'Card: shown unasked \(case\), ' -Since $quietStart
        $quietOpens = Get-LogMatches -Pattern 'Case: open by' -Regex 'Case: open by (link|burst): ' -Since $quietStart
        # A closed line says how long the case was open, so one line shows both the open and the shut this step asks for.
        $quietControl = Get-LogMatches -Pattern 'Case: closed after' -Regex 'Case: closed after [0-9]+ s of silence: ' -Since $shutStepStart
        Add-StepFinding -Name 'shutCaseLines' -Since $quietStart
        $v = Get-QuietVerdict -Seen $quietSeen -LogLines (@($quietCards).Count + @($quietOpens).Count) -ControlLines @($quietControl).Count -ControlWords 'no line shows the case was seen open and then shut'
        Add-Criterion -Run $run -Id 'shut-case-no-card' -Criterion 'A case that was opened and shut once, then left shut for five minutes, shows no card and logs no open.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($quietCards).Count + ' shown card line(s), ' + @($quietOpens).Count + ' open case line(s) in ' + $ShutCaseSeconds + ' seconds.')

        # ---- 5. Moved to ----
        Write-Section -Run $run -Title 'Moved to'
        $movedStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Put both AirPods in your ears and press Ctrl+Alt+Shift+A to connect them. Wait for the tick.'
        Wait-Owner -Run $run -Text 'Play something on this PC, such as a video, until you can hear it in the AirPods. Then play something on your iPhone so that it takes the AirPods from this PC. Do not touch Earshot. Wait ten seconds.'
        $movedSeen = Read-Answer -Run $run -Question 'Within ten seconds, did a small card open by itself saying Moved to and the name of your iPhone, with a Take back icon?' -Options @('yes', 'no', 'unsure')
        $movedTrue = 'not asked'
        if ($movedSeen -eq 'yes')
        {
            $movedTrue = Read-Answer -Run $run -Question 'Was that true: did your iPhone have the AirPods at that moment?' -Options @('yes', 'no', 'unsure')
        }

        $movedCards = Get-LogMatches -Pattern 'Card: shown unasked' -Regex 'Card: shown unasked \(moved to\), ' -Since $movedStart
        $movedVerdicts = Get-LogMatches -Pattern 'Move: ' -Regex 'Move: [0-9]+ messages? by \+[0-9]+ ms.*: moved\.' -Since $movedStart
        Add-StepFinding -Name 'movedToLines' -Since $movedStart
        Add-Finding -Run $run -Name 'movedToTrue' -Value $movedTrue
        if ($movedSeen -eq 'yes' -and $movedTrue -eq 'no') { $v = [ordered]@{ Outcome = 'fail'; Why = 'the card said Moved to, but your iPhone did not have the AirPods' } }
        elseif ($movedSeen -eq 'yes' -and $movedTrue -ne 'yes') { $v = [ordered]@{ Outcome = 'inconclusive'; Why = 'the card showed, and you were not sure it was true' } }
        else { $v = Get-SeenVerdict -Seen $movedSeen -Logged (@($movedCards).Count -gt 0 -and @($movedVerdicts).Count -gt 0) -LoggedWords 'the log needs a shown Moved to card and a moved verdict' }
        Add-Criterion -Run $run -Id 'moved-to-shows-and-is-true' -Criterion 'When the iPhone takes the AirPods, a compact card says Moved to within ten seconds, and it is true.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($movedCards).Count + ' shown card line(s), ' + @($movedVerdicts).Count + ' moved verdict line(s).')

        $takeStart = (Get-Date).ToUniversalTime()
        if ($movedSeen -eq 'yes')
        {
            Wait-Owner -Run $run -Text 'Press the Take back icon on the card. Wait for the tick.'
            $takeSeen = Read-Answer -Run $run -Question 'Did the Take back icon spin and then show a tick, and are the AirPods playing from this PC again?' -Options @('yes', 'no', 'unsure')
            $takePressed = Get-LogMatches -Pattern 'Card: button pressed' -Regex 'Card: button pressed \(Take back\)\.' -Since $takeStart
            Add-StepFinding -Name 'takeBackLines' -Since $takeStart
            $v = Get-SeenVerdict -Seen $takeSeen -Logged (@($takePressed).Count -gt 0) -LoggedWords 'no line says Take back was pressed'
            Add-Criterion -Run $run -Id 'take-back-returns-the-airpods' -Criterion 'Take back on the Moved to card connects the AirPods to this PC again, with a spin and then a tick.' `
                -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($takePressed).Count + ' Take back press line(s).')
        }
        else
        {
            Add-Criterion -Run $run -Id 'take-back-returns-the-airpods' -Criterion 'Take back on the Moved to card connects the AirPods to this PC again, with a spin and then a tick.' `
                -Outcome 'inconclusive' -Detail 'not reached: no Moved to card showed, so there was nothing to press.'
        }

        $ownStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon and choose Disconnect. Then do nothing for thirty seconds.'
        Wait-Seconds -Run $run -Seconds 30 -Reason 'watching for a Moved to card after Disconnect in Earshot'
        $ownSeen = Read-Answer -Run $run -Question 'After you chose Disconnect in Earshot, did a Moved to card open?' -Options @('yes', 'no', 'unsure')
        $ownCards = Get-LogMatches -Pattern 'Card: shown unasked' -Regex 'Card: shown unasked \(moved to\), ' -Since $ownStart
        $ownVerdicts = Get-LogMatches -Pattern 'Move: ' -Regex 'Move: [0-9]+ messages? by \+[0-9]+ ms.*: moved\.' -Since $ownStart
        $ownControl = Get-LogMatches -Pattern 'Move: own leave' -Regex 'Move: own leave \(' -Since $ownStart
        Add-StepFinding -Name 'ownDisconnectLines' -Since $ownStart
        $v = Get-QuietVerdict -Seen $ownSeen -LogLines (@($ownCards).Count + @($ownVerdicts).Count) -ControlLines @($ownControl).Count -ControlWords 'no line shows the Disconnect in Earshot happened'
        Add-Criterion -Run $run -Id 'own-disconnect-shows-nothing' -Criterion 'Disconnect in Earshot never shows Moved to.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($ownCards).Count + ' shown card line(s), ' + @($ownVerdicts).Count + ' moved verdict line(s).')

        $rangeStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Press Ctrl+Alt+Shift+A to connect the AirPods again and wait for the tick. Play something on this PC until you can hear it in the AirPods. Then, with the AirPods in your ears, walk out of range of this PC, for example into another room, and stay away for one minute. Then come back here.'
        $rangeSeen = Read-Answer -Run $run -Question 'When you came back, had a Moved to card opened on this PC while you were away?' -Options @('yes', 'no', 'unsure')
        $rangeCards = Get-LogMatches -Pattern 'Card: shown unasked' -Regex 'Card: shown unasked \(moved to\), ' -Since $rangeStart
        $rangeVerdicts = Get-LogMatches -Pattern 'Move: ' -Regex 'Move: [0-9]+ messages? by \+[0-9]+ ms.*: moved\.' -Since $rangeStart
        $rangeControl = Get-LogMatches -Pattern 'Move: leave seen' -Regex 'Move: leave seen, not started by Earshot, ' -Since $rangeStart
        Add-StepFinding -Name 'outOfRangeLines' -Since $rangeStart
        $v = Get-QuietVerdict -Seen $rangeSeen -LogLines (@($rangeCards).Count + @($rangeVerdicts).Count) -ControlLines @($rangeControl).Count -ControlWords 'no line shows the AirPods left this PC'
        Add-Criterion -Run $run -Id 'out-of-range-shows-nothing' -Criterion 'Walking out of range with the AirPods never shows Moved to.' `
            -Outcome $v.Outcome -Detail ([string]$v.Why + '; ' + @($rangeCards).Count + ' shown card line(s), ' + @($rangeVerdicts).Count + ' moved verdict line(s).')

        # ---- 6. Sides and steps, if you offer ----
        Write-Section -Run $run -Title 'Sides and steps'
        $sidesStart = (Get-Date).ToUniversalTime()
        $offer = Read-Answer -Run $run -Question 'Do you want to check the sides and steps now, with your iPhone beside this PC?' -Options @('yes', 'no')
        if ($offer -eq 'yes')
        {
            Wait-Owner -Run $run -Text 'Take both AirPods out of the case, side by side, with the battery screen of your iPhone beside this PC. Open the Earshot card and look at the left and right figures.'
            $bothSeen = Read-Answer -Run $run -Question 'Do the left and right figures on the card match the iPhone, each within one step of ten?' -Options @('yes', 'no', 'unsure')
            Wait-Owner -Run $run -Text 'Put the left AirPod in the case and keep the right one out. Wait ten seconds, then look at the card and your iPhone again.'
            $rightSeen = Read-Answer -Run $run -Question 'Does the card show the figure of the right AirPod, within one step of ten of the iPhone, and not the figure of the left one?' -Options @('yes', 'no', 'unsure')
            Wait-Owner -Run $run -Text 'Swap them: put the right AirPod in the case and take the left one out. Wait ten seconds, then look at the card and your iPhone again.'
            $leftSeen = Read-Answer -Run $run -Question 'Does the card show the figure of the left AirPod, within one step of ten of the iPhone, and not the figure of the right one?' -Options @('yes', 'no', 'unsure')
            Add-Finding -Run $run -Name 'sidesAndStepsAnswers' -Value ([string]$bothSeen + ', ' + $rightSeen + ', ' + $leftSeen)
            $answers = @($bothSeen, $rightSeen, $leftSeen)
            if ($answers -contains 'no') { $o = 'fail'; $w = 'you said a figure did not match your iPhone''s within one step' }
            elseif ($answers -contains 'unsure') { $o = 'inconclusive'; $w = 'you were not sure of every figure' }
            else { $o = 'pass'; $w = 'both sides matched your iPhone''s within one step, with a bud out each way' }
            Add-Criterion -Run $run -Id 'sides-and-steps' -Criterion 'With the AirPods side by side with the iPhone and one AirPod out each way, the left and right figures match the iPhone''s within one step of ten.' `
                -Outcome $o -Detail ([string]$w + '.')
        }
        else
        {
            Add-Finding -Run $run -Name 'sidesAndStepsOffered' -Value 'no'
            Add-Criterion -Run $run -Id 'sides-and-steps' -Criterion 'With the AirPods side by side with the iPhone and one AirPod out each way, the left and right figures match the iPhone''s within one step of ten.' `
                -Outcome 'inconclusive' -Detail 'not reached: you chose not to check the sides and steps.'
        }

        # The broadcast figures the gauge showed in the whole run: each is a plain number in steps of ten.
        $gaugeRead = Get-EarshotLogLines -Run $run -Pattern 'Battery: gauge' -SinceUtc $testStart
        $gaugeLines = @(@($gaugeRead) | Where-Object { $_ -cmatch 'Battery: gauge [0-9]+ from (left|right), ' })
        $offStep = @()
        foreach ($gaugeLine in @($gaugeLines))
        {
            if (([string]$gaugeLine) -cmatch 'Battery: gauge ([0-9]+) from ')
            {
                if (([int]$Matches[1] % 10) -ne 0) { $offStep = $offStep + @($gaugeLine) }
            }
        }

        Add-Finding -Run $run -Name 'gaugeFigureLines' -Value @($gaugeLines).Count
        if (@($gaugeLines).Count -eq 0) { $o = 'inconclusive'; $w = 'no gauge figure from a bud was logged' }
        elseif (@($offStep).Count -gt 0) { $o = 'fail'; $w = ([string]@($offStep).Count + ' of ' + @($gaugeLines).Count + ' gauge figure(s) were not in steps of ten') }
        else { $o = 'pass'; $w = 'all ' + @($gaugeLines).Count + ' gauge figure(s) were in steps of ten' }
        Add-Criterion -Run $run -Id 'battery-in-steps-of-ten' -Criterion 'Every figure the gauge showed from the AirPods'' broadcast was a plain number in steps of ten.' `
            -Outcome $o -Detail ([string]$w + '.')

        # The run leaves the PC at rest: the AirPods are let go with the shortcut, and the closing step blocks whatever is left.
        Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC with Ctrl+Alt+Shift+D if they are connected, then wait ten seconds.'

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
    Write-Host ('Test 25 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
