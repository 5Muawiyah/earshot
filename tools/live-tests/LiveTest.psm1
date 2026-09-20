<#
.SYNOPSIS
    Shared helpers for the Earshot live device tests.

.DESCRIPTION
    The live tests run against real AirPods on the owner's machine. Nothing here
    changes a device by itself: every step that does is announced, with what it
    will do, and is only run after the owner types y.

    Evidence for one run goes to
        %LOCALAPPDATA%\Earshot\livetest\<UTC timestamp>\<test>\
    holding summary.txt (plain reading), result.json (the criteria and their
    outcomes), one pair of captured output files per command, and copies of the
    JSON evidence Earshot itself wrote for each diag run.

    Errors are never hidden. A command that fails, times out or throws is written
    to the summary, kept in result.json and turns its criterion into a fail or an
    inconclusive, whichever the caller asked for.
#>

#Requires -Version 5.1

Set-StrictMode -Version 2.0

$script:PassOutcome = 'pass'
$script:FailOutcome = 'fail'
$script:UnclearOutcome = 'inconclusive'

# ---------------------------------------------------------------- small helpers

# WHY EVERY HELPER BELOW THAT ANSWERS WITH A LIST RETURNS ", $value" RATHER THAN "$value"
#
# PowerShell unrolls whatever a function writes to the pipeline, so a function ending
# "return $found" hands the caller $null when $found is empty and the bare element when it
# holds exactly one. Under Set-StrictMode -Version 2.0, which every one of these scripts
# runs under, both of those then throw PropertyNotFoundStrict on a later .Count: $null has
# no Count, and neither does a bare [string]. The throw lands in the script's own catch,
# which records "run: fail" and abandons every criterion after it, and it fires hardest on
# the success path, where the answer is "no matching line".
#
# ", $value" returns a one element array holding the value. The pipeline unrolls that one
# layer and the caller gets $value back exactly as it was, list or not.
#
# The second half of the rule, for callers: assign the call plainly, and put @() around the
# VARIABLE wherever .Count, .Length or an index is read from it.
#
#     $lines = Get-EarshotLogLines -Run $run -Pattern '...'      # plain
#     if (@($lines).Count -gt 0) { ... }                         # @() around the variable
#
# Not around the call. @(Get-EarshotLogLines ...) wraps the whole list in another list, because
# the comma has already made it one object by the time @() collects it, so .Count would read 1
# for every answer including none. @($lines) is a no-op when the comma is there and repairs the
# value when somebody takes it away, which is what makes the two halves belt and braces rather
# than one contradicting the other. tools\live-tests\selftest proves both halves by running the
# scripts, and LiveTestScriptTests holds the rule to the source.
# https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_return
# https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_strict_mode
# https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_arrays

# A property of a parsed JSON object, or $null when the object or the property is
# not there. Strict mode makes a plain $object.name throw for a missing member, and
# probe output leaves members out, so every read goes through here.
function Get-Field
{
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary])
    {
        if ($Object.Contains($Name)) { return ,$Object[$Name] }
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return ,$property.Value
}

# Get-Field along a path, for example Get-FieldPath $json @('device', 'connection').
function Get-FieldPath
{
    param(
        $Object,
        [Parameter(Mandatory = $true)][string[]]$Path
    )

    $current = $Object
    foreach ($name in $Path)
    {
        $current = Get-Field -Object $current -Name $name
        if ($null -eq $current) { return $null }
    }

    return ,$current
}

function Join-Parts
{
    param([Parameter(Mandatory = $true)][string[]]$Parts)

    $path = $Parts[0]
    for ($i = 1; $i -lt $Parts.Count; $i++)
    {
        $path = Join-Path $path $Parts[$i]
    }

    return $path
}

function Get-UtcStamp
{
    return (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
}

function Get-UtcNowText
{
    return (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
}

# ------------------------------------------------------------------ environment

# The live tests act on the real machine, so the two build-time safety switches must
# be off. With either set, Earshot refuses gate, gate-protect, install and uninstall
# outright, and in safe mode it refuses every diag target as well, which would look
# like a device failure. EARSHOT_DATA_ROOT alone does not stop a diag target: it would
# run the real device action and write its evidence to the redirected folder, so this
# check, not the application, is what stops the run.
function Assert-LiveEnvironment
{
    $set = @()
    foreach ($name in @('EARSHOT_SAFE_MODE', 'EARSHOT_DATA_ROOT'))
    {
        $value = [System.Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrEmpty($value)) { $set += ([string]$name + '=' + $value) }
    }

    if ($set.Count -gt 0)
    {
        throw ("These variables are set in this session, and Earshot refuses its live modes while they are: " +
            ($set -join ', ') + ". Open a new window without them and run the test again.")
    }
}

function Get-EarshotDataPaths
{
    $local = $env:LOCALAPPDATA
    if ([string]::IsNullOrEmpty($local)) { throw 'LOCALAPPDATA is not set, so the evidence folder cannot be found.' }

    return [ordered]@{
        LocalFolder    = (Join-Parts @($local, 'Earshot'))
        LiveTestFolder = (Join-Parts @($local, 'Earshot', 'livetest'))
        LogFolder      = (Join-Parts @($local, 'Earshot', 'logs'))
        SettingsFile   = (Join-Parts @($env:APPDATA, 'Earshot', 'settings.json'))
        MachineFolder  = (Join-Parts @($env:ProgramData, 'Earshot'))
    }
}

# The full path of the Earshot.exe the owner passed, checked. RequireRelease is for
# install and uninstall: those may only run from a release build, which is the
# installed copy under Program Files or an unzipped publish folder. Both carry the
# publish manifest Earshot.files.json, which install reads; a build output folder
# does not, and install would refuse it anyway.
function Resolve-EarshotExe
{
    param(
        [Parameter(Mandatory = $true)][string]$ExePath,
        [switch]$RequireRelease
    )

    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf))
    {
        throw ("Earshot.exe was not found at: " + $ExePath)
    }

    $full = (Resolve-Path -LiteralPath $ExePath).ProviderPath
    if ([System.IO.Path]::GetFileName($full) -ne 'Earshot.exe')
    {
        throw ("That is not Earshot.exe: " + $full)
    }

    if ($RequireRelease)
    {
        $folder = [System.IO.Path]::GetDirectoryName($full)
        $lower = $full.ToLowerInvariant()
        foreach ($fragment in @('\bin\debug\', '\bin\release\', '\obj\', '\artifacts\'))
        {
            if ($lower.Contains($fragment))
            {
                throw ("Install and uninstall never run from a build output folder. Use the installed copy or an unzipped release: " + $full)
            }
        }

        $manifest = Join-Path $folder 'Earshot.files.json'
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf))
        {
            throw ("There is no Earshot.files.json beside " + $full +
                ", so this is not a release build and install would refuse it. Use the installed copy or an unzipped release.")
        }
    }

    return $full
}

# ------------------------------------------------------------------ run context

