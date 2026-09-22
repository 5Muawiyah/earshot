<#
.SYNOPSIS
    The fake machine the live test scripts are run against by the self-test.

.DESCRIPTION
    Nothing here touches a device, a scheduled task, the registry outside a read, or any
    folder outside the sandbox the runner makes under %TEMP%. Earshot.exe is never started:
    Invoke-Earshot and Invoke-EarshotElevated are replaced, and Start-Process is replaced
    with one that throws, so a helper that grew a new way of starting something would stop
    the self-test rather than run it.

    Three things are faked.

    1. A small world: node state, render endpoint, protection, whether the tasks are set up.
       Each shipped command moves it the way the real one would, so a script that blocks and
       then reads the nodes sees Blocked, and a criterion's outcome follows from the fake
       inputs rather than from a table of answers.

    2. The reports. Every probe and diag target answers with the members the scripts read.
       The reports are written to the same files the real ones would be written to and read
       back through the real helpers, so Get-DiagEvidence, Copy-AppEvidence, Read-KsEvidence
       and Read-EarshotJsonFile all run for real.

    3. The Earshot log, and the evidence lists the scripts count: notifications, unelevated
       steps, watched battery values, installed services. Each of those holds 0, 1 or 2 items
       depending on the case, because 0 and 1 are where PowerShell unrolls a returned list
       into $null and into a bare value, and where a later .Count throws under strict mode.

    The owner is never asked anything: Read-Answer, Read-Note and Read-Host are answered from
    the tables below. A question that is not in a table is written to unanswered.txt, which
    the runner reads and fails the case on, so a new question cannot be answered by accident.
#>

#Requires -Version 5.1

Set-StrictMode -Version 2.0

# ----------------------------------------------------------------- the log fixture

# Every pattern the shipped scripts pass to Get-EarshotLogLines. The self-test writes each one
# 0, 1 or 2 times, so a run sees the empty answer, the single answer that PowerShell hands back
# as a bare string, and the list. No line matches more than one of these patterns.
$script:LogFixtures = @(
    # 45, not 30: deliberately different from BlockCoordinator.IdleGrace's default, so a script
    # that reads the grace period from this line rather than assuming the shipped constant is
    # provably reading it, and a script that still hardcodes 30 is provably not.
    [ordered]@{ Pattern = 'Blocking the nodes: the AirPods were not in use for'; Text = 'Blocking the nodes: the AirPods were not in use for 45 s' }
    [ordered]@{ Pattern = 'Idle block not issued'; Text = 'Idle block not issued: an operation was still in flight' }
    [ordered]@{ Pattern = 'Idle rule re-armed'; Text = 'Idle rule re-armed: the render endpoint changed' }
    [ordered]@{ Pattern = 'Session ending: block queued at'; Text = 'Session ending: block queued at 12:00:00' }
    [ordered]@{ Pattern = 'Session ending: no block issued'; Text = 'Session ending: no block issued, the nodes were already Blocked' }
    [ordered]@{ Pattern = 'WM_ENDSESSION received'; Text = 'WM_ENDSESSION received, flags 0x00000001' }
    [ordered]@{ Pattern = 'WM_QUERYENDSESSION received'; Text = 'WM_QUERYENDSESSION received, flags 0x00000000' }
    [ordered]@{ Pattern = 'Tray stopped.'; Text = 'Tray stopped.' }
    [ordered]@{ Pattern = 'TaskbarCreated received.'; Text = 'TaskbarCreated received.' }
    [ordered]@{ Pattern = 'connected at start-up'; Text = 'Logon check: connected at start-up' }
    [ordered]@{ Pattern = 'boot'; Text = 'Boot task finished at boot' }
    [ordered]@{ Pattern = 'args:'; Text = 'Gate rejected the args: status 1234' }
    [ordered]@{ Pattern = 'unregister'; Text = 'Notification client unregister requested' }

    # test 17 and test 18: the hand-back's own lines. None, one or two copies each, the same as
    # every other generic pattern, except for the bespoke cases below (handback-cut-short,
    # handback-not-reached, no-sleep-event, repaged-at-wake), which carry none of these generic
    # copies (they count as 0, like declined-start) and inject their own bespoke lines instead.
    [ordered]@{ Pattern = 'Hand-back (shutdown): started at'; Text = 'Hand-back (shutdown): started at 2026-09-22T01:31:42.000Z (WM_ENDSESSION, shutdown or restart); render Active; nodes Allowed; streaming none; Block at boot on' }
    [ordered]@{ Pattern = 'Hand-back (shutdown): disconnect'; Text = 'Hand-back (shutdown): disconnect S_OK, confirmed after 37 ms' }
    [ordered]@{ Pattern = 'Hand-back (shutdown): block sent at'; Text = 'Hand-back (shutdown): block sent at 2026-09-22T01:31:42.400Z' }
    [ordered]@{ Pattern = 'Hand-back (shutdown): finished in'; Text = 'Hand-back (shutdown): finished in 303 ms; disconnect confirmed; block Success' }
    [ordered]@{ Pattern = 'Hand-back (sleep): started at'; Text = 'Hand-back (sleep): started at 2026-09-22T01:41:00.000Z (PBT_APMSUSPEND); render Active; nodes Allowed; streaming none; Block at boot on' }
    [ordered]@{ Pattern = 'Hand-back (sleep): disconnect'; Text = 'Hand-back (sleep): disconnect S_OK, confirmed after 22 ms' }
    [ordered]@{ Pattern = 'Hand-back (sleep): block sent at'; Text = 'Hand-back (sleep): block sent at 2026-09-22T01:41:00.300Z' }
    [ordered]@{ Pattern = 'Hand-back (sleep): finished in'; Text = 'Hand-back (sleep): finished in 280 ms; disconnect confirmed; block Success' }
    [ordered]@{ Pattern = 'WM_POWERBROADCAST received: Suspend'; Text = 'WM_POWERBROADCAST received: Suspend (wParam 0x4).' }
    [ordered]@{ Pattern = 'WM_POWERBROADCAST received: ResumeAutomatic'; Text = 'WM_POWERBROADCAST received: ResumeAutomatic (wParam 0x12).' }
    [ordered]@{ Pattern = 'Hand-back (resume):'; Text = 'Hand-back (resume): the nodes were enabled and not in use, so they are blocked now' }
)

