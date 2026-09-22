<#
.SYNOPSIS
    Runs one shipped live test script against the fake machine, in this process.

.DESCRIPTION
    The runner starts one powershell.exe 5.1 per half and points it at this script. It is the
    only place the stubs are installed, and it installs them in two places, because a shipped
    script and the shared module resolve a command name differently:

      * in the global scope, which is what a test script's own calls find, and
      * inside LiveTest.psm1's session state, which is what the module's own helpers find
        when, for example, Get-NodeState calls Invoke-Earshot.

    Both are done the moment the test script imports the module, through a function named
    Import-Module that shadows the cmdlet. Nothing in the shipped scripts is edited to make
    this work: they are run exactly as they ship.

    What is replaced is only the device-touching and owner-prompting part: Invoke-Earshot,
    Invoke-EarshotElevated, Confirm-Step, Read-Answer, Read-Note, Wait-Owner, Wait-Seconds and
    Resolve-EarshotExe, plus Read-Host as a backstop for anything that asks directly and
    Start-Process as a backstop that throws. Everything else, including Get-EarshotLogLines,
    Get-DiagEvidence, Copy-AppEvidence, Read-KsEvidence, Get-TargetEndpointStates,
    Read-EarshotJsonFile, Add-Criterion and Complete-LiveTestRun, runs for real.

.PARAMETER Script
    The shipped script to run, by full path.

.PARAMETER SandboxRoot
    The sandbox the runner built for this case.

.PARAMETER TestId
    The TestId the script passes to New-LiveTestRun, which is how the fakes know which machine
    to present.

.PARAMETER Half
    first or resume.

.PARAMETER Case
    none, one or two: how many matching lines and list items the fake inputs hold. grace-doubled
    and grace-unparsable exist only for test 13. The rest exercise the at-rest closing step by
    intercepting its own labels ('at-rest-task', 'at-rest-nodes', 'at-rest-nodes-after',
    'at-rest-block'), never a label a shipped script uses itself, so none of them can change what
    any other criterion sees: atrest-decline (the offer declined), atrest-block-ineffective (the
    step answers success but the world does not move, so the re-read is what must catch it),
    atrest-setup-unknown and atrest-config-missing (the setup or Block at boot read fails or the
    file is missing), atrest-nodes-probe-fails and atrest-nodes-stay-unreadable (the node read
    fails once, or fails both times), atrest-guard-throws (something throws outside all of that,
    proving Close-AtRest's and Complete-LiveTestRun's own guards rather than any of the above).
    atrest-render-active (row 00 only) models the third Restore run of 2026-09-21: the allow pages
    the AirPods, so the closing step's own disconnect-first order is what keeps the block from
    being vetoed. atrest-disconnect-declined and atrest-disconnect-not-confirmed intercept
    'at-rest-disconnect' the same way atrest-decline intercepts 'at-rest-block'; atrest-audio-
    unreadable intercepts 'at-rest-audio' the way the other atrest-nodes-* cases intercept
    'at-rest-nodes', proving the disconnect is still offered when render cannot be read at all
    (fail closed). declined-start answers 'n' to the "ready to start" prompt, for the one test
    that needs to prove the at-rest reason is not set before the owner has actually agreed to go
    ahead.

.PARAMETER RunRoot
    The evidence folder, shared by the two halves of a resumable test.

.PARAMETER ExtraArguments
    Anything else the script takes, as name=value pairs joined with a semicolon.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Script,
    [Parameter(Mandatory = $true)][string]$SandboxRoot,
    [Parameter(Mandatory = $true)][string]$TestId,
    [Parameter(Mandatory = $true)][ValidateSet('first', 'resume')][string]$Half,
    [Parameter(Mandatory = $true)][ValidateSet(
        'none', 'one', 'two', 'grace-doubled', 'grace-unparsable',
        'atrest-decline', 'atrest-guard-throws', 'atrest-block-ineffective',
        'atrest-setup-unknown', 'atrest-config-missing', 'atrest-nodes-probe-fails', 'atrest-nodes-stay-unreadable',
        'atrest-render-active', 'atrest-disconnect-declined', 'atrest-disconnect-not-confirmed', 'atrest-audio-unreadable',
        'declined-start', 'handback-cut-short', 'handback-not-reached', 'no-sleep-event', 'repaged-at-wake')][string]$Case,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [string]$ExtraArguments = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Microsoft.PowerShell.Core\Import-Module (Join-Path $PSScriptRoot 'Fakes.psm1') -Force
