<#
.SYNOPSIS
    Blocks or allows your AirPods at the Windows device level, so the PC
    cannot page them at boot and steal them from your phone.

.DESCRIPTION
    Finds the Bluetooth device nodes belonging to your AirPods and disables
    or enables them. A disabled device node persists across reboots, so the
    block survives a power cycle. Nothing is unpaired, so allowing them
    again is instant.

.PARAMETER Name
    Substring of the AirPods' name as Windows knows it. Default 'AirPods'.
    If you renamed them, pass the new name.

.PARAMETER Action
    Status | Block | Allow | Toggle. Default Toggle.

.EXAMPLE
    .\AirPodsGate.ps1 -Action Status
    .\AirPodsGate.ps1 -Action Block
    .\AirPodsGate.ps1            # toggles
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Name = 'AirPods',
    [ValidateSet('Status', 'Block', 'Allow', 'Toggle')]
    [string]$Action = 'Toggle',
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Show-Toast {
    param([string]$Title, [string]$Text)
    if ($Quiet) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $icon = New-Object System.Windows.Forms.NotifyIcon
        $icon.Icon = [System.Drawing.SystemIcons]::Information
        $icon.BalloonTipTitle = $Title
        $icon.BalloonTipText = $Text
        $icon.Visible = $true
        $icon.ShowBalloonTip(3000)
        Start-Sleep -Seconds 4
        $icon.Dispose()
    } catch {
        Write-Host "$Title - $Text"
    }
}

function Get-AirPodsNodes {
    param([string]$Match)
    # Only Bluetooth bus nodes. Never the radio itself, never anything else.
    Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FriendlyName -like "*$Match*" -and
            $_.InstanceId -match '^(BTHENUM|BTHLE|BTHLEDEVICE|BTH)\\'
        }
}

function Get-GateState {
    param($Nodes)
    if (-not $Nodes) { return 'unknown' }
    $disabled = @($Nodes | Where-Object { $_.Status -eq 'Error' })
    if ($disabled.Count -eq $Nodes.Count) { return 'blocked' }
    if ($disabled.Count -eq 0) { return 'allowed' }
    return 'mixed'
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

$nodes = Get-AirPodsNodes -Match $Name

if (-not $nodes) {
    Write-Warning "No Bluetooth device nodes matched '*$Name*'."
    Write-Host ""
    Write-Host "Bluetooth devices Windows can currently see:" -ForegroundColor Cyan
    Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match '^(BTHENUM|BTHLE|BTHLEDEVICE|BTH)\\' } |
        Select-Object Status, FriendlyName, InstanceId |
        Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host "Re-run with -Name '<the name you see above>'."
    exit 1
}

$state = Get-GateState -Nodes $nodes

Write-Host ""
Write-Host "Matched $($nodes.Count) node(s) for '*$Name*':" -ForegroundColor Cyan
$nodes | Select-Object Status, FriendlyName, InstanceId |
    Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "Current state: $state" -ForegroundColor Yellow
Write-Host ""

if ($Action -eq 'Status') { exit 0 }

$target = switch ($Action) {
    'Block'  { 'blocked' }
    'Allow'  { 'allowed' }
    'Toggle' { if ($state -eq 'blocked') { 'allowed' } else { 'blocked' } }
}

if ($state -eq $target) {
    Write-Host "Already $target. Nothing to do."
    exit 0
}

if (-not (Test-Admin)) {
    Write-Warning "Changing device state needs an elevated shell."
    Write-Warning "Run Install-AirPodsGate.ps1 once to get a no-prompt shortcut."
    exit 1
}

$failed = @()
foreach ($node in $nodes) {
    try {
        if ($target -eq 'blocked') {
            if ($PSCmdlet.ShouldProcess($node.FriendlyName, 'Disable')) {
                Disable-PnpDevice -InstanceId $node.InstanceId -Confirm:$false
            }
        } else {
            if ($PSCmdlet.ShouldProcess($node.FriendlyName, 'Enable')) {
                Enable-PnpDevice -InstanceId $node.InstanceId -Confirm:$false
            }
        }
    } catch {
        $failed += "$($node.FriendlyName): $($_.Exception.Message)"
    }
}

if ($failed) {
    Write-Warning "Some nodes could not be changed:"
    $failed | ForEach-Object { Write-Warning "  $_" }
    Write-Warning "Nodes that are not currently present cannot be disabled."
    Write-Warning "Connect the AirPods to the PC once, then run Block again."
}

$now = Get-GateState -Nodes (Get-AirPodsNodes -Match $Name)
Write-Host "New state: $now" -ForegroundColor Green

if ($now -eq 'blocked') {
    Show-Toast -Title 'AirPods blocked' -Text 'The PC will not connect to them.'
} elseif ($now -eq 'allowed') {
    Show-Toast -Title 'AirPods allowed' -Text 'The PC can connect to them again.'
}
