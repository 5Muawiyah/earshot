<#
.SYNOPSIS
    Connect from every state.

.DESCRIPTION
    Earshot's Connect must reach the AirPods within 20 seconds from every ordinary state, with one
    retry, and a connection made in Windows' own settings while you are signed in must be left alone.
    This test starts from each state you can set up: the AirPods in use on your iPhone, the case
    opened next to this PC, this PC just woken from sleep, and (in the second half) this PC just
    restarted. In each it asks for three Connects from the Earshot card. Before every Connect it reads
    the state of the AirPods' entries in Windows (present or absent, disabled, flagged), and after it
    reads the line Earshot logs for the Connect: the path it took, the time to playing, or the outcome
    when it did not connect. In the first half it also asks for three connects through Windows' own
    Connect, and checks each is still there two minutes later.

    A state you cannot set up reads "not reached", never a pass. 20 seconds and 2 minutes are the
    figures this release was built to; they are not measured limits of Windows.

    It settles whether Earshot connects the AirPods within 20 seconds from each state, and whether a
    connection made through Windows is kept.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder. The second half needs the one the first half printed.

.PARAMETER Resume
    Run the second half, after the restart.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\23-ConnectEveryState.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
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

$run = New-LiveTestRun -TestId '23-connect-every-state' -Title 'Connect from every state' `
    -Settles 'Whether Earshot connects the AirPods within 20 seconds from each state, and whether a connection made through Windows is kept.' `
    -ExePath $ExePath -RunRoot $RunRoot

# The figures this release was built to: a Connect reaches playing within 20 s of being asked for, and a
# connection made through Windows is still there 2 minutes after it formed.
$BudgetMs = 20000
$KeepSeconds = 120

# Every leg ends with Earshot's own Disconnect, which blocks the nodes, so the closing step finds nothing to undo.
$atRestReason = ''

# The AirPods' own entries in Windows, from a read-only node read: the nodes state and how many entries are
# present, absent, disabled and flagged. "unreadable" when the read gave nothing, never a guess.
function Get-StartSummary
{
    param($Nodes)

    if ($null -eq $Nodes) { return 'unreadable' }
    $listed = Get-Field -Object $Nodes -Name 'nodes'
    $targets = @(@($listed) | Where-Object { $null -ne $_ -and (Get-Field -Object $_ -Name 'target') -eq $true })
    $present = @($targets | Where-Object { (Get-Field -Object $_ -Name 'present') -eq $true }).Count
    $disabled = @($targets | Where-Object { (Get-Field -Object $_ -Name 'problem') -eq 22 }).Count
    $flagged = @($targets | Where-Object { (Get-Field -Object $_ -Name 'configFlagsDisabled') -eq $true }).Count
    return ([string](Get-Field -Object $Nodes -Name 'nodeState') + ': ' + $present + ' present, ' + ($targets.Count - $present) + ' absent, ' +
        $disabled + ' disabled, ' + $flagged + ' flagged')
}

# The Connect line a click on the card left since the given moment, in the shape SwitchTimelineText writes.
# Exactly one line is a reading; none, two or one whose figures cannot be read are not, and say why.
function Get-ConnectAttempt
{
    param([Parameter(Mandatory = $true)][datetime]$Since)

    $n = '([0-9]+|-)'
    $all = Get-EarshotLogLines -Run $run -Pattern 'Switch to-pc: ' -SinceUtc $Since
    $lines = @(@($all) | Where-Object { $_ -match 'trigger click,' })
    if ($lines.Count -eq 0) { return [ordered]@{ Outcome = 'fail'; Ms = $null; Path = $null; Why = 'no Connect from the card was logged' } }
    if ($lines.Count -gt 1) { return [ordered]@{ Outcome = 'inconclusive'; Ms = $null; Path = $null; Why = ([string]$lines.Count + ' Connects from the card were logged, so it is not clear which was this one') } }

    $line = $lines[0]
    if ($line -cmatch ('Switch to-pc: active after ' + $n + ' ms \(trigger click, path ([a-z-]+),'))
    {
        # Both groups are read before any other match, which would replace $Matches.
        $figure = $Matches[1]
        $path = $Matches[2]
        if ($figure -cnotmatch '^[0-9]+$') { return [ordered]@{ Outcome = 'inconclusive'; Ms = $null; Path = $path; Why = 'the time to playing was not measured' } }
        $ms = [int]$figure
        if ($ms -le $BudgetMs) { return [ordered]@{ Outcome = 'pass'; Ms = $ms; Path = $path; Why = ('playing after ' + $ms + ' ms by the ' + $path + ' path') } }
        return [ordered]@{ Outcome = 'fail'; Ms = $ms; Path = $path; Why = ('playing only after ' + $ms + ' ms, more than ' + $BudgetMs + ' ms') }
    }

    if ($line -cmatch 'Switch to-pc: not active \(outcome ([A-Za-z]+), trigger click, path ([a-z-]+), blocked-again (yes|no|not-needed),')
    {
        return [ordered]@{ Outcome = 'fail'; Ms = $null; Path = $Matches[2]; Why = ('not connected: outcome ' + $Matches[1] + ', blocked again ' + $Matches[3]) }
    }

    return [ordered]@{ Outcome = 'inconclusive'; Ms = $null; Path = $null; Why = 'a Connect line whose figures could not be read' }
}

