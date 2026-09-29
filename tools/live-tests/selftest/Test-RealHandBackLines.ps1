<#
.SYNOPSIS
    Runs the real Hand-back log lines Earshot.Tests generates from the real HandBackText formatter
    through the same Get-EarshotLogLines reads and -match checks that 17-HandBackOnShutdown.ps1 and
    18-HandBackOnSleep.ps1 use to decide their criteria, in a real PowerShell 5.1 process.

    tools\live-tests\selftest\Fakes.psm1 hand-writes the Hand-back lines the self-test's fake log
    holds, so the self-test alone cannot show whether those hand-written lines actually match what
    HandBackText produces: a typo in the fixture would still let every self-test case for 17 and 18
    pass. This script is the other half of "keep one execution of the real boundary":
    Earshot.Tests.LiveTests.HandBackFormatterAgainstRealParserTests calls the real, unfaked
    HandBackText to build every line shape 17 and 18 read, writes them to a real log file, and this
    script reads them back with the real Get-EarshotLogLines and reruns the same -match checks the
    two scripts run, in this same 5.1 host. A line the real formatter writes that the real patterns
    do not recognise is a criterion that would pass vacuously (never see a line) or read the wrong
    thing on the owner's machine, so it is a problem here, not a pass.

.PARAMETER Root
    Repository root, so LiveTest.psm1 can be imported from tools\live-tests.

.PARAMETER LinesFile
    Path to a JSON file holding an array of full, real log-line strings (timestamp, level and the
    real HandBackText message already joined, one per shape 17 and 18 read).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$LinesFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$result = [ordered]@{ ok = $false; problems = @(); checked = 0 }
