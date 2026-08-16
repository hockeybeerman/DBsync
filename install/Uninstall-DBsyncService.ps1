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

if ($RemoveData) {
    $dataRoot = Join-Path $env:ProgramData 'DBsync'
    if (Test-Path $dataRoot) {
        Remove-Item -Recurse -Force $dataRoot
        Write-Host "Removed $dataRoot." -ForegroundColor Green
    }
}
