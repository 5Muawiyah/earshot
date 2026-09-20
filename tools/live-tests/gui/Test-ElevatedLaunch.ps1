<#
.SYNOPSIS
    The administrator prompt check (test-gui.md section 10.2). Proves this window's one
    elevated launch site can raise the Windows permission box and read the answer, before
    anything real (test 15, 00's uninstall variant, 07's plan B) is ever allowed to depend on it.

.DESCRIPTION
    Raises a real Windows administrator prompt, twice. Only the owner runs this, from the
    window, as a named live check ("Administrator prompt check"): nothing automated, no test
    and no gate ever runs it, and this is that launch site's (Invoke-EarshotElevated in
    LiveTest.psm1) first execution in any form. It imports the real, unfaked LiveTest.psm1
    and builds a run context
    by hand (ElevationRehearsalDecisions.ps1), the same way tools\live-tests\selftest\
    Test-RealLauncher.ps1 does for the unelevated launcher, with ExePath set to cmd.exe so
    nothing about Earshot itself is touched.

    Round 1: choose Yes on the Windows box. cmd.exe /c exit 7 runs elevated and exits
    immediately. Criterion approved-exit-code passes only if the real exit code (7) is read
    back; note H2 (design.md): Invoke-EarshotElevated never reads a process Handle for this
    launch (unlike the unelevated one), so PowerShell 5.1 may not fill in ExitCode at all for a
    -Verb RunAs process. That is reported as inconclusive here, never guessed as a pass.

    Round 2: choose No. Criterion declined-recorded passes only if the call returned nothing and
    the step it recorded has ran false and an error.

    One real question: whether the Windows box came to the front on its own, recorded as the
    finding promptCameToFront, never scored.

    result.json is written here directly, not through Complete-LiveTestRun, whose own closing
    check would run device probes against cmd.exe; leftAtRest is recorded as not-applicable
    ("this check starts cmd.exe only"), the same finding name and vocabulary
    Complete-LiveTestRun itself uses.

.PARAMETER RunRoot
    The evidence folder. This check's own subfolder is <RunRoot>\elevated-launch-rehearsal\.
#>

#Requires -Version 5.1

[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RunRoot)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Microsoft.PowerShell.Core\Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'LiveTest.psm1') -Force
. (Join-Path $PSScriptRoot 'ElevationRehearsalDecisions.ps1')

$folder = Join-Path $RunRoot 'elevated-launch-rehearsal'
$run = New-ElevationRehearsalRun -Folder $folder

try
{
    Write-Line -Run $run -Text 'Round 1: choose Yes on the Windows permission box when it appears.'
    $approveConsequence = 'Starts an elevated cmd.exe that immediately exits with code 7. Changes nothing else on this machine.'
    $approvedStep = Invoke-EarshotElevated -Run $run -Label 'approve' -Command @('/c', 'exit', '7') -Consequence $approveConsequence
    $approveOutcome = Get-ApprovedExitCodeOutcome -ReturnValue $approvedStep -LastStep (Get-LastStep -Run $run)
    Add-Criterion -Run $run -Id 'approved-exit-code' `
        -Criterion 'Approving the Windows permission box runs the elevated command, and its real exit code (7) is read back.' `
        -Outcome $approveOutcome.outcome -Detail $approveOutcome.detail

    Write-Line -Run $run -Text ''
    Write-Line -Run $run -Text 'Round 2: choose No on the Windows permission box when it appears.'
    $declineConsequence = 'Starts an elevated cmd.exe that immediately exits with code 7. Changes nothing else on this machine.'
    $declinedStep = Invoke-EarshotElevated -Run $run -Label 'decline' -Command @('/c', 'exit', '7') -Consequence $declineConsequence
    $declineOutcome = Get-DeclinedRecordedOutcome -ReturnValue $declinedStep -LastStep (Get-LastStep -Run $run)
    Add-Criterion -Run $run -Id 'declined-recorded' `
        -Criterion 'Declining the Windows permission box records a step that did not run, with an error recorded, and the call returns nothing.' `
        -Outcome $declineOutcome.outcome -Detail $declineOutcome.detail

    $promptCameToFront = Read-Answer -Run $run -Question 'Did the Windows permission box appear in front of everything, without you looking for it?'
    Add-Finding -Run $run -Name 'promptCameToFront' -Value $promptCameToFront -Detail 'Not scored: section 10.1 names this unknown until observed.'
}
catch
{
    Write-Failure -Run $run -Message ('The administrator prompt check stopped with an error: ' + ($_ | Out-String).Trim())
    Add-Criterion -Run $run -Id 'run' -Criterion 'The check ran to the end.' -Outcome 'fail' -Detail 'See the error above.'
}
finally
{
    # section 10.2: "leftAtRest = not-applicable (this check starts cmd.exe only)". Added the
    # same way Complete-LiveTestRun records every other leftAtRest value: as a finding, never a
    # separate member, so ParsedResult.LeftAtRest reads it identically either way.
    Add-Finding -Run $run -Name 'leftAtRest' -Value 'not-applicable' -Detail 'This check starts cmd.exe only.'
    $overall = Write-ElevationRehearsalResult -Run $run
    Write-Host ('Administrator prompt check finished: ' + $overall)
}

# 0 pass, 1 fail, 2 inconclusive: the same three the window's own ChildMessageKind.Exit already
# reads for every other script (Get-LiveTestExitCode's own rule, applied by hand here since this
# check does not call Complete-LiveTestRun).
switch ($overall)
{
    'pass' { exit 0 }
    'fail' { exit 1 }
    default { exit 2 }
}