# One state: ask whether the owner can set it up; if so, three Connects from the card, each from that state.
# The criterion passes only when all three connected within the budget.
function Invoke-State
{
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$CriterionId,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Reached
    )

    Write-Section -Run $run -Title $Name
    $criterion = 'Three Connects from the card, ' + $Name.ToLowerInvariant() + ', each within 20 seconds.'
    if (-not $Reached)
    {
        Add-Finding -Run $run -Name ([string]$Id + 'Reached') -Value 'not reached'
        Add-Criterion -Run $run -Id $CriterionId -Criterion $criterion -Outcome 'inconclusive' -Detail 'not reached: you could not set this state up.'
        return
    }

    Add-Finding -Run $run -Name ([string]$Id + 'Reached') -Value 'reached'
    $outcomes = @()
    $details = @()
    for ($i = 1; $i -le 3; $i++)
    {
        Set-State -Id $Id
        $start = Get-StartSummary -Nodes (Get-NodeState -Run $run -Label ('nodes-' + $Id + '-' + $i))
        $since = (Get-Date).ToUniversalTime()
        Wait-Owner -Run $run -Text 'Left-click the Earshot icon, then Connect on the card. Wait until the card says the AirPods are connected or gives a reason, then come back here.'
        $attempt = Get-ConnectAttempt -Since $since
        $outcomes = $outcomes + @($attempt.Outcome)
        $details = $details + @('Connect ' + $i + ' from ' + $start + ': ' + $attempt.Why)
        Add-Finding -Run $run -Name ([string]$Id + 'Connect' + $i + 'Ms') -Value $attempt.Ms
        Add-Finding -Run $run -Name ([string]$Id + 'Connect' + $i + 'Start') -Value $start
    }

    $outcome = 'pass'
    if (@($outcomes | Where-Object { $_ -eq 'fail' }).Count -gt 0) { $outcome = 'fail' }
    elseif (@($outcomes | Where-Object { $_ -eq 'inconclusive' }).Count -gt 0) { $outcome = 'inconclusive' }
    Add-Criterion -Run $run -Id $CriterionId -Criterion $criterion -Outcome $outcome -Detail ([string]($details -join '; ') + '.')
}

# Puts the AirPods in the state before one Connect, in the owner's hands.
function Set-State
{
    param([Parameter(Mandatory = $true)][string]$Id)

    switch ($Id)
    {
        'iphone'
        {
            Wait-Owner -Run $run -Text 'Put the AirPods in your ears and play something on your iPhone, so your iPhone takes the AirPods from this PC. Then come back here.'
        }
        'case'
        {
            Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC: left-click the Earshot icon and choose Disconnect. Then come back here.'
            Wait-Owner -Run $run -Text 'Put both AirPods in the case and close the lid. Wait ten seconds, then open the lid next to this PC and leave it open. Then come back here.'
        }
        'wake'
        {
            Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC: left-click the Earshot icon and choose Disconnect. Then come back here.'
            Wait-Owner -Run $run -Text 'Put this computer to sleep from Start, Power, Sleep. Wake it, sign in, and come back here.'
        }
        'restart'
        {
            Wait-Owner -Run $run -Text 'Do not connect the AirPods yet. If they are connected to this PC, choose Disconnect on the Earshot card first. Then come back here.'
        }
        default { throw ('No set-up for the state ' + [string]$Id + '.') }
    }
}

