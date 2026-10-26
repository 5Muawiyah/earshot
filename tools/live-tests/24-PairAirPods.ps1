<#
.SYNOPSIS
    Pair AirPods.

.DESCRIPTION
    Earshot's Pair AirPods must pair the owner's AirPods from the tray with the case in pairing mode,
    take them as the pair Earshot uses, set itself up with one Windows permission prompt and connect
    them once. Before that, the radio check must say "Bluetooth is off." while Bluetooth is off and
    stop saying it when Bluetooth is back on.

    The test turns Bluetooth off and on, asks you to remove your AirPods in Windows, then walks the
    Pair AirPods steps with the iPhone's Bluetooth off. It reads the lines Earshot logs for the radio
    check and for pairing, and the last pairing run in Earshot's own report (doctor), and holds each
    criterion to those lines. One Connect and one Disconnect follow. The Bluetooth trace is optional.

    What it records but cannot prove from the log: whether the list showed your AirPods with a name, what
    Earshot asked while pairing (nothing, a yes or no, or a number), whether Windows showed a box of
    its own, and the minor class the scan saw (Earshot does not log it, so it is only asked for).
    What stays unproved: the radio lines for a switch, a broken driver or no radio, the empty-name
    signal, undoing the pairing of a device that is not AirPods (no other headphones may be paired),
    and AirPods models other than yours.

    It settles whether Pair AirPods pairs and adopts the AirPods from the tray, sets up with one prompt,
    shows no old reading, connects once, and whether the radio check reads a real radio turned off.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\24-PairAirPods.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '24-pair-airpods' -Title 'Pair AirPods' `
    -Settles 'Whether Pair AirPods pairs and adopts the AirPods from the tray, sets up with one prompt, shows no old reading and connects once, and whether the radio check reads a real radio turned off.' `
    -ExePath $ExePath -RunRoot $RunRoot

# The figure this release was built to for a Connect: playing within 20 s of being asked for.
$BudgetMs = 20000

# The Connect and the Disconnect at the end leave the nodes as Earshot's own Disconnect does; the closing step blocks whatever is left.
$atRestReason = ''

# The "Switch to-pc" line a click on the card left since the given moment, as test 23 reads it. Exactly one line is a reading.
function Get-ConnectAttempt
{
    param([Parameter(Mandatory = $true)][datetime]$Since)

    $n = '([0-9]+|-)'
    $all = Get-EarshotLogLines -Run $run -Pattern 'Switch to-pc: ' -SinceUtc $Since
    $lines = @(@($all) | Where-Object { $_ -match 'trigger click,' })
    if ($lines.Count -eq 0) { return [ordered]@{ Outcome = 'fail'; Ms = $null; Why = 'no Connect from the card was logged' } }
    if ($lines.Count -gt 1) { return [ordered]@{ Outcome = 'inconclusive'; Ms = $null; Why = ([string]$lines.Count + ' Connects from the card were logged, so it is not clear which was this one') } }

    $line = $lines[0]
    if ($line -cmatch ('Switch to-pc: active after ' + $n + ' ms \(trigger click, path ([a-z-]+),'))
    {
        $figure = $Matches[1]
        $path = $Matches[2]
        if ($figure -cnotmatch '^[0-9]+$') { return [ordered]@{ Outcome = 'inconclusive'; Ms = $null; Why = 'the time to playing was not measured' } }
        $ms = [int]$figure
        if ($ms -le $BudgetMs) { return [ordered]@{ Outcome = 'pass'; Ms = $ms; Why = ('playing after ' + $ms + ' ms by the ' + $path + ' path') } }
        return [ordered]@{ Outcome = 'fail'; Ms = $ms; Why = ('playing only after ' + $ms + ' ms, more than ' + $BudgetMs + ' ms') }
    }

    if ($line -cmatch 'Switch to-pc: not active \(outcome ([A-Za-z]+), trigger click, path ([a-z-]+), blocked-again (yes|no|not-needed),')
    {
        return [ordered]@{ Outcome = 'fail'; Ms = $null; Why = ('not connected: outcome ' + $Matches[1] + ', blocked again ' + $Matches[3]) }
    }

    return [ordered]@{ Outcome = 'inconclusive'; Ms = $null; Why = 'a Connect line whose figures could not be read' }
}

