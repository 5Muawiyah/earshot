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
    Run one set of fake inputs rather than all three.

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
    [ValidateSet('', 'none', 'one', 'two')][string]$Case = '',
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
    [ordered]@{ Number = '00'; Id = '00-restore'; Script = '00-Restore.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '01'; Id = '01-a2dp-oneshot'; Script = '01-A2dpOneShot.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '02'; Id = '02-disconnect'; Script = '02-Disconnect.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '03'; Id = '03-allow-pages'; Script = '03-AllowPages.ps1'; Halves = @('first'); Extra = @('WatchSeconds=30') }
    [ordered]@{ Number = '04'; Id = '04-block-and-reboot'; Script = '04-BlockAndReboot.ps1'; Halves = @('first', 'resume'); Extra = @() }
    [ordered]@{ Number = '05'; Id = '05-allow'; Script = '05-Allow.ps1'; Halves = @('first', 'resume'); Extra = @() }
    [ordered]@{ Number = '06'; Id = '06-handsfree'; Script = '06-Handsfree.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '07'; Id = '07-task-runex'; Script = '07-TaskRunEx.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '08'; Id = '08-acceptance-power-cycle'; Script = '08-AcceptancePowerCycle.ps1'; Halves = @('first', 'resume'); Extra = @() }
    [ordered]@{ Number = '09'; Id = '09-shutdown-while-connected'; Script = '09-ShutdownWhileConnected.ps1'; Halves = @('first', 'resume'); Extra = @() }
    [ordered]@{ Number = '10'; Id = '10-shutdown-messages-v1'; Script = '10-ShutdownMessages.ps1'; Halves = @('first', 'resume'); Extra = @('Variant=1') }
    [ordered]@{ Number = '11'; Id = '11-battery-disconnected'; Script = '11-BatteryDisconnected.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '12'; Id = '12-callback-thread'; Script = '12-CallbackThread.ps1'; Halves = @('first'); Extra = @() }
    [ordered]@{ Number = '13'; Id = '13-grace-window'; Script = '13-GraceWindow.ps1'; Halves = @('first'); Extra = @('WatchMinutes=1') }
    [ordered]@{ Number = '14'; Id = '14-set-device-refusal'; Script = '14-SetDeviceRefusal.ps1'; Halves = @('first'); Extra = @('SpeakerAddress=C7D8E9F0A1B2') }
    [ordered]@{ Number = '15'; Id = '15-uninstall-reversal'; Script = '15-UninstallReversal.ps1'; Halves = @('first', 'resume'); Extra = @() }
)

$cases = @('none', 'one', 'two')
if (-not [string]::IsNullOrEmpty($Case)) { $cases = @($Case) }

if ([string]::IsNullOrEmpty($WorkRoot))
{
    $WorkRoot = Join-Path $env:TEMP ('earshot-live-selftest-' + [guid]::NewGuid().ToString('N'))
}

$result = [ordered]@{
    root = $Root; workRoot = $WorkRoot
    scripts = 0; halves = 0; cases = 0; runs = 0
    problems = @(); observed = @()
}

function Get-PowerShellHost
{
    $host51 = Join-Path ([System.Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $host51 -PathType Leaf) { return $host51 }
    return 'powershell.exe'
}

# Reads a result.json into the two things the expectations talk about: the overall outcome and
# each criterion by id. @(...) everywhere, because a run with one criterion writes it as an
# object rather than as a list of one.
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

    $errors = @()
    foreach ($entry in @($json.errors))
    {
        if ($null -eq $entry) { continue }
        $errors = $errors + @([string]$entry.message)
    }

    return [ordered]@{ Overall = [string]$json.overall; Criteria = $criteria; Errors = $errors }
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
    foreach ($caseName in $cases)
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

            if ($recorded.Errors.Count -gt 0)
            {
                $problems = $problems + @([string]$where + ': the run recorded ' + $recorded.Errors.Count + ' error(s): ' + ($recorded.Errors -join ' | '))
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
