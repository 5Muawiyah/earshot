<#
.SYNOPSIS
    Prints tools\live-tests\selftest\expectations.psd1 as JSON, for the "one" case only.

.DESCRIPTION
    tests\Earshot.Tests\TestWindow's coverage sweep proves the window's own driver writes "the
    same evidence" the self-test's own expectations.psd1 says a script and half must record for
    the fake inputs it was given. It has to read that file rather than copy its contents into a
    test (expectations.psd1 is off limits to edit, and a second, hand-kept copy of what it says
    would drift the moment the real one changed), so this loads it with Import-PowerShellDataFile,
    exactly as Invoke-SelfTest.ps1 does, and prints only the "one" entry of every "<id>|<half>"
    key, since that is the only case the sweep this feeds ever drives.

    Each row of the JSON array holds: key ("<id>|<half>"), overall (a string, or an array of
    strings when expectations.psd1 allows more than one), criteria (an array of {id, outcome}
    pairs, "outcome" being "any" when the machine's own answer is not pinned), findingNames (the
    exhaustive set of finding names when that case declares Findings, otherwise absent) and
    findingsIncludeNames (the finding names that case pins through FindingsInclude, never
    exhaustive, otherwise absent).
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$ExpectationsPath = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($ExpectationsPath))
{
    $ExpectationsPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\selftest\expectations.psd1')).Path
}

$expectations = Import-PowerShellDataFile -LiteralPath $ExpectationsPath

$rows = @()
foreach ($key in $expectations.Keys)
{
    $entry = $expectations[$key]
    if (-not $entry.Contains('one')) { continue }
    $wanted = $entry['one']

    $overall = @($wanted.Overall) | ForEach-Object { [string]$_ }
    $criteria = @()
    foreach ($id in $wanted.Criteria.Keys)
    {
        $criteria = $criteria + @([ordered]@{ id = [string]$id; outcome = [string]$wanted.Criteria[$id] })
    }

    $row = [ordered]@{ key = [string]$key; overall = $overall; criteria = $criteria }

    if ($wanted.Contains('Findings'))
    {
        $row['findingNames'] = @($wanted.Findings.Keys | ForEach-Object { [string]$_ })
    }

    if ($wanted.Contains('FindingsInclude'))
    {
        $row['findingsIncludeNames'] = @($wanted.FindingsInclude.Keys | ForEach-Object { [string]$_ })
    }

    $rows = $rows + @($row)
}

$rows | ConvertTo-Json -Depth 8 -Compress
