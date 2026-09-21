<#
.SYNOPSIS
    Prints tools\live-tests\selftest\Invoke-SelfTest.ps1's own $tests table as JSON, without
    running it.

.DESCRIPTION
    tests\Earshot.Tests\TestWindow's coverage sweep (the design's "same evidence" proof) has to
    drive exactly the scripts and halves Invoke-SelfTest.ps1 itself covers, so a script or a half
    added there is picked up here too, with no second list to keep in step by hand. Invoke-SelfTest.ps1
    is off limits to edit and is not safe to run in full for this (it runs every case against a
    fake machine and takes minutes), so this reads its $tests array out of the parsed source
    instead of executing the script, the same technique Invoke-GuiHalf.ps1 already uses to read a
    target script's own declared parameters.

    Printed as one compact JSON array, one object per row: number, id, script, halves (an array of
    "first"/"resume") and extra (an array of "Name=Value" strings, Invoke-SelfTest.ps1's own Extra
    list for that row). cases is deliberately not printed: the sweep this feeds only ever drives
    the shared "one" case.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$SelfTestScript = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($SelfTestScript))
{
    $SelfTestScript = (Resolve-Path (Join-Path $PSScriptRoot '..\..\selftest\Invoke-SelfTest.ps1')).Path
}

if (-not (Test-Path -LiteralPath $SelfTestScript -PathType Leaf))
{
    throw ('Invoke-SelfTest.ps1 was not found at ' + $SelfTestScript)
}

$parseErrorsRaw = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($SelfTestScript, [ref]$tokens, [ref]$parseErrorsRaw)
$parseErrors = @($parseErrorsRaw)
if ($parseErrors.Count -gt 0)
{
    throw ('Invoke-SelfTest.ps1 does not parse: ' + $parseErrors[0].Message)
}

$assignments = @($ast.FindAll(
        { param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq 'tests'
        }, $false))

if ($assignments.Count -ne 1)
{
    throw ('Expected exactly one $tests assignment in Invoke-SelfTest.ps1, found ' + $assignments.Count + '.')
}

# The right-hand side's own source text, evaluated on its own: a literal array of ordered
# hashtables, nothing here reads a variable or calls a function, so evaluating it outside the
# script that declares it is safe and exact, not an approximation of what it says.
$rhsText = $assignments[0].Right.Extent.Text
$block = [scriptblock]::Create($rhsText)
$rows = & $block

$plan = @()
foreach ($row in @($rows))
{
    $plan = $plan + @([ordered]@{
            number = [string]$row.Number
            id     = [string]$row.Id
            script = [string]$row.Script
            halves = @($row.Halves | ForEach-Object { [string]$_ })
            extra  = @($row.Extra | ForEach-Object { [string]$_ })
        })
}

$plan | ConvertTo-Json -Depth 6 -Compress