# The one pattern that is also written with a stamp in the past, so the -SinceUtc filter in
# Get-EarshotLogLines has a line to drop. Only test 13 reads it, and only with -SinceUtc.
$script:PastStampedPattern = 'Idle block not issued'

# ------------------------------------------------------------------ the fake world

# Where each test starts. The state is the one that test's own preconditions describe, so the
# script takes the path the owner would take on a machine that is set up properly.
$script:StartStates = @{
    '00-restore|first'                   = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'NotProtected'; SetUp = $true }
    '01-a2dp-oneshot|first'              = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'NotProtected'; SetUp = $true }
    '02-disconnect|first'                = @{ NodeState = 'Allowed'; Render = 'Active'; Protection = 'NotProtected'; SetUp = $true }
    '03-allow-pages|first'               = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '04-block-and-reboot|first'          = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '04-block-and-reboot|resume'         = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '05-allow|first'                     = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '05-allow|resume'                    = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '06-handsfree|first'                 = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'NotProtected'; SetUp = $true }
    '07-task-runex|first'                = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '08-acceptance-power-cycle|first'    = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '08-acceptance-power-cycle|resume'   = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '09-shutdown-while-connected|first'  = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '09-shutdown-while-connected|resume' = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '10-shutdown-messages-v1|first'      = @{ NodeState = 'Allowed'; Render = 'Active'; Protection = 'Protected'; SetUp = $true }
    '10-shutdown-messages-v1|resume'     = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '11-battery-disconnected|first'      = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '12-callback-thread|first'           = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '13-grace-window|first'              = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '14-set-device-refusal|first'        = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '15-uninstall-reversal|first'        = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '15-uninstall-reversal|resume'       = @{ NodeState = 'Allowed'; Render = 'Unplugged'; Protection = 'NotProtected'; SetUp = $true }
    '17-handback-on-shutdown|first'      = @{ NodeState = 'Allowed'; Render = 'Active'; Protection = 'Protected'; SetUp = $true }
    '17-handback-on-shutdown|resume'     = @{ NodeState = 'Blocked'; Render = 'Unplugged'; Protection = 'Protected'; SetUp = $true }
    '18-handback-on-sleep|first'         = @{ NodeState = 'Blocked'; Render = 'Active'; Protection = 'Protected'; SetUp = $true }
}

# The made up devices this fake machine has: the pinned pair, a phone with no A2DP sink, and a
# speaker that has one. None of the three addresses is real. They are only twelve hexadecimal
# characters of the right shape, so no real device identifier is committed.
$script:PinnedAddress = '0A1B2C3D4E8C'
$script:OtherAddress = 'B4C5D6E7F809'
$script:SpeakerAddress = 'C7D8E9F0A1B2'
$script:ContainerId = '5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13'
$script:SystemSid = 'S-1-5-18'

# What the owner answers. The key is a piece of the question, matched without case; the value is
# the answer, which must be one of the options the question offers.
$script:Answers = [ordered]@{
    'which state do you want to finish in'             = 'on'
    'leave protection on, as earshot ships'            = 'on'
    'did the audio move from your phone to this pc'    = 'yes'
    'did the audio stop playing on this pc'            = 'yes'
    'did the audio ever leave your phone'              = 'no'
    'did the airpods stay on your phone'               = 'yes'
    'do you want to restart now'                       = 'yes'
    'did the airpods stay connected to your phone'     = 'yes'
    'did the airpods connect to this pc after'         = 'yes'
    'did the airpods disconnect from this pc after'    = 'yes'
    'did any administrator prompt appear'              = 'no'
    'are the airpods playing from the phone again'     = 'yes'
    'is the tray glyph sharp'                          = 'yes'
    'did the icon come back at the right size'         = 'yes'
    'did the glyph ink flip'                           = 'yes'
    'did your keyboard focus stay'                     = 'yes'
    'did a fast double click still toggle only once'   = 'yes'
    'did the right click open the menu'                = 'yes'
    'could you reach the icon and open its menu'       = 'yes'
    'after turning it off in task manager'             = 'yes'
    'start earshot a second time'                      = 'yes'
    'with a second display attached'                   = 'yes'
    'with the taskbar set to hide itself'              = 'yes'
    'during a full-screen game'                        = 'yes'
    'in a contrast theme'                              = 'yes'
    'did the icon disappear cleanly'                   = 'yes'
    'did the airpods connect to this pc by themselves' = 'no'
    'is "hand back at shut down and sleep" ticked'     = 'yes'
    'did the airpods go back to your phone'            = 'yes'
    'did it take the airpods back by itself'           = 'no'
    'did earshot ever cut the connection'              = 'no'
    'did the list show your airpods and your phone'    = 'yes'
    'were devices that are not present now shown'      = 'yes'
    'did the first half install earshot again'         = 'yes'
}

