<#
.SYNOPSIS
    Runs one half of one shipped live test script under the test window's protocol, against the
    fake device.

.DESCRIPTION
    The sandboxed twin of Invoke-GuiHalf.ps1. Everything about the prompt protocol is identical
    and real: ReadHostShim.ps1, the same Read-Host shadow, the same hello/prompt/exit/crash
    messages, the same five real prompt helpers running unmodified inside the real,
    unstubbed LiveTest.psm1. Only the device side is replaced, by DeviceStubs.ps1 (which itself
    only reaches into the unchanged tools\live-tests\selftest\Fakes.psm1 for the fake machine).

    One addition Invoke-GuiHalf.ps1 does not need: when the real Wait-Owner is the caller, this
    file's own Read-Host wrapper moves the fake machine (Update-FakeWorldForOwnerAction) once the
    owner's reply arrives, because the real Wait-Owner itself only prints text and waits; it does
    not know about a fake machine. This test driver defines its own wrapper round the same core,
    which also moves the fake world when the caller is Wait-Owner.

.PARAMETER SandboxRoot
    The sandbox folder Fakes.psm1's Initialize-FakeMachine and New-FakeSandbox build fake files
    under. The caller must have already redirected LOCALAPPDATA, APPDATA, ProgramData and
    ProgramFiles for this process into "local", "roaming", "programdata" and "programfiles"
    beneath this same folder (SandboxOptions.cs), so the real, unstubbed New-LiveTestRun writes
    its evidence there too, never to this machine's real %LOCALAPPDATA%.

.PARAMETER TestId
    The TestId the target script passes to New-LiveTestRun, so the fake machine knows which
    machine to present (Fakes.psm1's StartStates are keyed by "TestId|Half").

.PARAMETER Case
    none, one, two or one of Fakes.psm1's named at-rest cases. Defaults to 'one': a normal
    machine with a small amount of real-looking evidence to count.

.PARAMETER SpeakerAddress
    Test 14's protected-device half only, forwarded the same way Invoke-GuiHalf.ps1 forwards it:
    only when the target script declares -SpeakerAddress and this is not empty.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Script,
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)][string]$SandboxRoot,
    [Parameter(Mandatory = $true)][string]$TestId,
    [string]$Case = 'one',
    [switch]$Resume,
    [int]$Variant = 0,
    [switch]$OfferUninstall,
    [switch]$AllowPlanB,
    [string]$SpeakerAddress = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Script -PathType Leaf))
{
    throw ('The live test script was not found: ' + $Script)
}

. (Join-Path $PSScriptRoot 'DeviceStubs.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'ReadHostShim.ps1')

$half = $(if ($Resume) { 'resume' } else { 'first' })
Initialize-FakeMachine -SandboxRoot $SandboxRoot -TestId $TestId -Half $half -Case $Case
New-FakeSandbox -SandboxRoot $SandboxRoot -Case $Case

# Fakes.psm1 (unchanged) never leaves a file at its own fake ExePath: no shipped script ever
# invokes it for real, so nothing in the fake world needed one to exist before this slice.
# resume.txt's -ExePath now needs a real file there too (ResumeFile.cs: "the exe must exist"),
# the same faithfully-fake-but-real-on-disk shape a real release folder would leave. A placeholder
# only, never run.
$fakeExePath = (Get-FakeContext).ExePath
if (-not (Test-Path -LiteralPath $fakeExePath))
{
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fakeExePath) | Out-Null
    Set-Content -LiteralPath $fakeExePath -Value '' -Encoding Ascii
}

# A function shadows the cmdlet, so the target script's own
# "Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force" reaches this, installs the
# three device stubs into both the global scope and the module's own session state, and only
# then lets the real cmdlet finish importing it.
function global:Import-Module
{
    Microsoft.PowerShell.Core\Import-Module @args
    Install-EarshotTwDeviceStubs
}

# The same stack walk ReadHostShim.ps1 uses (skipping its own frame and Read-Host's), kept
# separate and smaller: this only needs the immediate caller's name and its Text parameter, to
# decide whether to move the fake world once the reply arrives.
function Get-EarshotTwWaitOwnerText
{
    foreach ($frame in @(Get-PSCallStack))
    {
        $name = [string]$frame.Command
        if ($name -eq 'Get-EarshotTwWaitOwnerText' -or $name -eq 'Read-Host') { continue }
        if ($name -ne 'Wait-Owner') { return $null }
        $info = $frame.InvocationInfo
        if ($null -ne $info -and $info.BoundParameters.ContainsKey('Text')) { return [string]$info.BoundParameters['Text'] }
        return ''
    }

    return $null
}

function global:Read-Host
{
    param([Parameter(Position = 0)][string]$Prompt = '')

    $waitOwnerText = Get-EarshotTwWaitOwnerText
    $reply = Read-EarshotTwReply -Prompt $Prompt
    if ($null -ne $waitOwnerText)
    {
        Update-FakeWorldForOwnerAction -Text $waitOwnerText
    }

    return $reply
}

Send-EarshotTwMessage -Message ([ordered]@{
    type      = 'hello'
    protocol  = 1
    pid       = $PID
    psVersion = $PSVersionTable.PSVersion.ToString()
    script    = (Split-Path -Leaf $Script)
    half      = $half
})

function Get-EarshotTwScriptParameters
{
    param([Parameter(Mandatory = $true)][string]$Path)

    $parseErrorsRaw = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrorsRaw)
    $parseErrors = @($parseErrorsRaw)
    if ($parseErrors.Count -gt 0)
    {
        throw ('That test script does not parse: ' + $Path + ' (' + $parseErrors[0].Message + ')')
    }

    $names = @{}
    if ($null -ne $ast.ParamBlock)
    {
        foreach ($parameter in $ast.ParamBlock.Parameters)
        {
            $names[$parameter.Name.VariablePath.UserPath] = $true
        }
    }

    return $names
}

$declared = Get-EarshotTwScriptParameters -Path $Script

$forward = @{}
if ($declared.ContainsKey('ExePath')) { $forward['ExePath'] = $ExePath }
if ($declared.ContainsKey('RunRoot')) { $forward['RunRoot'] = $RunRoot }
if ($declared.ContainsKey('Resume') -and $Resume) { $forward['Resume'] = [switch]$true }
if ($declared.ContainsKey('Variant') -and $Variant -ne 0) { $forward['Variant'] = $Variant }
if ($declared.ContainsKey('OfferUninstall') -and $OfferUninstall) { $forward['OfferUninstall'] = [switch]$true }
if ($declared.ContainsKey('AllowPlanB') -and $AllowPlanB) { $forward['AllowPlanB'] = [switch]$true }
if ($declared.ContainsKey('SpeakerAddress') -and -not [string]::IsNullOrEmpty($SpeakerAddress)) { $forward['SpeakerAddress'] = $SpeakerAddress }

$global:LASTEXITCODE = 0
try
{
    & $Script @forward
}
catch
{
    $crashText = '' + $_
    if (-not [string]::IsNullOrEmpty($_.ScriptStackTrace))
    {
        $crashText = $crashText + [System.Environment]::NewLine + $_.ScriptStackTrace
    }

    Send-EarshotTwMessage -Message ([ordered]@{ type = 'crash'; message = $crashText })
    exit 3
}

Send-EarshotTwMessage -Message ([ordered]@{ type = 'exit'; code = $global:LASTEXITCODE })
exit $global:LASTEXITCODE
