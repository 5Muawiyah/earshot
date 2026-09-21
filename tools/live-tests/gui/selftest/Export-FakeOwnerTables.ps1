<#
.SYNOPSIS
    Prints Fakes.psm1's own Answers and Notes tables as JSON, without copying them into anything
    else.

.DESCRIPTION
    tests\Earshot.Tests\TestWindow's coverage sweep plays the owner while the window drives a
    shipped script through the real protocol, and it has to answer each Read-Answer/Read-Note the
    way Fakes.psm1's own Get-FakeAnswer/Get-FakeNote would for the "one" case, so the evidence it
    produces matches expectations.psd1's own "one" entries (which were worked out against exactly
    those answers). Fakes.psm1 does not export its tables, only the functions that read them, so
    this reaches into the module's own session state with the call operator (the same technique
    Run-OneHalf.ps1/DeviceStubs.ps1 already use to put stub functions inside LiveTest.psm1's
    session state) rather than re-typing the tables by hand, which would drift the moment
    Fakes.psm1's own tables changed.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$FakesModule = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($FakesModule))
{
    $FakesModule = (Resolve-Path (Join-Path $PSScriptRoot '..\..\selftest\Fakes.psm1')).Path
}

$module = Microsoft.PowerShell.Core\Import-Module $FakesModule -Force -PassThru
$answers = & $module { $script:Answers }
$notes = & $module { $script:Notes }

$answerPairs = @()
foreach ($key in $answers.Keys) { $answerPairs = $answerPairs + @([ordered]@{ key = [string]$key; value = [string]$answers[$key] }) }

$notePairs = @()
foreach ($key in $notes.Keys) { $notePairs = $notePairs + @([ordered]@{ key = [string]$key; value = [string]$notes[$key] }) }

# Arrays of {key, value} pairs, not a JSON object: Fakes.psm1's own [ordered] tables are matched
# in declaration order (Get-FakeAnswer/Get-FakeNote: "foreach ($key in $script:Answers.Keys)",
# first Contains match wins), and a JSON object's own member order is not something to depend on
# once it has round-tripped through a parser.
([ordered]@{ answers = $answerPairs; notes = $notePairs }) | ConvertTo-Json -Depth 6 -Compress
