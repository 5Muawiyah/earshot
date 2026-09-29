<#
.SYNOPSIS
    Runs the real switch lines Earshot writes, and the real settings it reads, through 16-FastSwitch.ps1's own
    functions, in a real PowerShell 5.1 process.

.DESCRIPTION
    tools\live-tests\selftest\Fakes.psm1 hand-writes the switch lines the self-test's fake log holds, so the self-test
    alone cannot show whether the lines 16-FastSwitch.ps1 parses are the lines Earshot writes: a fixture that drifted
    from the formatter would still let every case pass. This is the other half. Earshot.Tests calls the real, unfaked
    SwitchTimelineText for every shape, states what each figure must read as, and this script takes the functions out
    of 16-FastSwitch.ps1 itself (by parsing the file, never by copying them) and checks that they read every line as
    stated, refuse every mangled one, tell the legs apart by trigger through the real Get-EarshotLogLines, work out the
    owner-observed time and the median as stated, and read a settings file the way Earshot's own settings store does.

.PARAMETER Root
    Repository root, so LiveTest.psm1 and the script can be found under tools\live-tests.

.PARAMETER CasesFile
    A JSON file holding what to check: full log lines with what each must read as, lines that must not read at all, owner
    time cases, median cases and settings files with what Earshot reads from each.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$CasesFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$result = [ordered]@{ ok = $false; problems = @(); checked = 0 }