# Earshot's own report (doctor, read-only), written to the evidence folder. $null when it gave nothing.
function Get-DoctorReport
{
    param([Parameter(Mandatory = $true)][string]$Label)

    $evidence = Join-Path $run.Folder ([string]$Label + '.json')
    $result = Invoke-Earshot -Run $run -Label $Label -Command @('doctor', '--out', $evidence)
    if ($null -eq $result) { return $null }
    return $result.json
}

# The radio check's verdict by name (Ok, Off, SwitchedOff, Problem, NoAdapter, Unknown), or $null when the report has none.
function Get-RadioVerdict
{
    param($Report)

    $radio = Get-Field -Object $Report -Name 'radio'
    $state = Get-Field -Object $radio -Name 'state'
    if ($null -eq $state) { return $null }
    return [string]$state
}

# The pairing runs in the report that began at or after the given moment, newest last.
function Get-PairingRuns
{
    param($Report, [Parameter(Mandatory = $true)][datetime]$Since)

    $runs = Get-Field -Object $Report -Name 'pairing'
    $found = @()
    foreach ($entry in @($runs))
    {
        if ($null -eq $entry) { continue }
        $time = Get-Field -Object $entry -Name 'time'
        $stamp = $null
        if ($time -is [datetime]) { $stamp = $time.ToUniversalTime() }
        elseif ($null -ne $time)
        {
            $parsed = [datetime]::MinValue
            if ([datetime]::TryParse([string]$time, [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed))
            {
                $stamp = $parsed
            }
        }

        if ($null -ne $stamp -and $stamp -ge $Since) { $found = $found + @($entry) }
    }

    return ,$found
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

# What the AirPods' own entries in Windows read as, for a finding: present, absent, disabled and flagged.
function Get-NodeSummary
{
    param($Nodes)

    if ($null -eq $Nodes) { return 'unreadable' }
    $listed = Get-Field -Object $Nodes -Name 'nodes'
    $targets = @(@($listed) | Where-Object { $null -ne $_ -and (Get-Field -Object $_ -Name 'target') -eq $true })
    $present = @($targets | Where-Object { (Get-Field -Object $_ -Name 'present') -eq $true }).Count
    return ([string](Get-Field -Object $Nodes -Name 'nodeState') + ': ' + $present + ' present, ' + ($targets.Count - $present) + ' absent')
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot 1.5.0 or later is installed, set up, and running in the tray.',
        'Block at boot is on.',
        'The AirPods are paired with this PC and with your iPhone, and your iPhone and the AirPods case are with you.',
        'No other Bluetooth headphones or speakers are paired with this PC.'
    ) -PhysicalActions @(
        'Turn Bluetooth off and on again in quick settings.',
        'Remove your AirPods in Windows Settings, then pair them again with Pair AirPods in Earshot, with the case in pairing mode and Bluetooth off on your iPhone.',
        'Allow Earshot when Windows asks, once, for set-up.',
        'Open the case next to this PC, then connect once from the Earshot card.'
    )

    if ($ready)
    {
        Add-Finding -Run $run -Name 'blockAtBootAtStart' -Value (Get-BlockAtBootSetting -Run $run)

        # ---- Bluetooth off, then on ----
        Write-Section -Run $run -Title 'Bluetooth off, then on'
        $offStart = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Open quick settings and turn Bluetooth off. Then left-click the Earshot icon and choose Connect on the card; it will not connect, and that is expected. Read what the card says, then come back here.'
        $report = Get-DoctorReport -Label 'doctor-bluetooth-off'
        $verdictOff = Get-RadioVerdict -Report $report
        $offLines = Get-LogMatches -Pattern 'radio check: Off in' -Regex 'radio check: Off in [0-9]+ ms\.' -Since $offStart
        $card = Read-Answer -Run $run -Question 'Did the Earshot card say "Bluetooth is off."?' -Options @('yes', 'no', 'unsure')
        Add-Finding -Run $run -Name 'radioVerdictOff' -Value $verdictOff
        Add-Finding -Run $run -Name 'cardSaidBluetoothOff' -Value $card

        $offOutcome = 'pass'
        $offWhy = 'the radio check read Off, logged it, and the card said so'
        if ($null -eq $verdictOff) { $offOutcome = 'inconclusive'; $offWhy = 'the report gave no radio verdict' }
        elseif ($verdictOff -ne 'Off') { $offOutcome = $(if ($verdictOff -eq 'Ok') { 'fail' } else { 'inconclusive' }); $offWhy = 'the radio check read ' + $verdictOff + ' with Bluetooth turned off' }
        elseif ($offLines.Count -eq 0) { $offOutcome = 'fail'; $offWhy = 'the radio check read Off but no "radio check: Off" line was logged' }
        elseif ($card -eq 'no') { $offOutcome = 'fail'; $offWhy = 'the radio check read Off but the card did not say "Bluetooth is off."' }
        elseif ($card -eq 'unsure') { $offOutcome = 'inconclusive'; $offWhy = 'the radio check read Off and logged it; you were not sure what the card said' }
        Add-Criterion -Run $run -Id 'radio-off-seen' -Criterion 'With Bluetooth turned off, the radio check read Off and the card said "Bluetooth is off."' `
            -Outcome $offOutcome -Detail ($offWhy + '.')

        Wait-Owner -Run $run -Text 'Turn Bluetooth back on in quick settings. Watch the Earshot card if it is still open. Wait until the line goes, or ten seconds, then come back here.'
        $report = Get-DoctorReport -Label 'doctor-bluetooth-on'
        $verdictOn = Get-RadioVerdict -Report $report
        $gone = Read-Answer -Run $run -Question 'Did the "Bluetooth is off." line go from the card once Bluetooth was on?' -Options @('yes', 'no', 'unsure')
        Add-Finding -Run $run -Name 'radioVerdictOn' -Value $verdictOn
        Add-Finding -Run $run -Name 'cardLineWentAway' -Value $gone

        $onOutcome = 'pass'
        $onWhy = 'the radio check read Ok again and the line went'
        if ($null -eq $verdictOn) { $onOutcome = 'inconclusive'; $onWhy = 'the report gave no radio verdict' }
        elseif ($verdictOn -ne 'Ok') { $onOutcome = $(if ($verdictOn -eq 'Off') { 'fail' } else { 'inconclusive' }); $onWhy = 'the radio check read ' + $verdictOn + ' with Bluetooth turned on' }
        elseif ($gone -eq 'no') { $onOutcome = 'fail'; $onWhy = 'the radio check read Ok but the line stayed on the card' }
        elseif ($gone -eq 'unsure') { $onOutcome = 'inconclusive'; $onWhy = 'the radio check read Ok; you were not sure the line went' }
        Add-Criterion -Run $run -Id 'radio-cleared' -Criterion 'With Bluetooth back on, the radio check read Ok and the "Bluetooth is off." line went.' `
            -Outcome $onOutcome -Detail ($onWhy + '.')

        # ---- Pair AirPods ----
        Write-Section -Run $run -Title 'Pair AirPods'
        $pairingCriteria = @(
            @{ Id = 'listed-with-name'; Text = 'The list showed the AirPods with a name.' }
            @{ Id = 'pair-paired'; Text = 'Pair AirPods paired the AirPods from the tray and logged that it did.' }
            @{ Id = 'pair-radio-checked'; Text = 'Pair AirPods read the radio first and found it Ok.' }
            @{ Id = 'model-check'; Text = 'The model check found the new pair to be AirPods.' }
            @{ Id = 'pair-adopted'; Text = 'Earshot took the new pair as the one it uses.' }
            @{ Id = 'setup-one-prompt'; Text = 'Set-up asked for one Windows permission prompt and Earshot is set up.' }
            @{ Id = 'no-old-reading'; Text = 'No old battery reading showed after the new pair, and the new reading appeared once the case was opened.' }
            @{ Id = 'connect-after-pairing'; Text = 'One Connect from the card reached playing within 20 seconds.' }
            @{ Id = 'trace-saved'; Text = 'The Bluetooth trace recorded and was saved.' }
        )

        Wait-Owner -Run $run -Text 'Open Windows Settings, Bluetooth and devices, find your AirPods, open the three dots and choose Remove device. Then come back here.'
        $afterRemoval = Get-NodeSummary -Nodes (Get-NodeState -Run $run -Label 'nodes-after-removal')
        Add-Finding -Run $run -Name 'nodesAfterRemoval' -Value $afterRemoval

        $can = Read-Answer -Run $run -Question 'Can you put the case into pairing mode now, with Bluetooth off on your iPhone?' -Options @('yes', 'no')
        if ($can -ne 'yes')
        {
            Add-Finding -Run $run -Name 'pairReached' -Value 'not reached'
            foreach ($item in $pairingCriteria)
            {
                Add-Criterion -Run $run -Id $item.Id -Criterion $item.Text -Outcome 'inconclusive' -Detail 'not reached: you could not put the case into pairing mode.'
            }
        }
        else
        {
            Add-Finding -Run $run -Name 'pairReached' -Value 'reached'
            Wait-Owner -Run $run -Text 'On your iPhone, open Settings, Bluetooth, and turn Bluetooth off. Then come back here.'
            $model = Read-Answer -Run $run -Question 'Which AirPods do you have?' -Options @('airpods-pro-3-or-4', 'other')
            Add-Finding -Run $run -Name 'airpodsModelChoice' -Value $model
            $pairStart = (Get-Date).ToUniversalTime()
            Wait-Owner -Run $run -Text 'Right-click the Earshot icon and choose Pair AirPods. Earshot checks Bluetooth, then waits for you to press Look for AirPods. Do not press it yet. Come back here.'
            if ($model -eq 'airpods-pro-3-or-4')
            {
                Wait-Owner -Run $run -Text 'Open the lid of the case, then double-tap the front of the case until the light flashes white. Then come back here.'
            }
            else
            {
                Wait-Owner -Run $run -Text 'Open the lid of the case, then hold the button on the back until the light flashes white. Then come back here.'
            }

            Wait-Owner -Run $run -Text 'Press Look for AirPods in Earshot. When the list "Choose your AirPods" shows, look at it, and come back here without choosing yet.'
            $named = Read-Answer -Run $run -Question 'Does the list show your AirPods with a name?' -Options @('yes', 'no')
            Add-Finding -Run $run -Name 'listedWithName' -Value $named
            $minor = Read-Note -Run $run -Question 'What else did the list show about your AirPods, if anything? Type it, or none.'
            Add-Finding -Run $run -Name 'listNote' -Value $minor
            Add-Criterion -Run $run -Id 'listed-with-name' -Criterion 'The list showed the AirPods with a name.' `
                -Outcome $(if ($named -eq 'yes') { 'pass' } else { 'fail' }) `
                -Detail $(if ($named -eq 'yes') { 'the list showed the AirPods with a name.' } else { 'the list showed the AirPods as "Headphones with no name"; that is the signal the empty-name rule watches for.' })

            Wait-Owner -Run $run -Text 'Choose your AirPods in the list and press Pair. If Earshot asks "Does your device show ...", answer it. Allow anything Windows asks. Then come back here when Earshot says Paired, or gives a reason.'
            $asked = Read-Answer -Run $run -Question 'What did Earshot ask while pairing?' -Options @('nothing', 'yes-or-no', 'number')
            $dialog = Read-Answer -Run $run -Question 'Did Windows show a pairing box of its own?' -Options @('yes', 'no')
            Add-Finding -Run $run -Name 'pairingAsked' -Value $asked
            Add-Finding -Run $run -Name 'windowsShowedItsOwnBox' -Value $dialog

            # Set-up follows the pairing: one administrator prompt.
            Wait-Owner -Run $run -Text 'If Windows asks for permission for Earshot, allow it once. Wait until the Earshot card says it is using your AirPods, then come back here.'
            $prompts = Read-Answer -Run $run -Question 'How many Windows permission prompts did Earshot raise?' -Options @('none', 'one', 'more')
            Add-Finding -Run $run -Name 'permissionPrompts' -Value $prompts

            # The log and the report are read before anything else is asked of Earshot.
            $started = Get-LogMatches -Pattern 'Pair AirPods: ' -Regex 'Pair AirPods: started from the menu\.' -Since $pairStart
            $paired = Get-LogMatches -Pattern 'Pair AirPods: ' -Regex 'Pair AirPods: Paired, None\.' -Since $pairStart
            $failed = Get-LogMatches -Pattern 'Pair AirPods: ' -Regex 'Pair AirPods: (Failed|Cancelled|NotAirPods), [A-Za-z]+\.' -Since $pairStart
            $adopted = Get-LogMatches -Pattern 'Pair AirPods: ' -Regex 'Pair AirPods: the new pair is the one Earshot uses\.' -Since $pairStart
            $notAdopted = Get-LogMatches -Pattern 'Pair AirPods: ' -Regex 'Pair AirPods: the new pair was not adopted\.' -Since $pairStart
            $answered = Get-LogMatches -Pattern 'pair: Windows answered ' -Regex 'pair: Windows answered (Paired|AlreadyPaired) \(-?[0-9]+\)\.' -Since $pairStart
            $radioOk = Get-LogMatches -Pattern 'radio check: Ok in' -Regex 'radio check: Ok in [0-9]+ ms\.' -Since $pairStart
            if ($failed.Count -gt 0) { Add-Finding -Run $run -Name 'pairFailureLine' -Value ([string]$failed[0] -replace '^.*?(Pair AirPods: )', '$1') }

            $report = Get-DoctorReport -Label 'doctor-after-pair'
            $runs = Get-PairingRuns -Report $report -Since $pairStart
            $lastRun = $(if ($runs.Count -gt 0) { $runs[$runs.Count - 1] } else { $null })
            $modelCheck = [string](Get-Field -Object $lastRun -Name 'modelCheck')
            Add-Finding -Run $run -Name 'pairingRunsSinceStart' -Value $runs.Count
            Add-Finding -Run $run -Name 'pairingRunOutcome' -Value (Get-Field -Object $lastRun -Name 'outcome')
            Add-Finding -Run $run -Name 'pairingRunStatus' -Value (Get-Field -Object $lastRun -Name 'status')
            Add-Finding -Run $run -Name 'pairingRunDurationMs' -Value (Get-Field -Object $lastRun -Name 'durationMs')

            # pair-paired
            if ($started.Count -eq 0) { $o = 'fail'; $w = 'no "Pair AirPods: started from the menu" line was logged' }
            elseif ($paired.Count -gt 0 -and $answered.Count -gt 0) { $o = 'pass'; $w = 'Windows answered Paired and Earshot logged "Pair AirPods: Paired"' }
            elseif ($failed.Count -gt 0) { $o = 'fail'; $w = 'Earshot logged ' + ([string]$failed[0] -replace '^.*?(Pair AirPods: )', '$1') }
            else { $o = 'fail'; $w = 'the pairing started but no line says it paired' }
            Add-Criterion -Run $run -Id 'pair-paired' -Criterion 'Pair AirPods paired the AirPods from the tray and logged that it did.' -Outcome $o -Detail ($w + '.')

            # pair-radio-checked
            Add-Criterion -Run $run -Id 'pair-radio-checked' -Criterion 'Pair AirPods read the radio first and found it Ok.' `
                -Outcome $(if ($radioOk.Count -gt 0) { 'pass' } else { 'fail' }) `
                -Detail $(if ($radioOk.Count -gt 0) { 'a "radio check: Ok" line was logged after Pair AirPods was chosen.' } else { 'no "radio check: Ok" line was logged after Pair AirPods was chosen.' })

            # model-check
            if ($runs.Count -eq 0) { $o = 'inconclusive'; $w = 'the report lists no pairing run since the start' }
            elseif ($modelCheck -eq 'airpods') { $o = 'pass'; $w = 'the pairing run records the model check as airpods' }
            elseif ($modelCheck -eq 'not-airpods') { $o = 'fail'; $w = 'the pairing run records the model check as not-airpods' }
            else { $o = 'fail'; $w = 'the pairing run ended before the model check' }
            Add-Criterion -Run $run -Id 'model-check' -Criterion 'The model check found the new pair to be AirPods.' -Outcome $o -Detail ($w + '.')

            # pair-adopted
            if ($adopted.Count -gt 0 -and $notAdopted.Count -eq 0) { $o = 'pass'; $w = 'Earshot logged that the new pair is the one it uses' }
            elseif ($notAdopted.Count -gt 0) { $o = 'fail'; $w = 'Earshot logged that the new pair was not adopted' }
            else { $o = 'fail'; $w = 'no line says the new pair was adopted' }
            Add-Criterion -Run $run -Id 'pair-adopted' -Criterion 'Earshot took the new pair as the one it uses.' -Outcome $o -Detail ($w + '.')

            # setup-one-prompt
            $task = Get-TaskState -Run $run -Label 'task-after-pair'
            $setUp = (Get-Field -Object $task -Name 'setUp') -eq $true
            Add-Finding -Run $run -Name 'setUpAfterPair' -Value $setUp
            if ($null -eq $task) { $o = 'inconclusive'; $w = 'the set-up state could not be read' }
            elseif (-not $setUp) { $o = 'fail'; $w = 'Earshot is not set up after the pairing' }
            elseif ($prompts -eq 'one') { $o = 'pass'; $w = 'one permission prompt, and Earshot is set up' }
            elseif ($prompts -eq 'more') { $o = 'fail'; $w = 'more than one permission prompt' }
            else { $o = 'inconclusive'; $w = 'no permission prompt was raised, so Earshot was set up already and the one-prompt set-up was not exercised' }
            Add-Criterion -Run $run -Id 'setup-one-prompt' -Criterion 'Set-up asked for one Windows permission prompt and Earshot is set up.' -Outcome $o -Detail ($w + '.')

            # no-old-reading
            $before = Read-Answer -Run $run -Question 'Before you open the case: does the Earshot gauge or card show a battery figure for the AirPods?' -Options @('yes', 'no')
            Wait-Owner -Run $run -Text 'Put both AirPods in the case and close the lid. Wait ten seconds, then open the lid next to this PC and leave it open for half a minute. Then come back here.'
            $after = Read-Answer -Run $run -Question 'Now the case is open: does a battery figure appear for the AirPods?' -Options @('yes', 'no')
            Add-Finding -Run $run -Name 'readingBeforeCaseOpened' -Value $before
            Add-Finding -Run $run -Name 'readingAfterCaseOpened' -Value $after
            if ($before -eq 'yes') { $o = 'fail'; $w = 'a battery figure showed before the case was opened, so an old reading was still there' }
            elseif ($after -eq 'yes') { $o = 'pass'; $w = 'no figure before the case was opened, and one appeared when it was' }
            else { $o = 'inconclusive'; $w = 'no old figure showed, but no new one appeared either' }
            Add-Criterion -Run $run -Id 'no-old-reading' -Criterion 'No old battery reading showed after the new pair, and the new reading appeared once the case was opened.' -Outcome $o -Detail ($w + '.')

            # connect-after-pairing, then Disconnect
            $since = (Get-Date).ToUniversalTime()
            Wait-Owner -Run $run -Text 'Left-click the Earshot icon, then Connect on the card. Wait until the card says the AirPods are connected or gives a reason, then come back here.'
            $attempt = Get-ConnectAttempt -Since $since
            Add-Finding -Run $run -Name 'connectAfterPairingMs' -Value $attempt.Ms
            Add-Criterion -Run $run -Id 'connect-after-pairing' -Criterion 'One Connect from the card reached playing within 20 seconds.' -Outcome $attempt.Outcome -Detail ($attempt.Why + '.')
            Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC: left-click the Earshot icon and choose Disconnect. Then come back here.'

            # The optional trace.
            $trace = Read-Answer -Run $run -Question 'Do you want to try the Bluetooth trace now?' -Options @('yes', 'no')
            if ($trace -eq 'yes')
            {
                $traceStart = (Get-Date).ToUniversalTime()
                Wait-Owner -Run $run -Text 'Open Earshot settings and choose Record a Bluetooth trace. Allow the Windows permission prompt once. Wait about ten seconds, then press Stop. Wait for the card to say where it saved the trace, then come back here.'
                $recording = Get-LogMatches -Pattern 'Bluetooth trace: ' -Regex 'Bluetooth trace: recording\.' -Since $traceStart
                $saved = Get-LogMatches -Pattern 'Bluetooth trace ended: ' -Regex 'Bluetooth trace ended: (Saved|SavedInPlace)\.' -Since $traceStart
                $ended = Get-LogMatches -Pattern 'Bluetooth trace ended: ' -Regex 'Bluetooth trace ended: [A-Za-z]+\.' -Since $traceStart
                if ($recording.Count -gt 0 -and $saved.Count -gt 0) { $o = 'pass'; $w = 'the trace recorded and ended saved' }
                elseif ($ended.Count -gt 0) { $o = 'fail'; $w = 'the trace ended without being saved: ' + ([string]$ended[0] -replace '^.*?(Bluetooth trace ended: )', '$1') }
                else { $o = 'fail'; $w = 'no line says the trace ended' }
                Add-Criterion -Run $run -Id 'trace-saved' -Criterion 'The Bluetooth trace recorded and was saved.' -Outcome $o -Detail ($w + '.')
            }
            else
            {
                Add-Criterion -Run $run -Id 'trace-saved' -Criterion 'The Bluetooth trace recorded and was saved.' -Outcome 'inconclusive' -Detail 'not reached: you chose not to try the trace.'
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
    $overall = Complete-LiveTestRun -Run $run -AtRestReason $atRestReason
    Write-Host ('Test 24 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
