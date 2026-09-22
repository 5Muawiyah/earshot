<#
.SYNOPSIS
    Prints Fakes.psm1's own Answers and Notes tables, and LiveTest.psm1's own closing-step
    consequence texts, as JSON, without copying any of them into anything else.

.DESCRIPTION
    tests\Earshot.Tests\TestWindow's coverage sweep plays the owner while the window drives a
    shipped script through the real protocol, and it has to answer each Read-Answer/Read-Note the
    way Fakes.psm1's own Get-FakeAnswer/Get-FakeNote would for the "one" case, so the evidence it
    produces matches expectations.psd1's own "one" entries (which were worked out against exactly
    those answers). Neither module exports what this script reads: only the functions that read
    them, so this reaches into each module's own session state with the call operator (the same
    technique Run-OneHalf.ps1/DeviceStubs.ps1 already use to put stub functions inside
    LiveTest.psm1's session state) rather than re-typing any of it by hand, which would drift the
    moment the module's own text changed.

    closeAtRestConsequences carries LiveTest.psm1's own $script:AtRestDisconnectConsequence and
    $script:AtRestBlockWhilePlayingConsequence: the two texts PromptPresenter.cs matches exactly to
    give the closing step's disconnect offer, and the block offer after an unconfirmed disconnect,
    their own plain line. Importing LiveTest.psm1 only runs its function definitions and these
    module-level variable assignments; it never touches a device, since Close-AtRest itself is
    never called here.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$FakesModule = '',
    [string]$LiveTestModule = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($FakesModule))
{
    $FakesModule = (Resolve-Path (Join-Path $PSScriptRoot '..\..\selftest\Fakes.psm1')).Path
}

if ([string]::IsNullOrEmpty($LiveTestModule))
{
    $LiveTestModule = (Resolve-Path (Join-Path $PSScriptRoot '..\..\LiveTest.psm1')).Path
}

$fakes = Microsoft.PowerShell.Core\Import-Module $FakesModule -Force -PassThru
$answers = & $fakes { $script:Answers }
$notes = & $fakes { $script:Notes }

$liveTest = Microsoft.PowerShell.Core\Import-Module $LiveTestModule -Force -PassThru
$disconnectConsequence = & $liveTest { $script:AtRestDisconnectConsequence }
$blockWhilePlayingConsequence = & $liveTest { $script:AtRestBlockWhilePlayingConsequence }

$answerPairs = @()
foreach ($key in $answers.Keys) { $answerPairs = $answerPairs + @([ordered]@{ key = [string]$key; value = [string]$answers[$key] }) }

$notePairs = @()
foreach ($key in $notes.Keys) { $notePairs = $notePairs + @([ordered]@{ key = [string]$key; value = [string]$notes[$key] }) }

# Arrays of {key, value} pairs, not a JSON object: Fakes.psm1's own [ordered] tables are matched
# in declaration order (Get-FakeAnswer/Get-FakeNote: "foreach ($key in $script:Answers.Keys)",
# first Contains match wins), and a JSON object's own member order is not something to depend on
# once it has round-tripped through a parser.
([ordered]@{
        answers                  = $answerPairs
        notes                    = $notePairs
        closeAtRestConsequences  = [ordered]@{
            disconnect        = [string]$disconnectConsequence
            blockWhilePlaying = [string]$blockWhilePlayingConsequence
        }
    }) | ConvertTo-Json -Depth 6 -Compress
