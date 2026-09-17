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
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
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

    return $current
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
# be off. With either set, Earshot refuses gate, install and uninstall outright and
# refuses every diag target, which would look like a device failure.
function Assert-LiveEnvironment
{
    $set = @()
    foreach ($name in @('EARSHOT_SAFE_MODE', 'EARSHOT_DATA_ROOT'))
    {
        $value = [System.Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrEmpty($value)) { $set += ($name + '=' + $value) }
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
    while ($true)
    {
        $answer = (Read-Host ('  [' + $list + ']')).Trim().ToLowerInvariant()
        foreach ($option in $Options)
        {
            if ($answer -eq $option.ToLowerInvariant())
            {
                Write-Line -Run $Run -Text ('  Answer: ' + $option)
                [void]$Run.Answers.Add([ordered]@{ question = $Question; answer = $option; utc = (Get-UtcNowText) })
                return $option
            }
        }

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
    $answer = Read-Host '  Your answer (Enter to leave it blank)'
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

    return $quoted
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
            [void]$Run.Steps.Add($step)
            return $null
        }
    }
    else
    {
        Write-Line -Run $Run -Text ('Reading (changes nothing): ' + $line)
    }

    $stdout = Join-Path $Run.Folder ($index + '-' + $Label + '.stdout.txt')
    $stderr = Join-Path $Run.Folder ($index + '-' + $Label + '.stderr.txt')
    $step.stdoutFile = $stdout
    $step.stderrFile = $stderr

    $before = Get-AppEvidenceNames -Run $Run
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $process = $null
    try
    {
        $process = Start-Process -FilePath $Run.ExePath -ArgumentList (Get-QuotedArguments -Command $Command) `
            -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
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
        Write-Failure -Run $Run -Message ($Label + ': ' + $step.error)
        if (Confirm-Step -Run $Run -Prompt 'Stop that Earshot process now?' -Consequence 'Ends the run that is still going.')
        {
            try { $process.Kill() } catch { Write-Failure -Run $Run -Message ('It could not be stopped: ' + ($_ | Out-String).Trim()) }
        }

        [void]$Run.Steps.Add($step)
        return $null
    }

    $clock.Stop()
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
        Write-Failure -Run $Run -Message ($Label + ': ' + $step.error)
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

# The name install, uninstall and the gate use for an exit code, so the summary reads
# the way the log does. Anything else is reported as the number it was.
function Get-GateExitName
{
    param([int]$ExitCode)

    $names = @{
        0 = 'success'; 1 = 'partial'; 2 = 'failed'; 3 = 'not-present'; 4 = 'not-found'
        5 = 'no-identity'; 6 = 'not-available'; 8 = 'other-device-blocked'; 9 = 'status-not-written'
        10 = 'folder-not-secure'; 11 = 'device-blocked'; 12 = 'not-audio-sink'; 13 = 'other-device-protected'
        14 = 'no-manifest'; 15 = 'device-mismatch'; 16 = 'unsafe-environment'; 17 = 'no-config'
        1223 = 'the administrator prompt was declined'
        64 = 'bad command line'; 69 = 'not available in this build'; 77 = 'refused'
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

    if ($Run.LastCopied.Count -eq 0)
    {
        Write-Line -Run $Run -Text '  That run wrote no evidence file of its own.'
        return $null
    }

    $path = $Run.LastCopied[$Run.LastCopied.Count - 1]
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

    $evidence = Join-Path $Run.Folder ($Label + '.json')
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

    $evidence = Join-Path $Run.Folder ($Label + '.json')
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

    $evidence = Join-Path $Run.Folder ($Label + '.json')
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

    $evidence = Join-Path $Run.Folder ($Label + '.json')
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

    $evidence = Join-Path $Run.Folder ($Label + '.json')
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
            $states.Names += ($flow + ' ' + $state + ' ' + (Get-Field -Object $endpoint -Name 'friendlyName'))
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
        Write-Failure -Run $Run -Message ($Path + ' could not be read: ' + ($_ | Out-String).Trim())
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
    if (-not (Test-Path -LiteralPath $Run.LogFolder)) { return $found }
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

    return $found
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
function Add-Finding
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value,
        [string]$Detail = ''
    )

    [void]$Run.Findings.Add([ordered]@{ name = $Name; value = $Value; detail = $Detail; utc = (Get-UtcNowText) })
    $text = 'FINDING ' + $Name + ' = ' + $Value
    if (-not [string]::IsNullOrEmpty($Detail)) { $text = $text + ' (' + $Detail + ')' }
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
    if (-not [string]::IsNullOrEmpty($ExtraArguments)) { $command = $command + ' ' + $ExtraArguments }
    $file = Join-Path $Run.Folder 'resume.txt'
    Set-Content -LiteralPath $file -Value $command -Encoding UTF8
    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text 'AFTER YOU HAVE LOGGED BACK IN, run exactly this, in a normal (not administrator) window:'
    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('  ' + $command)
    Write-Line -Run $Run -Text ''
    Write-Line -Run $Run -Text ('It is also saved in ' + $file + '.')
}

function Complete-LiveTestRun
{
    param([Parameter(Mandatory = $true)]$Run)

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
        Write-Line -Run $Run -Text (('  finding      {0} = {1}' -f $finding.name, $finding.value))
    }

    if ($Run.Errors.Count -gt 0)
    {
        Write-Line -Run $Run -Text ('  ' + $Run.Errors.Count + ' error(s) were recorded. They are in result.json.')
    }

    Write-Line -Run $Run -Text ('  OVERALL: ' + $overall.ToUpperInvariant())
    Write-Line -Run $Run -Text ('  Evidence: ' + $Run.Folder)
    return $overall
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
    Get-EarshotLogLines, Save-EarshotLog,
    Add-Criterion, Add-Finding, Write-Failure, Write-ResumeInstruction, Complete-LiveTestRun