Initialize-FakeMachine -SandboxRoot $SandboxRoot -TestId $TestId -Half $Half -Case $Case
$fake = Get-FakeContext

# The stub bodies, as text, so the same definitions can be put in two session states, with the
# scope prefix added when they are installed.
#
# Everything the stubs and the driver need is defined in the global scope on purpose. A plain
# script gets its own scope under the global one, and neither a module's session state nor a
# function defined in the global scope can see into it, so a helper left at script scope would
# be found by the test script and not by LiveTest.psm1.
$global:EarshotStubText = @'
# Earshot.exe is never resolved and never started. The path is one inside the sandbox, so a
# script that prints it or compares it against the program folder gets a real looking answer.
function Resolve-EarshotExe
{
    param([Parameter(Mandatory = $true)][string]$ExePath, [switch]$RequireRelease)

    return (Get-FakeContext).ExePath
}

# The whole of the device side. It records the step the way the real one does, moves the fake
# machine, writes the report to the --out path the command named and the evidence file beside
# the run folders, then lets the real Copy-AppEvidence pick that file up.
#
# A handful of labels are intercepted ahead of the normal fake, every one of them belonging to
# Close-AtRest in LiveTest.psm1 alone (no shipped script uses any of them), so none of these
# cases can change what any other criterion sees:
#   'at-rest-task', 'at-rest-nodes'   case atrest-setup-unknown / atrest-nodes-probe-fails /
#                                      atrest-nodes-stay-unreadable: answered as a failed probe,
#                                      the real shape a probe fails in (an error recorded, $null
#                                      returned), never a thrown exception.
#   'at-rest-nodes-after'             case atrest-nodes-stay-unreadable only: the same failed
#                                      shape, so neither read this run makes ever answers.
#   'at-rest-nodes'                   case atrest-guard-throws: actually throws, for the one case
#                                      that exists to prove Close-AtRest's own catch and
#                                      Complete-LiveTestRun's wrapping one, not the null handling.
#   'at-rest-block'                   case atrest-decline: answered as a declined live step, the
#                                      same shape Invoke-Earshot itself writes when Confirm-Step
#                                      returns false, without moving the fake machine.
#                                      case atrest-block-ineffective: answered as a plain success,
#                                      but Update-FakeWorld (Fakes.psm1) leaves the world where it
#                                      was, so only the closing check's own re-read can catch it.
#   'at-rest-audio'                   case atrest-audio-unreadable: answered as a failed probe,
#                                      the same shape as the at-rest-nodes cases above; the
#                                      after-read is not intercepted, so the disconnect step still
#                                      confirms normally once it has run.
#   'at-rest-disconnect'              case atrest-disconnect-declined: answered as a declined live
#                                      step, the same shape as atrest-decline above, without moving
#                                      the fake machine.
function Invoke-Earshot
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command,
        [switch]$Live,
        [string]$Consequence = '',
        [int]$TimeoutSeconds = 240
    )

    $case = (Get-FakeContext).Case

    if ($Label -eq 'at-rest-nodes' -and $case -eq 'atrest-guard-throws')
    {
        throw 'self-test (atrest-guard-throws): something failed outside Close-AtRest''s own null handling, on purpose.'
    }

    if ($Label -eq 'at-rest-task' -and $case -eq 'atrest-setup-unknown')
    {
        return (Invoke-FakeFailedProbe -Run $Run -Label $Label -Command $Command)
    }

    if ($Label -eq 'at-rest-nodes' -and ($case -eq 'atrest-nodes-probe-fails' -or $case -eq 'atrest-nodes-stay-unreadable'))
    {
        return (Invoke-FakeFailedProbe -Run $Run -Label $Label -Command $Command)
    }

    if ($Label -eq 'at-rest-nodes-after' -and $case -eq 'atrest-nodes-stay-unreadable')
    {
        return (Invoke-FakeFailedProbe -Run $Run -Label $Label -Command $Command)
    }

    if ($Label -eq 'at-rest-block' -and $case -eq 'atrest-decline')
    {
        return (Invoke-FakeDeclinedStep -Run $Run -Label $Label -Command $Command -Consequence $Consequence)
    }

    # Both belong to Close-AtRest's own disconnect-first step alone (no shipped script uses
    # either label), so neither changes what any other criterion sees.
    if ($Label -eq 'at-rest-audio' -and $case -eq 'atrest-audio-unreadable')
    {
        return (Invoke-FakeFailedProbe -Run $Run -Label $Label -Command $Command)
    }

    if ($Label -eq 'at-rest-disconnect' -and $case -eq 'atrest-disconnect-declined')
    {
        return (Invoke-FakeDeclinedStep -Run $Run -Label $Label -Command $Command -Consequence $Consequence)
    }

    return (Invoke-FakeEarshot -Run $Run -Label $Label -Command $Command -Live:$Live -Consequence $Consequence -Elevated:$false)
}

