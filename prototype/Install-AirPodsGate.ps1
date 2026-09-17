<#
.SYNOPSIS
    One-time setup. Registers a scheduled task so the toggle runs elevated
    without a UAC prompt, and drops a shortcut on your desktop.

.DESCRIPTION
    Run this once, from an elevated PowerShell. After that, the desktop
    shortcut toggles the AirPods block with a single click and no prompt.

.PARAMETER Name
    Substring of the AirPods' name as Windows knows it. Default 'AirPods'.
#>

[CmdletBinding()]
param(
    [string]$Name = 'AirPods',
    [string]$TaskName = 'AirPodsGate'
)

$ErrorActionPreference = 'Stop'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Error "Run this from an elevated PowerShell (right click, Run as administrator)."
    exit 1
}

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$gatePath   = Join-Path $scriptDir 'AirPodsGate.ps1'

if (-not (Test-Path $gatePath)) {
    Write-Error "AirPodsGate.ps1 not found next to this installer."
    exit 1
}

Write-Host "Registering scheduled task '$TaskName'..." -ForegroundColor Cyan

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$gatePath`" -Name `"$Name`" -Action Toggle"

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME `
    -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)

Register-ScheduledTask -TaskName $TaskName -Action $action `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Task registered." -ForegroundColor Green

# Desktop shortcut that fires the task. schtasks /run does not prompt for UAC.
$desktop  = [Environment]::GetFolderPath('Desktop')
$lnkPath  = Join-Path $desktop 'Toggle AirPods.lnk'

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath       = "$env:SystemRoot\System32\schtasks.exe"
$lnk.Arguments        = "/run /tn `"$TaskName`""
$lnk.WorkingDirectory = $scriptDir
$lnk.IconLocation     = "$env:SystemRoot\System32\bthprops.cpl,0"
$lnk.Description      = 'Block or allow the AirPods connecting to this PC'
$lnk.WindowStyle      = 7
$lnk.Save()

Write-Host "Shortcut created: $lnkPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Connect the AirPods to this PC once, so Windows enumerates them."
Write-Host "  2. Double click 'Toggle AirPods' to block them."
Write-Host "  3. Pin the shortcut to your taskbar if you want it one click away."
Write-Host ""
Write-Host "To remove later: Unregister-ScheduledTask -TaskName '$TaskName'"
