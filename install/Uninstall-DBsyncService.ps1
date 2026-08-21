<#
.SYNOPSIS
    Stops and unregisters the DBsync Windows service.

.PARAMETER RemoveData
    Also delete %ProgramData%\DBsync — folder pairs, activity history, conflicts and the sync
    baseline. Synced files themselves are never touched.

.EXAMPLE
    .\Uninstall-DBsyncService.ps1
    .\Uninstall-DBsyncService.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$serviceName = 'DBsync'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session (Run as administrator).'
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Write-Host 'Stopping the service ...' -ForegroundColor Yellow
        Stop-Service -Name $serviceName -Force
    }

    & sc.exe delete $serviceName | Out-Null
    Write-Host 'Service unregistered.' -ForegroundColor Green
} else {
    Write-Host 'Service is not installed.' -ForegroundColor Yellow
}

# The tray app is registered per-machine and runs in the user's session, so it outlives the
# service unless it is taken down explicitly.
Write-Host 'Removing the tray app ...' -ForegroundColor Cyan
Get-Process -Name 'DBsync.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force

$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name $serviceName -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name $serviceName
}

$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\DBsync.lnk'
if (Test-Path $shortcut) { Remove-Item -Force $shortcut }

$arp = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$serviceName"
if (Test-Path $arp) { Remove-Item -Path $arp -Recurse -Force }

# Remove the binaries last. This script is running from inside that directory when launched from
# Add/Remove Programs, so step out of it first; PowerShell has already read the file into memory,
# but the working directory would keep a handle on it.
$installPath = Join-Path $env:ProgramFiles 'DBsync'
if (Test-Path $installPath) {
    Set-Location $env:SystemRoot
    Start-Sleep -Seconds 1
    try {
        Remove-Item -Recurse -Force $installPath -ErrorAction Stop
        Write-Host "Removed $installPath." -ForegroundColor Green
    } catch {
        # The uninstaller cannot always delete the file it is running from. Say so plainly rather
        # than reporting a clean uninstall over a directory that is still there.
        Write-Host "Could not fully remove $installPath - delete it manually." -ForegroundColor Yellow
    }
}

if ($RemoveData) {
    $dataRoot = Join-Path $env:ProgramData 'DBsync'
    if (Test-Path $dataRoot) {
        Remove-Item -Recurse -Force $dataRoot
        Write-Host "Removed $dataRoot." -ForegroundColor Green
    }
}