# What the owner types for a free-text note.
$script:Notes = [ordered]@{
    'twelve character address of your phone'  = 'B4C5D6E7F809'
    'how did the unsupplied third argument'   = 'the literal placeholder arrived'
    'what did the card near the tray say'     = 'Connected.'
    'what did the card say that time'         = 'Back on your phone.'
}

$script:World = $null
$script:Context = $null

# How many matching lines and list items each case holds. grace-doubled and grace-unparsable are
# test 13's own cases (see the "13 grace window" section of New-FakeSandbox below): they carry no
# generic matching lines of their own, only the bespoke ones that section adds, so they count as
# 0 here the same as none. Every atrest-* case and declined-start belong to the at-rest closing
# step and the preconditions prompt (Run-OneHalf.ps1's Invoke-Earshot and Read-Host intercept
# those by name, not by anything counted here), and carry no bespoke log content of their own
# either, so they all count as 0 too.
$script:CaseItemCounts = @{
    none = 0; one = 1; two = 2; 'grace-doubled' = 0; 'grace-unparsable' = 0
    'atrest-decline' = 0; 'atrest-guard-throws' = 0; 'atrest-block-ineffective' = 0
    'atrest-setup-unknown' = 0; 'atrest-config-missing' = 0; 'atrest-nodes-probe-fails' = 0; 'atrest-nodes-stay-unreadable' = 0
    'declined-start' = 0
    'handback-cut-short' = 0; 'handback-not-reached' = 0; 'no-sleep-event' = 1; 'repaged-at-wake' = 1
}

function Initialize-FakeMachine
{
    param(
        [Parameter(Mandatory = $true)][string]$SandboxRoot,
        [Parameter(Mandatory = $true)][string]$TestId,
        [Parameter(Mandatory = $true)][ValidateSet('first', 'resume')][string]$Half,
        [Parameter(Mandatory = $true)][ValidateSet(
            'none', 'one', 'two', 'grace-doubled', 'grace-unparsable',
            'atrest-decline', 'atrest-guard-throws', 'atrest-block-ineffective',
            'atrest-setup-unknown', 'atrest-config-missing', 'atrest-nodes-probe-fails', 'atrest-nodes-stay-unreadable',
            'declined-start', 'handback-cut-short', 'handback-not-reached', 'no-sleep-event', 'repaged-at-wake')][string]$Case
    )

    $counts = $script:CaseItemCounts
    $key = [string]$TestId + '|' + $Half
    if (-not $script:StartStates.ContainsKey($key))
    {
        throw ('The self-test has no start state for ' + $key + '. Add one to StartStates in Fakes.psm1.')
    }

    $start = $script:StartStates[$key]
    $script:Context = [ordered]@{
        SandboxRoot   = $SandboxRoot
        TestId        = $TestId
        Half          = $Half
        Case          = $Case
        Items         = $counts[$Case]
        ExePath       = (Join-Path (Join-Path $SandboxRoot 'release') 'Earshot.exe')
        ProgramFolder = (Join-Path (Join-Path $SandboxRoot 'programfiles') 'Earshot')
        DataFolder    = (Join-Path (Join-Path $SandboxRoot 'programdata') 'Earshot')
        Unanswered    = (Join-Path $SandboxRoot 'unanswered.txt')
        Evidence      = 0
    }

    $script:World = [ordered]@{
        NodeState  = $start.NodeState
        Render     = $start.Render
        Protection = $start.Protection
        SetUp      = $start.SetUp
    }

    # test 18's own case: the resume check never got the chance to re-block, because this computer
    # re-paged the AirPods first, so the nodes read Allowed, not Blocked, and the closing step's
    # own re-read is what has to offer the block.
    if ($Case -eq 'repaged-at-wake') { $script:World.NodeState = 'Allowed' }
}

function Get-FakeContext
{
    if ($null -eq $script:Context) { throw 'Initialize-FakeMachine was not called.' }
    return $script:Context
}

# ------------------------------------------------------------------- the sandbox

