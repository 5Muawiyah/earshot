<#
.SYNOPSIS
    Does the gate refuse to pin a phone, and to move the pin off a protected device?

.DESCRIPTION
    The pin decides which device Earshot may disable. If a phone could be pinned,
    one wrong click would disable the phone's Bluetooth nodes instead of the
    AirPods'. The gate therefore accepts a new device only when that address has an
    A2DP sink node, which a phone does not, and it refuses to move the pin while the
    record of services it turned off still names the device pinned now.

    This test reads the paired devices, asks you which address belongs to the phone,
    and asks the gate to pin it. The expected answer is a refusal, with the device
    record left exactly as it was.

    The second half needs a different device that does have an A2DP sink, such as a
    Bluetooth speaker. Without one it says so rather than pretending to have tested it.

.PARAMETER ExePath
    Earshot.exe: the installed copy or an unzipped release.

.PARAMETER RunRoot
    The evidence folder to write into. Leave it out for a new one.

.PARAMETER PhoneAddress
    The phone's twelve character Bluetooth address, upper case. Leave it out and the
    test lists the addresses it can see and asks.

.PARAMETER SpeakerAddress
    Optionally, the address of another Bluetooth audio device with an A2DP sink, for
    the protected-device half.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\14-SetDeviceRefusal.ps1 -ExePath "C:\Program Files\Earshot\Earshot.exe"
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$RunRoot = '',
    [string]$PhoneAddress = '',
    [string]$SpeakerAddress = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LiveTest.psm1') -Force

# The exit code the gate uses when the address given has no A2DP sink node.
$NotAudioSinkExit = 12
# The exit code the gate uses when the device pinned now still has services turned off.
$OtherDeviceProtectedExit = 13

$run = New-LiveTestRun -TestId '14-set-device-refusal' -Title 'The gate refuses to pin a phone' `
    -Settles 'That the pin can never be moved to a device with no A2DP sink, so a phone can never be the device Earshot disables.' `
    -ExePath $ExePath -RunRoot $RunRoot

# Twelve character addresses that appear in the node instance ids, other than the pinned one.
function Get-OtherAddresses
{
    param($NodesJson, [string]$Pinned)

    $found = @()
    foreach ($node in (Get-Field -Object $NodesJson -Name 'nodes'))
    {
        $instance = '' + (Get-Field -Object $node -Name 'instanceId')
        foreach ($match in [regex]::Matches($instance, '(?<address>[0-9A-Fa-f]{12})'))
        {
            $candidate = $match.Groups['address'].Value.ToUpperInvariant()
            if ($candidate -eq $Pinned) { continue }
            if (-not ($found -contains $candidate)) { $found = $found + @($candidate) }
        }
    }

    return $found
}