function New-LiveTestRun
{
    param(
        [Parameter(Mandatory = $true)][string]$TestId,
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$Settles,
        [Parameter(Mandatory = $true)][string]$ExePath,
        [string]$RunRoot = '',
        [switch]$RequireRelease
    )

    Assert-LiveEnvironment
    $exe = Resolve-EarshotExe -ExePath $ExePath -RequireRelease:$RequireRelease
    $paths = Get-EarshotDataPaths

    if ([string]::IsNullOrEmpty($RunRoot))
    {
        $RunRoot = Join-Path $paths.LiveTestFolder (Get-UtcStamp)
    }

    $folder = Join-Path $RunRoot $TestId
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $folder 'app-evidence') | Out-Null

    $run = [ordered]@{
        TestId          = $TestId
        Title           = $Title
        Settles         = $Settles
        ExePath         = $exe
        RunRoot         = $RunRoot
        Folder          = $folder
        SummaryPath     = (Join-Path $folder 'summary.txt')
        ResultPath      = (Join-Path $folder 'result.json')
        AppEvidence     = (Join-Path $folder 'app-evidence')
        AppLiveTest     = $paths.LiveTestFolder
        LogFolder       = $paths.LogFolder
        MachineFolder   = $paths.MachineFolder
        StartedUtc      = (Get-UtcNowText)
        StartedAt       = (Get-Date).ToUniversalTime()
        StepIndex       = 0
        Steps           = (New-Object System.Collections.ArrayList)
        Criteria        = (New-Object System.Collections.ArrayList)
        Findings        = (New-Object System.Collections.ArrayList)
        Answers         = (New-Object System.Collections.ArrayList)
        Errors          = (New-Object System.Collections.ArrayList)
        CopiedEvidence  = (New-Object System.Collections.ArrayList)
        LastCopied      = @()
    }

    Set-Content -LiteralPath $run.SummaryPath -Value '' -Encoding UTF8
    Write-Line -Run $run -Text ('Earshot live test ' + $TestId + ': ' + $Title)
    Write-Line -Run $run -Text ('Started (UTC): ' + $run.StartedUtc)
    Write-Line -Run $run -Text ('Earshot.exe:   ' + $exe)
    Write-Line -Run $run -Text ('Evidence:      ' + $folder)
    Write-Line -Run $run -Text ('This settles:  ' + $Settles)
    Write-Line -Run $run -Text ''
    return $run
}

function Write-Line
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Text = ''
    )

    Write-Host $Text
    Add-Content -LiteralPath $Run.SummaryPath -Value $Text -Encoding UTF8
}

function Write-Section
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Title
    )

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('--- ' + $Title + ' (' + (Get-UtcNowText) + ') ---')
}

# The preconditions and the physical actions a test needs, printed before anything runs.
function Show-Preconditions
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string[]]$Preconditions,
        [string[]]$PhysicalActions = @()
    )

    Write-Section -Run $Run -Title 'Preconditions'
    foreach ($item in $Preconditions) { Write-Line -Run $Run -Text ('  [ ] ' + $item) }
    if ($PhysicalActions.Count -gt 0)
    {
        Write-Line -Run $Run -Text ''
        Write-Line -Run $Run -Text 'Physical actions this test needs from you:'
        foreach ($item in $PhysicalActions) { Write-Line -Run $Run -Text ('  * ' + $item) }
    }

    Write-Line -Run $Run -Text ''
    $answer = Read-Host 'Are all of those true, and are you ready to start? [y/N]'
    Add-Content -LiteralPath $Run.SummaryPath -Value ('Ready? ' + $answer) -Encoding UTF8
    if ($answer -notmatch '^(y|yes)$')
    {
        Write-Line -Run $Run -Text 'Stopped before any step ran.'
        return $false
    }

    return $true
}

# ------------------------------------------------------------ asking the owner

# Asks before a step that changes something. Returns $true only on a typed y.
function Confirm-Step
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$Consequence = ''
    )

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('LIVE STEP: ' + $Prompt)
    if (-not [string]::IsNullOrEmpty($Consequence))
    {
        Write-Line -Run $Run -Text ('  What it does: ' + $Consequence)
    }

    $answer = Read-Host 'Run it now? [y/N]'
    Add-Content -LiteralPath $Run.SummaryPath -Value ('  Answer: ' + $answer) -Encoding UTF8
    if ($answer -match '^(y|yes)$') { return $true }

    Write-Line -Run $Run -Text '  Skipped at your request.'
    return $false
}

# A question only the owner can answer, recorded with its answer.
#
# A script started without a console gets end of input from Read-Host, which comes back as an empty
# string or as $null depending on how the input was closed, never as an error, so an unbounded loop
# here would spin for ever instead of stopping. Three empty answers in a row is taken as nobody being
# there, and the run is stopped with a message that says so. A wrong word is just asked again:
# someone is clearly at the keyboard.
$script:EmptyAnswerLimit = 3

function Read-Answer
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Question,
        [string[]]$Options = @('yes', 'no', 'unsure')
    )

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('QUESTION: ' + $Question)
    $list = $Options -join '/'
    $empty = 0
    while ($true)
    {
        $typed = Read-Host ('  [' + $list + ']')
        $answer = ''
        if ($null -ne $typed) { $answer = ([string]$typed).Trim().ToLowerInvariant() }
        foreach ($option in $Options)
        {
            if ($answer -eq $option.ToLowerInvariant())
            {
                Write-Line -Run $Run -Text ('  Answer: ' + $option)
                [void]$Run.Answers.Add([ordered]@{ question = $Question; answer = $option; utc = (Get-UtcNowText) })
                return $option
            }
        }

        if ([string]::IsNullOrEmpty($answer))
        {
            $empty++
            if ($empty -ge $script:EmptyAnswerLimit)
            {
                throw ('This question was left blank ' + [string]$empty + ' times, so this run is being stopped rather than ' +
                    'left waiting: "' + $Question + '". These tests have to be run by hand, in a console you can type in. ' +
                    'Nothing was changed by the question itself, and the evidence so far is in ' + $Run.Folder + '.')
            }

            Write-Host ('  Nothing was typed. Answer with one of: ' + $list)
            continue
        }

        $empty = 0
        Write-Host ('  Please answer with one of: ' + $list)
    }
}

# A free-text note from the owner, for example what the tray card said.
function Read-Note
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Question
    )

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('NOTE: ' + $Question)
    # Read-Host comes back as $null when the input was closed rather than left blank, the same as in
    # Read-Answer above, and a caller that trims the answer would throw on it. It is made a string here.
    $typed = Read-Host '  Your answer (Enter to leave it blank)'
    $answer = ''
    if ($null -ne $typed) { $answer = [string]$typed }
    Write-Line -Run $Run -Text ('  Answer: ' + $answer)
    [void]$Run.Answers.Add([ordered]@{ question = $Question; answer = $answer; utc = (Get-UtcNowText) })
    return $answer
}