# Builds the folders, the fake log and the JSON files Earshot owns. Everything is under
# $SandboxRoot, which the runner puts in %TEMP%: no real ProgramData, Program Files or
# Earshot folder is written to, and the child process has LOCALAPPDATA, APPDATA,
# ProgramData and ProgramFiles pointed here.
function New-FakeSandbox
{
    param(
        [Parameter(Mandatory = $true)][string]$SandboxRoot,
        [Parameter(Mandatory = $true)][ValidateSet(
            'none', 'one', 'two', 'grace-doubled', 'grace-unparsable',
            'atrest-decline', 'atrest-guard-throws', 'atrest-block-ineffective',
            'atrest-setup-unknown', 'atrest-config-missing', 'atrest-nodes-probe-fails', 'atrest-nodes-stay-unreadable',
            'declined-start', 'handback-cut-short', 'handback-not-reached', 'no-sleep-event', 'repaged-at-wake')][string]$Case
    )

    $counts = $script:CaseItemCounts
    $items = $counts[$Case]
    $local = Join-Path $SandboxRoot 'local'
    $data = Join-Path (Join-Path $SandboxRoot 'programdata') 'Earshot'
    $folders = @(
        $local
        (Join-Path $SandboxRoot 'roaming')
        (Join-Path (Join-Path $SandboxRoot 'programfiles') 'Earshot')
        (Join-Path $SandboxRoot 'release')
        $data
        (Join-Path (Join-Path $local 'Earshot') 'logs')
        (Join-Path (Join-Path $local 'Earshot') 'livetest')
        (Join-Path (Join-Path $SandboxRoot 'roaming') 'Earshot')
    )

    foreach ($folder in $folders) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }

    # The log lines are stamped an hour ahead of now. Several scripts read the log with
    # -SinceUtc set from a time they take part way through the run, and a line stamped when the
    # sandbox was built would be dropped by that filter, which would hide the very counts this
    # self-test is about. One line per the pattern named in PastStampedPattern is stamped in
    # 2001 instead, so the filter has something it must drop.
    $ahead = (Get-Date).ToUniversalTime().AddHours(1)
    $lines = @()
    $index = 0
    foreach ($fixture in $script:LogFixtures)
    {
        for ($i = 0; $i -lt $items; $i++)
        {
            $index = $index + 1
            $stamp = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
            $lines = $lines + @([string]$stamp + ' INFO  ' + $fixture.Text + ' (' + ($i + 1) + ')')
        }

        if ($fixture.Pattern -eq $script:PastStampedPattern)
        {
            $lines = $lines + @('2001-02-03T04:05:06.007Z INFO  ' + $fixture.Text + ' (before the window)')
        }
    }

    # 13 grace window: two cases that exist only for that script, neither reachable by choosing an
    # item count. Both carry a "Blocking the nodes" line, which is the one thing that pattern
    # counts, so 13-GraceWindow.ps1 still sees itself as blocked in each.
    #
    # grace-doubled: an automatic block failed first (the line NoteAutomaticBlock writes,
    # BlockCoordinator.cs:2439, "...the nodes are still enabled (...). The idle rule tries again
    # after N s..."), which is what doubles IdleDelay away from IdleGrace
    # (BlockCoordinator.cs:320), then the block itself reports that doubled figure, 90, not the
    # grace, 45.
    #
    # grace-unparsable: the block line is there, so the idle rule plainly fired, but its figure is
    # not a number Get-DelaySecondsAtBlock's regex can read. "not measured" would be dishonest for
    # this one; the script has to say the figure could not be parsed, not that nothing blocked.
    if ($Case -eq 'grace-doubled')
    {
        $index = $index + 1
        $failStamp = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$failStamp + ' WARN  idle block: the nodes are still enabled (a fake reason). The idle rule tries again after 90 s if they are still enabled and not in use.')

        $index = $index + 1
        $blockStamp = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$blockStamp + ' INFO  Blocking the nodes: the AirPods were not in use for 90 s with nothing in flight (2001-02-03T04:05:06.009Z).')
    }
    elseif ($Case -eq 'grace-unparsable')
    {
        $index = $index + 1
        $blockStamp = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$blockStamp + ' INFO  Blocking the nodes: the AirPods were not in use for a while with nothing in flight (2001-02-03T04:05:06.009Z).')
    }

    # test 17 and test 18, handback-cut-short: the hold ran out before the block it had already
    # sent came back, on both the shutdown and the sleep shape, so whichever of the two scripts
    # reads this sees its own "started", "disconnect" and "cut short" lines, and no "finished in"
    # line at all. The sleep shape carries its own point (test 18's own case): the block was never
    # sent either, so "cut short ... block was not sent" reads as a fail there.
    elseif ($Case -eq 'handback-cut-short')
    {
        $index = $index + 1; $q1 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$q1 + ' INFO  WM_QUERYENDSESSION received: shutdown or restart (lParam 0x00000000).')
        $index = $index + 1; $e1 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$e1 + ' INFO  WM_ENDSESSION received: ending, shutdown or restart (lParam 0x00000000).')
        $index = $index + 1; $t1 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$t1 + ' INFO  Hand-back (shutdown): started at ' + $t1 + ' (WM_ENDSESSION, shutdown or restart); render Active; nodes Allowed; streaming none; Block at boot on')
        $index = $index + 1; $t2 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$t2 + ' INFO  Hand-back (shutdown): disconnect S_OK, confirmed after 20 ms')
        $index = $index + 1; $t3 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$t3 + ' INFO  Hand-back (shutdown): block sent at ' + $t3)
        $index = $index + 1; $t4 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$t4 + ' WARN  Hand-back (shutdown): cut short at 4000 ms; still running: block; block was sent at ' + $t3)

        $index = $index + 1; $s1 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$s1 + ' INFO  Hand-back (sleep): started at ' + $s1 + ' (PBT_APMSUSPEND); render Active; nodes Allowed; streaming none; Block at boot on')
        $index = $index + 1; $s2 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$s2 + ' INFO  Hand-back (sleep): disconnect S_OK, not confirmed within 750 ms')
        $index = $index + 1; $s3 = $ahead.AddSeconds($index).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $lines = $lines + @([string]$s3 + ' WARN  Hand-back (sleep): cut short at 1500 ms; still running: disconnect, block; block was not sent')
    }

    # A line with no stamp at all and a line no pattern looks for, so reading a log that holds
    # more than the searched lines is covered too.
    $lines = $lines + @('no stamp on this line at all, and no pattern looks for it')
    $lines = $lines + @('2001-02-03T04:05:06.008Z INFO  a line no pattern looks for')

    Set-Content -LiteralPath (Join-Path (Join-Path (Join-Path $local 'Earshot') 'logs') 'earshot.log') `
        -Value $lines -Encoding UTF8

    Write-FakeMachineFiles -DataFolder $data -BlockAtBoot $true -Items $items

    # atrest-config-missing is the real failure shape Get-BlockAtBootSetting sees when
    # config.json is not there: Read-EarshotJsonFile returns $null without throwing, the same as
    # a probe that could not answer. Removing the file Write-FakeMachineFiles just wrote is
    # simpler and more honest than teaching that function a case it otherwise has no reason to
    # know about.
    if ($Case -eq 'atrest-config-missing')
    {
        Remove-Item -LiteralPath (Join-Path $data 'config.json') -Force -ErrorAction SilentlyContinue
    }

    Set-Content -LiteralPath (Join-Path (Join-Path $SandboxRoot 'roaming') 'Earshot\settings.json') `
        -Value (@{ ProtectAudioQuality = $true; OpenOnStartup = $true } | ConvertTo-Json) -Encoding UTF8
    Set-Content -LiteralPath (Join-Path (Join-Path $SandboxRoot 'release') 'Earshot.files.json') `
        -Value (@{ files = @() } | ConvertTo-Json) -Encoding UTF8
}

# config.json, device.json and protection.json, as the SYSTEM side of Earshot writes them.
function Write-FakeMachineFiles
{
    param(
        [Parameter(Mandatory = $true)][string]$DataFolder,
        [Parameter(Mandatory = $true)][bool]$BlockAtBoot,
        [Parameter(Mandatory = $true)][int]$Items
    )

    New-Item -ItemType Directory -Force -Path $DataFolder | Out-Null
    Set-Content -LiteralPath (Join-Path $DataFolder 'config.json') `
        -Value (@{ BlockAtBoot = $BlockAtBoot } | ConvertTo-Json) -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $DataFolder 'device.json') `
        -Value ([ordered]@{ Address = $script:PinnedAddress; ContainerId = $script:ContainerId } | ConvertTo-Json) -Encoding UTF8

    # ProtectionRecord keeps plain GUIDs, so that is what this writes.
    $disabled = @()
    for ($i = 0; $i -lt $Items; $i++)
    {
        $disabled = $disabled + @('0000111' + $i + '-0000-1000-8000-00805F9B34FB')
    }

    Set-Content -LiteralPath (Join-Path $DataFolder 'protection.json') `
        -Value ([ordered]@{ DisabledServices = $disabled; PendingProtect = $null } | ConvertTo-Json -Depth 5) -Encoding UTF8
}

