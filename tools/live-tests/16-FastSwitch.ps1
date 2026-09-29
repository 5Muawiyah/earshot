<#
.SYNOPSIS
    Switching the AirPods between the phone and this PC, each way.

.DESCRIPTION
    A switch to this PC is Earshot's connect and a switch to the phone is its disconnect, each
    started by a left click or by one of the two switch shortcuts, and each measured by Earshot
    itself: it writes one line per switch to its log ("Switch to-pc: ..." and "Switch to-phone:
    ..."), with the phases the handover went through. This test does the switching, asks you what
    you heard before it shows you any figure, and reads those lines.

    It settles how long a handover takes each way on the real AirPods, which path each switch to
    this PC took, whether the phone takes the AirPods back by itself (playing, and idle), whether
    this PC is at rest after every switch to the phone, whether protection survives switching,
    and the first real press of a registered shortcut.

    This may be the first time the shortcuts are pressed for real. The test records whether it
    was, from Earshot's log and from your own answer, in the finding firstRealPressOfShortcuts.

    Each round is one switch to this PC and one to the phone. The first round is by left click,
    the rest by shortcut. After the rounds there is one more switch to this PC, an optional leg
    that measures the direct path, and one switch to the phone with nothing playing on the phone.
    The last thing you do is always a switch to the phone.

    The phone side of a switch cannot be seen from this PC. The time from Earshot letting the
    AirPods go to the moment you heard the phone in them is what you observed, includes your
    reaction time, and is recorded under a name that says so. It is never mixed with a figure
    Earshot measured.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.PARAMETER Rounds
    How many rounds of switching each way. 3 by default. A choice of how long the sitting is, not a
    measurement.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\16-FastSwitch.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [ValidateRange(1, 10)][int]$Rounds = 3
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

$run = New-LiveTestRun -TestId '16-fast-switch' -Title 'Switching between the phone and this PC' `
    -Settles 'How long a handover takes each way on the real AirPods, which path each switch to this PC took, whether the phone takes the AirPods back by itself, whether this PC is at rest after every switch to the phone, whether protection survives switching, and the first real press of a registered shortcut.' `
    -ExePath $ExePath -RunRoot $RunRoot

# The last thing this test does is a switch to the phone, which blocks the nodes, so the closing step
# finds nothing to undo and no reason is given for leaving them enabled.
$atRestReason = ''

# The defaults Earshot ships for the two shortcuts, used only for a settings file that does not hold them.
$script:DefaultToPc = 'Ctrl+Alt+Shift+A'
$script:DefaultToPhone = 'Ctrl+Alt+Shift+D'

$script:ClickHow = @{
    'to-pc'    = 'left-click the Earshot icon, then Connect on the card'
    'to-phone' = 'left-click the Earshot icon, then Disconnect on the card'
}

# ------------------------------------------------------------------ reading Earshot's own lines

# A figure in a switch line: whole milliseconds, or "-" for a phase that did not run or a clock that
# failed. $null for "-" and for anything else, never a guessed 0.
function ConvertTo-Ms
{
    param([string]$Token)

    $value = 0
    if ($Token -cmatch '^[0-9]+$' -and [int]::TryParse($Token, [ref]$value)) { return $value }
    return $null
}

# The UTC moment in a switch line's accepted field. $null when it is not there to read.
function ConvertTo-AcceptedUtc
{
    param([string]$Text)

    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    $parsed = [datetime]::MinValue
    if ([datetime]::TryParseExact($Text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) { return $parsed }
    return $null
}

# Reads one "Switch to-pc:" or "Switch to-phone:" line, in the exact shapes Earshot writes. $null when the
# line is not one of them or a figure in it is mangled, so a line that cannot be read is never taken for
# one that says 0.
function ConvertFrom-SwitchLine
{
    param([string]$Line)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    $n = '([0-9]+|-)'
    $stamp = '([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z)'
    $end = '\)\.\s*$'

    if ($Line -cmatch ('Switch to-pc: active after ' + $n + ' ms \(trigger ([a-z-]+), path ([a-z-]+), queued ' + $n + ', first-pass ' + $n + ', status ' + $n + ', allow ' + $n + ', endpoints ' + $n + ', connect ' + $n + ', protection ' + $n + ', total ' + $n + ', accepted ' + $stamp + $end))
    {
        $m = $Matches
        $accepted = ConvertTo-AcceptedUtc -Text $m[12]
        if ($null -eq $accepted) { return $null }
        return [ordered]@{
            Direction = 'to-pc'; Kind = 'active'; Trigger = $m[2]; Path = $m[3]
            ActiveMs = (ConvertTo-Ms -Token $m[1]); QueuedMs = (ConvertTo-Ms -Token $m[4]); TotalMs = (ConvertTo-Ms -Token $m[11])
            Phases = ('queued ' + $m[4] + ', first-pass ' + $m[5] + ', status ' + $m[6] + ', allow ' + $m[7] + ', endpoints ' + $m[8] + ', connect ' + $m[9] + ', protection ' + $m[10])
            AcceptedUtc = $accepted
        }
    }

    if ($Line -cmatch ('Switch to-pc: not active \(outcome ([A-Za-z]+), trigger ([a-z-]+), path ([a-z-]+), blocked-again (yes|no|not-needed), total ' + $n + ', accepted ' + $stamp + $end))
    {
        $m = $Matches
        $accepted = ConvertTo-AcceptedUtc -Text $m[6]
        if ($null -eq $accepted) { return $null }
        return [ordered]@{
            Direction = 'to-pc'; Kind = 'notactive'; Trigger = $m[2]; Path = $m[3]; Outcome = $m[1]; BlockedAgain = $m[4]
            TotalMs = (ConvertTo-Ms -Token $m[5]); ActiveMs = $null; QueuedMs = $null; AcceptedUtc = $accepted
        }
    }

    if ($Line -cmatch ('Switch to-phone: released after ' + $n + ' ms, at rest after ' + $n + ' ms \(trigger ([a-z-]+), queued ' + $n + ', block ' + $n + ', total ' + $n + ', accepted ' + $stamp + $end))
    {
        $m = $Matches
        $accepted = ConvertTo-AcceptedUtc -Text $m[7]
        if ($null -eq $accepted) { return $null }
        return [ordered]@{
            Direction = 'to-phone'; Kind = 'released'; Trigger = $m[3]; AtRest = $true; Reason = $null
            ReleasedMs = (ConvertTo-Ms -Token $m[1]); AtRestMs = (ConvertTo-Ms -Token $m[2]); QueuedMs = (ConvertTo-Ms -Token $m[4])
            BlockMs = (ConvertTo-Ms -Token $m[5]); TotalMs = (ConvertTo-Ms -Token $m[6]); AcceptedUtc = $accepted
        }
    }

    if ($Line -cmatch ('Switch to-phone: released after ' + $n + ' ms, not at rest: ([^()]+) \(trigger ([a-z-]+), queued ' + $n + ', block ' + $n + ', total ' + $n + ', accepted ' + $stamp + $end))
    {
        $m = $Matches
        $accepted = ConvertTo-AcceptedUtc -Text $m[7]
        if ($null -eq $accepted) { return $null }
        return [ordered]@{
            Direction = 'to-phone'; Kind = 'released'; Trigger = $m[3]; AtRest = $false; Reason = $m[2]
            ReleasedMs = (ConvertTo-Ms -Token $m[1]); AtRestMs = $null; QueuedMs = (ConvertTo-Ms -Token $m[4])
            BlockMs = (ConvertTo-Ms -Token $m[5]); TotalMs = (ConvertTo-Ms -Token $m[6]); AcceptedUtc = $accepted
        }
    }

    if ($Line -cmatch ('Switch to-phone: not released \(outcome ([A-Za-z]+), at rest (yes|no: [^,()]+), trigger ([a-z-]+), total ' + $n + ', accepted ' + $stamp + $end))
    {
        $m = $Matches
        $accepted = ConvertTo-AcceptedUtc -Text $m[5]
        if ($null -eq $accepted) { return $null }
        return [ordered]@{
            Direction = 'to-phone'; Kind = 'notreleased'; Trigger = $m[3]; Outcome = $m[1]; AtRest = ($m[2] -ceq 'yes')
            TotalMs = (ConvertTo-Ms -Token $m[4]); ReleasedMs = $null; AtRestMs = $null; QueuedMs = $null; AcceptedUtc = $accepted
        }
    }

    if ($Line -cmatch ('Switch (to-pc|to-phone): cancelled \(trigger ([a-z-]+), total ' + $n + ', accepted ' + $stamp + $end))
    {
        $m = $Matches
        $accepted = ConvertTo-AcceptedUtc -Text $m[4]
        if ($null -eq $accepted) { return $null }
        return [ordered]@{
            Direction = $m[1]; Kind = 'cancelled'; Trigger = $m[2]; TotalMs = (ConvertTo-Ms -Token $m[3]); AcceptedUtc = $accepted
            ActiveMs = $null; ReleasedMs = $null; AtRestMs = $null; QueuedMs = $null
        }
    }

    return $null
}

# What one leg of the sitting left in the log: the lines whose trigger is the one this leg was asked to
# use, parsed. The trigger is what ties a line to a leg, so a press made some other way, or a line from
# before the leg, is never counted as this leg's. Sample is the one parsed line of the wanted kind when
# there is exactly one line, and $null otherwise: no line, two lines and a line that could not be read
# are all "not measured", and none is ever read as a figure.
function Get-SwitchLeg
{
    param(
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][datetime]$Since,
        [Parameter(Mandatory = $true)][string]$Trigger
    )

    $wantedKind = $(if ($Direction -eq 'to-pc') { 'active' } else { 'released' })
    $lines = Get-EarshotLogLines -Run $run -Pattern ('Switch ' + $Direction + ': ') -SinceUtc $Since
    $mine = @()
    $unparsable = 0
    $others = 0
    foreach ($line in @($lines))
    {
        if ($line -notmatch ('trigger ' + [regex]::Escape($Trigger) + ','))
        {
            $others = $others + 1
            continue
        }

        $item = ConvertFrom-SwitchLine -Line $line
        if ($null -eq $item)
        {
            $unparsable = $unparsable + 1
            continue
        }

        $mine = $mine + @($item)
    }

    $count = @($mine).Count + $unparsable
    $sample = $null
    if ($count -eq 1 -and @($mine).Count -eq 1 -and @($mine)[0].Kind -eq $wantedKind) { $sample = @($mine)[0] }

    $why = 'one line'
    if ($count -eq 0) { $why = 'no line with trigger ' + $Trigger }
    elseif ($count -gt 1) { $why = [string]$count + ' lines with trigger ' + $Trigger + ', so it is not clear which is this switch' }
    elseif ($unparsable -gt 0) { $why = 'a line with trigger ' + $Trigger + ' whose figures could not be read' }
    elseif ($null -eq $sample) { $why = 'a line with trigger ' + $Trigger + ' that is not a completed switch (' + @($mine)[0].Kind + ')' }

    return [ordered]@{ Direction = $Direction; Trigger = $Trigger; Lines = $count; Others = $others; Unparsable = $unparsable; Sample = $sample; Why = $why }
}

# The middle of the figures, rounded down to a whole millisecond. $null for none.
function Get-MedianMs
{
    param([int[]]$Values)

    $sorted = @($Values | Sort-Object)
    $count = $sorted.Count
    if ($count -eq 0) { return $null }
    if (($count % 2) -eq 1) { return [int]$sorted[[int](($count - 1) / 2)] }
    $low = [int]$sorted[[int](($count / 2) - 1)]
    $high = [int]$sorted[[int]($count / 2)]
    return [int][math]::Floor(($low + $high) / 2)
}

# The time from Earshot letting the AirPods go to the owner hearing the phone, as the owner observed it: the
# moment they pressed Enter, less the moment Earshot accepted the switch and the release it measured. $null
# unless the line belongs to this leg (accepted after the leg began), the release was measured, and the
# answer is not negative.
function Get-OwnerObservedMs
{
    param($AcceptedUtc, $ReleasedMs, [datetime]$HeardUtc, [datetime]$LegStartUtc)

    if ($null -eq $AcceptedUtc -or $null -eq $ReleasedMs) { return $null }
    if ($AcceptedUtc -lt $LegStartUtc) { return $null }
    $ms = [int][math]::Round((($HeardUtc - $AcceptedUtc).TotalMilliseconds) - $ReleasedMs)
    if ($ms -lt 0) { return $null }
    return $ms
}

# The two shortcuts as Earshot reads the settings file: a member the file does not hold takes its default,
# and a file written before the two members existed reads as on when nothing was typed in it.
function Get-SwitchShortcuts
{
    param($Settings)

    $enabled = $true
    $toPc = $script:DefaultToPc
    $toPhone = $script:DefaultToPhone
    $hotkeys = Get-Field -Object $Settings -Name 'Hotkeys'
    if ($null -ne $hotkeys)
    {
        $enabledField = Get-Field -Object $hotkeys -Name 'Enabled'
        $pcField = Get-Field -Object $hotkeys -Name 'SwitchToPc'
        $phoneField = Get-Field -Object $hotkeys -Name 'SwitchToPhone'
        if ($null -ne $pcField -or $null -ne $phoneField)
        {
            if ($null -ne $enabledField) { $enabled = ($enabledField -eq $true) }
            if ($null -ne $pcField) { $toPc = [string]$pcField }
            if ($null -ne $phoneField) { $toPhone = [string]$phoneField }
        }
        else
        {
            $typed = @()
            foreach ($name in @('ToggleConnection', 'ToggleAudioProtection', 'ToggleBlockAtBoot', 'SpeakStatus'))
            {
                $text = Get-Field -Object $hotkeys -Name $name
                if ($null -ne $text -and -not [string]::IsNullOrWhiteSpace([string]$text)) { $typed = $typed + @([string]$text) }
            }

            if ($null -ne $enabledField) { $enabled = ($enabledField -eq $true) -or (@($typed).Count -eq 0) }
            if (@($typed) -contains $toPc) { $toPc = '' }
            if (@($typed) -contains $toPhone) { $toPhone = '' }
        }
    }

    return [ordered]@{ Enabled = $enabled; ToPc = $toPc; ToPhone = $toPhone }
}

function Get-How
{
    param([string]$Direction, [bool]$UseClick, $Shortcuts)

    if ($UseClick) { return $script:ClickHow[$Direction] }
    if ($Direction -eq 'to-pc') { return 'press ' + $Shortcuts.ToPc }
    return 'press ' + $Shortcuts.ToPhone
}

function Get-Trigger
{
    param([string]$Direction, [bool]$UseClick)

    if ($UseClick) { return 'click' }
    if ($Direction -eq 'to-pc') { return 'shortcut-to-pc' }
    return 'shortcut-to-phone'
}

# "yes" when any leg answered yes, "unsure" when none did but one was unsure, "no" when every leg said no.
function Get-CombinedAnswer
{
    param($Answers)

    $list = @($Answers)
    if ($list.Count -eq 0) { return $null }
    if ($list -contains 'yes') { return 'yes' }
    if ($list -contains 'unsure') { return 'unsure' }
    return 'no'
}

# Pass when every answer is yes, fail when any is no, inconclusive otherwise (an unsure with no no). Never a
# pass for an empty list.
function Get-AnswerOutcome
{
    param($Answers)

    $list = @($Answers)
    if ($list.Count -eq 0) { return 'inconclusive' }
    if ($list -contains 'no') { return 'fail' }
    if ($list -contains 'unsure') { return 'inconclusive' }
    return 'pass'
}

# ---------------------------------------------------------------------------------- the sitting

$toPcLegs = New-Object System.Collections.ArrayList
$toPhoneLegs = New-Object System.Collections.ArrayList
$directLeg = $null
$idleAnswer = $null
$firstRealPress = $null
$shortcutsOwnerSaid = $null
$nodeReadsAfterToPhone = New-Object System.Collections.ArrayList

# One switch to this PC. Nothing measured is shown until the owner has answered what they heard and whether
# the wait was acceptable.
function Invoke-ToPcLeg
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Trigger,
        [Parameter(Mandatory = $true)][bool]$UseClick,
        [Parameter(Mandatory = $true)][string]$How
    )

    Write-Section -Run $run -Title $Name
    $start = (Get-Date).ToUniversalTime()
    Write-Line -Run $run -Text ('Switch to this PC: ' + $How + '.')
    if ($UseClick)
    {
        Wait-Owner -Run $run -Text 'Start something playing on this PC. Then switch to this PC by left-clicking the Earshot icon, then Connect on the card. When the sound from this PC is in the AirPods, press Enter.'
    }
    else
    {
        Wait-Owner -Run $run -Text 'Start something playing on this PC. Then switch to this PC with its shortcut. When the sound from this PC is in the AirPods, press Enter.'
    }

    $heard = Read-Answer -Run $run -Question 'Did you hear this PC in the AirPods?'
    $acceptable = Read-Answer -Run $run -Question 'Was that wait acceptable?'

    $leg = Get-SwitchLeg -Direction 'to-pc' -Since $start -Trigger $Trigger
    $sample = $leg.Sample
    if ($null -ne $sample)
    {
        Write-Line -Run $run -Text ('Earshot measured ' + $(if ($null -ne $sample.ActiveMs) { [string]$sample.ActiveMs + ' ms' } else { 'no figure' }) + ' to the AirPods being active, by the ' + $sample.Path + ' path (' + $sample.Phases + ').')
    }
    else
    {
        Write-Line -Run $run -Text ('Nothing measured for this switch: ' + $leg.Why + '.')
    }

    $cutOut = Read-Answer -Run $run -Question 'Did the sound cut out or change after it had started?'
    $audio = Get-AudioState -Run $run -Label ('audio-' + $Name.ToLowerInvariant().Replace(' ', '-'))
    $states = Get-TargetEndpointStates -AudioJson $audio
    Write-Line -Run $run -Text ('Render reads ' + $states.Render + ' after this switch.')

    $protect = Get-EarshotLogLines -Run $run -Pattern 'protect-on (' -SinceUtc $start
    $protectRan = 'unknown'
    if ($null -ne $sample) { $protectRan = $(if (@($protect).Count -gt 0) { 'yes' } else { 'no' }) }

    $record = [ordered]@{
        Name = $Name; Trigger = $Trigger; Heard = $heard; Acceptable = $acceptable; CutOut = $cutOut
        Render = $states.Render; Leg = $leg; Sample = $sample; ProtectRan = $protectRan; StartUtc = $start
    }
    return $record
}

