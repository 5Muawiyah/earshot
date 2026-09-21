<#
.SYNOPSIS
    Prints 03-AllowPages.ps1's own -WatchSeconds default and 13-GraceWindow.ps1's own
    -WatchMinutes default as JSON, without running either script.

.DESCRIPTION
    The window's own silence watchdog waits Data\tests.json's maxSilenceSeconds for a row before
    it says anything; 03 and 13 are the two rows whose own scripts can legitimately run longer
    than every other row's shared 900 s allowance, so their own entries are set from what those
    scripts actually wait by default. That figure is a parameter default chosen inside the script,
    not this file's to duplicate by hand: reading it out of the parsed source, the same technique
    Export-SelfTestPlan.ps1 already uses for Invoke-SelfTest.ps1's own $tests table, means a
    changed default is picked up here too, with nothing to fall out of step.

.PARAMETER Script03Path
.PARAMETER Script13Path
    Left out for the two shipped scripts beside this file's own tools\live-tests folder.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$Script03Path = '',
    [string]$Script13Path = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Script03Path))
{
    $Script03Path = (Resolve-Path (Join-Path $PSScriptRoot '..\03-AllowPages.ps1')).Path
}

if ([string]::IsNullOrEmpty($Script13Path))
{
    $Script13Path = (Resolve-Path (Join-Path $PSScriptRoot '..\13-GraceWindow.ps1')).Path
}

# The default value of one -Name parameter in a script's own param() block, read from the parsed
# source rather than executed: safe against a script that requires -ExePath (mandatory) or would
# otherwise start something real.
function Get-ParameterDefault
{
    param([Parameter(Mandatory = $true)][string]$ScriptPath, [Parameter(Mandatory = $true)][string]$Name)

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf))
    {
        throw ('script not found: ' + $ScriptPath)
    }

    $parseErrorsRaw = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrorsRaw)
    $parseErrors = @($parseErrorsRaw)
    if ($parseErrors.Count -gt 0)
    {
        throw ('script does not parse: ' + $ScriptPath + ': ' + $parseErrors[0].Message)
    }

    $parameters = @($ast.FindAll(
            { param($node)
                $node -is [System.Management.Automation.Language.ParameterAst] -and
                $node.Name.VariablePath.UserPath -eq $Name
            }, $true))

    if ($parameters.Count -ne 1)
    {
        throw ('Expected exactly one -' + $Name + ' parameter in ' + $ScriptPath + ', found ' + $parameters.Count + '.')
    }

    if ($null -eq $parameters[0].DefaultValue)
    {
        throw ('-' + $Name + ' in ' + $ScriptPath + ' has no default value.')
    }

    return [int]$parameters[0].DefaultValue.SafeGetValue()
}

$result = [ordered]@{
    watchSeconds = Get-ParameterDefault -ScriptPath $Script03Path -Name 'WatchSeconds'
    watchMinutes = Get-ParameterDefault -ScriptPath $Script13Path -Name 'WatchMinutes'
}

$result | ConvertTo-Json -Compress