# ------------------------------------------------------------------- the reports

function Get-FakeNodes
{
    $nodes = @()
    $disabled = ($script:World.NodeState -eq 'Blocked')
    $problem = 0
    if ($disabled) { $problem = 22 }

    foreach ($service in @('0000110B', '0000111E', '0000110A'))
    {
        $nodes = $nodes + @([ordered]@{
                instanceId          = ('BTHENUM\{' + $service + '-0000-1000-8000-00805F9B34FB}_VID&0001005D_PID&2038\8&' + $script:PinnedAddress + '&0&' + $script:PinnedAddress + '_C00000000')
                target              = $true
                present             = $true
                status              = '0x0180200A'
                problem             = $problem
                configFlagsDisabled = $disabled
            })
    }

    # One node belonging to another paired device, so the address sweep in test 14 has
    # something other than the pinned device to find.
    $nodes = $nodes + @([ordered]@{
            instanceId          = ('BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\7&' + $script:OtherAddress + '&0&' + $script:OtherAddress + '_C00000000')
            target              = $false
            present             = $true
            status              = '0x0180200A'
            problem             = 0
            configFlagsDisabled = $false
        })

    return [ordered]@{
        address   = $script:PinnedAddress
        container = $script:ContainerId
        nodeState = $script:World.NodeState
        nodes     = $nodes
    }
}

function Get-FakeAudio
{
    $render = $script:World.Render
    $capture = 'NotPresent'
    $connection = 'Disconnected'
    if ($render -eq 'Active')
    {
        $connection = 'Connected'
        if ($script:World.Protection -ne 'Protected') { $capture = 'Active' }
    }

    return [ordered]@{
        resolution = 'Pinned'
        device     = [ordered]@{ connection = $connection; containerId = $script:ContainerId; address = $script:PinnedAddress }
        groups     = @(
            [ordered]@{
                containerId = $script:ContainerId
                endpoints   = @(
                    [ordered]@{ flow = 'Render'; state = $render; friendlyName = 'AirPods Pro 3 (Stereo)' }
                    [ordered]@{ flow = 'Capture'; state = $capture; friendlyName = 'AirPods Pro 3 (Hands-Free)' }
                )
            })
    }
}

function Get-FakeServices
{
    $listed = @()
    for ($i = 0; $i -lt $script:Context.Items; $i++)
    {
        $listed = $listed + @([ordered]@{ label = ('Service ' + ($i + 1)); guid = ('0000111' + $i + '-0000-1000-8000-00805F9B34FB') })
    }

    return [ordered]@{
        protection        = $script:World.Protection
        complete          = $true
        connected         = ($script:World.Render -eq 'Active')
        installedServices = $listed
    }
}