function Wait-Owner
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Text
    )

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('DO THIS: ' + $Text)
    [void](Read-Host '  Press Enter when it is done')
    Write-Line -Run $Run -Text ('  Done at ' + (Get-UtcNowText))
}

function Wait-Seconds
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][int]$Seconds,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    Write-Line -Run $Run -Text ('Waiting ' + $Seconds + ' s: ' + $Reason)
    for ($i = $Seconds; $i -gt 0; $i--)
    {
        Write-Progress -Activity $Reason -Status ($i.ToString() + ' s left') -SecondsRemaining $i
        Start-Sleep -Seconds 1
    }

    Write-Progress -Activity $Reason -Completed
}

# ------------------------------------------------------------- running Earshot

function Get-CommandText
{
    param(
        [Parameter(Mandatory = $true)][string]$ExePath,
        [Parameter(Mandatory = $true)][string[]]$Command
    )

    return ('"' + $ExePath + '" ' + ((Get-QuotedArguments -Command $Command) -join ' '))
}

function Get-QuotedArguments
{
    param([Parameter(Mandatory = $true)][string[]]$Command)

    $quoted = @()
    foreach ($argument in $Command)
    {
        if ($argument -match '\s') { $quoted += ('"' + $argument + '"') }
        else { $quoted += $argument }
    }

    return ,$quoted
}

