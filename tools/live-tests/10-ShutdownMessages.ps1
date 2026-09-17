<#
.SYNOPSIS
    Which end-session messages arrive, for each way of restarting.

.DESCRIPTION
    Windows does not treat every restart the same. A Start menu restart sends
    WM_QUERYENDSESSION and waits; shutdown /r /f skips the query; a Windows Update
    restart is different again. Earshot's session-end block is a backstop, started
    without waiting, and whether it ever completes depends on which of these happened.

    Run this once per variant. Each run notes the time, tells you exactly how to
    restart, and reads the log afterwards for the query, the end and whether a block
    was queued and finished.

    Variants:
      1  Start menu, Restart
      2  shutdown /r /t 0
      3  shutdown /r /f
      4  a Windows Update restart, whenever one is next offered
      5  sign out, then sign back in

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Variant
    Which restart to use, 1 to 5.

.PARAMETER Resume
    Read the log after the restart.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\10-ShutdownMessages.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe" -Variant 1
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [ValidateRange(1, 5)][int]$Variant = 1,
    [switch]$Resume
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

if ($Resume -and [string]::IsNullOrEmpty($RunRoot))
{
    throw 'The second half needs -RunRoot, the folder the first half printed. It is in resume.txt in that folder.'
}

$variants = @{
    1 = 'Start menu, Power, Restart'
    2 = 'type: shutdown /r /t 0'
    3 = 'type: shutdown /r /f'
    4 = 'a Windows Update restart, from Settings, Windows Update, Restart now'
    5 = 'sign out from the Start menu, then sign back in'
}

$run = New-LiveTestRun -TestId ('10-shutdown-messages-v' + $Variant) -Title ('End-session messages, variant ' + $Variant) `
    -Settles 'Which end-session messages Earshot actually receives for each kind of restart, and whether the block it queues at that moment ever finishes.' `
    -ExePath $ExePath -RunRoot $RunRoot

try
{
    Add-Finding -Run $run -Name 'variant' -Value ($Variant.ToString() + ': ' + $variants[$Variant])

    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot is installed, set up and running in the tray.',
            'Block at boot is on.',
            'The AirPods are connected to this PC, so the session-end backstop has something to do.'
        ) -PhysicalActions @(
            ('Restart the machine this way: ' + $variants[$Variant]),
            'This script never restarts anything. You do it.',
            'After logging back in, run the command this half prints.'
        )

        if ($ready)
        {
            Write-Section -Run $run -Title 'Before the restart'
            $audio = Get-AudioState -Run $run -Label 'audio-before'
            $states = Get-TargetEndpointStates -AudioJson $audio
            $nodes = Get-NodeState -Run $run -Label 'nodes-before'
            Write-Line -Run $run -Text ('Render ' + $states.Render + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState'))
            Add-Finding -Run $run -Name 'stateBeforeRestart' -Value ('render ' + $states.Render + ', nodes ' + (Get-Field -Object $nodes -Name 'nodeState'))
            Save-EarshotLog -Run $run -Label 'earshot-log-before'

            $marker = Get-UtcNowText
            Set-Content -LiteralPath (Join-Path $run.Folder 'restart-started-utc.txt') -Value $marker -Encoding UTF8
            Write-Line -Run $run -Text ('The log from ' + $marker + ' onwards is what the second half reads.')

            Write-Section -Run $run -Title 'Now restart'
            Write-Line -Run $run -Text ('Restart this way, yourself: ' + $variants[$Variant])
            Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath -ExtraArguments ('-Variant ' + $Variant)
        }
    }
    else
    {
        Write-Section -Run $run -Title 'What reached Earshot'
        $since = [datetime]::MinValue
        $markerFile = Join-Path $run.Folder 'restart-started-utc.txt'
        if (Test-Path -LiteralPath $markerFile)
        {
            $text = (Get-Content -LiteralPath $markerFile -Raw).Trim()
            try
            {
                $since = [datetime]::ParseExact($text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
                Write-Line -Run $run -Text ('Reading the log from ' + $text + ' onwards.')
            }
            catch
            {
                Write-Failure -Run $run -Message ('The marker time ' + $text + ' could not be read, so the whole log is searched: ' + ($_ | Out-String).Trim())
            }
        }

        $query = Get-EarshotLogLines -Run $run -Pattern 'WM_QUERYENDSESSION received' -SinceUtc $since
        $end = Get-EarshotLogLines -Run $run -Pattern 'WM_ENDSESSION received' -SinceUtc $since
        $queued = Get-EarshotLogLines -Run $run -Pattern 'Session ending: block queued at' -SinceUtc $since
        $noBlock = Get-EarshotLogLines -Run $run -Pattern 'Session ending: no block issued' -SinceUtc $since
        $stopped = Get-EarshotLogLines -Run $run -Pattern 'Tray stopped.' -SinceUtc $since

        foreach ($group in @(@('query', $query), @('end', $end), @('queued', $queued), @('no block', $noBlock), @('stopped', $stopped)))
        {
            Write-Line -Run $run -Text ('  ' + $group[0] + ': ' + $group[1].Count + ' line(s)')
            foreach ($line in $group[1]) { Write-Line -Run $run -Text ('    ' + $line) }
        }

        Add-Criterion -Run $run -Id 'query-arrived' -Criterion 'WM_QUERYENDSESSION reached Earshot and was logged with its flags.' `
            -Outcome $(if ($query.Count -gt 0) { 'pass' } else { 'fail' }) `
            -Detail ([string]$query.Count + ' line(s). A forced restart is allowed to skip the query; that is the finding, not a defect.')

        Add-Criterion -Run $run -Id 'end-arrived' -Criterion 'WM_ENDSESSION reached Earshot and was logged with its flags.' `
            -Outcome $(if ($end.Count -gt 0) { 'pass' } else { 'fail' }) -Detail ([string]$end.Count + ' line(s).')

        Add-Criterion -Run $run -Id 'block-queued' -Criterion 'A block was queued at session end, or the log says why not.' `
            -Outcome $(if ($queued.Count -gt 0 -or $noBlock.Count -gt 0) { 'pass' } else { 'inconclusive' }) `
            -Detail ([string]$queued.Count + ' queued, ' + $noBlock.Count + ' explained.')

        Add-Finding -Run $run -Name ('variant' + $Variant + 'Messages') `
            -Value ('query ' + $query.Count + ', end ' + $end.Count + ', queued ' + $queued.Count + ', no block ' + $noBlock.Count)

        $nodes = Get-NodeState -Run $run -Label 'nodes-after'
        $nodeState = Get-Field -Object $nodes -Name 'nodeState'
        Write-Line -Run $run -Text ('Nodes after the restart: ' + $nodeState)
        Add-Finding -Run $run -Name ('variant' + $Variant + 'NodesAfter') -Value $nodeState `
            -Detail 'Blocked means either the queued block finished or the boot task caught it'

        Save-EarshotLog -Run $run
        Write-Line -Run $run -Text ''
        Write-Line -Run $run -Text 'Run the other variants the same way: -Variant 2, 3, 4 and 5.'
    }
}
catch
{
    Write-Failure -Run $run -Message ('The test stopped with an error: ' + ($_ | Out-String).Trim())
    Add-Criterion -Run $run -Id 'run' -Criterion 'The test ran to the end.' -Outcome 'fail' -Detail 'See the error above.'
}
finally
{
    $overall = Complete-LiveTestRun -Run $run
    Write-Host ('Test 10 finished: ' + $overall)
}