function Get-FakeTask
{
    $rows = @()
    foreach ($name in @('\Earshot\Boot', '\Earshot\Gate', '\Earshot\Protect'))
    {
        $rows = $rows + @([ordered]@{
                path           = $name
                present        = $script:World.SetUp
                userId         = $script:SystemSid
                logonType      = 'ServiceAccount'
                runLevel       = 'Highest'
                trayMayRun     = $true
                userMask       = '0x00000113'
                lastTaskResult = 0
            })
    }

    return [ordered]@{ setUp = $script:World.SetUp; tasks = $rows }
}

function Get-FakeTopology
{
    return [ordered]@{
        adapters = @(
            [ordered]@{ adapterId = 'A2DP source filter'; guardPassed = $true; ksControlActivated = $true; pinCount = 2 }
            [ordered]@{ adapterId = 'Handsfree filter'; guardPassed = $true; ksControlActivated = $true; pinCount = 3 }
        )
    }
}

# The filters a diag ks run finds, for the filter word the script passed. With protection on
# there is no Handsfree filter, which is what test 01 and test 02 are checking.
function Get-FakeKsFilters
{
    param([Parameter(Mandatory = $true)][string]$Filter)

    $roles = @()
    if ($Filter -eq 'src' -or $Filter -eq 'all') { $roles = $roles + @('A2DP') }
    if (($Filter -eq 'wave' -or $Filter -eq 'all') -and $script:World.Protection -ne 'Protected') { $roles = $roles + @('HFP') }

    $rows = @()
    foreach ($role in $roles)
    {
        $rows = $rows + @([ordered]@{
                role              = $role
                name              = ([string]$role + ' filter')
                guardPassed       = $true
                ksControlActivated = $true
                requestSent       = $true
                notSentReason     = $null
                hr                = '0x00000000'
                hrName            = 'S_OK'
                accepted          = $true
                bytesReturned     = 0
                callMilliseconds  = 40
            })
    }

    return ,$rows
}

function Get-FakeKsEvidence
{
    param(
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][string]$Filter,
        [string]$FilterChoice = ''
    )

    # Plain, not @(...): Get-FakeKsFilters returns its list with a leading comma, and @() round
    # the call would wrap that list in another one.
    $filters = Get-FakeKsFilters -Filter $Filter
    $reached = (@($filters).Count -gt 0)
    $notifications = @()
    if ($reached)
    {
        for ($i = 0; $i -lt $script:Context.Items; $i++)
        {
            $notifications = $notifications + @([ordered]@{
                    sequence                 = ($i + 1)
                    utc                      = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
                    kind                     = 'StateChanged'
                    newState                 = $(if ($Action -eq 'reconnect') { 'Active' } else { 'Unplugged' })
                    millisecondsAfterRequest = (400 * ($i + 1))
                    threadId                 = (7000 + $i)
                    apartment                = 'MTA'
                })
        }
    }

    $choice = $Filter
    if (-not [string]::IsNullOrEmpty($FilterChoice)) { $choice = $FilterChoice }
    return [ordered]@{
        action                                    = $Action
        filterChoice                              = $choice
        payload                                   = 'documented'
        error                                     = $null
        sendFault                                 = $null
        filters                                   = $filters
        notifications                             = $notifications
        confirmation                              = [ordered]@{ reached = $reached; unreachable = (-not $reached); source = 'notification' }
        millisecondsToFirstWantedStateNotification = $(if ($reached) { 1200 } else { $null })
    }
}

function Get-FakeGateEvidence
{
    param(
        [Parameter(Mandatory = $true)][string]$Verb,
        [string]$Argument = ''
    )

    # set-device is the one verb the gate is meant to refuse. The phone has no A2DP sink, so it
    # is refused with 12; the speaker has one, so it is refused with 13 instead, because the
    # device pinned now still has services turned off.
    $exit = 0
    $result = 'Success'
    if ($Verb -eq 'set-device')
    {
        $exit = 12
        $result = 'NotAudioSink'
        if ($Argument -eq $script:SpeakerAddress)
        {
            $exit = 13
            $result = 'OtherDeviceProtected'
        }
    }

    return [ordered]@{
        outcome            = 'Completed'
        nonce              = '4f1c9b7a2e6d4a118c3f0b5d9e2a7c64'
        runMilliseconds    = 12000
        lastTaskResult     = $exit
        lastTaskResultCode = $exit
        statusFile         = [ordered]@{ exitCode = $exit; result = $result; nonce = '4f1c9b7a2e6d4a118c3f0b5d9e2a7c64' }
    }
}

function Get-FakeUnelevatedEvidence
{
    param([Parameter(Mandatory = $true)][string]$Mode)

    $steps = @()
    for ($i = 0; $i -lt $script:Context.Items; $i++)
    {
        $steps = $steps + @([ordered]@{
                step     = ('BluetoothSetServiceState ' + $Mode + ' ' + ($i + 1))
                code     = 5
                codeName = 'ERROR_ACCESS_DENIED'
                detail   = 'a non-elevated caller was refused'
            })
    }

    return [ordered]@{ steps = $steps }
}

function Get-FakeSweepEvidence
{
    $matched = @()
    foreach ($suffix in @('A', 'B'))
    {
        $matched = $matched + @([ordered]@{
                instanceId   = ('BTHENUM\DEV_' + $script:PinnedAddress + '_' + $suffix)
                friendlyName = ('AirPods Pro 3 node ' + $suffix)
                present      = $true
                keys         = 41
            })
    }

    $values = @()
    for ($i = 0; $i -lt $script:Context.Items; $i++)
    {
        $values = $values + @([ordered]@{ key = ('{104EA319-6EE2-4701-BD47-8DDBF425BBE5} ' + ($i + 2)); value = (70 + $i) })
    }

    return [ordered]@{
        matched                          = $matched
        watchedValues                    = $values
        containerIdReadOnAMatchedNode    = $true
    }
}