try
{
    if (-not $Resume)
    {
        $ready = Show-Preconditions -Run $run -Preconditions @(
            'Earshot 1.5.0 or later is installed, set up, and running in the tray.',
            'Block at boot is on.',
            'The AirPods are paired with this PC and with your iPhone, and your iPhone is with you.',
            'Nothing is connected to this PC through Bluetooth audio except, later, the AirPods.'
        ) -PhysicalActions @(
            'Three times, connect the AirPods through Windows: Settings, Bluetooth and devices, Connect.',
            'For each state, set it up, then connect from the Earshot card, three times: the AirPods in use on your iPhone, the case opened next to this PC, this PC just woken from sleep.',
            'Restart this PC when asked, sign in, and run the command this half prints, for three Connects after the restart.'
        )

        if ($ready)
        {
            Add-Finding -Run $run -Name 'blockAtBootAtStart' -Value (Get-BlockAtBootSetting -Run $run)

            # ---- Windows' own Connect, three times ----
            Write-Section -Run $run -Title 'Connect through Windows'
            $kept = @()
            $keptDetails = @()
            for ($i = 1; $i -le 3; $i++)
            {
                Wait-Owner -Run $run -Text 'Open Settings, then Bluetooth and devices. Find your AirPods and choose Connect there, not in Earshot. When they are connected, come back here.'
                $formed = (Get-TargetEndpointStates -AudioJson (Get-AudioState -Run $run -Label ('audio-windows-' + $i + '-formed'))).Render
                $later = $null
                if ($formed -eq 'Active')
                {
                    Wait-Seconds -Run $run -Seconds $KeepSeconds -Reason 'checking the connection Windows made is still there two minutes later'
                    $later = (Get-TargetEndpointStates -AudioJson (Get-AudioState -Run $run -Label ('audio-windows-' + $i + '-later'))).Render
                }

                $state = $(if ($formed -ne 'Active') { 'inconclusive' } elseif ($later -eq 'Active') { 'pass' } else { 'fail' })
                $kept = $kept + @($state)
                $keptDetails = $keptDetails + @('Windows connect ' + $i + ': render ' + [string]$formed + $(if ($formed -eq 'Active') { ', then ' + $later + ' after ' + $KeepSeconds + ' s' } else { ', so it did not form' }))
                Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC: left-click the Earshot icon and choose Disconnect. Then come back here.'
            }

            $keptOutcome = 'pass'
            if (@($kept | Where-Object { $_ -eq 'fail' }).Count -gt 0) { $keptOutcome = 'fail' }
            elseif (@($kept | Where-Object { $_ -eq 'inconclusive' }).Count -gt 0) { $keptOutcome = 'inconclusive' }
            Add-Criterion -Run $run -Id 'windows-connects-kept' -Criterion 'Three connections made through Windows each stayed for two minutes.' `
                -Outcome $keptOutcome -Detail ([string]($keptDetails -join '; ') + '.')

            # ---- Earshot's Connect from each state ----
            $can = Read-Answer -Run $run -Question 'Can you set this up now: the AirPods in your ears and playing from your iPhone?' -Options @('yes', 'no')
            Invoke-State -Id 'iphone' -CriterionId 'connects-iphone' -Name 'In use on the iPhone' -Reached ($can -eq 'yes')
            $can = Read-Answer -Run $run -Question 'Can you set this up now: the AirPods in their case, opened next to this PC?' -Options @('yes', 'no')
            Invoke-State -Id 'case' -CriterionId 'connects-case' -Name 'Case opened by this PC' -Reached ($can -eq 'yes')
            $can = Read-Answer -Run $run -Question 'Can you set this up now: this PC put to sleep and woken three times?' -Options @('yes', 'no')
            Invoke-State -Id 'wake' -CriterionId 'connects-wake' -Name 'After this PC wakes' -Reached ($can -eq 'yes')

            Save-EarshotLog -Run $run

            # ---- the restart ----
            $restart = Read-Answer -Run $run -Question 'Do you want to restart now, for the three Connects after a restart?' -Options @('yes', 'no')
            if ($restart -eq 'yes')
            {
                Wait-Owner -Run $run -Text 'Disconnect the AirPods from this PC: left-click the Earshot icon and choose Disconnect. Then come back here.'
                Write-Section -Run $run -Title 'Now restart'
                Write-Line -Run $run -Text 'Restart from the Start menu. Sign in, then run the command below.'
                Add-Criterion -Run $run -Id 'restart-reached' -Criterion 'This PC was restarted for the three Connects after a restart.' `
                    -Outcome 'pass' -Detail 'You chose to restart; the three Connects after it are in the second half.'
                Write-ResumeInstruction -Run $run -ScriptPath $PSCommandPath
            }
            else
            {
                Add-Finding -Run $run -Name 'restartReached' -Value 'not reached'
                Add-Criterion -Run $run -Id 'restart-reached' -Criterion 'This PC was restarted for the three Connects after a restart.' `
                    -Outcome 'inconclusive' -Detail 'not reached: you chose not to restart.'
            }
        }
    }
    else
    {
        # The first Connect is the one straight after the restart; the next two start from Earshot's own
        # Disconnect, which leaves the nodes as a restart does.
        Add-Finding -Run $run -Name 'blockAtBootAfterRestart' -Value (Get-BlockAtBootSetting -Run $run)
        $can = Read-Answer -Run $run -Question 'Did you restart this PC and sign in before running this?' -Options @('yes', 'no')
        Invoke-State -Id 'restart' -CriterionId 'connects-restart' -Name 'After a restart' -Reached ($can -eq 'yes')
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
    Write-Host ('Test 23 finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive.
exit (Get-LiveTestExitCode -Overall $overall)