# One switch to the phone, with the phone playing aloud. The owner presses Enter the moment the phone's sound
# moves into the AirPods, and that moment is kept as they gave it.
function Invoke-ToPhoneLeg
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Trigger,
        [Parameter(Mandatory = $true)][bool]$UseClick,
        [Parameter(Mandatory = $true)][string]$How
    )

    Write-Section -Run $run -Title $Name
    $start = (Get-Date).ToUniversalTime()
    Write-Line -Run $run -Text ('Switch to the phone: ' + $How + '.')
    if ($UseClick)
    {
        Wait-Owner -Run $run -Text 'Start something playing aloud on the phone. Then switch to phone by left-clicking the Earshot icon, then Disconnect on the card. The moment the sound from the phone moves into the AirPods, press Enter.'
    }
    else
    {
        Wait-Owner -Run $run -Text 'Start something playing aloud on the phone. Then switch to phone with its shortcut. The moment the sound from the phone moves into the AirPods, press Enter.'
    }

    $heardAt = (Get-Date).ToUniversalTime()
    $takenBack = Read-Answer -Run $run -Question 'Did the phone take the AirPods back by itself?'
    $acceptable = Read-Answer -Run $run -Question 'Was that wait acceptable?'

    $leg = Get-SwitchLeg -Direction 'to-phone' -Since $start -Trigger $Trigger
    $sample = $leg.Sample
    if ($null -ne $sample)
    {
        Write-Line -Run $run -Text ('Earshot measured ' + $(if ($null -ne $sample.ReleasedMs) { [string]$sample.ReleasedMs + ' ms' } else { 'no figure' }) + ' to letting the AirPods go' + $(if ($sample.AtRest) { ', and ' + [string]$sample.AtRestMs + ' ms to the nodes reading Blocked.' } else { ', and it says the machine is not at rest: ' + $sample.Reason + '.' }))
    }
    else
    {
        Write-Line -Run $run -Text ('Nothing measured for this switch: ' + $leg.Why + '.')
    }

    # This script's own read of the nodes, not the line's word.
    $nodes = Get-NodeState -Run $run -Label ('nodes-' + $Name.ToLowerInvariant().Replace(' ', '-'))
    $nodeState = Get-Field -Object $nodes -Name 'nodeState'
    Write-Line -Run $run -Text ('The nodes read ' + $nodeState + ' after this switch.')
    [void]$nodeReadsAfterToPhone.Add($nodeState)

    $observed = $null
    if ($takenBack -eq 'yes' -and $null -ne $sample)
    {
        $observed = Get-OwnerObservedMs -AcceptedUtc $sample.AcceptedUtc -ReleasedMs $sample.ReleasedMs -HeardUtc $heardAt -LegStartUtc $start
    }

    $record = [ordered]@{
        Name = $Name; Trigger = $Trigger; TakenBack = $takenBack; Acceptable = $acceptable; NodeState = $nodeState
        Leg = $leg; Sample = $sample; OwnerObservedMs = $observed; StartUtc = $start; Playing = $true
    }
    return $record
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed from a release built from the current head, set up, and running in the tray.',
        'Block at boot is on.',
        'Play from a phone is off.',
        'Both switch shortcuts are set and shortcuts are switched on (Ctrl+Alt+Shift+A switches to this PC and Ctrl+Alt+Shift+D switches to the phone, unless you changed them).',
        'The AirPods are on your phone and playing there, and this PC is not connected to them.',
        'The AirPods are paired with this PC and with your phone.'
    ) -PhysicalActions @(
        'Wear the AirPods.',
        'Have something to play on this PC and something to play on the phone.',
        'Switch each way when asked: the first time by left-clicking the Earshot icon, after that by pressing the shortcut.',
        'This may be the first time you press the shortcuts for real. This test records whether it was.'
    )

    if ($ready)
    {
        $testStart = (Get-Date).ToUniversalTime()

        # ---- The start reads as required ----
        Write-Section -Run $run -Title 'The start'
        $paths = Get-EarshotDataPaths
        $settings = Read-EarshotJsonFile -Run $run -Path $paths.SettingsFile
        $shortcuts = Get-SwitchShortcuts -Settings $settings
        $streamingBlock = Get-Field -Object $settings -Name 'Streaming'
        $streamingOn = ((Get-Field -Object $streamingBlock -Name 'Enabled') -eq $true)
        $blockAtBoot = Get-BlockAtBootSetting -Run $run
        $nodesAtStart = Get-NodeState -Run $run -Label 'nodes-start'
        $nodeStateAtStart = Get-Field -Object $nodesAtStart -Name 'nodeState'
        $audioAtStart = Get-AudioState -Run $run -Label 'audio-start'
        $statesAtStart = Get-TargetEndpointStates -AudioJson $audioAtStart
        Write-Line -Run $run -Text ('Nodes ' + $nodeStateAtStart + ', render ' + $statesAtStart.Render + ', Block at boot ' + $blockAtBoot + ', Play from a phone ' + $(if ($streamingOn) { 'on' } else { 'off' }) + '.')
        Write-Line -Run $run -Text ('Shortcuts ' + $(if ($shortcuts.Enabled) { 'on' } else { 'off' }) + ': to this PC "' + $shortcuts.ToPc + '", to the phone "' + $shortcuts.ToPhone + '".')

        $wrong = @()
        $unknown = @()
        if ($null -eq $nodeStateAtStart) { $unknown = $unknown + @('the nodes could not be read') }
        elseif ($nodeStateAtStart -ne 'Blocked') { $wrong = $wrong + @('the nodes read ' + $nodeStateAtStart + ', not Blocked') }
        if ($null -eq $statesAtStart.Render) { $unknown = $unknown + @('the render endpoint could not be read') }
        elseif ($statesAtStart.Render -eq 'Active') { $wrong = $wrong + @('the AirPods are already active on this PC') }
        if ($null -eq $blockAtBoot) { $unknown = $unknown + @('Block at boot could not be read') }
        elseif ($blockAtBoot -ne $true) { $wrong = $wrong + @('Block at boot is off') }
        if ($null -eq $settings) { $unknown = $unknown + @('settings.json could not be read') }
        if ($streamingOn) { $wrong = $wrong + @('Play from a phone is on') }
        if (-not $shortcuts.Enabled) { $wrong = $wrong + @('shortcuts are switched off') }
        if ([string]::IsNullOrWhiteSpace($shortcuts.ToPc)) { $wrong = $wrong + @('no shortcut is set for switching to this PC') }
        if ([string]::IsNullOrWhiteSpace($shortcuts.ToPhone)) { $wrong = $wrong + @('no shortcut is set for switching to the phone') }
        Add-Criterion -Run $run -Id 'preconditions' -Criterion 'The start reads as this test needs it.' `
            -Outcome $(if (@($wrong).Count -gt 0) { 'fail' } elseif (@($unknown).Count -gt 0) { 'inconclusive' } else { 'pass' }) `
            -Detail $(if (@($wrong).Count -gt 0) { 'Not as needed: ' + (@($wrong) -join '; ') + '.' } elseif (@($unknown).Count -gt 0) { 'Not known: ' + (@($unknown) -join '; ') + '.' } else { 'Nodes Blocked, the AirPods not active on this PC, Block at boot on, Play from a phone off, both shortcuts set and on.' })

        # ---- Is this the first real press? ----
        $pressesAll = Get-EarshotLogLines -Run $run -Pattern 'Hotkey: switch to'
        $pressesSince = Get-EarshotLogLines -Run $run -Pattern 'Hotkey: switch to' -SinceUtc $testStart
        $pressesLoggedBefore = @($pressesAll).Count - @($pressesSince).Count
        $shortcutsOwnerSaid = Read-Answer -Run $run -Question 'Before this test, had you pressed either switch shortcut on this PC?'
        if ($pressesLoggedBefore -gt 0) { $firstRealPress = 'no' }
        elseif ($shortcutsOwnerSaid -eq 'no') { $firstRealPress = 'yes' }
        elseif ($shortcutsOwnerSaid -eq 'yes') { $firstRealPress = 'no' }
        else { $firstRealPress = 'unknown' }
        Write-Line -Run $run -Text ('Switch shortcut presses in the log before this test: ' + $pressesLoggedBefore + '. You said: ' + $shortcutsOwnerSaid + '. So this is ' + $(if ($firstRealPress -eq 'yes') { 'the first real press of a registered shortcut' } elseif ($firstRealPress -eq 'no') { 'not the first press' } else { 'not known to be the first press' }) + '.')

        # ---- The rounds ----
        for ($i = 1; $i -le $Rounds; $i++)
        {
            $useClick = ($i -eq 1)
            $pcHow = Get-How -Direction 'to-pc' -UseClick $useClick -Shortcuts $shortcuts
            $phoneHow = Get-How -Direction 'to-phone' -UseClick $useClick -Shortcuts $shortcuts
            [void]$toPcLegs.Add((Invoke-ToPcLeg -Name ('To this PC, round ' + $i) -Trigger (Get-Trigger -Direction 'to-pc' -UseClick $useClick) `
                        -UseClick $useClick -How $pcHow))
            [void]$toPhoneLegs.Add((Invoke-ToPhoneLeg -Name ('To the phone, round ' + $i) -Trigger (Get-Trigger -Direction 'to-phone' -UseClick $useClick) `
                        -UseClick $useClick -How $phoneHow))
        }

        # ---- One more to this PC, so the AirPods are here for the legs that follow ----
        $shortcutPcHow = Get-How -Direction 'to-pc' -UseClick $false -Shortcuts $shortcuts
        [void]$toPcLegs.Add((Invoke-ToPcLeg -Name 'To this PC, before the last legs' -Trigger 'shortcut-to-pc' `
                    -UseClick $false -How $shortcutPcHow))

        # ---- Optional: the direct path ----
        Write-Section -Run $run -Title 'The direct path (optional)'
        $doDirect = Confirm-Step -Run $run -Prompt 'Measure the direct path: the phone takes the AirPods from this PC, and you switch back to this PC straight away.' `
            -Consequence 'Nothing is sent by this script. You use your phone and the switch shortcut. If Earshot has already blocked the nodes by the time you switch, the leg says so and settles nothing.'
        if ($doDirect)
        {
            $directStart = (Get-Date).ToUniversalTime()
            Write-Line -Run $run -Text ('Switch to this PC: ' + $shortcutPcHow + '.')
            Wait-Owner -Run $run -Text 'On your phone, choose the AirPods as the output and start playing there, so the phone takes them from this PC. As soon as the phone plays in them, switch to this PC with its shortcut. When the sound from this PC is in the AirPods, press Enter.'
            $directLegRead = Get-SwitchLeg -Direction 'to-pc' -Since $directStart -Trigger 'shortcut-to-pc'
            $directSample = $directLegRead.Sample
            if ($null -ne $directSample -and $directSample.Path -eq 'direct')
            {
                Write-Line -Run $run -Text ('Earshot measured ' + $directSample.ActiveMs + ' ms by the direct path.')
            }
            elseif ($null -ne $directSample)
            {
                Write-Line -Run $run -Text ('The switch took the ' + $directSample.Path + ' path, not the direct one, so this leg settles nothing about the direct path.')
            }
            else
            {
                Write-Line -Run $run -Text ('Nothing measured for the direct path: ' + $directLegRead.Why + '.')
            }

            $directLeg = $directLegRead
        }

        # ---- Once, to the phone with the phone idle ----
        Write-Section -Run $run -Title 'To the phone, with nothing playing on the phone'
        $idleStart = (Get-Date).ToUniversalTime()
        $phoneShortcutHow = Get-How -Direction 'to-phone' -UseClick $false -Shortcuts $shortcuts
        Write-Line -Run $run -Text ('Switch to the phone: ' + $phoneShortcutHow + '.')
        Wait-Owner -Run $run -Text 'Pause everything on the phone. Then switch to phone with its shortcut. Then press play on the phone, and press Enter.'
        $idleAnswer = Read-Answer -Run $run -Question 'Did the sound come out of the AirPods when you pressed play on the phone?' -Options @('yes', 'no')
        $idleLegRead = Get-SwitchLeg -Direction 'to-phone' -Since $idleStart -Trigger 'shortcut-to-phone'
        $idleSample = $idleLegRead.Sample
        $idleNodes = Get-NodeState -Run $run -Label 'nodes-to-the-phone-idle'
        $idleNodeState = Get-Field -Object $idleNodes -Name 'nodeState'
        Write-Line -Run $run -Text ('The nodes read ' + $idleNodeState + ' after this switch.')
        [void]$nodeReadsAfterToPhone.Add($idleNodeState)
        [void]$toPhoneLegs.Add([ordered]@{
                Name = 'To the phone, phone idle'; Trigger = 'shortcut-to-phone'; TakenBack = $null; Acceptable = $null; NodeState = $idleNodeState
                Leg = $idleLegRead; Sample = $idleSample; OwnerObservedMs = $null; StartUtc = $idleStart; Playing = $false
            })

        # ---- The end ----
        Write-Section -Run $run -Title 'The end'
        $services = Get-ServiceState -Run $run -Label 'services-end'
        $protectionAtEnd = Get-Field -Object $services -Name 'protection'
        $protectSetting = Get-ProtectAudioSetting -Run $run
        $protectOn = ($null -eq $protectSetting -or $protectSetting -eq $true)
        Write-Line -Run $run -Text ('Protection reads ' + $protectionAtEnd + ' at the end; Protect audio quality is ' + $(if ($protectOn) { 'on' } else { 'off' }) + '.')

        # ==== The criteria ====
        $toPcSamples = @($toPcLegs | Where-Object { $null -ne $_.Sample -and $null -ne $_.Sample.ActiveMs })
        $toPcMissing = @($toPcLegs | Where-Object { $null -eq $_.Sample -or $null -eq $_.Sample.ActiveMs })
        Add-Criterion -Run $run -Id 'to-pc-measured' -Criterion 'Every switch to this PC has exactly one Earshot line with a figure.' `
            -Outcome $(if (@($toPcLegs).Count -gt 0 -and @($toPcMissing).Count -eq 0) { 'pass' } else { 'inconclusive' }) `
            -Detail $(if (@($toPcMissing).Count -eq 0) { [string]@($toPcSamples).Count + ' switch(es) to this PC, each with one line.' } else { [string]@($toPcMissing).Count + ' of ' + @($toPcLegs).Count + ' switch(es) to this PC not measured, for example ' + $toPcMissing[0].Name + ': ' + $toPcMissing[0].Leg.Why + '.' })

        $allToPc = Get-EarshotLogLines -Run $run -Pattern 'Switch to-pc: ' -SinceUtc $testStart
        $notActiveCount = 0
        $activeSeen = 0
        foreach ($line in @($allToPc))
        {
            $item = ConvertFrom-SwitchLine -Line $line
            if ($null -eq $item) { continue }
            if ($item.Kind -eq 'notactive') { $notActiveCount = $notActiveCount + 1 }
            elseif ($item.Kind -eq 'active') { $activeSeen = $activeSeen + 1 }
        }

        Add-Criterion -Run $run -Id 'to-pc-no-timeout' -Criterion 'No switch to this PC ran out of time.' `
            -Outcome $(if ($notActiveCount -gt 0) { 'fail' } elseif ($activeSeen -gt 0) { 'pass' } else { 'inconclusive' }) `
            -Detail $(if ($notActiveCount -gt 0) { [string]$notActiveCount + ' "not active" line(s): the AirPods did not become active in time.' } elseif ($activeSeen -gt 0) { 'No "not active" line among ' + $activeSeen + ' switch line(s).' } else { 'No switch line was read at all, so a missing timeout proves nothing.' })

        $heardAnswers = @($toPcLegs | ForEach-Object { $_.Heard })
        $wrongRender = @($toPcLegs | Where-Object { $null -ne $_.Render -and $_.Render -ne 'Active' })
        $unreadRender = @($toPcLegs | Where-Object { $null -eq $_.Render })
        $heardOutcome = Get-AnswerOutcome -Answers $heardAnswers
        if ($heardOutcome -eq 'pass' -and @($wrongRender).Count -gt 0) { $heardOutcome = 'fail' }
        elseif ($heardOutcome -eq 'pass' -and @($unreadRender).Count -gt 0) { $heardOutcome = 'inconclusive' }
        Add-Criterion -Run $run -Id 'to-pc-heard' -Criterion 'You heard this PC in the AirPods after every switch to this PC, and this script read the render endpoint as active.' `
            -Outcome $heardOutcome `
            -Detail ('You answered: ' + (@($heardAnswers) -join ', ') + '. Render after each: ' + (@($toPcLegs | ForEach-Object { [string]$_.Render }) -join ', ') + '.')

        Add-Criterion -Run $run -Id 'to-pc-acceptable' -Criterion 'Every wait for this PC was acceptable to you.' `
            -Outcome (Get-AnswerOutcome -Answers @($toPcLegs | ForEach-Object { $_.Acceptable })) `
            -Detail ('You answered: ' + (@($toPcLegs | ForEach-Object { $_.Acceptable }) -join ', ') + ', each before the figure was shown.')

        $toPhoneSamples = @($toPhoneLegs | Where-Object { $null -ne $_.Sample -and $null -ne $_.Sample.ReleasedMs })
        $toPhoneMissing = @($toPhoneLegs | Where-Object { $null -eq $_.Sample -or $null -eq $_.Sample.ReleasedMs })
        Add-Criterion -Run $run -Id 'to-phone-measured' -Criterion 'Every switch to the phone has exactly one Earshot line with a figure.' `
            -Outcome $(if (@($toPhoneLegs).Count -gt 0 -and @($toPhoneMissing).Count -eq 0) { 'pass' } else { 'inconclusive' }) `
            -Detail $(if (@($toPhoneMissing).Count -eq 0) { [string]@($toPhoneSamples).Count + ' switch(es) to the phone, each with one line.' } else { [string]@($toPhoneMissing).Count + ' of ' + @($toPhoneLegs).Count + ' switch(es) to the phone not measured, for example ' + $toPhoneMissing[0].Name + ': ' + $toPhoneMissing[0].Leg.Why + '.' })

        $notBlocked = @($nodeReadsAfterToPhone | Where-Object { $null -ne $_ -and $_ -ne 'Blocked' })
        $unreadNodes = @($nodeReadsAfterToPhone | Where-Object { $null -eq $_ })
        Add-Criterion -Run $run -Id 'to-phone-at-rest' -Criterion 'This script read the nodes as Blocked after every switch to the phone.' `
            -Outcome $(if (@($notBlocked).Count -gt 0) { 'fail' } elseif (@($unreadNodes).Count -gt 0 -or @($nodeReadsAfterToPhone).Count -eq 0) { 'inconclusive' } else { 'pass' }) `
            -Detail ('Node reads after each switch to the phone: ' + (@($nodeReadsAfterToPhone | ForEach-Object { [string]$_ }) -join ', ') + '.')

        $playingLegs = @($toPhoneLegs | Where-Object { $_.Playing })
        Add-Criterion -Run $run -Id 'to-phone-taken-back' -Criterion 'The phone took the AirPods back by itself after every switch to the phone with the phone playing.' `
            -Outcome (Get-AnswerOutcome -Answers @($playingLegs | ForEach-Object { $_.TakenBack })) `
            -Detail ('You answered: ' + (@($playingLegs | ForEach-Object { $_.TakenBack }) -join ', ') + '.')

        Add-Criterion -Run $run -Id 'to-phone-acceptable' -Criterion 'Every wait for the phone was acceptable to you.' `
            -Outcome (Get-AnswerOutcome -Answers @($playingLegs | ForEach-Object { $_.Acceptable })) `
            -Detail ('You answered: ' + (@($playingLegs | ForEach-Object { $_.Acceptable }) -join ', ') + ', each before the figure was shown.')

        # Which way each switch was started, from the lines themselves: counted by trigger, whatever else is in
        # the leg's lines, so a line that cannot be read still shows the route was used.
        $shortcutPc = 0; $shortcutPhone = 0; $clickPc = 0; $clickPhone = 0; $anyLine = 0
        foreach ($leg in @($toPcLegs | ForEach-Object { $_.Leg }) + @($directLeg))
        {
            if ($null -eq $leg) { continue }
            $anyLine = $anyLine + $leg.Lines + $leg.Others
            if ($leg.Trigger -eq 'shortcut-to-pc') { $shortcutPc = $shortcutPc + $leg.Lines }
            if ($leg.Trigger -eq 'click') { $clickPc = $clickPc + $leg.Lines }
        }

        foreach ($leg in @($toPhoneLegs | ForEach-Object { $_.Leg }))
        {
            $anyLine = $anyLine + $leg.Lines + $leg.Others
            if ($leg.Trigger -eq 'shortcut-to-phone') { $shortcutPhone = $shortcutPhone + $leg.Lines }
            if ($leg.Trigger -eq 'click') { $clickPhone = $clickPhone + $leg.Lines }
        }

        Add-Criterion -Run $run -Id 'shortcut-route' -Criterion 'Earshot logged a switch started by each shortcut, one each way.' `
            -Outcome $(if ($anyLine -eq 0) { 'inconclusive' } elseif ($shortcutPc -ge 1 -and $shortcutPhone -ge 1) { 'pass' } else { 'fail' }) `
            -Detail ('Lines with trigger shortcut-to-pc: ' + $shortcutPc + ', shortcut-to-phone: ' + $shortcutPhone + '.')
        Add-Criterion -Run $run -Id 'click-route' -Criterion 'Earshot logged a switch started by a left click, one each way.' `
            -Outcome $(if ($anyLine -eq 0) { 'inconclusive' } elseif ($clickPc -ge 1 -and $clickPhone -ge 1) { 'pass' } else { 'fail' }) `
            -Detail ('Lines with trigger click, to this PC: ' + $clickPc + ', to the phone: ' + $clickPhone + '.')

        Add-Criterion -Run $run -Id 'protection-held' -Criterion 'With Protect audio quality on, the services read Protected at the end.' `
            -Outcome $(if (-not $protectOn) { 'inconclusive' } elseif ($protectionAtEnd -eq 'Protected') { 'pass' } elseif ($null -eq $protectionAtEnd) { 'inconclusive' } else { 'fail' }) `
            -Detail $(if (-not $protectOn) { 'Protect audio quality is off, so there is nothing to hold.' } else { 'The services read ' + $protectionAtEnd + '.' })

        # ==== The findings: a declared block, null where nothing was measured ====
        $uncontendedPc = @($toPcSamples | Where-Object { $null -ne $_.Sample.QueuedMs -and $_.Sample.QueuedMs -eq 0 })
        $contendedPc = @($toPcSamples | Where-Object { $null -eq $_.Sample.QueuedMs -or $_.Sample.QueuedMs -ne 0 })
        $uncontendedPhone = @($toPhoneSamples | Where-Object { $null -ne $_.Sample.QueuedMs -and $_.Sample.QueuedMs -eq 0 })
        $contendedPhone = @($toPhoneSamples | Where-Object { $null -eq $_.Sample.QueuedMs -or $_.Sample.QueuedMs -ne 0 })

        $pcMs = @($uncontendedPc | ForEach-Object { [int]$_.Sample.ActiveMs })
        $pcMin = $null; $pcMax = $null; $pcMedian = $null; $pcCount = $null
        $pcList = $null; $pcPaths = $null; $pcPhases = $null
        if (@($pcMs).Count -gt 0)
        {
            $pcList = @($pcMs)
            $pcMin = [int](@($pcMs | Measure-Object -Minimum).Minimum)
            $pcMax = [int](@($pcMs | Measure-Object -Maximum).Maximum)
            $pcMedian = Get-MedianMs -Values $pcMs
            $pcCount = @($pcMs).Count
            $pcPaths = @($uncontendedPc | ForEach-Object { [string]$_.Sample.Path })
            $pcPhases = @($uncontendedPc | ForEach-Object { [string]$_.Sample.Phases })
        }

        Add-Finding -Run $run -Name 'toPcActiveMs' -Value $pcList -Detail 'Earshot''s own figure for each switch to this PC that did not wait behind another operation, accepted to render active'
        Add-Finding -Run $run -Name 'toPcMinMs' -Value $pcMin
        Add-Finding -Run $run -Name 'toPcMedianMs' -Value $pcMedian -Detail 'the middle figure; the mean of the two middle ones, rounded down, when there is an even number'
        Add-Finding -Run $run -Name 'toPcMaxMs' -Value $pcMax
        Add-Finding -Run $run -Name 'toPcSamples' -Value $pcCount -Detail 'how many switches the three figures above are taken from'
        Add-Finding -Run $run -Name 'toPcPaths' -Value $pcPaths -Detail 'the path each of those switches took'
        Add-Finding -Run $run -Name 'toPcPhases' -Value $pcPhases -Detail 'the phases of each of those switches, in milliseconds, "-" for a phase that did not run'

        $protectAnswers = @($toPcLegs | ForEach-Object { $_.ProtectRan })
        Add-Finding -Run $run -Name 'protectionRanOnSwitch' -Value $(if (@($protectAnswers).Count -gt 0) { $protectAnswers } else { $null }) `
            -Detail 'whether Earshot logged a protect-on during each switch to this PC, in order; unknown where the switch was not measured'
        Add-Finding -Run $run -Name 'soundCutOutAfterStart' -Value (Get-CombinedAnswer -Answers @($toPcLegs | ForEach-Object { $_.CutOut })) `
            -Detail 'owner-observed: yes if the sound cut out or changed after it had started on any switch to this PC'

        $phoneReleased = $null; $phoneAtRest = $null
        if (@($uncontendedPhone).Count -gt 0)
        {
            $phoneReleased = @($uncontendedPhone | ForEach-Object { [int]$_.Sample.ReleasedMs })
            $atRestFigures = @($uncontendedPhone | Where-Object { $null -ne $_.Sample.AtRestMs } | ForEach-Object { [int]$_.Sample.AtRestMs })
            if (@($atRestFigures).Count -gt 0) { $phoneAtRest = @($atRestFigures) }
        }

        Add-Finding -Run $run -Name 'toPhoneReleasedMs' -Value $phoneReleased -Detail 'Earshot''s own figure for each switch to the phone that did not wait behind another operation, accepted to the AirPods let go'
        Add-Finding -Run $run -Name 'toPhoneAtRestMs' -Value $phoneAtRest -Detail 'accepted to the nodes reading Blocked, for the switches where they did'

        $observedList = @($playingLegs | Where-Object { $null -ne $_.OwnerObservedMs } | ForEach-Object { [int]$_.OwnerObservedMs })
        Add-Finding -Run $run -Name 'phoneHeardAfterReleaseMsOwnerObserved' -Value $(if (@($observedList).Count -gt 0) { $observedList } else { $null }) `
            -Detail 'owner-observed: from Earshot letting the AirPods go to you hearing the phone in them; includes your reaction time; never merged with a figure Earshot measured'
        Add-Finding -Run $run -Name 'phoneTakesThemBackWhenIdle' -Value $idleAnswer -Detail 'owner-observed: whether the phone''s sound came out of the AirPods when you pressed play after the switch'

        $pcYes = @($toPcSamples | Where-Object { $_.Acceptable -eq 'yes' } | ForEach-Object { [int]$_.Sample.ActiveMs })
        $pcNo = @($toPcSamples | Where-Object { $_.Acceptable -eq 'no' } | ForEach-Object { [int]$_.Sample.ActiveMs })
        $phoneYes = @($toPhoneSamples | Where-Object { $_.Playing -and $_.Acceptable -eq 'yes' } | ForEach-Object { [int]$_.Sample.ReleasedMs })
        $phoneNo = @($toPhoneSamples | Where-Object { $_.Playing -and $_.Acceptable -eq 'no' } | ForEach-Object { [int]$_.Sample.ReleasedMs })
        Add-Finding -Run $run -Name 'toPcAcceptedUpToMs' -Value $(if (@($pcYes).Count -gt 0) { [int](@($pcYes | Measure-Object -Maximum).Maximum) } else { $null }) -Detail 'the longest switch to this PC you said was acceptable'
        Add-Finding -Run $run -Name 'toPcRejectedFromMs' -Value $(if (@($pcNo).Count -gt 0) { [int](@($pcNo | Measure-Object -Minimum).Minimum) } else { $null }) -Detail 'the shortest switch to this PC you said was not acceptable'
        Add-Finding -Run $run -Name 'toPhoneAcceptedUpToMs' -Value $(if (@($phoneYes).Count -gt 0) { [int](@($phoneYes | Measure-Object -Maximum).Maximum) } else { $null }) -Detail 'the longest switch to the phone you said was acceptable'
        Add-Finding -Run $run -Name 'toPhoneRejectedFromMs' -Value $(if (@($phoneNo).Count -gt 0) { [int](@($phoneNo | Measure-Object -Minimum).Minimum) } else { $null }) -Detail 'the shortest switch to the phone you said was not acceptable'

        $directMs = $null
        if ($null -ne $directLeg -and $null -ne $directLeg.Sample -and $directLeg.Sample.Path -eq 'direct') { $directMs = $directLeg.Sample.ActiveMs }
        Add-Finding -Run $run -Name 'directPathActiveMs' -Value $directMs `
            -Detail $(if ($null -eq $directLeg) { 'the optional leg was not run' } elseif ($null -eq $directMs) { 'the leg did not take the direct path, or its line could not be read, so it settles nothing' } else { 'Earshot''s own figure for a switch to this PC that took the direct path' })

        $excluded = @()
        foreach ($item in @($contendedPc)) { $excluded = $excluded + @('to-pc ' + $item.Name + ': queued ' + $(if ($null -ne $item.Sample.QueuedMs) { [string]$item.Sample.QueuedMs + ' ms' } else { 'not known' })) }
        foreach ($item in @($contendedPhone)) { $excluded = $excluded + @('to-phone ' + $item.Name + ': queued ' + $(if ($null -ne $item.Sample.QueuedMs) { [string]$item.Sample.QueuedMs + ' ms' } else { 'not known' })) }
        Add-Finding -Run $run -Name 'contendedSwitchesExcluded' -Value $(if (@($excluded).Count -gt 0) { $excluded } else { $null }) `
            -Detail 'switches that waited behind another operation, left out of the figures above; none when nothing was left out'

        Add-Finding -Run $run -Name 'firstRealPressOfShortcuts' -Value $firstRealPress `
            -Detail ('yes when Earshot''s log holds no earlier shortcut press (' + $pressesLoggedBefore + ' found) and you said you had not pressed either; a log that has rolled over cannot show older presses')

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
    Write-Host ('Test 16 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