$workDir = $null
try
{
    Import-Module (Join-Path $Root 'tools\live-tests\LiveTest.psm1') -Force -DisableNameChecking

    if (-not (Test-Path -LiteralPath $LinesFile))
    {
        $result.problems += ('LinesFile not found: ' + $LinesFile)
        $result | ConvertTo-Json -Depth 6
        exit 1
    }

    # Windows PowerShell 5.1 hands a top-level JSON array back as ONE object: @(... | ConvertFrom-Json) is then a
    # list of one, and Set-Content below wrote every line joined by spaces as a single log line, so each pattern
    # was found "once" in a line that held them all. Piping the parsed value through ForEach-Object unrolls it.
    $parsed = Get-Content -LiteralPath $LinesFile -Raw | ConvertFrom-Json
    $lines = @($parsed | ForEach-Object { $_ })
    if (@($lines).Count -eq 0)
    {
        $result.problems += 'LinesFile held no lines.'
        $result | ConvertTo-Json -Depth 6
        exit 1
    }

    $workDir = Join-Path $env:TEMP ('earshot-handback-lines-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    Set-Content -LiteralPath (Join-Path $workDir 'earshot.log') -Value $lines -Encoding UTF8
    $run = [ordered]@{ LogFolder = $workDir }
    $since = [datetime]::MinValue

    function Test-One([string]$Name, [bool]$Condition, [string]$Detail)
    {
        $script:result.checked++
        if (-not $Condition)
        {
            $script:result.problems += ([string]$Name + ': ' + $Detail)
        }
    }

    # The log holds one line for each line given: a run that collapsed them into one would find every pattern in it.
    $writtenBack = Get-Content -LiteralPath (Join-Path $workDir 'earshot.log')
    Test-One 'one log line for each line given' (@($writtenBack).Count -eq @($lines).Count -and @($lines).Count -gt 1) ('wrote ' + @($writtenBack).Count + ' line(s) for ' + @($lines).Count + ' given')

    # ---- 17-HandBackOnShutdown.ps1's patterns, copied from that script's resume half ----
    $started = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): started at' -SinceUtc $since
    Test-One 'shutdown started' (@($started).Count -ge 1) ('found ' + @($started).Count)

    $disconnectLinesRead = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): disconnect' -SinceUtc $since
    $disconnectLines = @($disconnectLinesRead)
    $confirmedSeen = @($disconnectLines | Where-Object { $_ -match 'confirmed after (\d+) ms' })
    Test-One 'shutdown disconnect confirmed after Ms' ($confirmedSeen.Count -ge 1) 'no "confirmed after N ms" line matched'
    $notConfirmedSeen = @($disconnectLines | Where-Object { $_ -match 'not confirmed within (\d+) ms' })
    Test-One 'shutdown disconnect not confirmed within Ms' ($notConfirmedSeen.Count -ge 1) 'no "not confirmed within N ms" line matched'

    $blockSent = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): block sent at' -SinceUtc $since
    Test-One 'shutdown block sent at' (@($blockSent).Count -ge 1) ('found ' + @($blockSent).Count)
    Test-One 'shutdown block sent timestamp readable' ((@($blockSent)[0]) -match '(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)') 'the block-sent timestamp regex did not match'

    $finished = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): finished in' -SinceUtc $since
    Test-One 'shutdown finished in' (@($finished).Count -ge 1) ('found ' + @($finished).Count)
    Test-One 'shutdown finished Ms readable' ((@($finished)[0]) -match 'finished in (\d+) ms') 'the finished-in-N-ms regex did not match'

    $cutShort = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): cut short at' -SinceUtc $since
    Test-One 'shutdown cut short at' (@($cutShort).Count -ge 1) ('found ' + @($cutShort).Count)
    Test-One 'shutdown cut short still-running readable' ((@($cutShort)[0]) -match 'still running:\s*([^;]*)') 'the still-running regex did not match'
    Test-One 'shutdown cut short block-sent-at readable' ((@($cutShort)[0]) -match 'block was sent at (\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)') 'the cut-short block-sent-at regex did not match'

    # ---- 18-HandBackOnSleep.ps1's patterns ----
    $sStarted = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): started at' -SinceUtc $since
    Test-One 'sleep started' (@($sStarted).Count -ge 1) ('found ' + @($sStarted).Count)

    $sDisconnectLinesRead = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): disconnect' -SinceUtc $since
    $sDisconnectLines = @($sDisconnectLinesRead)
    Test-One 'sleep disconnect confirmed after Ms' ((@($sDisconnectLines | Where-Object { $_ -match 'confirmed after (\d+) ms' })).Count -ge 1) 'no "confirmed after N ms" line matched'
    Test-One 'sleep disconnect not confirmed within Ms' ((@($sDisconnectLines | Where-Object { $_ -match 'not confirmed within (\d+) ms' })).Count -ge 1) 'no "not confirmed within N ms" line matched'

    $sBlockSent = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): block sent at' -SinceUtc $since
    Test-One 'sleep block sent at' (@($sBlockSent).Count -ge 1) ('found ' + @($sBlockSent).Count)

    $sFinished = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): finished in' -SinceUtc $since
    Test-One 'sleep finished in' (@($sFinished).Count -ge 1) ('found ' + @($sFinished).Count)

    $sCutShortRead = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (sleep): cut short at' -SinceUtc $since
    $sCutShort = @($sCutShortRead)
    Test-One 'sleep cut short at' ($sCutShort.Count -ge 1) ('found ' + $sCutShort.Count)
    Test-One 'sleep cut short block-was-not-sent readable' ((@($sCutShort | Where-Object { $_ -match 'block was not sent' })).Count -ge 1) 'the "block was not sent" phrase did not match any cut-short line'

    $suspendLogged = Get-EarshotLogLines -Run $run -Pattern 'WM_POWERBROADCAST received: Suspend' -SinceUtc $since
    Test-One 'suspend logged' (@($suspendLogged).Count -ge 1) ('found ' + @($suspendLogged).Count)

    $resumeAutomatic = Get-EarshotLogLines -Run $run -Pattern 'WM_POWERBROADCAST received: ResumeAutomatic' -SinceUtc $since
    Test-One 'resume automatic logged' (@($resumeAutomatic).Count -ge 1) ('found ' + @($resumeAutomatic).Count)

    $resumeCheck = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (resume):' -SinceUtc $since
    Test-One 'resume check logged' (@($resumeCheck).Count -ge 1) ('found ' + @($resumeCheck).Count)

    # ---- 20-HandBackOnExit.ps1's patterns ----
    $xStarted = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): started at' -SinceUtc $since
    Test-One 'exit started' (@($xStarted).Count -ge 1) ('found ' + @($xStarted).Count)
    Test-One 'exit started carries two stamps' (@([regex]::Matches([string]@($xStarted)[0], '\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z')).Count -ge 2) 'the started line does not hold its own stamp after the log stamp'

    $xDisconnectRead = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): disconnect' -SinceUtc $since
    $xDisconnect = @($xDisconnectRead)
    Test-One 'exit disconnect confirmed after Ms' ((@($xDisconnect | Where-Object { $_ -match 'confirmed after (\d+) ms' })).Count -ge 1) 'no "confirmed after N ms" line matched'

    $xBlockSent = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): block sent at' -SinceUtc $since
    Test-One 'exit block sent at' (@($xBlockSent).Count -ge 1) ('found ' + @($xBlockSent).Count)
    Test-One 'exit block sent carries two stamps' (@([regex]::Matches([string]@($xBlockSent)[0], '\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z')).Count -ge 2) 'the block-sent line does not hold its own stamp after the log stamp'

    $xFinished = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): finished in' -SinceUtc $since
    Test-One 'exit finished in' (@($xFinished).Count -ge 1) ('found ' + @($xFinished).Count)
    Test-One 'exit finished Ms readable' ((@($xFinished)[0]) -match 'finished in (\d+) ms') 'the finished-in-N-ms regex did not match'

    $xCutShort = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): cut short at' -SinceUtc $since
    Test-One 'exit cut short at' (@($xCutShort).Count -ge 1) ('found ' + @($xCutShort).Count)
    Test-One 'exit cut short still-running readable' ((@($xCutShort)[0]) -match 'still running:\s*([^;]*)') 'the still-running regex did not match'

    $xSays = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (exit): Exit will say' -SinceUtc $since
    Test-One 'exit will say' (@($xSays).Count -ge 1) ('found ' + @($xSays).Count)

    $xPaused = Get-EarshotLogLines -Run $run -Pattern 'before Earshot lets the AirPods go (hand-back on Exit), paused' -SinceUtc $since
    Test-One 'pause before the exit hand-back' (@($xPaused).Count -ge 1) ('found ' + @($xPaused).Count)

    # ---- 21-PauseOnLeave.ps1's patterns ----
    $ownPaused = Get-EarshotLogLines -Run $run -Pattern 'before Earshot lets the AirPods go (Disconnect), paused' -SinceUtc $since
    Test-One 'pause before Earshot''s own disconnect' (@($ownPaused).Count -ge 1) ('found ' + @($ownPaused).Count)
    Test-One 'pause before Earshot''s own disconnect Ms readable' ((@($ownPaused)[0]) -match 'paused \S+ in (\d+) ms') 'the paused-in-N-ms regex did not match'

    $leftRead = Get-EarshotLogLines -Run $run -Pattern 'Pause on leave: the AirPods left this PC' -SinceUtc $since
    $left = @($leftRead)
    $leftPaused = @($left | Where-Object { $_ -cmatch '\. Paused ' })
    $leftNotPaused = @($left | Where-Object { $_ -cmatch 'Not paused:' })
    Test-One 'a leave that paused is told from the rest' ($leftPaused.Count -eq 1) ('found ' + $leftPaused.Count + ' of ' + $left.Count)
    $notPausedLookingPaused = @($leftNotPaused | Where-Object { $_ -cmatch '\. Paused ' })
    Test-One 'a leave that did not pause is not counted as one that did' (($leftNotPaused.Count -eq 1) -and ($notPausedLookingPaused.Count -eq 0)) ('found ' + $leftNotPaused.Count + ' not paused, ' + $notPausedLookingPaused.Count + ' of them shaped like a pause: ' + ($left -join ' || '))
    Test-One 'phone pause Ms after the change readable' ($leftPaused.Count -ge 1 -and $leftPaused[0] -match 'Paused \S+ (\d+) ms after the change was seen') 'the after-the-change regex did not match'
    Test-One 'phone pause reading age readable' ($leftPaused.Count -ge 1 -and $leftPaused[0] -match 'at the last reading, (\d+) ms before') 'the reading-age regex did not match'

    $notPlaying = Get-EarshotLogLines -Run $run -Pattern 'Not paused: this PC was not playing to them' -SinceUtc $since
    Test-One 'not paused because this PC was not playing' (@($notPlaying).Count -ge 1) ('found ' + @($notPlaying).Count)
    Test-One 'not paused reason readable' ((@($notPlaying)[0]) -match 'Not paused: (.+)$') 'the reason regex did not match'

    $result.ok = (@($result.problems).Count -eq 0)
}
catch
{
    $result.problems += ('The real-lines check threw: ' + ($_ | Out-String).Trim())
    $result.ok = $false
}
finally
{
    if ($workDir -and (Test-Path -LiteralPath $workDir)) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
}

$result | ConvertTo-Json -Depth 6
if ($result.ok) { exit 0 } else { exit 1 }