function Invoke-EarshotElevated
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command,
        [Parameter(Mandatory = $true)][string]$Consequence,
        [int]$TimeoutSeconds = 600
    )

    return (Invoke-FakeEarshot -Run $Run -Label $Label -Command $Command -Live -Consequence $Consequence -Elevated)
}

# Every live step is approved, so every step a sitting would run is run.
function Confirm-Step
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$Consequence = ''
    )

    Write-Line -Run $Run -Text ('LIVE STEP (self-test, approved): ' + $Prompt)
    return $true
}

function Read-Answer
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Question,
        [string[]]$Options = @('yes', 'no', 'unsure')
    )

    $answer = Get-FakeAnswer -Question $Question -Options $Options
    Write-Line -Run $Run -Text ('QUESTION: ' + $Question)
    Write-Line -Run $Run -Text ('  Answer: ' + $answer)
    [void]$Run.Answers.Add([ordered]@{ question = $Question; answer = $answer; utc = (Get-UtcNowText) })
    return $answer
}

function Read-Note
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Question
    )

    $answer = Get-FakeNote -Question $Question
    Write-Line -Run $Run -Text ('NOTE: ' + $Question)
    Write-Line -Run $Run -Text ('  Answer: ' + $answer)
    [void]$Run.Answers.Add([ordered]@{ question = $Question; answer = $answer; utc = (Get-UtcNowText) })
    return $answer
}

# The owner does the physical thing, so the fake machine moves with it.
function Wait-Owner
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Text
    )

    Write-Line -Run $Run -Text ('DO THIS (self-test, done): ' + $Text)
    Update-FakeWorldForOwnerAction -Text $Text
}

function Wait-Seconds
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][int]$Seconds,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    Write-Line -Run $Run -Text ('Waiting ' + $Seconds + ' s (self-test, not waited): ' + $Reason)
}

# A backstop. Nothing in a self-test run may block on input, so anything that still asks is
# recorded as a gap and answered with y, which is what Show-Preconditions wants, except for
# declined-start: that case exists to prove the owner saying no to "ready to start" leaves the
# at-rest reason unset (09-ShutdownWhileConnected.ps1 and 10-ShutdownMessages.ps1 only set it
# after this answer), so it answers n instead.
function Read-Host
{
    param([Parameter(Position = 0)][string]$Prompt = '')

    if ($Prompt -notmatch 'ready to start')
    {
        Write-FakeGap -Text ('Read-Host was reached directly: ' + $Prompt)
        return 'y'
    }

    if ((Get-FakeContext).Case -eq 'declined-start') { return 'n' }
    return 'y'
}

# A backstop. Nothing in a self-test run starts a process, so a helper that grew a new way of
# starting one stops the self-test rather than running it.
function Start-Process
{
    throw ('The self-test never starts a process. Something reached Start-Process: ' + ($args -join ' '))
}

# The System event log side, for tests 17 and 18: never a real Get-WinEvent call, the fake table
# in Fakes.psm1 instead (Get-FakePowerEvents), case-aware the same way every other fake answer is.
function Get-PowerEvents
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][datetime]$SinceUtc
    )

    return (Get-FakePowerEvents)
}
'@

