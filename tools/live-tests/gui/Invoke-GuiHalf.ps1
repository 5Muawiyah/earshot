<#
.SYNOPSIS
    Runs one half of one shipped live test script under the test window's protocol.

.DESCRIPTION
    Dot-sources ReadHostShim.ps1 for the message and reply grammar, defines the two-line
    global:Read-Host wrapper, sends a hello message, then
    runs the named script directly with only the parameters it declares, the way
    Run-LiveTests.ps1 works out what to forward and the way
    tools\live-tests\selftest\Run-OneHalf.ps1 runs a script and passes its exit code on.

    Nothing here is replaced inside the shipped script or LiveTest.psm1: Read-Host is the only
    seam. Every other prompt helper, and every device, task and
    folder call the shipped script makes, runs unchanged.

.PARAMETER Script
    Full path to one of the 16 shipped scripts.

.PARAMETER ExePath
    Earshot.exe, forwarded to the script.

.PARAMETER RunRoot
    The evidence folder. Every script declares -RunRoot; the window always passes one so it
    knows the evidence folder before the script prints it.

.PARAMETER Resume
    Runs the second half of a two-half test.

.PARAMETER Variant
    Test 10 only: 1 to 5.

.PARAMETER OfferUninstall
    Test 00's elevated variant only.

.PARAMETER AllowPlanB
    Test 07's elevated variant only.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Script,
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [switch]$Resume,
    [int]$Variant = 0,
    [switch]$OfferUninstall,
    [switch]$AllowPlanB
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Script -PathType Leaf))
{
    throw ('The live test script was not found: ' + $Script)
}

. (Join-Path $PSScriptRoot 'ReadHostShim.ps1')

# The one seam this driver adds: a function shadows the cmdlet, and LiveTest.psm1's own
# helpers resolve it from the global scope (proved by local probe). The
# real Show-Preconditions, Confirm-Step, Read-Answer, Read-Note and Wait-Owner run unchanged and
# still write their own lines to summary.txt and their own entries to $Run.Answers.
function global:Read-Host
{
    param([Parameter(Position = 0)][string]$Prompt = '')
    return (Read-EarshotTwReply -Prompt $Prompt)
}

Send-EarshotTwMessage -Message ([ordered]@{
    type      = 'hello'
    protocol  = 1
    pid       = $PID
    psVersion = $PSVersionTable.PSVersion.ToString()
    script    = (Split-Path -Leaf $Script)
    half      = $(if ($Resume) { 'resume' } else { 'first' })
})

# The parameters the named script declares, read from its own param block, the way
# Run-LiveTests.ps1 works out what it may forward.
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