$workDir = $null
try
{
    Import-Module (Join-Path $Root 'tools\live-tests\LiveTest.psm1') -Force -DisableNameChecking
    $cases = Get-Content -LiteralPath $CasesFile -Raw | ConvertFrom-Json

    function Test-One([string]$Name, [bool]$Condition, [string]$Detail)
    {
        $script:result.checked++
        if (-not $Condition) { $script:result.problems += ([string]$Name + ': ' + $Detail) }
    }

    # The functions and the two default chords, taken out of the shipped script by parsing it. Nothing is run.
    $scriptPath = Join-Path $Root 'tools\live-tests\16-FastSwitch.ps1'
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
    Test-One 'the script parses' (@($parseErrors).Count -eq 0) 'it did not'
    $wanted = @('ConvertTo-Ms', 'ConvertTo-AcceptedUtc', 'ConvertFrom-SwitchLine', 'Get-SwitchLeg', 'Get-MedianMs', 'Get-OwnerObservedMs', 'Get-SwitchShortcuts', 'Get-AnswerOutcome', 'Get-CombinedAnswer')
    $found = @()
    foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false))
    {
        if ($wanted -contains $function.Name)
        {
            . ([scriptblock]::Create($function.Extent.Text))
            $found = $found + @($function.Name)
        }
    }

    Test-One 'every function this checks is in the script' (@($found).Count -eq $wanted.Count) ('found ' + (@($found) -join ', '))
    foreach ($assignment in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $false))
    {
        if ($assignment.Left.Extent.Text -eq '$script:DefaultToPc' -or $assignment.Left.Extent.Text -eq '$script:DefaultToPhone')
        {
            . ([scriptblock]::Create($assignment.Extent.Text))
        }
    }

    $workDir = Join-Path $env:TEMP ('earshot-switch-lines-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    $run = [ordered]@{ LogFolder = $workDir }

    function Write-Log([string[]]$Lines)
    {
        Set-Content -LiteralPath (Join-Path $workDir 'earshot.log') -Value $Lines -Encoding UTF8
    }

    # ---- every line the real formatter writes reads as stated ----
    foreach ($case in @($cases.lines))
    {
        $parsed = ConvertFrom-SwitchLine -Line ([string]$case.line)
        Test-One ('reads ' + $case.label) ($null -ne $parsed) ('the line was not recognised: ' + $case.line)
        if ($null -eq $parsed) { continue }
        foreach ($property in $case.expected.PSObject.Properties)
        {
            $actual = $parsed[$property.Name]
            $want = $property.Value
            $same = $false
            if ($null -eq $want -and $null -eq $actual) { $same = $true }
            elseif ($null -ne $want -and $null -ne $actual)
            {
                if ($property.Name -eq 'AcceptedUtc') { $same = ($actual.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture) -ceq [string]$want) }
                else { $same = ([string]$actual -ceq [string]$want) }
            }

            Test-One ([string]$case.label + ' ' + $property.Name) $same ('read ' + $(if ($null -eq $actual) { 'null' } else { [string]$actual }) + ', wanted ' + $(if ($null -eq $want) { 'null' } else { [string]$want }))
        }

        # Through the real log reader, with the leg's own trigger: exactly one line, read as a sample of the wanted kind.
        $direction = [string]$parsed.Direction
        $kindWanted = $(if ($direction -eq 'to-pc') { 'active' } else { 'released' })
        Write-Log -Lines @([string]$case.line)
        $leg = Get-SwitchLeg -Direction $direction -Since ([datetime]::MinValue) -Trigger ([string]$parsed.Trigger)
        Test-One ([string]$case.label + ' leg finds it') ($leg.Lines -eq 1) ('the leg found ' + $leg.Lines + ' line(s)')
        Test-One ([string]$case.label + ' leg sample') (($parsed.Kind -eq $kindWanted) -eq ($null -ne $leg.Sample)) ('kind ' + $parsed.Kind + ', sample ' + $(if ($null -eq $leg.Sample) { 'null' } else { 'set' }))

        # Another trigger's leg does not count it as its own.
        $otherTrigger = $(if ([string]$parsed.Trigger -eq 'click') { 'shortcut-to-pc' } else { 'click' })
        $other = Get-SwitchLeg -Direction $direction -Since ([datetime]::MinValue) -Trigger $otherTrigger
        Test-One ([string]$case.label + ' is not another trigger''s') ($other.Lines -eq 0 -and $other.Others -eq 1 -and $null -eq $other.Sample) ('lines ' + $other.Lines + ', others ' + $other.Others)

        # Two copies are not one switch.
        Write-Log -Lines @([string]$case.line, [string]$case.line)
        $twice = Get-SwitchLeg -Direction $direction -Since ([datetime]::MinValue) -Trigger ([string]$parsed.Trigger)
        Test-One ([string]$case.label + ' twice is ambiguous') ($twice.Lines -eq 2 -and $null -eq $twice.Sample) ('lines ' + $twice.Lines)
    }

    # ---- a line that is not one of the shapes, or whose figure is mangled, reads as nothing ----
    foreach ($bad in @($cases.mangled))
    {
        Test-One ('does not read ' + $bad.label) ($null -eq (ConvertFrom-SwitchLine -Line ([string]$bad.line))) ('it read a figure out of: ' + $bad.line)
    }

    Test-One 'an empty line reads as nothing' ($null -eq (ConvertFrom-SwitchLine -Line '')) 'it read something'

    # ---- the owner-observed time ----
    foreach ($case in @($cases.owner))
    {
        $accepted = $(if ($null -eq $case.accepted) { $null } else { [datetime]::Parse([string]$case.accepted, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() })
        $heard = [datetime]::Parse([string]$case.heard, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $legStart = [datetime]::Parse([string]$case.legStart, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $released = $(if ($null -eq $case.releasedMs) { $null } else { [int]$case.releasedMs })
        $ms = Get-OwnerObservedMs -AcceptedUtc $accepted -ReleasedMs $released -HeardUtc $heard -LegStartUtc $legStart
        Test-One ('owner-observed ' + $case.label) ((($null -eq $ms) -and ($null -eq $case.expected)) -or (($null -ne $ms) -and ($null -ne $case.expected) -and ([string]$ms -eq [string]$case.expected))) ('read ' + $(if ($null -eq $ms) { 'null' } else { [string]$ms }) + ', wanted ' + $(if ($null -eq $case.expected) { 'null' } else { [string]$case.expected }))
    }

    # ---- the median ----
    foreach ($case in @($cases.median))
    {
        $values = @($case.values | ForEach-Object { [int]$_ })
        $ms = Get-MedianMs -Values $values
        Test-One ('median ' + $case.label) ((($null -eq $ms) -and ($null -eq $case.expected)) -or (($null -ne $ms) -and ($null -ne $case.expected) -and ([string]$ms -eq [string]$case.expected))) ('read ' + $(if ($null -eq $ms) { 'null' } else { [string]$ms }) + ', wanted ' + $(if ($null -eq $case.expected) { 'null' } else { [string]$case.expected }))
    }

    # ---- the settings: what Earshot's own store reads from a file is what this script reads ----
    foreach ($case in @($cases.settings))
    {
        $settings = ([string]$case.json | ConvertFrom-Json)
        $read = Get-SwitchShortcuts -Settings $settings
        Test-One ('settings ' + $case.label + ' enabled') ([bool]$read.Enabled -eq [bool]$case.enabled) ('read ' + $read.Enabled + ', Earshot reads ' + $case.enabled)
        Test-One ('settings ' + $case.label + ' to this PC') ([string]$read.ToPc -ceq [string]$case.toPc) ('read "' + $read.ToPc + '", Earshot reads "' + $case.toPc + '"')
        Test-One ('settings ' + $case.label + ' to the phone') ([string]$read.ToPhone -ceq [string]$case.toPhone) ('read "' + $read.ToPhone + '", Earshot reads "' + $case.toPhone + '"')
    }

    Test-One 'there were lines to check' (@($cases.lines).Count -gt 0) 'none were given'
    Test-One 'there were settings to check' (@($cases.settings).Count -gt 0) 'none were given'
    $result.ok = (@($result.problems).Count -eq 0)
}
catch
{
    $result.problems += ('The real-lines check threw: ' + ($_ | Out-String).Trim())
    $result.ok = $false
}
finally
{
    if ($workDir -and (Test-Path -LiteralPath $workDir)) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
}

$result | ConvertTo-Json -Depth 6
if ($result.ok) { exit 0 } else { exit 1 }