# The fake side of Invoke-Earshot, kept here rather than in the stub text so it is written once.
# It is called from both copies of the stub, so it has to be global.
function global:Invoke-FakeEarshot
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command,
        [switch]$Live,
        [string]$Consequence = '',
        [switch]$Elevated
    )

    if ($Live -and [string]::IsNullOrEmpty($Consequence))
    {
        throw 'A live step must say what it does before it asks.'
    }

    $answer = Get-FakeCommandAnswer -Command $Command
    $exitCode = 0
    if ($null -ne $answer.Evidence -and ($answer.Evidence -is [System.Collections.IDictionary]) -and $answer.Evidence.Contains('statusFile'))
    {
        $exitCode = [int]$answer.Evidence['statusFile']['exitCode']
    }

    $Run.StepIndex = $Run.StepIndex + 1
    $step = [ordered]@{
        index        = $Run.StepIndex
        label        = $Label
        command      = ($Command -join ' ')
        commandText  = (Get-CommandText -ExePath $Run.ExePath -Command $Command)
        live         = [bool]$Live
        elevated     = [bool]$Elevated
        startedUtc   = (Get-UtcNowText)
        finishedUtc  = (Get-UtcNowText)
        ran          = $true
        exitCode     = $exitCode
        exitName     = (Get-GateExitName -ExitCode $exitCode)
        milliseconds = 120
        timedOut     = $false
        error        = $null
        stdoutFile   = $null
        stderrFile   = $null
        jsonFile     = $null
    }

    Write-Line -Run $Run -Text ('Running (self-test, nothing real): ' + $step.commandText)

    # The report goes to the --out path the command named, and is read back from there, so the
    # shipped Get-OutPath and the JSON parse both run.
    $parsed = $null
    $outPath = Get-OutPath -Command $Command
    if ($null -ne $outPath -and $outPath.EndsWith('.json') -and $null -ne $answer.Report)
    {
        $step.jsonFile = $outPath
        $folder = [System.IO.Path]::GetDirectoryName($outPath)
        if (-not [string]::IsNullOrEmpty($folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
        Set-Content -LiteralPath $outPath -Value ($answer.Report | ConvertTo-Json -Depth 8) -Encoding UTF8
        $parsed = (Get-Content -LiteralPath $outPath -Raw | ConvertFrom-Json)
    }

    # The evidence file goes where Earshot writes its own, so the shipped Copy-AppEvidence and
    # Get-DiagEvidence find it exactly as they would on the real machine. The set of files that
    # were there first is built here rather than through LiveTest.psm1's own Get-AppEvidenceNames,
    # which the module does not export.
    $before = @{}
    if (Test-Path -LiteralPath $Run.AppLiveTest)
    {
        foreach ($existing in (Get-ChildItem -LiteralPath $Run.AppLiveTest -Filter '*.json' -File)) { $before[$existing.Name] = $true }
    }

    if ($null -ne $answer.Evidence)
    {
        New-Item -ItemType Directory -Force -Path $Run.AppLiveTest | Out-Null
        $name = Get-FakeEvidenceName -Label $Label
        Set-Content -LiteralPath (Join-Path $Run.AppLiveTest $name) `
            -Value ($answer.Evidence | ConvertTo-Json -Depth 8) -Encoding UTF8
    }

    Update-FakeWorld -Command $Command -Evidence $answer.Evidence
    Copy-AppEvidence -Run $Run -Before $before
    [void]$Run.Steps.Add($step)

    $returned = [pscustomobject]$step
    Add-Member -InputObject $returned -MemberType NoteProperty -Name 'json' -Value $parsed
    return $returned
}

# The fake answer for case atrest-decline's one intercepted step: exactly what the real
# Invoke-Earshot records when Confirm-Step returns false, without moving the fake machine, so
# Close-AtRest's re-read afterwards still finds the nodes wherever they already were.
function global:Invoke-FakeDeclinedStep
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command,
        [Parameter(Mandatory = $true)][string]$Consequence
    )

    $Run.StepIndex = $Run.StepIndex + 1
    $step = [ordered]@{
        index        = $Run.StepIndex
        label        = $Label
        command      = ($Command -join ' ')
        commandText  = (Get-CommandText -ExePath $Run.ExePath -Command $Command)
        live         = $true
        elevated     = $false
        startedUtc   = (Get-UtcNowText)
        finishedUtc  = $null
        ran          = $false
        exitCode     = $null
        exitName     = $null
        milliseconds = $null
        timedOut     = $false
        error        = 'skipped at the owner request'
        stdoutFile   = $null
        stderrFile   = $null
        jsonFile     = $null
    }

    Write-Line -Run $Run -Text ('LIVE STEP (self-test, declined): ' + $step.commandText)
    Write-Line -Run $Run -Text ('  What it does: ' + $Consequence)
    Write-Line -Run $Run -Text '  Skipped at your request (self-test, case atrest-decline).'
    [void]$Run.Steps.Add($step)
    return $null
}

# The fake answer for every atrest-*-fails/unknown case's intercepted labels: the real shape a
# probe fails in. The real Invoke-Earshot never throws on a failed probe; Start-Process failing
# to start, a timeout and an unreadable exit code all end the same way, with an error recorded on
# the step and $null returned to the caller. This is that shape, not an exception, so it exercises
# the null handling Close-AtRest actually has to do rather than only its outer catch.
function global:Invoke-FakeFailedProbe
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command
    )

    $Run.StepIndex = $Run.StepIndex + 1
    $message = 'self-test: Earshot.exe could not be started (case ' + (Get-FakeContext).Case + ', forced on purpose).'
    $step = [ordered]@{
        index        = $Run.StepIndex
        label        = $Label
        command      = ($Command -join ' ')
        commandText  = (Get-CommandText -ExePath $Run.ExePath -Command $Command)
        live         = $false
        elevated     = $false
        startedUtc   = (Get-UtcNowText)
        finishedUtc  = $null
        ran          = $false
        exitCode     = $null
        exitName     = $null
        milliseconds = $null
        timedOut     = $false
        error        = $message
        stdoutFile   = $null
        stderrFile   = $null
        jsonFile     = $null
    }

    Write-Line -Run $Run -Text ('Reading (changes nothing, self-test): ' + $step.commandText)
    [void]$Run.Steps.Add($step)
    # Write-Failure, not a plain Write-Line: the real Invoke-Earshot records every one of its own
    # failure paths this way, which is what puts it in result.json's errors, not only in the
    # printed summary.
    Write-Failure -Run $Run -Message $message
    return $null
}

# Puts the stubs in the global scope, where a test script's own calls find them, and inside
# LiveTest.psm1, where the module's own helpers find them.
function global:Install-EarshotStubs
{
    . ([scriptblock]::Create(($global:EarshotStubText -replace '(?m)^function ', 'function global:')))
    $module = Get-Module 'LiveTest'
    if ($null -eq $module) { throw 'LiveTest.psm1 was imported but Get-Module did not find it.' }
    & $module ([scriptblock]::Create(($global:EarshotStubText -replace '(?m)^function ', 'function script:')))
}

# A function shadows a cmdlet of the same name, so this is what the shipped script's own
# "Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force" reaches. The real cmdlet is
# called by its fully qualified name, then the stubs go on top.
function global:Import-Module
{
    Microsoft.PowerShell.Core\Import-Module @args
    Install-EarshotStubs
}

$forward = @{ ExePath = $fake.ExePath; RunRoot = $RunRoot }
foreach ($pair in @($ExtraArguments -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }))
{
    $split = $pair.IndexOf('=')
    if ($split -lt 1) { throw ('An extra argument must be name=value, not: ' + $pair) }
    $name = $pair.Substring(0, $split)
    $value = $pair.Substring($split + 1)
    if ($value -eq 'true') { $forward[$name] = [switch]$true }
    elseif ($value -match '^[0-9]+$') { $forward[$name] = [int]$value }
    else { $forward[$name] = $value }
}

$global:LASTEXITCODE = 0
& $Script @forward
exit $global:LASTEXITCODE
