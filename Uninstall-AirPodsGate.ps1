<#
.SYNOPSIS
    Removes the scheduled task and shortcut, and re-enables the AirPods nodes.
#>

[CmdletBinding()]
param(
    [string]$Name = 'AirPods',
    [string]$TaskName = 'AirPodsGate'
)

$ErrorActionPreference = 'Continue'

Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FriendlyName -like "*$Name*" -and
        $_.InstanceId -match '^(BTHENUM|BTHLE|BTHLEDEVICE|BTH)\\' -and
        $_.Status -eq 'Error'
    } | ForEach-Object {
        Write-Host "Re-enabling $($_.FriendlyName)"
        Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false
    }

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Toggle AirPods.lnk') -ErrorAction SilentlyContinue

Write-Host "Removed." -ForegroundColor Green