try
{
    $ready = Show-Preconditions -Run $run -Preconditions @(
        'Earshot is installed and set up, with the AirPods pinned.',
        'Your phone is paired with this PC, so its Bluetooth nodes exist to be seen.',
        'The Earshot tray is closed, so it does not move the pin at the same time.'
    ) -PhysicalActions @(
        'Have your phone nearby and paired. Nothing on the phone is changed by this test.'
    )

    if ($ready)
    {
        Write-Section -Run $run -Title 'What is pinned now'
        $nodes = Get-NodeState -Run $run -Label 'nodes-before'
        $pinned = ('' + (Get-Field -Object $nodes -Name 'address')).ToUpperInvariant()
        $container = '' + (Get-Field -Object $nodes -Name 'container')
        Write-Line -Run $run -Text ('Pinned address ' + $pinned + ', container ' + $container + ', state ' + (Get-Field -Object $nodes -Name 'nodeState'))
        $deviceFileBefore = Read-EarshotJsonFile -Run $run -Path (Join-Path $run.MachineFolder 'device.json')
        Write-Line -Run $run -Text ('device.json holds address ' + (Get-Field -Object $deviceFileBefore -Name 'Address') +
            ', container ' + (Get-Field -Object $deviceFileBefore -Name 'ContainerId'))

        if ([string]::IsNullOrEmpty($PhoneAddress))
        {
            $others = Get-OtherAddresses -NodesJson $nodes -Pinned $pinned
            Write-Line -Run $run -Text 'Addresses seen in the Bluetooth node list, other than the pinned one:'
            foreach ($address in $others) { Write-Line -Run $run -Text ('  ' + $address) }
            Write-Line -Run $run -Text 'Windows Bluetooth settings shows which device each one is, under the device properties.'
            $PhoneAddress = (Read-Note -Run $run -Question 'Type the twelve character address of your phone, upper case.').Trim().ToUpperInvariant()
        }
        else
        {
            $PhoneAddress = $PhoneAddress.Trim().ToUpperInvariant()
        }

        if ($PhoneAddress -notmatch '^[0-9A-F]{12}$')
        {
            Add-Criterion -Run $run -Id 'phone-address' -Criterion 'A phone address was available to try.' -Outcome 'inconclusive' `
                -Detail ('"' + $PhoneAddress + '" is not twelve upper-case hexadecimal characters, so nothing was sent.')
        }
        elseif ($PhoneAddress -eq $pinned)
        {
            Add-Criterion -Run $run -Id 'phone-address' -Criterion 'A phone address was available to try.' -Outcome 'inconclusive' `
                -Detail 'That is the address already pinned, so it would not be a move.'
        }
        else
        {
            Write-Section -Run $run -Title 'Ask the gate to pin the phone'
            Write-Line -Run $run -Text 'The expected answer is a refusal. Earshot only pins a device that has an A2DP sink node.'
            $Address = $PhoneAddress
            $attempt = Invoke-Earshot -Run $run -Label 'gate-set-device-phone' -Command @('diag', 'gate', 'set-device', $Address) -Live `
                -Consequence 'Asks the elevated gate to pin your phone as the device Earshot may disable. It is expected to refuse and change nothing.'

            if ($null -eq $attempt)
            {
                Add-Criterion -Run $run -Id 'refuses-phone' -Criterion 'The gate refuses to pin a device with no A2DP sink.' `
                    -Outcome 'inconclusive' -Detail 'The step was skipped.'
            }
            else
            {
                $evidence = Get-DiagEvidence -Run $run
                $statusExit = Get-FieldPath -Object $evidence -Path @('statusFile', 'exitCode')
                $statusResult = Get-FieldPath -Object $evidence -Path @('statusFile', 'result')
                Write-Line -Run $run -Text ('The gate answered ' + $statusResult + ' (exit ' + $statusExit + ').')

                Add-Criterion -Run $run -Id 'refuses-phone' -Criterion 'The gate refuses to pin a device with no A2DP sink.' `
                    -Outcome $(if ($statusExit -eq $NotAudioSinkExit) { 'pass' } elseif ($statusExit -eq 0) { 'fail' } else { 'inconclusive' }) `
                    -Detail ('It answered ' + $statusResult + ', exit ' + $statusExit + '. ' + $NotAudioSinkExit + ' is the not-audio-sink refusal. A zero would mean a phone can be pinned, which must never happen.')

                $deviceFileAfter = Read-EarshotJsonFile -Run $run -Path (Join-Path $run.MachineFolder 'device.json')
                $unchanged = ((Get-Field -Object $deviceFileAfter -Name 'Address') -eq (Get-Field -Object $deviceFileBefore -Name 'Address')) -and
                    ((Get-Field -Object $deviceFileAfter -Name 'ContainerId') -eq (Get-Field -Object $deviceFileBefore -Name 'ContainerId'))
                Add-Criterion -Run $run -Id 'device-file-unchanged' -Criterion 'The device record is exactly as it was before the refused request.' `
                    -Outcome $(if ($unchanged) { 'pass' } else { 'fail' }) `
                    -Detail ('It now holds address ' + (Get-Field -Object $deviceFileAfter -Name 'Address') + ', container ' + (Get-Field -Object $deviceFileAfter -Name 'ContainerId') + '.')

                Add-Finding -Run $run -Name 'setDevicePhoneRefusal' -Value ('' + $statusResult + ' (' + $statusExit + ')')

                $nodesAfter = Get-NodeState -Run $run -Label 'nodes-after'
                Add-Criterion -Run $run -Id 'phone-untouched' -Criterion 'Nothing on the phone was disabled.' `
                    -Outcome $(if ((Get-Field -Object $nodesAfter -Name 'address').ToUpperInvariant() -eq $pinned) { 'pass' } else { 'fail' }) `
                    -Detail ('The nodes still resolve through ' + (Get-Field -Object $nodesAfter -Name 'address') + '.')
            }
        }

        Write-Section -Run $run -Title 'Moving the pin off a protected device'
        Write-Line -Run $run -Text 'Earshot also refuses to move the pin while its record still names services it turned off on the'
        Write-Line -Run $run -Text 'device pinned now, because that record is the only way to put them back.'
        $protectionFile = Read-EarshotJsonFile -Run $run -Path (Join-Path $run.MachineFolder 'protection.json')
        $disabled = Get-Field -Object $protectionFile -Name 'DisabledServices'
        $disabledCount = 0
        if ($null -ne $disabled) { $disabledCount = @($disabled).Count }
        Write-Line -Run $run -Text ('protection.json lists ' + $disabledCount + ' service(s) turned off on the device pinned now.')

        if ([string]::IsNullOrEmpty($SpeakerAddress))
        {
            Add-Criterion -Run $run -Id 'refuses-protected-move' -Criterion 'The gate refuses to move the pin while services are still turned off on the device pinned now.' `
                -Outcome 'inconclusive' `
                -Detail 'This half needs a second Bluetooth audio device with an A2DP sink. Pair one and run again with -SpeakerAddress, or leave this untested and say so.'
        }
        elseif ($disabledCount -eq 0)
        {
            Add-Criterion -Run $run -Id 'refuses-protected-move' -Criterion 'The gate refuses to move the pin while services are still turned off on the device pinned now.' `
                -Outcome 'inconclusive' -Detail 'Nothing is recorded as turned off, so there is nothing for the guard to protect. Turn protection on first, then run this half again.'
        }
        else
        {
            $Address = $SpeakerAddress.Trim().ToUpperInvariant()
            $move = Invoke-Earshot -Run $run -Label 'gate-set-device-speaker' -Command @('diag', 'gate', 'set-device', $Address) -Live `
                -Consequence 'Asks the gate to move the pin to the other audio device while services are still turned off on the AirPods. It is expected to refuse.'
            if ($null -ne $move)
            {
                $evidence = Get-DiagEvidence -Run $run
                $statusExit = Get-FieldPath -Object $evidence -Path @('statusFile', 'exitCode')
                $statusResult = Get-FieldPath -Object $evidence -Path @('statusFile', 'result')
                Add-Criterion -Run $run -Id 'refuses-protected-move' -Criterion 'The gate refuses to move the pin while services are still turned off on the device pinned now.' `
                    -Outcome $(if ($statusExit -eq $OtherDeviceProtectedExit) { 'pass' } elseif ($statusExit -eq 0) { 'fail' } else { 'inconclusive' }) `
                    -Detail ('It answered ' + $statusResult + ', exit ' + $statusExit + '. ' + $OtherDeviceProtectedExit + ' is the other-device-protected refusal.')
                Add-Finding -Run $run -Name 'setDeviceProtectedRefusal' -Value ('' + $statusResult + ' (' + $statusExit + ')')
            }
        }

        Save-EarshotLog -Run $run
    }
}
catch
{
    Write-Failure -Run $run -Message ('The test stopped with an error: ' + ($_ | Out-String).Trim())
    Add-Criterion -Run $run -Id 'run' -Criterion 'The test ran to the end.' -Outcome 'fail' -Detail 'See the error above. Run 00-Restore.ps1 before the next test.'
}
finally
{
    $overall = Complete-LiveTestRun -Run $run
    Write-Host ('Test 14 finished: ' + $overall)
}