function Get-FakeBattery
{
    return [ordered]@{ hasSource = $false; hasValue = $false }
}

# What Get-PowerEvents answers with, for tests 17 and 18. A fixed set with one of each id the real
# helper asks for, at increasing timestamps: a 1074 shutdown request, a 42 sleep, a 107 wake, a 27
# boot type. none/one/two take the first 0, 1 or 2 of those, in order, which keeps 17's
# shutdown-was-clean (needs only the 1074) and 18's sleep-happened (needs the 42 AND a later 107)
# each reading exactly what the shared cases can honestly show: shutdown-was-clean can pass on the
# shared "one" case, sleep-happened stays inconclusive until the bespoke case below supplies both.
# no-sleep-event is the one case that answers empty regardless of the shared count, test 18's own
# point: nothing here says a sleep happened at all.
function Get-FakePowerEvents
{
    $fake = Get-FakeContext
    if ($fake.Case -eq 'no-sleep-event') { return , @() }

    $all = @(
        [ordered]@{ utc = '2026-09-22T01:31:42.000Z'; id = 1074; provider = 'User32'; message = 'shutdown.exe (2036) has initiated the restart of computer on behalf of user for the following reason: No title for this reason could be found' }
        [ordered]@{ utc = '2026-09-22T01:41:00.000Z'; id = 42; provider = 'Microsoft-Windows-Kernel-Power'; message = 'The system is entering sleep.' }
        [ordered]@{ utc = '2026-09-22T01:45:12.000Z'; id = 107; provider = 'Microsoft-Windows-Kernel-Power'; message = 'The system has resumed from sleep.' }
        [ordered]@{ utc = '2026-09-22T01:45:20.000Z'; id = 27; provider = 'Microsoft-Windows-Kernel-Boot'; message = 'The boot type was 0x0.' }
    )

    $counts = $script:CaseItemCounts
    $count = 1
    if ($counts.ContainsKey($fake.Case)) { $count = $counts[$fake.Case] }
    if ($fake.Case -eq 'handback-cut-short' -or $fake.Case -eq 'repaged-at-wake') { $count = 4 }

    return , @($all | Select-Object -First $count)
}

# ------------------------------------------------------------- how a command moves the world

# Whether a ks run reached the state it asked for. The evidence is the table this module built,
# so it is read straight rather than through the shipped Get-FieldPath, which keeps the fakes
# independent of the module they are used to test.
function Test-FakeReached
{
    param($Evidence)

    if ($null -eq $Evidence) { return $false }
    if (-not ($Evidence -is [System.Collections.IDictionary])) { return $false }
    if (-not $Evidence.Contains('confirmation')) { return $false }
    return ($Evidence['confirmation']['reached'] -eq $true)
}

# The one place a shipped command changes the fake machine. It is deliberately the same shape
# as the real effect, so a criterion that reads the state back after a step gets the answer the
# real gate would have given.
function Update-FakeWorld
{
    param(
        [Parameter(Mandatory = $true)][string[]]$Command,
        $Evidence
    )

    $text = ($Command -join ' ')

    # atrest-block-ineffective proves the closing check's re-read, not the step's own reported
    # success, is what decides leftAtRest: the step below still answers as a plain success (see
    # Get-FakeGateEvidence), but the world does not actually move, the same as a block the real
    # gate accepted the task for yet vetoed or only partly carried out.
    if ($text -eq 'diag gate block' -and (Get-FakeContext).Case -eq 'atrest-block-ineffective') { return }

    switch -Regex ($text)
    {
        '^diag gate allow' { $script:World.NodeState = 'Allowed' }
        '^diag gate block' { $script:World.NodeState = 'Blocked'; $script:World.Render = 'Unplugged' }
        '^diag gate protect-on' { $script:World.Protection = 'Protected' }
        '^diag gate protect-off' { $script:World.Protection = 'NotProtected' }
        '^diag gate setboot-off' { Write-FakeMachineFiles -DataFolder $script:Context.DataFolder -BlockAtBoot $false -Items $script:Context.Items }
        '^diag gate setboot-on' { Write-FakeMachineFiles -DataFolder $script:Context.DataFolder -BlockAtBoot $true -Items $script:Context.Items }
        '^diag (ks reconnect|connect)'
        {
            if (Test-FakeReached -Evidence $Evidence) { $script:World.Render = 'Active' }
        }
        '^diag (ks disconnect|disconnect)'
        {
            if (Test-FakeReached -Evidence $Evidence) { $script:World.Render = 'Unplugged' }
        }
        '^uninstall'
        {
            $script:World.NodeState = 'Allowed'
            $script:World.Protection = 'NotProtected'
            $script:World.SetUp = $false
            foreach ($folder in @($script:Context.ProgramFolder, $script:Context.DataFolder))
            {
                if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
            }
        }
        '^install'
        {
            $script:World.SetUp = $true
            New-Item -ItemType Directory -Force -Path $script:Context.ProgramFolder | Out-Null
            Write-FakeMachineFiles -DataFolder $script:Context.DataFolder -BlockAtBoot $true -Items $script:Context.Items
        }
    }
}

