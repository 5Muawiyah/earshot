<#
.SYNOPSIS
    Hand back on shut down, then the next boot.

.DESCRIPTION
    Earshot now disconnects and blocks inside the budget Windows gives it at shut down (the
    WM_ENDSESSION hold), in a fixed order: release the phone's link, disconnect, confirm render
    left ACTIVE, then block. This test shuts down while connected, the same setup as test 09, and
    reads whether the hand-back actually ran in time, and whether the next boot leaves the AirPods
    alone.

    It settles whether Earshot hands the AirPods back inside the time Windows gives it at shut
    down, and whether the next boot leaves them alone.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the power cycle.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\17-HandBackOnShutdown.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [switch]$Resume
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

if ($Resume -and [string]::IsNullOrEmpty($RunRoot))
{
    throw 'The second half needs -RunRoot, the folder the first half printed. It is in resume.txt in that folder.'
}

$run = New-LiveTestRun -TestId '17-handback-on-shutdown' -Title 'Hand back on shut down, then the next boot' `
    -Settles 'Whether Earshot hands the AirPods back inside the time Windows gives it at shut down, and whether the next boot leaves them alone.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Set below, in the first half only: this test shuts down deliberately with the AirPods connected,
# so the closing at-rest check must not offer to block them before that shutdown happens.
$atRestReason = ''

# The file the first half writes the shutdown's own start time to, so the second half can read the
# log and the event log only from that point on, never from an earlier run's lines.
$startedFile = ''
if (-not [string]::IsNullOrEmpty($RunRoot)) { $startedFile = Join-Path $run.Folder 'shutdown-start.txt' }

# Parses "<label> <number> ms" out of a log line. $null when the line is empty or the figure is
# not there to read, never a guessed 0.
function Get-MillisecondsFigure
{
    param([string]$Line, [string]$Pattern)

    if ([string]::IsNullOrEmpty($Line)) { return $null }
    if ($Line -match $Pattern) { return [int]$Matches[1] }
    return $null
}

# The newest *-gate-block.json Earshot itself wrote at or after SinceUtc, or $null when there is
# none. Earshot's own evidence files are named <UTC stamp>-<label>.json (DiagContext.NewEvidenceFile),
# so the newest one at or after the hand-back started is found by its own startedUtc field, read
# from the file, never assumed from the file name alone.
function Get-NewestGateBlockEvidence
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][datetime]$SinceUtc
    )

    if (-not (Test-Path -LiteralPath $Run.AppLiveTest)) { return $null }
    $newest = $null
    $newestStarted = [datetime]::MinValue
    foreach ($file in (Get-ChildItem -LiteralPath $Run.AppLiveTest -Filter '*-gate-block.json' -File))
    {
        try
        {
            $evidence = (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
            $text = [string](Get-Field -Object $evidence -Name 'startedUtc')
            if ([string]::IsNullOrEmpty($text)) { continue }
            $started = [datetime]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
            if ($started -ge $SinceUtc -and $started -ge $newestStarted)
            {
                $newest = $evidence
                $newestStarted = $started
            }
        }
        catch
        {
            continue
        }
    }

    return $newest
}

# The newest test 09 result.json's overall outcome, from any earlier sitting, or 'not-run'. Test 09
# is a precondition recorded as a finding here, not one that refuses: the owner may have run it in
# an earlier session, under a different RunRoot than this one.
function Get-Test09Result
{
    $paths = Get-EarshotDataPaths
    if (-not (Test-Path -LiteralPath $paths.LiveTestFolder)) { return 'not-run' }
    $newestPath = $null
    $newestWrite = [datetime]::MinValue
    foreach ($folder in (Get-ChildItem -LiteralPath $paths.LiveTestFolder -Directory -ErrorAction SilentlyContinue))
    {
        $candidate = Join-Path $folder.FullName '09-shutdown-while-connected\result.json'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $candidate
        if ($item.LastWriteTimeUtc -ge $newestWrite)
        {
            $newestPath = $candidate
            $newestWrite = $item.LastWriteTimeUtc
        }
    }

    if ($null -eq $newestPath) { return 'not-run' }
    try
    {
        $result = (Get-Content -LiteralPath $newestPath -Raw | ConvertFrom-Json)
        $overall = [string](Get-Field -Object $result -Name 'overall')
        if ([string]::IsNullOrEmpty($overall)) { return 'not-run' }
        return $overall
    }
    catch
    {
        return 'not-run'
    }
}

try
{
    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed from a release built from the current head, set up, and running in the tray.',
            'Block at boot is on.',
            '"Hand back at shut down and sleep" is ticked in the menu.',
            'The AirPods are paired with this PC and available to connect.'
        ) -PhysicalActions @(
            'Connect the AirPods to this PC with a left click on the tray icon and keep audio playing from this PC.',
            'Shut down from the Start menu (not Restart), straight away, while they are still connected and playing.',
            'Listen: notice whether the AirPods go back to your phone as the screen goes dark, or just after.',
            'Wait about ten seconds with the machine off, start it again, sign in, and run the command this half prints.'
        )

        if ($ready)
        {
            # Only now, with the owner committed to going ahead: nothing has shut down yet if they
            # said no above, so the closing check must still offer a block in that case.
            $atRestReason = 'This half deliberately shuts down with the AirPods still connected (the nodes enabled): that ' +
                'is the case the hand-back exists to catch, in order, inside the budget Windows gives it.'

            Write-Section -Run $run -Title 'Connect and confirm'
            Wait-Owner -Run $run -Text 'Left-click the tray icon to connect the AirPods to this PC, and play something so they stay in use.'
            $audio = Get-AudioState -Run $run -Label 'audio-connected'
            $states = Get-TargetEndpointStates -AudioJson $audio
            $nodes = Get-NodeState -Run $run -Label 'nodes-connected'
            Write-Line -Run $run -Text ('Render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }) + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState'))

            Add-Criterion -Run $run -Id 'connected-first' -Criterion 'The AirPods are connected to this PC and the nodes are enabled before the shutdown.' `
                -Outcome $(if ($states.Render -eq 'Active' -and (Get-Field -Object $nodes -Name 'nodeState') -eq 'Allowed') { 'pass' } else { 'inconclusive' }) `
                -Detail ('render ' + $states.Render + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState') + '. This is the state the shutdown has to be started from.')

            Add-Finding -Run $run -Name 'blockAtBootAtShutdown' -Value (Get-BlockAtBootSetting -Run $run)
            Add-Finding -Run $run -Name 'fastStartupAtShutdown' -Value (Get-FastStartupSetting -Run $run) `
                -Detail 'HiberbootEnabled under HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power, read before the shutdown'

            $paths = Get-EarshotDataPaths
            $settings = Read-EarshotJsonFile -Run $run -Path $paths.SettingsFile
            $handBackSetting = Get-Field -Object $settings -Name 'HandBackOnShutdownAndSleep'
            Add-Finding -Run $run -Name 'handBackSettingAtShutdown' -Value $handBackSetting `
                -Detail 'HandBackOnShutdownAndSleep read from settings.json before the shutdown'

            $handBackTicked = Read-Answer -Run $run -Question 'Is "Hand back at shut down and sleep" ticked in the tray menu right now?'
            if ($handBackTicked -ne 'yes')
            {
                Write-Line -Run $run -Text 'The setting is not ticked: the hand-back this test is about will not run.'
            }

            Add-Finding -Run $run -Name 'test09Result' -Value (Get-Test09Result) -Detail 'the newest 09-ShutdownWhileConnected result.json overall, from any earlier sitting'

            $shutdownStartUtc = (Get-Date).ToUniversalTime()
            $startedFile = Join-Path $run.Folder 'shutdown-start.txt'
            Set-Content -LiteralPath $startedFile -Value $shutdownStartUtc.ToString('o') -Encoding UTF8
            Save-EarshotLog -Run $run

            Write-Section -Run $run -Title 'Now shut down, straight away'
            Write-Line -Run $run -Text 'Shut down from the Start menu while the audio is still playing on this PC. Do not restart,'
            Write-Line -Run $run -Text 'and do not disconnect first. The point is to catch the hand-back doing its job.'
            Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
        }
    }
    else
    {
        # Missing only when the first half never reached the point of shutting down (the owner said
        # no to "ready to start", so there is nothing to resume from): not a failure of this half,
        # so nothing here throws. Reading with no floor at all is the honest fallback: every hand-
        # back and end-session line the log or the event log holds is read, not none of them.
        if ([string]::IsNullOrEmpty($startedFile) -or -not (Test-Path -LiteralPath $startedFile -PathType Leaf))
        {
            Write-Line -Run $run -Text 'shutdown-start.txt was not found; the first half likely never reached the shutdown. Reading with no time floor.'
            $shutdownStartUtc = [datetime]::MinValue
        }
        else
        {
            $startedText = ([string](Get-Content -LiteralPath $startedFile -Raw)).Trim()
            $shutdownStartUtc = [datetime]::Parse($startedText, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        }

        Write-Section -Run $run -Title 'After the boot'
        $nodes = Get-NodeState -Run $run -Label 'nodes-after-boot'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        $audio = Get-AudioState -Run $run -Label 'audio-after-boot'
        $states = Get-TargetEndpointStates -AudioJson $audio
        Write-Line -Run $run -Text ('Nodes ' + $nodeState + ', render ' + $states.Render + ', capture ' + $(if ([string]::IsNullOrEmpty($states.Capture)) { 'none' } else { $states.Capture }))

        Add-Criterion -Run $run -Id 'nodes-after-boot' -Criterion 'The nodes are blocked again after the boot.' `
            -Outcome $(if ($nodeState -eq 'Blocked') { 'pass' } else { 'fail' }) `
            -Detail ('They read ' + $nodeState + '.')

        $paged = Read-Answer -Run $run -Question 'Did the AirPods connect to this PC by themselves after this boot?'
        Add-Criterion -Run $run -Id 'not-paged-at-boot' -Criterion 'Windows did not page the AirPods at the boot after this shutdown.' `
            -Outcome $(if ($paged -eq 'no' -and $states.Render -ne 'Active') { 'pass' } elseif ($paged -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $paged + '; the render endpoint reads ' + $states.Render + '.')

        $heard = Read-Answer -Run $run -Question 'Before the screen went dark, or just after, did the AirPods go back to your phone?'
        Add-Criterion -Run $run -Id 'heard-handed-back' -Criterion 'The AirPods went back to the phone at the moment of shutting down.' `
            -Outcome $(if ($heard -eq 'yes') { 'pass' } elseif ($heard -eq 'unsure') { 'inconclusive' } else { 'fail' }) `
            -Detail ('You answered ' + $heard + '.')

        Write-Section -Run $run -Title 'What the log says about the hand-back'
        $querySession = Get-EarshotLogLines -Run $run -Pattern 'WM_QUERYENDSESSION received' -SinceUtc $shutdownStartUtc
        $endSession = Get-EarshotLogLines -Run $run -Pattern 'WM_ENDSESSION received' -SinceUtc $shutdownStartUtc
        $started = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): started at' -SinceUtc $shutdownStartUtc
        $disconnect = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): disconnect' -SinceUtc $shutdownStartUtc
        $blockSent = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): block sent at' -SinceUtc $shutdownStartUtc
        $queued = Get-EarshotLogLines -Run $run -Pattern 'Session ending: block queued at' -SinceUtc $shutdownStartUtc
        $finished = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): finished in' -SinceUtc $shutdownStartUtc
        $cutShort = Get-EarshotLogLines -Run $run -Pattern 'Hand-back (shutdown): cut short at' -SinceUtc $shutdownStartUtc

        foreach ($line in (@($querySession) + @($endSession) | Select-Object -Last 4)) { Write-Line -Run $run -Text ('  ' + $line) }
        foreach ($line in (@($started) + @($disconnect) + @($blockSent) + @($queued) + @($finished) + @($cutShort))) { Write-Line -Run $run -Text ('  ' + $line) }

        Add-Criterion -Run $run -Id 'end-session-logged' -Criterion 'WM_QUERYENDSESSION or WM_ENDSESSION reached Earshot and was logged.' `
            -Outcome $(if (@($querySession).Count -gt 0 -or @($endSession).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($querySession).Count + ' query line(s), ' + @($endSession).Count + ' end line(s).')

        Add-Criterion -Run $run -Id 'handback-started' -Criterion 'The hand-back started exactly once.' `
            -Outcome $(if (@($started).Count -eq 1) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($started).Count + ' "started at" line(s).')

        $disconnectLine = $(if (@($disconnect).Count -gt 0) { @($disconnect)[-1] } else { $null })
        Add-Criterion -Run $run -Id 'handback-disconnect-confirmed' -Criterion 'The disconnect confirmed before the block was sent.' `
            -Outcome $(if ($null -eq $disconnectLine) { 'inconclusive' } elseif ($disconnectLine -match 'confirmed after') { 'pass' } elseif ($disconnectLine -match 'not confirmed') { 'fail' } else { 'inconclusive' }) `
            -Detail $(if ($null -eq $disconnectLine) { 'no disconnect line was logged' } else { $disconnectLine })

        Add-Criterion -Run $run -Id 'handback-block-sent' -Criterion 'The hand-back sent the block, or the query already had.' `
            -Outcome $(if (@($blockSent).Count -gt 0 -or @($queued).Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($blockSent).Count + ' "block sent at" line(s), ' + @($queued).Count + ' "block queued at" line(s).')

        $stillRunning = $null
        if (@($cutShort).Count -gt 0 -and (@($cutShort)[-1]) -match 'still running:\s*([^;]*)') { $stillRunning = $Matches[1].Trim() }
        Add-Criterion -Run $run -Id 'handback-finished' -Criterion 'The hand-back finished inside its own budget.' `
            -Outcome $(if (@($finished).Count -gt 0) { 'pass' } elseif (@($cutShort).Count -gt 0) { 'fail' } else { 'fail' }) `
            -Detail $(if (@($finished).Count -gt 0) { @($finished)[-1] } elseif (@($cutShort).Count -gt 0) { @($cutShort)[-1] } else { 'no "finished in" or "cut short" line was logged' })

        $events = Get-PowerEvents -Run $run -SinceUtc $shutdownStartUtc
        $shutdownEvents = @($events | Where-Object { $_.id -eq 1074 })
        $dirtyEvents = @($events | Where-Object { $_.id -eq 41 })
        Add-Criterion -Run $run -Id 'shutdown-was-clean' -Criterion 'The shutdown itself was clean: a 1074 event and no dirty-boot 41 event after it.' `
            -Outcome $(if (@($shutdownEvents).Count -gt 0 -and @($dirtyEvents).Count -eq 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]@($shutdownEvents).Count + ' shutdown-request event(s), ' + @($dirtyEvents).Count + ' dirty-boot event(s).')

        $bootEvent = @($events | Where-Object { $_.id -eq 27 } | Select-Object -Last 1)
        $bootType = $(if (@($bootEvent).Count -gt 0) { @($bootEvent)[0].message } else { $null })

        Add-Finding -Run $run -Name 'handBackFinishedMs' -Value (Get-MillisecondsFigure -Line $(if (@($finished).Count -gt 0) { @($finished)[-1] } else { $null }) -Pattern 'finished in (\d+) ms')
        Add-Finding -Run $run -Name 'handBackDisconnectMs' -Value (Get-MillisecondsFigure -Line $disconnectLine -Pattern '(?:confirmed after|not confirmed within) (\d+) ms')
        Add-Finding -Run $run -Name 'handBackBlockSentAfterMs' -Value $(
            if (@($blockSent).Count -gt 0 -and (@($blockSent)[-1]) -match '(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)')
            {
                $sentAt = [datetime]::Parse($Matches[1], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
                [int]([math]::Round((New-TimeSpan -Start $shutdownStartUtc -End $sentAt).TotalMilliseconds))
            }
            else { $null })
        Add-Finding -Run $run -Name 'handBackCutShortStillRunning' -Value $stillRunning
        Add-Finding -Run $run -Name 'endSessionFlags' -Value $(if (@($endSession).Count -gt 0 -and (@($endSession)[-1]) -match 'WM_ENDSESSION received:\s*(.+?)\s*\(lParam') { $Matches[1].Trim() } else { $null })
        Add-Finding -Run $run -Name 'queryToEndMs' -Value $(
            if (@($querySession).Count -gt 0 -and @($endSession).Count -gt 0 -and
                (@($querySession)[-1]) -match '^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})Z' )
            {
                $queryStamp = [datetime]::ParseExact($Matches[1], "yyyy-MM-dd'T'HH:mm:ss.fff", [System.Globalization.CultureInfo]::InvariantCulture)
                if ((@($endSession)[-1]) -match '^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})Z')
                {
                    $endStamp = [datetime]::ParseExact($Matches[1], "yyyy-MM-dd'T'HH:mm:ss.fff", [System.Globalization.CultureInfo]::InvariantCulture)
                    [int]([math]::Round((New-TimeSpan -Start $queryStamp -End $endStamp).TotalMilliseconds))
                }
                else { $null }
            }
            else { $null })
        Add-Finding -Run $run -Name 'bootType' -Value $bootType

        $gateEvidence = Get-NewestGateBlockEvidence -Run $run -SinceUtc $shutdownStartUtc
        $gateBlockRunMs = $null
        $vetoSeen = 'no-evidence'
        if ($null -ne $gateEvidence)
        {
            $gateBlockRunMs = Get-Field -Object $gateEvidence -Name 'runMilliseconds'
            $veto = $false
            $gateSteps = Get-Field -Object $gateEvidence -Name 'steps'
            foreach ($step in @($gateSteps))
            {
                if ((Get-Field -Object $step -Name 'codeName') -eq 'CR_REMOVE_VETOED') { $veto = $true }
            }

            $vetoSeen = $(if ($veto) { 'yes' } else { 'no' })
        }

        Add-Finding -Run $run -Name 'gateBlockRunMs' -Value $gateBlockRunMs -Detail 'from the newest *-gate-block.json whose startedUtc is at or after the shutdown'
        Add-Finding -Run $run -Name 'vetoSeen' -Value $vetoSeen -Detail 'CR_REMOVE_VETOED in that evidence''s steps'

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
    Write-Host ('Test 17 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
