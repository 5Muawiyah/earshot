<#
.SYNOPSIS
    Runs every shipped live test script, both halves of every resumable one, against a fake
    machine, and checks what each run recorded.

.DESCRIPTION
    The live tests are the only record of what the owner's AirPods actually did, and they are
    written in PowerShell under Set-StrictMode -Version 2.0. Under strict mode a run can be
    parsed, reviewed and still throw on its first criterion, because PowerShell unrolls a
    returned list: a helper that ends "return $found" hands back $null for no matches and a
    bare string for one, and .Count on either throws. The throw lands in the script's own
    catch, which records "run: fail" and abandons every criterion after it, and it happens on
    the success path, where the answer is "no matching line".

    A parse test cannot see that, and neither can a scan of the source. This runs the scripts.

    Each case is one shipped script, one half, one set of fake inputs, in its own
    powershell.exe 5.1 process, against its own sandbox under %TEMP%. Only the device-touching
    and owner-prompting helpers are replaced (see Run-OneHalf.ps1). Three sets of fake inputs
    are used, because the counts that matter are 0, 1 and more than 1:

      none  every log pattern and every evidence list is empty
      one   exactly one matching line and one item in every list
      two   two of each

    A case passes when all of these hold:

      * nothing was written to the error stream, and the output holds no PropertyNotFoundStrict
        or other terminating error,
      * result.json exists, records no error, and carries no "run" criterion, which is the one
        the scripts add from their own catch,
      * every criterion the expectations name was recorded, with no extra and none missing,
      * the overall outcome is the one the fake inputs imply,
      * every criterion whose outcome the fake inputs decide has that outcome, and
      * no question, option or command fell outside the fakes.

.PARAMETER Root
    The repository root. Defaults to the folder three above this script.

.PARAMETER WorkRoot
    Where the sandboxes go. Defaults to a new folder under %TEMP%.

.PARAMETER Case
    Run one set of fake inputs rather than all three. Also accepts a case that belongs to a
    single row's own Cases override (see $tests below), such as one of 13's grace cases or the
    at-rest closing step's atrest-decline and atrest-read-fails.

.PARAMETER Test
    Run one test, by its number, rather than all of them.

.PARAMETER Keep
    Leave the sandboxes behind for reading.

.PARAMETER Observed
    Print what each case recorded, as a table, instead of checking it. For working on the
    expectations, never for proving anything.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Invoke-SelfTest.ps1
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$WorkRoot = '',
    [ValidateSet('', 'none', 'one', 'two', 'grace-doubled', 'grace-unparsable',
        'atrest-decline', 'atrest-guard-throws', 'atrest-block-ineffective',
        'atrest-setup-unknown', 'atrest-config-missing', 'atrest-nodes-probe-fails', 'atrest-nodes-stay-unreadable',
        'declined-start')][string]$Case = '',
    [string]$Test = '',
    [switch]$Keep,
    [switch]$Observed
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Root)) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }
$scriptFolder = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$driver = Join-Path $PSScriptRoot 'Run-OneHalf.ps1'
$expectationsPath = Join-Path $PSScriptRoot 'expectations.psd1'
Import-Module (Join-Path $PSScriptRoot 'Fakes.psm1') -Force

