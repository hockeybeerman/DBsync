<#
.SYNOPSIS
    Builds, publishes and registers the DBsync Windows service.

.DESCRIPTION
    Publishes the service and CLI to a target directory, registers the service with the SCM,
    configures automatic restart on failure, and starts it. Run from an elevated PowerShell
    session — registering a service requires administrator rights.

.PARAMETER InstallPath
    Where the binaries are published. Defaults to %ProgramFiles%\DBsync.

.PARAMETER SkipBuild
    Register an existing publish output instead of rebuilding.

.EXAMPLE
    .\Install-DBsyncService.ps1
    .\Install-DBsyncService.ps1 -InstallPath 'D:\Apps\DBsync'
#>
[CmdletBinding()]
param(
    [string] $InstallPath = (Join-Path $env:ProgramFiles 'DBsync'),
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$serviceName = 'DBsync'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell session (Run as administrator).'
    }
}

Assert-Administrator

if (-not $SkipBuild) {
    Write-Host "Publishing DBsync to $InstallPath ..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repoRoot 'src\DBsync.Service\DBsync.Service.csproj') `
        -c Release -o $InstallPath
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the service failed.' }

    dotnet publish (Join-Path $repoRoot 'src\DBsync.Cli\DBsync.Cli.csproj') `
        -c Release -o $InstallPath
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed.' }

    # The tray app is the only way to actually use DBsync. Publishing the service without it
    # leaves a machine that syncs correctly and offers the user no way to see or change anything.
    dotnet publish (Join-Path $repoRoot 'src\DBsync.Tray\DBsync.Tray.csproj') `
        -c Release -o $InstallPath
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the tray app failed.' }
}

$exe = Join-Path $InstallPath 'DBsync.Service.exe'
if (-not (Test-Path $exe)) { throw "Service executable not found at $exe." }

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host 'Stopping the existing service ...' -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host 'Registering the service ...' -ForegroundColor Cyan
& sc.exe create $serviceName binPath= "`"$exe`"" start= auto DisplayName= 'DBsync' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'sc.exe create failed.' }

& sc.exe description $serviceName 'Keeps local folders in sync with network shares.' | Out-Null

# Folders left un-synced are invisible to the user, so always come back after a crash.
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Write-Host 'Starting the service ...' -ForegroundColor Cyan
Start-Service -Name $serviceName
Get-Service -Name $serviceName | Format-Table -AutoSize

$trayExe = Join-Path $InstallPath 'DBsync.Tray.exe'
if (Test-Path $trayExe) {
    Write-Host 'Registering the tray app ...' -ForegroundColor Cyan

    # Per-machine, so the tray comes up for whoever logs in - the service is machine-wide and the
    # UI that drives it should not be tied to the account that happened to run the installer.
    $runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    New-ItemProperty -Path $runKey -Name $serviceName -Value "`"$trayExe`"" `
        -PropertyType String -Force | Out-Null

    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startMenu 'DBsync.lnk'))
    $shortcut.TargetPath = $trayExe
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = 'Keep local folders in sync with network shares.'
    $shortcut.Save()

    # Start it for the installing user now rather than making them log out and back in. The
    # installer is elevated; the tray must not be, or every window it owns inherits that.
    if (-not (Get-Process -Name 'DBsync.Tray' -ErrorAction SilentlyContinue)) {
        & explorer.exe $trayExe
    }
}

Write-Host ''
Write-Host 'Installed. Next steps:' -ForegroundColor Green
Write-Host '  The DBsync icon is in the notification area. Windows 11 hides new tray icons by'
Write-Host '  default - click the ^ chevron, then drag DBsync onto the taskbar to pin it.'
Write-Host ''
Write-Host "  $InstallPath\dbsync.exe status"
Write-Host "  $InstallPath\dbsync.exe add --local C:\Work --share \\server\share"
Write-Host ''
Write-Host 'NOTE: the service runs as LocalSystem, which has no network identity. For a UNC'
Write-Host 'destination that requires authentication, pass --user/--password when adding the pair'
Write-Host '(they are filed in Windows Credential Manager), or reconfigure the service to run as a'
Write-Host 'domain account that already has access to the share.'
