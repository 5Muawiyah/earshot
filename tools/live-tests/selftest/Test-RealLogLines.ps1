<#
.SYNOPSIS
    Reads an Earshot log the way a power loss leaves it, through the real Get-EarshotLogLines in a real PowerShell 5.1 process.

.DESCRIPTION
    The self-test's fake log never holds a NUL byte, so it cannot show what the reader does with the first line after a power
    loss. Windows commits a file's length before it commits its data, so a log cut by a power loss can hold a run of NUL bytes
    where the last lines were, and the next entry Earshot writes follows the run on the same line. The run seen on 25 October
    2026 was 4,153 NULs between 13 October 12:55:56 and 24 October 22:44:15.

    Get-EarshotLogLines decides whether a line is at or after the given time by reading the stamp at its start. A line led by
    NULs has no stamp at the start, so the old reader never looked at its time: it kept every such line whatever its time, and
    returned it with the NULs in front. This script writes a log with a NUL run before one old and one new entry (the case
    log-line-after-nul-run), asks the real function for the lines since a time between them, and checks that the old one is
    left out, the new one is kept, and the returned text has no NUL in it.

.PARAMETER Root
    Repository root, so LiveTest.psm1 can be found under tools\live-tests.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$result = [ordered]@{ ok = $false; problems = @(); checked = 0; cases = @() }
$workDir = $null
try
{
    Import-Module (Join-Path $Root 'tools\live-tests\LiveTest.psm1') -Force -DisableNameChecking

    function Test-One([string]$Name, [bool]$Condition, [string]$Detail)
    {
        $script:result.checked++
        if (-not $Condition) { $script:result.problems += ([string]$Name + ': ' + $Detail) }
    }

    $workDir = Join-Path $env:TEMP ('earshot-log-lines-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    $run = [ordered]@{ LogFolder = $workDir }
    $nul = [string][char]0

    # ---- log-line-after-nul-run ----
    # A normal line, then the run, then the first entry written after the power came back (old by the time asked for), then a
    # normal line that is new. The NUL run sits in front of the first entry on the same line, as it did on 24 October.
    $oldBeforeLoss = '2026-10-13T12:55:56.187Z INFO Tray started before the power loss.'
    $afterLossOld = [string]($nul * 4153) + '2026-10-13T12:56:10.000Z INFO Tray started the first time after the run.'
    $afterLossNew = [string]($nul * 7) + '2026-10-24T22:44:15.219Z INFO Tray started the second time after the run.'
    $plainNew = '2026-10-24T22:44:16.000Z INFO Tray started the third time.'
    $text = ($oldBeforeLoss, $afterLossOld, $afterLossNew, $plainNew) -join "`r`n"
    [System.IO.File]::WriteAllBytes((Join-Path $workDir 'earshot.log'), [System.Text.Encoding]::ASCII.GetBytes([string]$text + "`r`n"))

    $since = [datetime]::SpecifyKind([datetime]'2026-10-24T00:00:00', [System.DateTimeKind]::Utc)
    $lines = Get-EarshotLogLines -Run $run -Pattern 'Tray started' -SinceUtc $since
    $result.cases += 'log-line-after-nul-run'
    Test-One 'log-line-after-nul-run: the new lines are kept' (@($lines).Count -eq 2) ('expected 2 lines since 24 October, got ' + @($lines).Count + ': ' + (($lines | ForEach-Object { $_.TrimStart($nul).Substring(0, 24) }) -join ' | '))
    Test-One 'log-line-after-nul-run: an old line behind a NUL run is left out' (@(@($lines) -match 'first time after the run').Count -eq 0) 'the line stamped 13 October behind the run was kept'
    Test-One 'log-line-after-nul-run: the new line behind a NUL run is kept' (@(@($lines) -match 'second time after the run').Count -eq 1) 'the line stamped 24 October behind the run was dropped'
    foreach ($line in @($lines))
    {
        Test-One 'log-line-after-nul-run: no NUL is returned' ($line.IndexOf([char]0) -lt 0) 'a returned line still starts with NUL bytes'
        Test-One 'log-line-after-nul-run: a returned line starts with its stamp' ($line -match '^\d{4}-\d{2}-\d{2}T') ('a returned line does not start with a stamp: ' + $line.Substring(0, [Math]::Min(30, $line.Length)))
    }

    # No time asked for: every line with the text comes back, with the run taken off the front.
    $all = Get-EarshotLogLines -Run $run -Pattern 'Tray started'
    Test-One 'all lines are returned with no time asked for' (@($all).Count -eq 4) ('expected 4, got ' + @($all).Count)

    $result.ok = (@($result.problems).Count -eq 0)
}
catch
{
    $result.problems += ('threw: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
}
finally
{
    if ($workDir -and (Test-Path -LiteralPath $workDir)) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
}

$result | ConvertTo-Json -Depth 4
if ($result.ok) { exit 0 } else { exit 1 }
