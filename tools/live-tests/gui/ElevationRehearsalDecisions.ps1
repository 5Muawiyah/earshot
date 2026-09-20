<#
.SYNOPSIS
    Every part of Test-ElevatedLaunch.ps1 that does not itself raise a Windows administrator
    prompt: building its run context, deciding a criterion's outcome from a step
    Invoke-EarshotElevated recorded, and writing its own result.json. Kept in this separate file,
    dot-sourced by Test-ElevatedLaunch.ps1 and, independently, by its own tests, so "everything
    short of" the real elevated launch can be exercised against synthetic step data: nothing in
    this file starts a process, raises a prompt, or elevates.
#>

# A minimal run context built by hand, the same members Test-RealLauncher.ps1 builds for the
# unelevated launcher, plus Criteria/Findings/Answers so Confirm-Step, Add-Criterion, Add-Finding
# and Read-Answer (all real, all unfaked, called only from Test-ElevatedLaunch.ps1 itself) have
# what they read and write.
function New-ElevationRehearsalRun
{
    param([Parameter(Mandatory = $true)][string]$Folder)

    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    $run = [ordered]@{
        TestId         = 'elevated-launch-rehearsal'
        Title          = 'Administrator prompt check'
        Settles        = 'Whether this window''s one elevated launch site can raise the Windows permission box and read the answer, before test 15, 00''s uninstall variant or 07''s plan B ever depend on it.'
        ExePath        = (Join-Path $env:SystemRoot 'System32\cmd.exe')
        Folder         = $Folder
        SummaryPath    = (Join-Path $Folder 'summary.txt')
        AppLiveTest    = (Join-Path $Folder 'app-evidence-source')
        AppEvidence    = (Join-Path $Folder 'app-evidence')
        StepIndex      = 0
        Steps          = (New-Object System.Collections.ArrayList)
        Criteria       = (New-Object System.Collections.ArrayList)
        Findings       = (New-Object System.Collections.ArrayList)
        Answers        = (New-Object System.Collections.ArrayList)
        Errors         = (New-Object System.Collections.ArrayList)
        CopiedEvidence = (New-Object System.Collections.ArrayList)
        LastCopied     = @()
        StartedUtc     = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    }
    Set-Content -LiteralPath $run.SummaryPath -Value '' -Encoding UTF8
    return $run
}

function Get-LastStep
{
    param([Parameter(Mandatory = $true)]$Run)
    if ($Run.Steps.Count -eq 0) { return $null }
    return $Run.Steps[$Run.Steps.Count - 1]
}

# design.md note H2: Invoke-EarshotElevated never reads a process Handle for this launch site
# (unlike the unelevated one Invoke-Earshot uses), so PowerShell 5.1 may never fill in ExitCode
# for a -Verb RunAs process. $ReturnValue is $null for both a decline and an unreadable exit
# code, so $LastStep (the step Invoke-EarshotElevated itself appended to $Run.Steps) is what
# tells them apart: ran false is a decline; ran true with no exitCode is H2's own case, reported
# inconclusive, never guessed as a pass or scored as a fail.
function Get-ApprovedExitCodeOutcome
{
    param($ReturnValue, $LastStep)

    if ($null -ne $ReturnValue -and $ReturnValue.exitCode -eq 7)
    {
        return [ordered]@{ outcome = 'pass'; detail = 'exitCode read back: 7.' }
    }

    if ($null -ne $LastStep -and $LastStep.ran -eq $true -and $null -eq $LastStep.exitCode)
    {
        return [ordered]@{
            outcome = 'inconclusive'
            detail  = 'The elevated process started, but its exit code could not be read (design.md note H2). Neither a pass nor a fail.'
        }
    }

    $detail = 'no step was recorded at all.'
    if ($null -ne $LastStep -and -not [string]::IsNullOrEmpty($LastStep.error)) { $detail = $LastStep.error }
    return [ordered]@{ outcome = 'fail'; detail = ('Round 1 needed Yes on the Windows box: ' + $detail) }
}

# section 10.3: a declined prompt's own shape is "a step with elevated true, ran false and an
# error", and the call itself returns nothing.
function Get-DeclinedRecordedOutcome
{
    param($ReturnValue, $LastStep)

    $shapeOk = ($null -eq $ReturnValue) -and ($null -ne $LastStep) -and ($LastStep.ran -eq $false) -and
        (-not [string]::IsNullOrEmpty($LastStep.error))

    if ($shapeOk)
    {
        return [ordered]@{
            outcome = 'pass'
            detail  = 'The declined step recorded ran=false with an error, and the call returned nothing, as section 10.3 describes.'
        }
    }

    return [ordered]@{
        outcome = 'fail'
        detail  = 'Round 2 needed No on the Windows box; the declined shape was not what section 10.3 describes.'
    }
}

# The same shape and recompute rule Complete-LiveTestRun uses (none gives inconclusive; any fail
# gives fail; else any inconclusive gives inconclusive; else pass), written by hand here because
# this check does not call Complete-LiveTestRun (section 10.2: its own closing check would run
# device probes against cmd.exe).
function Write-ElevationRehearsalResult
{
    param([Parameter(Mandatory = $true)]$Run)

    $outcomes = @($Run.Criteria | ForEach-Object { $_.outcome })
    $overall = 'inconclusive'
    if ($outcomes.Count -eq 0) { $overall = 'inconclusive' }
    elseif ($outcomes -contains 'fail') { $overall = 'fail' }
    elseif ($outcomes -contains 'inconclusive') { $overall = 'inconclusive' }
    else { $overall = 'pass' }

    $result = [ordered]@{
        test        = $Run.TestId
        title       = $Run.Title
        settles     = $Run.Settles
        exe         = $Run.ExePath
        startedUtc  = $Run.StartedUtc
        finishedUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
        overall     = $overall
        criteria    = @($Run.Criteria)
        findings    = @($Run.Findings)
        answers     = @($Run.Answers)
        steps       = @($Run.Steps)
        errors      = @($Run.Errors)
        appEvidence = @($Run.CopiedEvidence)
        atRest      = $null
        folder      = $Run.Folder
    }

    ($result | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $Run.Folder 'result.json') -Encoding UTF8
    return $overall
}
