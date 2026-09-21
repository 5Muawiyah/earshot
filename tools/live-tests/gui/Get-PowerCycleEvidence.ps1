<#
.SYNOPSIS
    Reads raw System event log rows for the power-cycle question, read-only, no elevation.
    Decides nothing: PowerCycle.cs is where the verdict comes from.

.DESCRIPTION
    Local probe, 2026-09-20: the System log is readable by the owner's
    normal account. Every shut down from the Start menu or Explorer wrote User32 1074 with type
    text "power off" and Kernel-Power 109 with ShutdownActionType 6; every restart wrote 5;
    shutdown.exe wrote 4; each start wrote Kernel-General 12 and Kernel-Boot 27, with BootType.
    The number is used, not the localised text.
    https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-power_action

.PARAMETER SinceUtc
    Only rows at or after this UTC time are returned (round-trip "o" format).

.OUTPUTS
    JSON: { "kernelPower109": [ { "utc": ..., "shutdownActionType": <int> } ], "kernelGeneral12":
    [ { "utc": ... } ], "kernelBoot27": [ { "utc": ..., "bootType": <int> } ] } or
    { "error": "<message>" } if the log could not be read.
#>

#Requires -Version 5.1

[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$SinceUtc)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-EarshotTwUtcText
{
    param([Parameter(Mandatory = $true)][datetime]$Value)
    return $Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
}

# Get-WinEvent throws when nothing in the window matches, which is a real answer ("no rows"),
# not a read failure. Only a genuine problem reading the log (access denied, the log itself
# missing) is allowed to become this script's overall error.
function Get-EarshotTwEventRows
{
    param(
        [Parameter(Mandatory = $true)][string]$ProviderName,
        [Parameter(Mandatory = $true)][int]$Id,
        [Parameter(Mandatory = $true)][datetime]$Since
    )

    try
    {
        return @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = $ProviderName; Id = $Id; StartTime = $Since } -ErrorAction Stop)
    }
    catch [Exception]
    {
        if ($_.Exception.Message -match 'No events were found') { return @() }
        throw
    }
}

try
{
    $since = [datetime]::Parse($SinceUtc, [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)

    $power109 = @()
    foreach ($e in (Get-EarshotTwEventRows -ProviderName 'Microsoft-Windows-Kernel-Power' -Id 109 -Since $since))
    {
        $xml = [xml]$e.ToXml()
        $type = $null
        foreach ($d in $xml.Event.EventData.Data)
        {
            if ($d.Name -eq 'ShutdownActionType') { $type = [int]$d.'#text' }
        }

        $power109 += [ordered]@{ utc = (Get-EarshotTwUtcText -Value $e.TimeCreated); shutdownActionType = $type }
    }

    $general12 = @()
    foreach ($e in (Get-EarshotTwEventRows -ProviderName 'Microsoft-Windows-Kernel-General' -Id 12 -Since $since))
    {
        $general12 += [ordered]@{ utc = (Get-EarshotTwUtcText -Value $e.TimeCreated) }
    }

    $boot27 = @()
    foreach ($e in (Get-EarshotTwEventRows -ProviderName 'Microsoft-Windows-Kernel-Boot' -Id 27 -Since $since))
    {
        $xml = [xml]$e.ToXml()
        $bootType = $null
        foreach ($d in $xml.Event.EventData.Data)
        {
            if ($d.Name -eq 'BootType') { $bootType = [int]$d.'#text' }
        }

        $boot27 += [ordered]@{ utc = (Get-EarshotTwUtcText -Value $e.TimeCreated); bootType = $bootType }
    }

    ([ordered]@{ kernelPower109 = $power109; kernelGeneral12 = $general12; kernelBoot27 = $boot27 } | ConvertTo-Json -Compress -Depth 6)
}
catch
{
    ([ordered]@{ error = ('' + $_) } | ConvertTo-Json -Compress)
}