# Runs Earshot.exe with the given command line.
#   -Live        the command changes something, so the owner is asked first.
#   -Consequence one line saying what the change is. Required with -Live.
# A command whose array holds --json and --out <path> has that file parsed into the
# returned object's Json member. The exit code, the captured output and any failure
# to start are recorded whatever happens.
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

    $line = Get-CommandText -ExePath $Run.ExePath -Command $Command
    $Run.StepIndex = $Run.StepIndex + 1
    $index = '{0:d2}' -f $Run.StepIndex
    $step = [ordered]@{
        index        = $Run.StepIndex
        label        = $Label
        command      = ($Command -join ' ')
        commandText  = $line
        live         = [bool]$Live
        elevated     = $false
        startedUtc   = (Get-UtcNowText)
        finishedUtc  = $null
        ran          = $false
        exitCode     = $null
        exitName     = $null
        milliseconds = $null
        timedOut     = $false
        error        = $null
        stdoutFile   = $null
        stderrFile   = $null
        jsonFile     = $null
    }

    if ($Live)
    {
        if ([string]::IsNullOrEmpty($Consequence))
        {
            throw 'A live step must say what it does before it asks.'
        }

        if (-not (Confirm-Step -Run $Run -Prompt $line -Consequence $Consequence))
        {
            $step.error = 'skipped at the owner request'
            Write-Line -Run $Run -Text '  exit: skipped, no exit code or elapsed time.'
            [void]$Run.Steps.Add($step)
            return $null
        }
    }
    else
    {
        Write-Line -Run $Run -Text ('Reading (changes nothing): ' + $line)
    }

    $stdout = Join-Path $Run.Folder ([string]$index + '-' + $Label + '.stdout.txt')
    $stderr = Join-Path $Run.Folder ([string]$index + '-' + $Label + '.stderr.txt')
    $step.stdoutFile = $stdout
    $step.stderrFile = $stderr

    $before = Get-AppEvidenceNames -Run $Run
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $process = $null
    try
    {
        $process = Start-Process -FilePath $Run.ExePath -ArgumentList (Get-QuotedArguments -Command $Command) `
            -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr

        # Windows PowerShell 5.1 only fills ExitCode in for a -PassThru process whose handle was
        # read while it was still running. Without this line ExitCode reads null after the wait,
        # and every live step is recorded as failed whatever Earshot returned. Proved against a
        # real launch, not a fake one, in tools\live-tests\selftest\Test-RealLauncher.ps1.
        $null = $process.Handle
    }
    catch
    {
        $clock.Stop()
        $step.error = ('Earshot.exe could not be started: ' + ($_ | Out-String).Trim())
        [void]$Run.Steps.Add($step)
        Write-Failure -Run $Run -Message $step.error
        return $null
    }

    if (-not $process.WaitForExit($TimeoutSeconds * 1000))
    {
        $clock.Stop()
        $step.timedOut = $true
        $step.error = ('It did not finish within ' + $TimeoutSeconds + ' s.')
        Write-Failure -Run $Run -Message ([string]$Label + ': ' + $step.error)
        if (Confirm-Step -Run $Run -Prompt 'Stop that Earshot process now?' -Consequence 'Ends the run that is still going.')
        {
            try { $process.Kill() } catch { Write-Failure -Run $Run -Message ('It could not be stopped: ' + ($_ | Out-String).Trim()) }
        }

        [void]$Run.Steps.Add($step)
        return $null
    }

    $clock.Stop()
    if ($null -eq $process.ExitCode)
    {
        $step.ran = $true
        $step.error = 'Earshot.exe ran, but PowerShell did not give its exit code, so this step says nothing either way.'
        [void]$Run.Steps.Add($step)
        Write-Failure -Run $Run -Message ([string]$Label + ': ' + $step.error)
        return $null
    }

    $step.ran = $true
    $step.exitCode = $process.ExitCode
    $step.exitName = Get-GateExitName -ExitCode $process.ExitCode
    $step.milliseconds = [int]$clock.Elapsed.TotalMilliseconds
    $step.finishedUtc = Get-UtcNowText

    Write-Line -Run $Run -Text ('  exit ' + $step.exitCode + ' after ' + $step.milliseconds + ' ms')
    foreach ($file in @($stdout, $stderr))
    {
        if (Test-Path -LiteralPath $file)
        {
            $text = (Get-Content -LiteralPath $file -Raw)
            if (-not [string]::IsNullOrWhiteSpace($text))
            {
                foreach ($outLine in ($text -split "`r?`n"))
                {
                    if (-not [string]::IsNullOrWhiteSpace($outLine)) { Write-Line -Run $Run -Text ('  | ' + $outLine.TrimEnd()) }
                }
            }
        }
    }

    $parsed = $null
    $jsonPath = Get-OutPath -Command $Command
    if ($null -ne $jsonPath)
    {
        $step.jsonFile = $jsonPath
        if (Test-Path -LiteralPath $jsonPath)
        {
            try
            {
                $parsed = (Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json)
            }
            catch
            {
                $step.error = ('The JSON at ' + $jsonPath + ' could not be read: ' + ($_ | Out-String).Trim())
                Write-Failure -Run $Run -Message $step.error
            }
        }
        else
        {
            $step.error = ('No output file was written at ' + $jsonPath + '.')
            Write-Failure -Run $Run -Message $step.error
        }
    }

    Copy-AppEvidence -Run $Run -Before $before
    [void]$Run.Steps.Add($step)

    # The stored step stays plain, so result.json holds only what can be written out.
    # The parsed report goes to the caller on a copy of it.
    $returned = [pscustomobject]$step
    Add-Member -InputObject $returned -MemberType NoteProperty -Name 'json' -Value $parsed
    return $returned
}

# install and uninstall need one administrator prompt, which only the owner may approve.
# An elevated start cannot have its output redirected, so the exit code and Earshot's
# own log are the record.
function Invoke-EarshotElevated
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command,
        [Parameter(Mandatory = $true)][string]$Consequence,
        [int]$TimeoutSeconds = 600
    )

    $line = Get-CommandText -ExePath $Run.ExePath -Command $Command
    $Run.StepIndex = $Run.StepIndex + 1
    $step = [ordered]@{
        index       = $Run.StepIndex
        label       = $Label
        command     = ($Command -join ' ')
        commandText = $line
        live        = $true
        elevated    = $true
        startedUtc  = (Get-UtcNowText)
        finishedUtc = $null
        ran         = $false
        exitCode    = $null
        exitName    = $null
        timedOut    = $false
        error       = $null
    }

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text 'This step raises a Windows administrator prompt. Only you can approve it.'
    if (-not (Confirm-Step -Run $Run -Prompt $line -Consequence $Consequence))
    {
        $step.error = 'skipped at the owner request'
        Write-Line -Run $Run -Text '  exit: skipped, no exit code or elapsed time.'
        [void]$Run.Steps.Add($step)
        return $null
    }

    $before = Get-AppEvidenceNames -Run $Run
    $process = $null
    try
    {
        $process = Start-Process -FilePath $Run.ExePath -ArgumentList (Get-QuotedArguments -Command $Command) -Verb RunAs -PassThru
    }
    catch
    {
        $step.error = ('The elevated run could not be started (a declined prompt reports 1223): ' + ($_ | Out-String).Trim())
        [void]$Run.Steps.Add($step)
        Write-Failure -Run $Run -Message $step.error
        return $null
    }

    if (-not $process.WaitForExit($TimeoutSeconds * 1000))
    {
        $step.timedOut = $true
        $step.error = ('It did not finish within ' + $TimeoutSeconds + ' s.')
        [void]$Run.Steps.Add($step)
        Write-Failure -Run $Run -Message ([string]$Label + ': ' + $step.error)
        return $null
    }

    # No $process.Handle read here. That line is what makes ExitCode reliable for the
    # -NoNewWindow -PassThru process in Invoke-Earshot above, proved against a real launch
    # in tools\live-tests\selftest\Test-RealLauncher.ps1. Whether the same is true, false, or
    # unnecessary for a process started with -Verb RunAs from a non-elevated parent has not
    # been measured on this machine, and this run cannot raise the administrator prompt that
    # would let it be, so nothing is guessed: an unreadable exit code is reported as such
    # below and never scored as a pass or a fail.
    if ($null -eq $process.ExitCode)
    {
        $step.ran = $true
        $step.error = 'The elevated run started, but PowerShell did not give its exit code, so this step says nothing either way.'
        [void]$Run.Steps.Add($step)
        Write-Failure -Run $Run -Message ([string]$Label + ': ' + $step.error)
        return $null
    }

    $step.ran = $true
    $step.exitCode = $process.ExitCode
    $step.finishedUtc = Get-UtcNowText
    $step.exitName = Get-GateExitName -ExitCode $step.exitCode
    Write-Line -Run $Run -Text ('  exit ' + $step.exitCode + ' (' + $step.exitName + ')')
    Write-Line -Run $Run -Text ('  Earshot wrote what it did to its own log. See ' + $Run.LogFolder + '.')
    Copy-AppEvidence -Run $Run -Before $before
    [void]$Run.Steps.Add($step)
    return [pscustomobject]$step
}

# The name an exit code is given, so the summary reads the way the log does. Every step goes
# through here, a read-only probe as much as a gate run. Anything the table does not hold is
# reported as the number it was.
#
# The first block is GateExitCode in src\Earshot\Boot\Gate\GateActions.cs, value for value:
# install and uninstall exit with exactly those numbers, so a wrong name here would point
# the owner at the wrong remedy. GateExitNameTableTests holds the two tables to each other.
# There is no 1: no mode returns it, and a .NET crash does.
#
# The second block is ExitCodes in src\Earshot\ExitCodes.cs, the BSD sysexits numbering every
# mode shares. A probe ends in one of those rather than in a gate code: 78 is what `probe nodes`
# and `probe services` return on a machine with no pinned device, which is a normal answer and
# not a fault, so leaving it unnamed read as a non-answer in the evidence.
function Get-GateExitName
{
    param([int]$ExitCode)

    $names = @{
        0 = 'success'; 2 = 'partial'; 3 = 'failed'; 4 = 'not-present'; 5 = 'not-found'
        6 = 'no-identity'; 7 = 'not-available'; 8 = 'other-device-blocked'; 9 = 'status-not-written'
        10 = 'folder-not-secure'; 11 = 'device-blocked'; 12 = 'not-audio-sink'; 13 = 'other-device-protected'
        14 = 'no-manifest'; 15 = 'device-mismatch'; 16 = 'unsafe-environment'; 17 = 'no-config'
        20 = 'rejected'; 21 = 'not-elevated'; 22 = 'running-as-system'
        1223 = 'the administrator prompt was declined'
        64 = 'bad command line'; 69 = 'not available in this build'; 70 = 'a check inside Earshot failed'
        71 = 'a system call failed'; 74 = 'the report could not be written'; 77 = 'refused'
        78 = 'nothing configured to read'
    }

    if ($names.ContainsKey($ExitCode)) { return $names[$ExitCode] }
    return ('exit code ' + $ExitCode + ', see the log for its name')
}

# The --out path in a command, or $null.
function Get-OutPath
{
    param([Parameter(Mandatory = $true)][string[]]$Command)

    for ($i = 0; $i -lt $Command.Count - 1; $i++)
    {
        if ($Command[$i] -eq '--out') { return $Command[$i + 1] }
    }

    return $null
}

function Get-AppEvidenceNames
{
    param([Parameter(Mandatory = $true)]$Run)

    $names = @{}
    if (Test-Path -LiteralPath $Run.AppLiveTest)
    {
        foreach ($file in (Get-ChildItem -LiteralPath $Run.AppLiveTest -Filter '*.json' -File))
        {
            $names[$file.Name] = $true
        }
    }

    return $names
}

# Earshot's diag targets write their own JSON evidence beside the run folders. Each
# new file is copied into this test's app-evidence folder so one folder holds it all.
function Copy-AppEvidence
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)]$Before
    )

    $Run.LastCopied = @()
    if (-not (Test-Path -LiteralPath $Run.AppLiveTest)) { return }
    foreach ($file in (Get-ChildItem -LiteralPath $Run.AppLiveTest -Filter '*.json' -File | Sort-Object Name))
    {
        if ($Before.ContainsKey($file.Name)) { continue }
        $destination = Join-Path $Run.AppEvidence $file.Name
        try
        {
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
            [void]$Run.CopiedEvidence.Add($file.Name)
            $Run.LastCopied = $Run.LastCopied + @($destination)
            Write-Line -Run $Run -Text ('  Evidence from Earshot: ' + $destination)
        }
        catch
        {
            Write-Failure -Run $Run -Message ('The evidence file ' + $file.FullName + ' could not be copied: ' + ($_ | Out-String).Trim())
        }
    }
}

# The JSON evidence Earshot wrote for the diag run that has just finished, parsed. Null
# when that run wrote none, which is itself worth recording.
function Get-DiagEvidence
{
    param([Parameter(Mandatory = $true)]$Run)

    $copied = @($Run.LastCopied)
    if ($copied.Count -eq 0)
    {
        Write-Line -Run $Run -Text '  That run wrote no evidence file of its own.'
        return $null
    }

    $path = $copied[$copied.Count - 1]
    try
    {
        return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    }
    catch
    {
        Write-Failure -Run $Run -Message ('The evidence at ' + $path + ' could not be read: ' + ($_ | Out-String).Trim())
        return $null
    }
}

# Reads a diag ks or diag connect evidence file into the few values the criteria need,
# and prints them. Anything missing comes back as $null rather than a guess.
function Read-KsEvidence
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        $Evidence
    )

    $summary = [ordered]@{
        Action        = (Get-Field -Object $Evidence -Name 'action')
        FilterChoice  = (Get-Field -Object $Evidence -Name 'filterChoice')
        Payload       = (Get-Field -Object $Evidence -Name 'payload')
        Error         = (Get-Field -Object $Evidence -Name 'error')
        SendFault     = (Get-Field -Object $Evidence -Name 'sendFault')
        Reached       = (Get-FieldPath -Object $Evidence -Path @('confirmation', 'reached'))
        Unreachable   = (Get-FieldPath -Object $Evidence -Path @('confirmation', 'unreachable'))
        Source        = (Get-FieldPath -Object $Evidence -Path @('confirmation', 'source'))
        MillisToState = (Get-Field -Object $Evidence -Name 'millisecondsToFirstWantedStateNotification')
        Filters       = @()
        AcceptedRoles = @()
        RejectedRoles = @()
        Threads       = @()
        Apartments    = @()
    }

    foreach ($filter in (Get-Field -Object $Evidence -Name 'filters'))
    {
        $role = Get-Field -Object $filter -Name 'role'
        $row = [ordered]@{
            role          = $role
            name          = (Get-Field -Object $filter -Name 'name')
            guardPassed   = (Get-Field -Object $filter -Name 'guardPassed')
            activated     = (Get-Field -Object $filter -Name 'ksControlActivated')
            sent          = (Get-Field -Object $filter -Name 'requestSent')
            notSentReason = (Get-Field -Object $filter -Name 'notSentReason')
            hr            = (Get-Field -Object $filter -Name 'hr')
            hrName        = (Get-Field -Object $filter -Name 'hrName')
            accepted      = (Get-Field -Object $filter -Name 'accepted')
            bytesReturned = (Get-Field -Object $filter -Name 'bytesReturned')
            callMs        = (Get-Field -Object $filter -Name 'callMilliseconds')
        }

        $summary.Filters = $summary.Filters + @($row)
        if ($row.accepted -eq $true) { $summary.AcceptedRoles = $summary.AcceptedRoles + @($role) }
        elseif ($row.sent -eq $true) { $summary.RejectedRoles = $summary.RejectedRoles + @($role) }
    }

    foreach ($notification in (Get-Field -Object $Evidence -Name 'notifications'))
    {
        $thread = Get-Field -Object $notification -Name 'threadId'
        $apartment = Get-Field -Object $notification -Name 'apartment'
        if ($null -ne $thread -and -not ($summary.Threads -contains $thread)) { $summary.Threads = $summary.Threads + @($thread) }
        if ($null -ne $apartment -and -not ($summary.Apartments -contains $apartment)) { $summary.Apartments = $summary.Apartments + @($apartment) }
    }

    Write-Line -Run $Run -Text ('  action ' + $summary.Action + ', filters ' + $summary.FilterChoice + ', payload ' + $summary.Payload)
    foreach ($row in $summary.Filters)
    {
        Write-Line -Run $Run -Text ('  filter ' + $row.role + ' (' + $row.name + '): sent ' + $row.sent +
            ', hr ' + $row.hr + ' ' + $row.hrName + ', accepted ' + $row.accepted +
            ', bytes ' + $row.bytesReturned + ', call ' + $row.callMs + ' ms' +
            ', guard ' + $row.guardPassed + ', IKsControl ' + $row.activated +
            ', not sent because: ' + $row.notSentReason)
    }

    Write-Line -Run $Run -Text ('  confirmation: reached ' + $summary.Reached + ', unreachable ' + $summary.Unreachable +
        ', source ' + $summary.Source + ', ms to the wanted state ' + $summary.MillisToState)
    if ($summary.Threads.Count -gt 0)
    {
        Write-Line -Run $Run -Text ('  notification threads ' + ($summary.Threads -join ', ') + ', apartments ' + ($summary.Apartments -join ', '))
    }

    if (-not [string]::IsNullOrEmpty($summary.Error)) { Write-Failure -Run $Run -Message ('The run reported: ' + $summary.Error) }
    if (-not [string]::IsNullOrEmpty($summary.SendFault)) { Write-Failure -Run $Run -Message ('The send faulted: ' + $summary.SendFault) }
    return $summary
}

# ------------------------------------------------------------- reading the state

# The pinned device and the node state, read without elevation. Changes nothing.
function Get-NodeState
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Label = 'nodes'
    )

    $evidence = Join-Path $Run.Folder ([string]$Label + '.json')
    $result = Invoke-Earshot -Run $Run -Label $Label -Command @('probe', 'nodes', '--json', '--out', $evidence)
    if ($null -eq $result) { return $null }
    return $result.json
}

function Get-AudioState
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Label = 'audio'
    )

    $evidence = Join-Path $Run.Folder ([string]$Label + '.json')
    $result = Invoke-Earshot -Run $Run -Label $Label -Command @('probe', 'audio', '--json', '--out', $evidence)
    if ($null -eq $result) { return $null }
    return $result.json
}

# Walks the device topology to the Bluetooth filters and reads the pin count on each,
# proving the connect path is reachable without sending anything to a filter.
function Get-TopologyState
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Label = 'topology'
    )

    $evidence = Join-Path $Run.Folder ([string]$Label + '.json')
    $result = Invoke-Earshot -Run $Run -Label $Label -Command @('probe', 'topology', '--json', '--out', $evidence)
    if ($null -eq $result) { return $null }
    foreach ($adapter in (Get-Field -Object $result.json -Name 'adapters'))
    {
        Write-Line -Run $Run -Text ('  ' + (Get-Field -Object $adapter -Name 'adapterId') +
            ': guard passed ' + (Get-Field -Object $adapter -Name 'guardPassed') +
            ', IKsControl activated ' + (Get-Field -Object $adapter -Name 'ksControlActivated') +
            ', pins ' + (Get-Field -Object $adapter -Name 'pinCount'))
    }

    return $result.json
}

function Get-ServiceState
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Label = 'services'
    )

    $evidence = Join-Path $Run.Folder ([string]$Label + '.json')
    $result = Invoke-Earshot -Run $Run -Label $Label -Command @('probe', 'services', '--json', '--out', $evidence)
    if ($null -eq $result) { return $null }
    return $result.json
}

function Get-TaskState
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Label = 'task'
    )

    $evidence = Join-Path $Run.Folder ([string]$Label + '.json')
    $result = Invoke-Earshot -Run $Run -Label $Label -Command @('probe', 'task', '--json', '--out', $evidence)
    if ($null -eq $result) { return $null }
    return $result.json
}

# The endpoint states of the pinned container from a probe audio report:
# @{ Render = 'Active'|'Unplugged'|...; Capture = ...; Connection = ... }.
function Get-TargetEndpointStates
{
    param($AudioJson)

    $states = [ordered]@{ Render = $null; Capture = $null; Connection = $null; Container = $null; Names = @() }
    $device = Get-Field -Object $AudioJson -Name 'device'
    if ($null -ne $device)
    {
        $states.Connection = Get-Field -Object $device -Name 'connection'
        $states.Container = Get-Field -Object $device -Name 'containerId'
    }

    $groups = Get-Field -Object $AudioJson -Name 'groups'
    if ($null -eq $groups -or $null -eq $states.Container) { return $states }

    foreach ($group in $groups)
    {
        if ((Get-Field -Object $group -Name 'containerId') -ne $states.Container) { continue }
        foreach ($endpoint in (Get-Field -Object $group -Name 'endpoints'))
        {
            $flow = Get-Field -Object $endpoint -Name 'flow'
            $state = Get-Field -Object $endpoint -Name 'state'
            $states.Names += ([string]$flow + ' ' + $state + ' ' + (Get-Field -Object $endpoint -Name 'friendlyName'))
            if ($flow -eq 'Render' -and ($null -eq $states.Render -or $state -eq 'Active')) { $states.Render = $state }
            if ($flow -eq 'Capture' -and ($null -eq $states.Capture -or $state -eq 'Active')) { $states.Capture = $state }
        }
    }

    return $states
}

# Reads a JSON file Earshot owns, without changing it. Null when it is not there or
# cannot be read, with the reason recorded.
function Read-EarshotJsonFile
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        Write-Line -Run $Run -Text ('There is no file at ' + $Path + '.')
        return $null
    }

    try
    {
        return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    }
    catch
    {
        Write-Failure -Run $Run -Message ([string]$Path + ' could not be read: ' + ($_ | Out-String).Trim())
        return $null
    }
}

# Block at boot, as the SYSTEM-owned config.json records it. That file is the authority:
# the setting is not in the user settings, because the boot task cannot read those.
function Get-BlockAtBootSetting
{
    param([Parameter(Mandatory = $true)]$Run)

    $config = Read-EarshotJsonFile -Run $Run -Path (Join-Path $Run.MachineFolder 'config.json')
    return (Get-Field -Object $config -Name 'BlockAtBoot')
}

# Protect audio quality, as the user settings record it.
function Get-ProtectAudioSetting
{
    param([Parameter(Mandatory = $true)]$Run)

    $paths = Get-EarshotDataPaths
    $settings = Read-EarshotJsonFile -Run $Run -Path $paths.SettingsFile
    return (Get-Field -Object $settings -Name 'ProtectAudioQuality')
}

# Whether Fast Startup is on, read from the registry value Windows keeps it in. Read only:
# nothing here turns it on or off, because that changes how the machine shuts down.
#
# Fast Startup is a hybrid shutdown, so it applies to a shutdown and never to a restart. A test
# that powers the machine right down is the only one whose evidence can say anything about it,
# which is why the value is recorded there rather than left to a free-text note.
# https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/shutdown-and-restart-notifications
function Get-FastStartupSetting
{
    param([Parameter(Mandatory = $true)]$Run)

    $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
    try
    {
        $entry = Get-ItemProperty -LiteralPath $key -Name 'HiberbootEnabled'
        $value = Get-Field -Object $entry -Name 'HiberbootEnabled'
        if ($null -eq $value) { return 'unknown' }
        if ([int]$value -eq 0) { return 'off' }
        return 'on'
    }
    catch
    {
        Write-Failure -Run $Run -Message ('HiberbootEnabled could not be read under ' + $key + ', so Fast Startup is recorded as unknown: ' +
            ($_ | Out-String).Trim())
        return 'unknown'
    }
}

# The Earshot log lines written at or after the given UTC time whose text holds the
# pattern. The log is plain text, one entry per line, starting with the UTC stamp.
function Get-EarshotLogLines
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [datetime]$SinceUtc = [datetime]::MinValue
    )

    $found = @()
    if (-not (Test-Path -LiteralPath $Run.LogFolder)) { return ,$found }
    foreach ($file in (Get-ChildItem -LiteralPath $Run.LogFolder -Filter '*.log' -File | Sort-Object LastWriteTimeUtc))
    {
        foreach ($line in (Get-Content -LiteralPath $file.FullName))
        {
            if ($line -notmatch [regex]::Escape($Pattern)) { continue }
            if ($SinceUtc -gt [datetime]::MinValue)
            {
                if ($line -match '^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})Z')
                {
                    $stamp = [datetime]::ParseExact($Matches[1], "yyyy-MM-dd'T'HH:mm:ss.fff", [System.Globalization.CultureInfo]::InvariantCulture)
                    if ($stamp -lt $SinceUtc) { continue }
                }
            }

            $found += $line
        }
    }

    return ,$found
}

# Copies the Earshot log into the evidence folder, so a later run or a log roll cannot lose it.
function Save-EarshotLog
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Label = 'earshot-log'
    )

    if (-not (Test-Path -LiteralPath $Run.LogFolder))
    {
        Write-Line -Run $Run -Text ('There is no log folder at ' + $Run.LogFolder + ' yet.')
        return
    }

    $destination = Join-Path $Run.Folder $Label
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($file in (Get-ChildItem -LiteralPath $Run.LogFolder -Filter '*.log' -File))
    {
        try
        {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
        }
        catch
        {
            Write-Failure -Run $Run -Message ('The log ' + $file.FullName + ' could not be copied: ' + ($_ | Out-String).Trim())
        }
    }

    Write-Line -Run $Run -Text ('Log copied to ' + $destination)
}

# ------------------------------------------------------------------- recording

function Add-Criterion
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Criterion,
        [Parameter(Mandatory = $true)][ValidateSet('pass', 'fail', 'inconclusive')][string]$Outcome,
        [string]$Detail = ''
    )

    [void]$Run.Criteria.Add([ordered]@{
        id        = $Id
        criterion = $Criterion
        outcome   = $Outcome
        detail    = $Detail
        utc       = (Get-UtcNowText)
    })

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('[' + $Outcome.ToUpperInvariant() + '] ' + $Id + ': ' + $Criterion)
    if (-not [string]::IsNullOrEmpty($Detail)) { Write-Line -Run $Run -Text ('        ' + $Detail) }
}

# A measured value or a decision the build was waiting for, kept in result.json so the
# skill can read it back without re-reading the whole summary.
#
# $Value takes $null on purpose. A report leaves a member out when the read behind it failed
# or the answer is not known, which is a finding in itself: the application writes null to say
# so, and a test that threw on it would lose the whole sitting for the one case it was there to
# investigate. Mandatory alone would reject $null, so [AllowNull] is what lets it through, and
# it is written down as "not recorded" rather than as an empty value.
# https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_functions_advanced_parameters
function Add-Finding
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][AllowNull()]$Value,
        [string]$Detail = ''
    )

    [void]$Run.Findings.Add([ordered]@{ name = $Name; value = $Value; detail = $Detail; utc = (Get-UtcNowText) })
    $text = 'FINDING ' + $Name + ' = ' + $(if ($null -eq $Value) { 'not recorded' } else { $Value })
    if (-not [string]::IsNullOrEmpty($Detail)) { $text = [string]$text + ' (' + $Detail + ')' }
    Write-Line -Run $Run -Text $text
}

# Every failure is written down and shown. Nothing here swallows one.
function Write-Failure
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Message
    )

    [void]$Run.Errors.Add([ordered]@{ utc = (Get-UtcNowText); message = $Message })
    Write-Line -Run $Run -Text ('ERROR: ' + $Message)
}

# What to run after the machine has been restarted. The test cannot survive a reboot,
# so the exact command is printed and written to resume.txt in the run folder.
function Write-ResumeInstruction
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string]$ExtraArguments = ''
    )

    $command = 'powershell -NoProfile -ExecutionPolicy Bypass -File "' + $ScriptPath + '" -ExePath "' + $Run.ExePath +
        '" -RunRoot "' + $Run.RunRoot + '" -Resume'
    if (-not [string]::IsNullOrEmpty($ExtraArguments)) { $command = [string]$command + ' ' + $ExtraArguments }
    $file = Join-Path $Run.Folder 'resume.txt'
    Set-Content -LiteralPath $file -Value $command -Encoding UTF8
    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text 'AFTER YOU HAVE LOGGED BACK IN, run exactly this, in a normal (not administrator) window:'
    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('  ' + $command)
    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('It is also saved in ' + $file + '.')
}

# ------------------------------------------------------------------- being at rest

# The vocabulary Add-Finding -Name 'leftAtRest' takes, so every run says one of the same five
# things rather than a free string per script.
$script:AtRestYes           = 'yes'
$script:AtRestNo             = 'no'
$script:AtRestOnPurpose      = 'no-on-purpose'
$script:AtRestNotApplicable  = 'not-applicable'
$script:AtRestUnknown        = 'unknown'

# What Complete-LiveTestRun calls before it writes result.json, on every script, so no test can
# forget it. "At rest" means the AirPods Bluetooth nodes read Blocked, so Windows has nothing to
# page at the next boot: the state the 19 September incident was missing, because test 01 and
# test 05 both need the nodes Allowed and both ended that way with nobody asked to put them back.
#
#   not set up, or Block at boot off   at rest does not apply here; say so
#   nodes read Blocked                 at rest already; say so
#   $Reason is given                   deliberately not at rest; the reason is printed, no offer
#   anything else                      offer ONE live block; warn loudly if it is declined, the
#                                       step fails, or the nodes still do not read Blocked after
#
# Never throws. A failure in here is recorded as an error and as leftAtRest = 'unknown', and
# Complete-LiveTestRun still writes result.json: the record of what happened matters more than a
# tidy exit from this one check.
function Close-AtRest
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$Reason = ''
    )

    $detail = [ordered]@{
        before      = $null
        setUp       = $null
        blockAtBoot = $null
        reason      = $(if ([string]::IsNullOrEmpty($Reason)) { $null } else { $Reason })
        offered     = $false
        accepted    = $null
        blockStep   = $null
        after       = $null
    }

    Write-Section -Run $Run -Title 'Is the machine at rest?'

    try
    {
        $task = Get-TaskState -Run $Run -Label 'at-rest-task'
        $setUp = Get-Field -Object $task -Name 'setUp'
        $blockAtBoot = Get-BlockAtBootSetting -Run $Run
        $detail.setUp = $setUp
        $detail.blockAtBoot = $blockAtBoot

        if ($setUp -ne $true -or $blockAtBoot -ne $true)
        {
            Write-Line -Run $Run -Text ('Earshot is not set up, or Block at boot is off (set up ' + $setUp +
                ', Block at boot ' + $blockAtBoot + '), so being at rest does not apply here.')
            Add-Finding -Run $Run -Name 'leftAtRest' -Value $script:AtRestNotApplicable `
                -Detail ('set up ' + $setUp + ', Block at boot ' + $blockAtBoot + '.')
            return $detail
        }

        $nodes = Get-NodeState -Run $Run -Label 'at-rest-nodes'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $detail.before = $nodeState
        Write-Line -Run $Run -Text ('The AirPods Bluetooth nodes read ' + $nodeState + ' as this run ends.')

        if ($nodeState -eq 'Blocked')
        {
            Write-Line -Run $Run -Text 'The machine is at rest: Windows has nothing to page at the next boot.'
            Add-Finding -Run $Run -Name 'leftAtRest' -Value $script:AtRestYes -Detail ('nodes read ' + $nodeState + '.')
            $detail.after = $nodeState
            return $detail
        }

        if (-not [string]::IsNullOrEmpty($Reason))
        {
            Write-Line -Run $Run -Text ('The machine is deliberately NOT at rest: ' + $Reason)
            Add-Finding -Run $Run -Name 'leftAtRest' -Value $script:AtRestOnPurpose -Detail $Reason
            $detail.after = $nodeState
            return $detail
        }

        $detail.offered = $true
        $consequence = 'Blocks the AirPods Bluetooth nodes so this PC does not page them at the next boot. ' +
            'If the AirPods are playing through this PC right now, that stops.'
        $beforeStepCount = $Run.Steps.Count
        $block = Invoke-Earshot -Run $Run -Label 'at-rest-block' -Command @('diag', 'gate', 'block') -Live -Consequence $consequence

        $lastStep = $null
        if ($Run.Steps.Count -gt $beforeStepCount) { $lastStep = $Run.Steps[$Run.Steps.Count - 1] }
        $declined = ($null -ne $lastStep -and $lastStep.error -eq 'skipped at the owner request')
        $detail.accepted = (-not $declined)
        if ($null -ne $block) { $detail.blockStep = [ordered]@{ exitCode = $block.exitCode; exitName = $block.exitName } }
        elseif ($null -ne $lastStep) { $detail.blockStep = [ordered]@{ error = $lastStep.error } }

        $nodesAfter = Get-NodeState -Run $Run -Label 'at-rest-nodes-after'
        $stateAfter = Get-Field -Object $nodesAfter -Name 'nodeState'
        $detail.after = $stateAfter

        if ($stateAfter -eq 'Blocked')
        {
            Write-Line -Run $Run -Text 'The machine is at rest now: the nodes read Blocked.'
            Add-Finding -Run $Run -Name 'leftAtRest' -Value $script:AtRestYes -Detail ('nodes read ' + $stateAfter + ' after the block.')
            return $detail
        }

        Write-Line -Run $Run -Text ''
        Write-Line -Run $Run -Text '=========================================================================='
        Write-Line -Run $Run -Text 'THE MACHINE IS NOT AT REST.'
        Write-Line -Run $Run -Text ('The AirPods Bluetooth nodes read ' + $stateAfter + ', not Blocked.')
        Write-Line -Run $Run -Text 'If this PC is shut down or restarted like this, Windows will page the AirPods at the next boot,'
        Write-Line -Run $Run -Text 'and they will bounce between the phone and this PC.'
        Write-Line -Run $Run -Text 'To fix it: start the Earshot tray, whose own start-up check blocks the nodes when they are not'
        Write-Line -Run $Run -Text ('in use, or run: "' + $Run.ExePath + '" diag gate block')
        Write-Line -Run $Run -Text '=========================================================================='
        Add-Finding -Run $Run -Name 'leftAtRest' -Value $script:AtRestNo `
            -Detail ('nodes read ' + $stateAfter + '; offered ' + $detail.offered + ', accepted ' + $detail.accepted + '.')
        return $detail
    }
    catch
    {
        Write-Failure -Run $Run -Message ('The at-rest check itself failed: ' + ($_ | Out-String).Trim())
        Add-Finding -Run $Run -Name 'leftAtRest' -Value $script:AtRestUnknown -Detail 'The read this check makes failed; see the error above.'
        return $detail
    }
}

function Complete-LiveTestRun
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [string]$AtRestReason = ''
    )

    $atRestDetail = $null
    try
    {
        $atRestDetail = Close-AtRest -Run $Run -Reason $AtRestReason
    }
    catch
    {
        # Close-AtRest guards its own body. Reaching here means something failed outside that
        # guard, and the record still matters more than a tidy exit, so it is caught here too
        # rather than letting result.json go unwritten.
        Write-Failure -Run $Run -Message ('The at-rest check failed outright: ' + ($_ | Out-String).Trim())
        Add-Finding -Run $Run -Name 'leftAtRest' -Value $script:AtRestUnknown -Detail 'The check crashed outside its own guard; see the error above.'
        $atRestDetail = [ordered]@{
            before = $null; setUp = $null; blockAtBoot = $null
            reason = $(if ([string]::IsNullOrEmpty($AtRestReason)) { $null } else { $AtRestReason })
            offered = $false; accepted = $null; blockStep = $null; after = $null
        }
    }

    $outcomes = @($Run.Criteria | ForEach-Object { $_.outcome })
    $overall = $script:UnclearOutcome
    if ($outcomes.Count -eq 0) { $overall = $script:UnclearOutcome }
    elseif ($outcomes -contains $script:FailOutcome) { $overall = $script:FailOutcome }
    elseif ($outcomes -contains $script:UnclearOutcome) { $overall = $script:UnclearOutcome }
    else { $overall = $script:PassOutcome }

    $result = [ordered]@{
        test        = $Run.TestId
        title       = $Run.Title
        settles     = $Run.Settles
        exe         = $Run.ExePath
        startedUtc  = $Run.StartedUtc
        finishedUtc = (Get-UtcNowText)
        overall     = $overall
        criteria    = @($Run.Criteria)
        findings    = @($Run.Findings)
        answers     = @($Run.Answers)
        steps       = @($Run.Steps)
        errors      = @($Run.Errors)
        appEvidence = @($Run.CopiedEvidence)
        atRest      = $atRestDetail
        folder      = $Run.Folder
    }

    try
    {
        ($result | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $Run.ResultPath -Encoding UTF8
    }
    catch
    {
        Write-Line -Run $Run -Text ('ERROR: result.json could not be written: ' + ($_ | Out-String).Trim())
    }

    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text '================ result ================'
    foreach ($criterion in $Run.Criteria)
    {
        Write-Line -Run $Run -Text (('  {0,-12} {1}  {2}' -f $criterion.outcome, $criterion.id, $criterion.criterion))
    }

    foreach ($finding in $Run.Findings)
    {
        $shown = 'not recorded'
        if ($null -ne $finding.value) { $shown = $finding.value }
        Write-Line -Run $Run -Text (('  finding      {0} = {1}' -f $finding.name, $shown))
    }

    if ($Run.Errors.Count -gt 0)
    {
        Write-Line -Run $Run -Text ('  ' + $Run.Errors.Count + ' error(s) were recorded. They are in result.json.')
    }

    Write-Line -Run $Run -Text ('  OVERALL: ' + $overall.ToUpperInvariant())
    Write-Line -Run $Run -Text ('  Evidence: ' + $Run.Folder)
    return $overall
}

# The process exit code for an overall outcome, so a run that failed is visible to anything that
# does not read result.json: 0 pass, 1 fail, 2 inconclusive. Every script ends with it, and the
# launcher passes it on.
function Get-LiveTestExitCode
{
    param([Parameter(Mandatory = $true)][string]$Overall)

    if ($Overall -eq $script:PassOutcome) { return 0 }
    if ($Overall -eq $script:FailOutcome) { return 1 }
    return 2
}

Export-ModuleMember -Function `
    Get-Field, Get-FieldPath, Join-Parts, Get-UtcStamp, Get-UtcNowText,
    Assert-LiveEnvironment, Get-EarshotDataPaths, Resolve-EarshotExe,
    New-LiveTestRun, Write-Line, Write-Section, Show-Preconditions,
    Confirm-Step, Read-Answer, Read-Note, Wait-Owner, Wait-Seconds,
    Get-CommandText, Get-QuotedArguments, Invoke-Earshot, Invoke-EarshotElevated,
    Get-GateExitName, Get-OutPath, Copy-AppEvidence, Get-DiagEvidence, Read-KsEvidence,
    Get-NodeState, Get-AudioState, Get-TopologyState, Get-ServiceState, Get-TaskState,
    Get-TargetEndpointStates, Read-EarshotJsonFile, Get-BlockAtBootSetting, Get-ProtectAudioSetting,
    Get-EarshotLogLines, Save-EarshotLog, Get-FastStartupSetting,
    Add-Criterion, Add-Finding, Write-Failure, Write-ResumeInstruction, Close-AtRest, Complete-LiveTestRun,
    Get-LiveTestExitCode