# Every shipped test, with the halves it has and anything beyond -ExePath and -RunRoot it takes.
# A test added to tools\live-tests without a row here is reported as not covered.
$tests = @(
    # atrest-guard-throws is added on top of the shared three: it forces something to throw
    # unexpectedly deep in the at-rest closing step (Run-OneHalf.ps1's Invoke-Earshot, label
    # 'at-rest-nodes'), proving Close-AtRest's own catch and Complete-LiveTestRun's wrapping one,
    # not the null handling (see 01's cases for that). 00-Restore's own criteria are untouched.
    [ordered]@{ Number = '00'; Id = '00-restore'; Script = '00-Restore.ps1'; Halves = @('first'); Extra = @()
        Cases = @('none', 'one', 'two', 'atrest-guard-throws') }
    # Added on top of the shared three: 01 never calls "diag gate block" itself and ends with the
    # nodes Allowed in every case, so this is where the at-rest closing step's own offer is the
    # only "diag gate block" step in the run, exercised cleanly (Run-OneHalf.ps1's Invoke-Earshot,
    # labels 'at-rest-task', 'at-rest-nodes', 'at-rest-nodes-after' and 'at-rest-block'):
    # atrest-decline (declined, no block sent), atrest-block-ineffective (the step answers success
    # but the world does not move, proving the re-read decides, not the step's own exit code),
    # atrest-setup-unknown and atrest-config-missing (the setup or Block at boot read fails or is
    # missing, and this must still check the nodes rather than call it not-applicable),
    # atrest-nodes-probe-fails (the first node read fails but the later one, after the offer,
    # still answers) and atrest-nodes-stay-unreadable (neither read ever answers, so the honest
    # record is unknown, never a guessed no).
    [ordered]@{ Number = '01'; Id = '01-a2dp-oneshot'; Script = '01-A2dpOneShot.ps1'; Halves = @('first'); Extra = @()
        Cases = @('none', 'one', 'two', 'atrest-decline', 'atrest-block-ineffective',
            'atrest-setup-unknown', 'atrest-config-missing', 'atrest-nodes-probe-fails', 'atrest-nodes-stay-unreadable') }
    [ordered]@{ Number = '02'; Id = '02-disconnect'; Script = '02-Disconnect.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '03'; Id = '03-allow-pages'; Script = '03-AllowPages.ps1'; Halves = @('first'); Extra = @('WatchSeconds=30') }
    [ordered]@{ Number = '04'; Id = '04-block-and-reboot'; Script = '04-BlockAndReboot.ps1'; Halves = @('first', 'resume'); Extra = @() }
    [ordered]@{ Number = '05'; Id = '05-allow'; Script = '05-Allow.ps1'; Halves = @('first', 'resume'); Extra = @() }
    [ordered]@{ Number = '06'; Id = '06-handsfree'; Script = '06-Handsfree.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '07'; Id = '07-task-runex'; Script = '07-TaskRunEx.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '08'; Id = '08-acceptance-power-cycle'; Script = '08-AcceptancePowerCycle.ps1'; Halves = @('first', 'resume'); Extra = @() }
    # declined-start is added on top of the shared three: the owner says no to "ready to start",
    # so nothing runs and nothing is shut down, which is exactly when the at-rest reason must NOT
    # be set (09-ShutdownWhileConnected.ps1 now sets it only once $ready is true).
    [ordered]@{ Number = '09'; Id = '09-shutdown-while-connected'; Script = '09-ShutdownWhileConnected.ps1'; Halves = @('first', 'resume'); Extra = @()
        Cases = @('none', 'one', 'two', 'declined-start') }
    [ordered]@{ Number = '10'; Id = '10-shutdown-messages-v1'; Script = '10-ShutdownMessages.ps1'; Halves = @('first', 'resume'); Extra = @('Variant=1') }
    [ordered]@{ Number = '11'; Id = '11-battery-disconnected'; Script = '11-BatteryDisconnected.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '12'; Id = '12-callback-thread'; Script = '12-CallbackThread.ps1'; Halves = @('first'); Extra = @() }
    # Cases adds two fake-input sets beyond the shared none/one/two: 'grace-doubled' is a failed
    # automatic block followed by a block line reporting a doubled delay, and 'grace-unparsable'
    # is a block line whose figure this script cannot parse. Only test 13 reads either, so only
    # its row asks for them; every other row keeps the shared three.
    [ordered]@{ Number = '13'; Id = '13-grace-window'; Script = '13-GraceWindow.ps1'; Halves = @('first'); Extra = @('WatchMinutes=1')
        Cases = @('none', 'one', 'two', 'grace-doubled', 'grace-unparsable') }
    [ordered]@{ Number = '14'; Id = '14-set-device-refusal'; Script = '14-SetDeviceRefusal.ps1'; Halves = @('first'); Extra = @('SpeakerAddress=C7D8E9F0A1B2') }
    [ordered]@{ Number = '15'; Id = '15-uninstall-reversal'; Script = '15-UninstallReversal.ps1'; Halves = @('first', 'resume'); Extra = @() }
)

$defaultCases = @('none', 'one', 'two')
$cases = $defaultCases
$caseExplicit = (-not [string]::IsNullOrEmpty($Case))
if ($caseExplicit) { $cases = @($Case) }

if ([string]::IsNullOrEmpty($WorkRoot))
{
    $WorkRoot = Join-Path $env:TEMP ('earshot-live-selftest-' + [guid]::NewGuid().ToString('N'))
}

$result = [ordered]@{
    root = $Root; workRoot = $WorkRoot
    scripts = 0; halves = 0; cases = 0; runs = 0
    problems = @(); observed = @()
}

# The shipped scripts are run with Windows PowerShell 5.1 on the machine, so every case runs in
# that host. This used to fall back to the bare name when the path was not there, which handed
# Start-Process a command it could not resolve and turned a missing host into a
# CommandNotFoundException some way into the run. It now says which path it looked in.
function Get-PowerShellHost
{
    $host51 = Join-Path ([System.Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $host51 -PathType Leaf))
    {
        throw ('Windows PowerShell 5.1 is not installed at ' + $host51 + '. The self-test runs the shipped live test scripts in that host.')
    }
    return $host51
}

# Reads a result.json into the things the expectations talk about: the overall outcome, each
# criterion by id, each finding by name, the errors, and how many steps actually ran (exitCode
# read, not merely offered or declined) against each distinct command line. @(...) everywhere,
# because a run with one criterion, finding, error or step writes it as an object rather than as
# a list of one.
function Read-RunResult
{
    param([Parameter(Mandatory = $true)][string]$Path)

    $json = (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    $criteria = [ordered]@{}
    foreach ($criterion in @($json.criteria))
    {
        if ($null -eq $criterion) { continue }
        $criteria[[string]$criterion.id] = [string]$criterion.outcome
    }

    # Findings are kept as-is, $null included: Add-Finding's own convention is that $null means
    # the read did not answer, and Test-Expectations compares against that literally rather than
    # against the string "not recorded" the summary prints for a person to read.
    $findings = [ordered]@{}
    foreach ($finding in @($json.findings))
    {
        if ($null -eq $finding) { continue }
        $findings[[string]$finding.name] = $finding.value
    }

    $errors = @()
    foreach ($entry in @($json.errors))
    {
        if ($null -eq $entry) { continue }
        $errors = $errors + @([string]$entry.message)
    }

    # Only a step that actually ran counts as "sent": a declined or failed-to-start step still
    # has a row in result.json (that is what proves it was offered), but ran is $false, and a
    # command that was only offered is not the same claim as one that was sent.
    $stepCommandCounts = [ordered]@{}
    foreach ($step in @($json.steps))
    {
        if ($null -eq $step -or $step.ran -ne $true) { continue }
        $command = [string]$step.command
        if ($stepCommandCounts.Contains($command)) { $stepCommandCounts[$command] = $stepCommandCounts[$command] + 1 }
        else { $stepCommandCounts[$command] = 1 }
    }

    $summary = ''
    $folder = [string]$json.folder
    if (-not [string]::IsNullOrEmpty($folder))
    {
        $summaryPath = Join-Path $folder 'summary.txt'
        if (Test-Path -LiteralPath $summaryPath) { $summary = ('' + (Get-Content -LiteralPath $summaryPath -Raw)) }
    }

    return [ordered]@{
        Overall = [string]$json.overall; Criteria = $criteria; Findings = $findings; Errors = $errors
        StepCommandCounts = $stepCommandCounts; Summary = $summary
    }
}

# One shipped script, one half, one sandbox. Returns the exit code, the captured output and
# where the two output files went.
function Invoke-Half
{
    param(
        [Parameter(Mandatory = $true)]$TestRow,
        [Parameter(Mandatory = $true)][string]$Half,
        [Parameter(Mandatory = $true)][string]$CaseName,
        [Parameter(Mandatory = $true)][string]$SandboxRoot,
        [Parameter(Mandatory = $true)][string]$RunRoot
    )

    $extra = @($TestRow.Extra)
    if ($Half -eq 'resume') { $extra = $extra + @('Resume=true') }

    # Quoted one by one, and the extra options joined into a single value: powershell.exe -File
    # binds each word on its own, so a list passed as several words would be read as positional
    # arguments the driver does not take.
    $arguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $driver + '"'),
        '-Script', ('"' + (Join-Path $scriptFolder $TestRow.Script) + '"'),
        '-SandboxRoot', ('"' + $SandboxRoot + '"'),
        '-TestId', $TestRow.Id,
        '-Half', $Half,
        '-Case', $CaseName,
        '-RunRoot', ('"' + $RunRoot + '"'))
    if ($extra.Count -gt 0) { $arguments = $arguments + @('-ExtraArguments', ('"' + ($extra -join ';') + '"')) }

    $outFile = Join-Path $SandboxRoot ([string]$TestRow.Number + '-' + $Half + '.out.txt')
    $errFile = Join-Path $SandboxRoot ([string]$TestRow.Number + '-' + $Half + '.err.txt')

    # The child inherits these, and nothing outside the sandbox is written to. The two Earshot
    # safety switches are cleared because Assert-LiveEnvironment refuses to run with them set,
    # and nothing here can reach a device: Earshot.exe is never resolved and Start-Process
    # throws inside the child.
    $saved = @{}
    foreach ($name in @('LOCALAPPDATA', 'APPDATA', 'ProgramData', 'ProgramFiles', 'EARSHOT_SAFE_MODE', 'EARSHOT_DATA_ROOT'))
    {
        $saved[$name] = [System.Environment]::GetEnvironmentVariable($name)
    }

    try
    {
        $env:LOCALAPPDATA = Join-Path $SandboxRoot 'local'
        $env:APPDATA = Join-Path $SandboxRoot 'roaming'
        $env:ProgramData = Join-Path $SandboxRoot 'programdata'
        $env:ProgramFiles = Join-Path $SandboxRoot 'programfiles'
        $env:EARSHOT_SAFE_MODE = ''
        $env:EARSHOT_DATA_ROOT = ''
        # Start-Process rather than the call operator: a child that writes to its error stream
        # under a redirect is turned into a terminating NativeCommandError by PowerShell 5.1,
        # and this runner has to read that stream rather than stop on it.
        $process = Start-Process -FilePath (Get-PowerShellHost) -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $exit = $process.ExitCode
    }
    finally
    {
        foreach ($name in $saved.Keys)
        {
            $value = $saved[$name]
            if ($null -eq $value) { $value = '' }
            [System.Environment]::SetEnvironmentVariable($name, $value)
        }
    }

    $output = ''
    if (Test-Path -LiteralPath $outFile) { $output = ('' + (Get-Content -LiteralPath $outFile -Raw)) }
    $errors = ''
    if (Test-Path -LiteralPath $errFile) { $errors = ('' + (Get-Content -LiteralPath $errFile -Raw)) }

    return [ordered]@{ Exit = $exit; Output = $output; Errors = $errors; OutFile = $outFile; ErrFile = $errFile }
}

# The at-rest closing step's default outcome for every script and half, worked out from each
# one's own StartState (Fakes.psm1) and its own gate calls, reasoned by hand once and pinned
# here rather than repeated per case in expectations.psd1. Applied only to the shared none/one/two
# cases below: a row's own bespoke case (atrest-decline and the rest, grace-doubled, ...) has a
# different, deliberately provoked outcome and carries its own explicit expectation instead.
#
#   00-restore|first                    yes, 1   allows on its own, then the offer blocks again
#   01-a2dp-oneshot|first                yes, 1   never blocks itself; the offer is the only one
#   02-disconnect|first                  yes, 2   one block mid-test, allows again, then the offer
#   03-allow-pages|first                 yes, 1   blocks itself at the end; nothing left to offer
#   04-block-and-reboot|first            yes, 1   blocks itself; nothing left to offer
#   04-block-and-reboot|resume            yes, 0   starts and stays Blocked; nothing to offer
#   05-allow|first                 no-on-purpose, 0   the fake owner always chooses the restart
#   05-allow|resume                      yes, 1   starts Allowed (the enable held); the offer blocks
#   06-handsfree|first                   yes, 2   one block mid-test, allows again, then the offer
#   07-task-runex|first                  yes, 1   never blocks itself; the offer is the only one
#   08-acceptance-power-cycle|first      yes, 0   starts Blocked; nothing to offer
#   08-acceptance-power-cycle|resume     yes, 0   stays Blocked; nothing to offer
#   09-shutdown-while-connected|first   no-on-purpose, 0   the reason is always given once ready
#   09-shutdown-while-connected|resume    yes, 0   stays Blocked; nothing to offer
#   10-shutdown-messages-v1|first  no-on-purpose, 0   the reason is always given once ready
#   10-shutdown-messages-v1|resume        yes, 0   stays Blocked; nothing to offer
#   11-battery-disconnected|first        yes, 1   never blocks itself; the offer is the only one
#   12-callback-thread|first             yes, 1   never blocks itself; the offer is the only one
#   13-grace-window|first                yes, 0   starts and stays Blocked; nothing to offer
#   14-set-device-refusal|first          yes, 1   set-device never touches the nodes
#   15-uninstall-reversal|first          yes, 1   uninstall allows, install does not re-block
#   15-uninstall-reversal|resume          yes, 1   stays Allowed from the first half; the offer blocks
$script:AtRestDefaults = @{
    '00-restore|first'                   = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '01-a2dp-oneshot|first'              = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '02-disconnect|first'                = @{ LeftAtRest = 'yes'; BlockCount = 2 }
    '03-allow-pages|first'               = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '04-block-and-reboot|first'          = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '04-block-and-reboot|resume'         = @{ LeftAtRest = 'yes'; BlockCount = 0 }
    '05-allow|first'                     = @{ LeftAtRest = 'no-on-purpose'; BlockCount = 0 }
    '05-allow|resume'                    = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '06-handsfree|first'                 = @{ LeftAtRest = 'yes'; BlockCount = 2 }
    '07-task-runex|first'                = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '08-acceptance-power-cycle|first'    = @{ LeftAtRest = 'yes'; BlockCount = 0 }
    '08-acceptance-power-cycle|resume'   = @{ LeftAtRest = 'yes'; BlockCount = 0 }
    '09-shutdown-while-connected|first'  = @{ LeftAtRest = 'no-on-purpose'; BlockCount = 0 }
    '09-shutdown-while-connected|resume' = @{ LeftAtRest = 'yes'; BlockCount = 0 }
    '10-shutdown-messages-v1|first'      = @{ LeftAtRest = 'no-on-purpose'; BlockCount = 0 }
    '10-shutdown-messages-v1|resume'     = @{ LeftAtRest = 'yes'; BlockCount = 0 }
    '11-battery-disconnected|first'      = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '12-callback-thread|first'           = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '13-grace-window|first'              = @{ LeftAtRest = 'yes'; BlockCount = 0 }
    '14-set-device-refusal|first'        = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '15-uninstall-reversal|first'        = @{ LeftAtRest = 'yes'; BlockCount = 1 }
    '15-uninstall-reversal|resume'       = @{ LeftAtRest = 'yes'; BlockCount = 1 }
}

# What the expectations for one case say should have been recorded, against what was. The
# expectations file is keyed "<test id>|<half>", and holds one map of criterion to outcome per
# set of fake inputs. An outcome of "any" means the criterion must be recorded but its outcome
# is not this machine's to decide, which is true of the two criteria that read the real
# registry.
function Test-Expectations
{
    param(
        [Parameter(Mandatory = $true)][string]$Where,
        [Parameter(Mandatory = $true)][string]$TestId,
        [Parameter(Mandatory = $true)][string]$Half,
        [Parameter(Mandatory = $true)][string]$CaseName,
        [Parameter(Mandatory = $true)]$Recorded,
        [Parameter(Mandatory = $true)]$Expectations
    )

    $problems = @()
    $key = [string]$TestId + '|' + $Half

    # The at-rest default table, checked on every shared-input case regardless of what the
    # per-case entry below does or does not say about leftAtRest and "diag gate block": this is
    # what pins the closing step on halves whose own expectations entry never mentions it, so a
    # mutation that stops offering the block, or that trusts a step's exit code over the re-read,
    # cannot pass by simply not being named anywhere.
    if (@('none', 'one', 'two') -contains $CaseName -and $script:AtRestDefaults.Contains($key))
    {
        $wantedAtRest = $script:AtRestDefaults[$key]
        $actualAtRest = $(if ($Recorded.Findings.Contains('leftAtRest')) { $Recorded.Findings['leftAtRest'] } else { $null })
        if ([string]$actualAtRest -ne [string]$wantedAtRest.LeftAtRest)
        {
            $problems = $problems + @([string]$Where + ': leftAtRest was ' + $actualAtRest + ', and the default table for "' + $key + '" implies ' + $wantedAtRest.LeftAtRest + '.')
        }

        $actualCount = 0
        if ($Recorded.StepCommandCounts.Contains('diag gate block')) { $actualCount = [int]$Recorded.StepCommandCounts['diag gate block'] }
        if ($actualCount -ne $wantedAtRest.BlockCount)
        {
            $problems = $problems + @([string]$Where + ': "diag gate block" ran ' + $actualCount + ' time(s), and the default table for "' + $key + '" implies ' + $wantedAtRest.BlockCount + '.')
        }
    }
    if (-not $Expectations.Contains($key))
    {
        return ,@([string]$Where + ': there is no entry for "' + $key + '" in expectations.psd1.')
    }

    $entry = $Expectations[$key]
    if (-not $entry.Contains($CaseName))
    {
        return ,@([string]$Where + ': expectations.psd1 has no "' + $CaseName + '" for "' + $key + '".')
    }

    # Overall is one outcome, or the few a criterion marked "any" leaves open.
    $wanted = $entry[$CaseName]
    $allowed = @($wanted.Overall)
    if (-not ($allowed -contains $Recorded.Overall))
    {
        $problems = $problems + @([string]$Where + ': the run came out ' + $Recorded.Overall + ', and the fake inputs imply ' + ($allowed -join ' or ') + '.')
    }

    $wantedCriteria = $wanted.Criteria
    foreach ($id in $wantedCriteria.Keys)
    {
        if (-not $Recorded.Criteria.Contains($id))
        {
            $problems = $problems + @([string]$Where + ': the criterion "' + $id + '" was never recorded.')
            continue
        }

        $outcome = [string]$wantedCriteria[$id]
        if ($outcome -eq 'any') { continue }
        if ($Recorded.Criteria[$id] -ne $outcome)
        {
            $problems = $problems + @([string]$Where + ': "' + $id + '" came out ' + $Recorded.Criteria[$id] + ', and the fake inputs imply ' + $outcome + '.')
        }
    }

    foreach ($id in $Recorded.Criteria.Keys)
    {
        if ($id -eq 'run') { continue }
        if (-not $wantedCriteria.Contains($id))
        {
            $problems = $problems + @([string]$Where + ': "' + $id + '" was recorded and expectations.psd1 does not name it.')
        }
    }

    # Findings is optional; a block that omits it entirely is not checked at all, the same as
    # before. A block that declares it is exhaustive, the same as Criteria: every finding the run
    # records must be named, and every named one must be recorded with the value given. $null in
    # the expectation is a real, checked value (not measured), not "skip this one"; only the
    # string 'any' skips the value check for a finding that must still be recorded.
    if ($wanted.Contains('Findings'))
    {
        $wantedFindings = $wanted.Findings
        foreach ($name in $wantedFindings.Keys)
        {
            if (-not $Recorded.Findings.Contains($name))
            {
                $problems = $problems + @([string]$Where + ': the finding "' + $name + '" was never recorded.')
                continue
            }

            $expected = $wantedFindings[$name]
            if ($expected -is [string] -and $expected -eq 'any') { continue }

            $actual = $Recorded.Findings[$name]
            $isMatch = $false
            if ($null -eq $expected -and $null -eq $actual) { $isMatch = $true }
            elseif ($null -ne $expected -and $null -ne $actual -and [string]$expected -eq [string]$actual) { $isMatch = $true }

            if (-not $isMatch)
            {
                $shownExpected = $(if ($null -eq $expected) { 'null' } else { [string]$expected })
                $shownActual = $(if ($null -eq $actual) { 'null' } else { [string]$actual })
                $problems = $problems + @([string]$Where + ': the finding "' + $name + '" was ' + $shownActual + ', and the fake inputs imply ' + $shownExpected + '.')
            }
        }

        foreach ($name in $Recorded.Findings.Keys)
        {
            if (-not $wantedFindings.Contains($name))
            {
                $problems = $problems + @([string]$Where + ': the finding "' + $name + '" was recorded and expectations.psd1 does not name it.')
            }
        }
    }

    # FindingsInclude is optional and, unlike Findings, never exhaustive: it names one or two
    # findings worth pinning down on a script that already records others this file does not
    # want to enumerate. A finding named here that the run does not record is still a failure;
    # a finding the run records that is not named here is not checked at all.
    if ($wanted.Contains('FindingsInclude'))
    {
        $wantedFindings = $wanted.FindingsInclude
        foreach ($name in $wantedFindings.Keys)
        {
            if (-not $Recorded.Findings.Contains($name))
            {
                $problems = $problems + @([string]$Where + ': the finding "' + $name + '" was never recorded.')
                continue
            }

            $expected = $wantedFindings[$name]
            if ($expected -is [string] -and $expected -eq 'any') { continue }

            $actual = $Recorded.Findings[$name]
            $isMatch = $false
            if ($null -eq $expected -and $null -eq $actual) { $isMatch = $true }
            elseif ($null -ne $expected -and $null -ne $actual -and [string]$expected -eq [string]$actual) { $isMatch = $true }

            if (-not $isMatch)
            {
                $shownExpected = $(if ($null -eq $expected) { 'null' } else { [string]$expected })
                $shownActual = $(if ($null -eq $actual) { 'null' } else { [string]$actual })
                $problems = $problems + @([string]$Where + ': the finding "' + $name + '" was ' + $shownActual + ', and the fake inputs imply ' + $shownExpected + '.')
            }
        }
    }

    # Steps is optional and, unlike Criteria and Findings, not exhaustive: only the command lines
    # worth pinning a count against are named. It exists for the at-rest closing step, to prove
    # "exactly one diag gate block was sent" and "no diag gate block was sent" by counting steps
    # that actually ran (see Read-RunResult), not merely offered ones.
    if ($wanted.Contains('Steps'))
    {
        $wantedSteps = $wanted.Steps
        foreach ($command in $wantedSteps.Keys)
        {
            $expectedCount = [int]$wantedSteps[$command]
            $actualCount = 0
            if ($Recorded.StepCommandCounts.Contains($command)) { $actualCount = [int]$Recorded.StepCommandCounts[$command] }
            if ($actualCount -ne $expectedCount)
            {
                $problems = $problems + @([string]$Where + ': "' + $command + '" ran ' + $actualCount +
                    ' time(s), and the fake inputs imply ' + $expectedCount + '.')
            }
        }
    }

    # SummaryContains is optional too: a short list of phrases summary.txt must hold, for example
    # the words of the at-rest warning block, so that block is proven to reach the file a person
    # would actually read, not only result.json.
    if ($wanted.Contains('SummaryContains'))
    {
        foreach ($phrase in @($wanted.SummaryContains))
        {
            if (-not $Recorded.Summary.Contains($phrase))
            {
                $problems = $problems + @([string]$Where + ': summary.txt does not hold the expected text "' + $phrase + '".')
            }
        }
    }

    return ,$problems
}

# Signatures of a run that threw. The scripts catch their own throws, so the first three are
# what an abandoned run leaves in the summary; the last two are what escapes to the error stream.
$terminatingSignatures = @(
    'PropertyNotFoundStrict'
    'The property ''Count'' cannot be found'
    'You cannot call a method on a null-valued expression'
    'Cannot index into a null array'
    'The variable ''$'
)

$expectations = $null
if (-not $Observed)
{
    if (-not (Test-Path -LiteralPath $expectationsPath -PathType Leaf))
    {
        $result.problems += ('There is no expectations file at ' + $expectationsPath + '.')
        $result.ok = $false
        $result | ConvertTo-Json -Depth 6
        exit 1
    }

    $expectations = Import-PowerShellDataFile -LiteralPath $expectationsPath
}

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
$seen = @{}

foreach ($row in $tests)
{
    if (-not [string]::IsNullOrEmpty($Test) -and $Test -ne $row.Number) { continue }
    if (-not (Test-Path -LiteralPath (Join-Path $scriptFolder $row.Script) -PathType Leaf))
    {
        $result.problems += ('The self-test names a script that is not there: ' + $row.Script)
        continue
    }

    $result.scripts = $result.scripts + 1
    $result.halves = $result.halves + @($row.Halves).Count
    $seen[$row.Script] = $true

    # A row's own Cases replaces the shared none/one/two only for the default sweep. An explicit
    # -Case on the command line always wins, the same as before, so "-Test 13 -Case one" still
    # runs exactly that one case.
    $rowCases = $cases
    if (-not $caseExplicit -and $row.Contains('Cases')) { $rowCases = @($row.Cases) }

    foreach ($caseName in $rowCases)
    {
        $result.cases = $result.cases + 1
        $sandbox = Join-Path $WorkRoot ([string]$row.Number + '-' + $caseName)
        New-FakeSandbox -SandboxRoot $sandbox -Case $caseName
        $runRoot = Join-Path $sandbox 'local\Earshot\livetest\run'
        $caseProblems = 0

        foreach ($half in @($row.Halves))
        {
            $result.runs = $result.runs + 1
            $run = Invoke-Half -TestRow $row -Half $half -CaseName $caseName -SandboxRoot $sandbox -RunRoot $runRoot
            $where = [string]$row.Script + ' ' + $half + ' ' + $caseName
            $problems = @()

            if (-not [string]::IsNullOrWhiteSpace($run.Errors))
            {
                $problems = $problems + @([string]$where + ': something was written to the error stream: ' + $run.Errors.Trim())
            }

            foreach ($signature in $terminatingSignatures)
            {
                if ($run.Output.Contains($signature))
                {
                    $problems = $problems + @([string]$where + ': the run threw (' + $signature + '). See ' + $run.OutFile)
                }
            }

            if ($run.Exit -lt 0 -or $run.Exit -gt 2)
            {
                $problems = $problems + @([string]$where + ': the run exited ' + $run.Exit + ', which is not 0, 1 or 2.')
            }

            $gapFile = Join-Path $sandbox 'unanswered.txt'
            if (Test-Path -LiteralPath $gapFile)
            {
                $problems = $problems + @([string]$where + ': the fakes did not cover ' + (('' + (Get-Content -LiteralPath $gapFile -Raw)).Trim()))
                Remove-Item -LiteralPath $gapFile -Force
            }

            $resultPath = Join-Path (Join-Path $runRoot $row.Id) 'result.json'
            if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf))
            {
                $problems = $problems + @([string]$where + ': no result.json was written at ' + $resultPath)
                $result.problems += $problems
                $caseProblems = $caseProblems + $problems.Count
                continue
            }

            $recorded = Read-RunResult -Path $resultPath
            if ($recorded.Criteria.Contains('run'))
            {
                $problems = $problems + @([string]$where + ': the script recorded its own catch-all "run" criterion, so it stopped early.')
            }

            # Ordinarily a run that recorded any error is a problem: the fakes never fail a probe,
            # so nothing should throw. atrest-read-fails is the one case built to force exactly
            # that, so it declares how many errors it expects (ExpectedErrors) and is checked
            # against that instead of the blanket zero.
            $expectedErrorCount = 0
            $expectationKey = [string]$row.Id + '|' + $half
            if (-not $Observed -and $null -ne $expectations -and $expectations.Contains($expectationKey) -and
                $expectations[$expectationKey].Contains($caseName) -and $expectations[$expectationKey][$caseName].Contains('ExpectedErrors'))
            {
                $expectedErrorCount = [int]$expectations[$expectationKey][$caseName]['ExpectedErrors']
            }

            if ($recorded.Errors.Count -ne $expectedErrorCount)
            {
                $problems = $problems + @([string]$where + ': the run recorded ' + $recorded.Errors.Count +
                    ' error(s) (expected ' + $expectedErrorCount + '): ' + ($recorded.Errors -join ' | '))
            }

            if ($Observed)
            {
                $shown = @()
                foreach ($id in $recorded.Criteria.Keys) { $shown = $shown + @([string]$id + '=' + $recorded.Criteria[$id]) }
                $result.observed += ([ordered]@{ where = $where; overall = $recorded.Overall; criteria = ($shown -join ', ') })
            }
            else
            {
                # Assigned first, then wrapped: Test-Expectations returns its list with a leading
                # comma, and @() round the call would add the whole list as one item, which reads
                # as one problem even when there are none.
                $found = Test-Expectations -Where $where -TestId $row.Id -Half $half -CaseName $caseName -Recorded $recorded -Expectations $expectations
                $problems = $problems + @($found)
            }

            $result.problems += $problems
            $caseProblems = $caseProblems + $problems.Count
        }

        # The evidence of a case that passed is not worth keeping.
        if (-not $Keep -and -not $Observed -and $caseProblems -eq 0)
        {
            Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# A shipped script with no row in $tests would never be run, which is the way this self-test
# could quietly stop covering something.
if ([string]::IsNullOrEmpty($Test))
{
    foreach ($file in (Get-ChildItem -LiteralPath $scriptFolder -Filter '*.ps1' -File | Sort-Object Name))
    {
        if ($file.Name -eq 'Run-LiveTests.ps1') { continue }
        if (-not $seen.ContainsKey($file.Name))
        {
            $result.problems += ('The self-test does not run ' + $file.Name + '. Add a row to $tests in Invoke-SelfTest.ps1.')
        }
    }
}

$result.ok = ($result.problems.Count -eq 0)
$result | ConvertTo-Json -Depth 6
if ($result.ok) { exit 0 } else { exit 1 }