# The report a command answers with, and the evidence file it writes beside the run folders.
# Both come back so the caller can write them where the real command would have.
function Get-FakeCommandAnswer
{
    param([Parameter(Mandatory = $true)][string[]]$Command)

    $report = $null
    $evidence = $null
    $text = ($Command -join ' ')

    if ($Command[0] -eq 'probe')
    {
        switch ($Command[1])
        {
            'nodes' { $report = Get-FakeNodes }
            'audio' { $report = Get-FakeAudio }
            'services' { $report = Get-FakeServices }
            'task' { $report = Get-FakeTask }
            'topology' { $report = Get-FakeTopology }
            'battery' { $report = Get-FakeBattery }
            default { $report = [ordered]@{ target = $Command[1] } }
        }
    }
    elseif ($Command[0] -eq 'diag')
    {
        switch ($Command[1])
        {
            'ks' { $evidence = Get-FakeKsEvidence -Action $Command[2] -Filter $Command[3] -FilterChoice $(if ($Command.Count -gt 4) { [string]$Command[4] } else { '' }) }
            'connect' { $evidence = Get-FakeKsEvidence -Action 'reconnect' -Filter 'all' }
            'disconnect' { $evidence = Get-FakeKsEvidence -Action 'disconnect' -Filter 'all' }
            'gate' { $evidence = Get-FakeGateEvidence -Verb $Command[2] -Argument $(if ($Command.Count -gt 3) { [string]$Command[3] } else { '' }) }
            'protect-unelevated' { $evidence = Get-FakeUnelevatedEvidence -Mode $Command[2] }
            'battery-sweep' { $evidence = Get-FakeSweepEvidence }
            default { $evidence = [ordered]@{ target = $Command[1] } }
        }
    }

    return [ordered]@{ Report = $report; Evidence = $evidence; Text = $text }
}

# ---------------------------------------------------------------- answering the owner

function Get-FakeAnswer
{
    param(
        [Parameter(Mandatory = $true)][string]$Question,
        [Parameter(Mandatory = $true)][string[]]$Options
    )

    $lower = $Question.ToLowerInvariant()
    foreach ($key in $script:Answers.Keys)
    {
        if ($lower.Contains($key))
        {
            $answer = $script:Answers[$key]
            if (-not ($Options -contains $answer))
            {
                Write-FakeGap -Text ('The answer "' + $answer + '" is not one of the options ' + ($Options -join '/') + ' for: ' + $Question)
                return $Options[0]
            }

            return $answer
        }
    }

    Write-FakeGap -Text ('No answer in Fakes.psm1 for the question: ' + $Question)
    return $Options[0]
}

function Get-FakeNote
{
    param([Parameter(Mandatory = $true)][string]$Question)

    $lower = $Question.ToLowerInvariant()
    foreach ($key in $script:Notes.Keys)
    {
        if ($lower.Contains($key)) { return $script:Notes[$key] }
    }

    Write-FakeGap -Text ('No note in Fakes.psm1 for the question: ' + $Question)
    return 'nothing was typed'
}

# What Wait-Owner asks the owner to do by hand is the other way the machine changes during a
# run, so the fake owner does it. The phrases that hand the AirPods back are matched first,
# because several of them also hold the word connect.
function Update-FakeWorldForOwnerAction
{
    param([Parameter(Mandatory = $true)][string]$Text)

    $lower = $Text.ToLowerInvariant()
    foreach ($away in @('once more', 'stop using', 'disconnect the airpods'))
    {
        if ($lower.Contains($away))
        {
            $script:World.Render = 'Unplugged'
            return
        }
    }

    # test 18's own sleep instruction: the hand-back released the link before sleep, so render
    # reads not-ACTIVE by the time the owner is back to answer. repaged-at-wake is that one case's
    # own point: this computer took the AirPods back by itself on waking, so render stays ACTIVE.
    if ($lower.Contains('put this computer to sleep'))
    {
        if ((Get-FakeContext).Case -ne 'repaged-at-wake') { $script:World.Render = 'Unplugged' }
        return
    }

    # "left-click its tray icon once" is the acceptance test's way of saying connect them.
    foreach ($towards in @('connect', 'left-click'))
    {
        if ($lower.Contains($towards))
        {
            $script:World.Render = 'Active'
            return
        }
    }
}

# A question, an option or a command the fakes do not cover. The runner fails the case on it
# rather than letting the run carry on with a value nobody chose.
function Write-FakeGap
{
    param([Parameter(Mandatory = $true)][string]$Text)

    Add-Content -LiteralPath $script:Context.Unanswered -Value $Text -Encoding UTF8
}

function Get-FakeEvidenceName
{
    param([Parameter(Mandatory = $true)][string]$Label)

    $script:Context.Evidence = $script:Context.Evidence + 1
    return ('{0:d3}-{1}.json' -f $script:Context.Evidence, $Label)
}

Export-ModuleMember -Function `
    Initialize-FakeMachine, Get-FakeContext, New-FakeSandbox, Write-FakeMachineFiles,
    Get-FakeNodes, Get-FakeAudio, Get-FakeServices, Get-FakeTask, Get-FakeTopology,
    Get-FakeKsFilters, Get-FakeKsEvidence, Get-FakeGateEvidence, Get-FakeUnelevatedEvidence,
    Get-FakeSweepEvidence, Get-FakeBattery, Get-FakePowerEvents, Test-FakeReached, Update-FakeWorld, Get-FakeCommandAnswer,
    Get-FakeAnswer, Get-FakeNote, Update-FakeWorldForOwnerAction, Write-FakeGap, Get-FakeEvidenceName
