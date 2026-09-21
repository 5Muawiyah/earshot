<#
    Device-side stubs only. This is not a second fake machine: it imports
    tools\live-tests\selftest\Fakes.psm1 unchanged for the fake machine's own state and its
    command answers, the same module tools\live-tests\selftest\Run-OneHalf.ps1 uses for the
    fully-scripted self-test.

    What is different from Run-OneHalf.ps1: only Resolve-EarshotExe, Invoke-Earshot and
    Invoke-EarshotElevated are replaced here. Confirm-Step, Read-Answer, Read-Note, Wait-Owner
    and Read-Host are never touched by this file, because Run-GuiHalfAgainstFakes.ps1 sends every
    one of them through the real ReadHostShim.ps1, to a real owner (human or scripted through
    ChildRunner), exactly as Invoke-GuiHalf.ps1 does for a real run: this file with the unchanged
    Fakes.psm1.
#>

Microsoft.PowerShell.Core\Import-Module (Join-Path $PSScriptRoot '..\..\selftest\Fakes.psm1') -Force

# Reads the fake machine's answer for one command, writes its --out report and its app-evidence
# file exactly where the real Invoke-Earshot would, moves the fake world, and returns the same
# step shape the real one records. Mirrors selftest\Run-OneHalf.ps1's own Invoke-FakeEarshot,
# which is not exported by Fakes.psm1 (it is Run-OneHalf.ps1's own helper), so this is a second,
# independent implementation against the same exported functions, kept deliberately close to it.
function global:Invoke-EarshotTwFakeCommand
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command,
        [switch]$Live,
        [switch]$Elevated
    )

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

    $before = @{}
    if (Test-Path -LiteralPath $Run.AppLiveTest)
    {
        foreach ($existing in (Get-ChildItem -LiteralPath $Run.AppLiveTest -Filter '*.json' -File)) { $before[$existing.Name] = $true }
    }

    if ($null -ne $answer.Evidence)
    {
        New-Item -ItemType Directory -Force -Path $Run.AppLiveTest | Out-Null
        $name = Get-FakeEvidenceName -Label $Label
        Set-Content -LiteralPath (Join-Path $Run.AppLiveTest $name) -Value ($answer.Evidence | ConvertTo-Json -Depth 8) -Encoding UTF8
    }

    Update-FakeWorld -Command $Command -Evidence $answer.Evidence
    Copy-AppEvidence -Run $Run -Before $before
    [void]$Run.Steps.Add($step)

    $returned = [pscustomobject]$step
    Add-Member -InputObject $returned -MemberType NoteProperty -Name 'json' -Value $parsed
    return $returned
}

# A declined live step, in the exact shape the real Invoke-Earshot records when Confirm-Step
# returns false: ran false, no exit code, the fixed error text Complete-LiveTestRun and the
# result deriver both key off.
function global:Add-EarshotTwDeclinedStep
{
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Command,
        [switch]$Elevated
    )

    $Run.StepIndex = $Run.StepIndex + 1
    $step = [ordered]@{
        index        = $Run.StepIndex
        label        = $Label
        command      = ($Command -join ' ')
        commandText  = (Get-CommandText -ExePath $Run.ExePath -Command $Command)
        live         = $true
        elevated     = [bool]$Elevated
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

    [void]$Run.Steps.Add($step)
}

$global:EarshotTwDeviceStubText = @'
function Resolve-EarshotExe
{
    param([Parameter(Mandatory = $true)][string]$ExePath, [switch]$RequireRelease)
    return (Get-FakeContext).ExePath
}

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

    if ($Live)
    {
        if ([string]::IsNullOrEmpty($Consequence))
        {
            throw 'A live step must say what it does before it asks.'
        }

        $line = Get-CommandText -ExePath $Run.ExePath -Command $Command
        if (-not (Confirm-Step -Run $Run -Prompt $line -Consequence $Consequence))
        {
            Add-EarshotTwDeclinedStep -Run $Run -Label $Label -Command $Command
            return $null
        }
    }

    return (Invoke-EarshotTwFakeCommand -Run $Run -Label $Label -Command $Command -Live:$Live -Elevated:$false)
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

    $line = Get-CommandText -ExePath $Run.ExePath -Command $Command
    if (-not (Confirm-Step -Run $Run -Prompt $line -Consequence $Consequence))
    {
        Add-EarshotTwDeclinedStep -Run $Run -Label $Label -Command $Command -Elevated
        return $null
    }

    return (Invoke-EarshotTwFakeCommand -Run $Run -Label $Label -Command $Command -Live -Elevated)
}
'@

# Puts the three stubs in the global scope, where a shipped script's own calls find them, and
# inside LiveTest.psm1's session state, where the module's own helpers (Get-TaskState and the
# rest calling Invoke-Earshot internally) find them. The same two-session-state technique as
# selftest\Run-OneHalf.ps1's Install-EarshotStubs, applied to a smaller set of names.
function global:Install-EarshotTwDeviceStubs
{
    . ([scriptblock]::Create(($global:EarshotTwDeviceStubText -replace '(?m)^function ', 'function global:')))
    $module = Get-Module 'LiveTest'
    if ($null -eq $module)
    {
        throw 'LiveTest.psm1 was imported but Get-Module did not find it.'
    }

    & $module ([scriptblock]::Create(($global:EarshotTwDeviceStubText -replace '(?m)^function ', 'function script:')))
}
