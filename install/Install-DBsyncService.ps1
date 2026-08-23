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
    [switch] $SkipBuild,

    # Windows account the service logs on as, e.g. 'CONTOSO\alice' or '.\alice'. Omit for
    # LocalSystem. LocalSystem has no network identity of its own, so a share that authenticates
    # sees the computer account rather than a person; running as a user is what makes a share the
    # user can already reach work with no stored credentials at all.
    [string] $ServiceAccount,

    # Only for accounts that need one. Built-in service accounts and managed accounts do not.
    # Left empty, the script prompts without echoing.
    [System.Security.SecureString] $ServicePassword
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

# '.\name' is the form everyone writes for a local account, and the form this script's own help
# suggests - but NTAccount does not understand it, so spell out the machine name.
function Resolve-AccountName([string] $account) {
    $trimmed = $account.Trim()
    if ($trimmed.StartsWith('.\')) { return "$env:COMPUTERNAME\" + $trimmed.Substring(2) }
    return $trimmed
}

function Resolve-AccountSid([string] $account) {
    try {
        return (New-Object System.Security.Principal.NTAccount($account)).Translate(
            [System.Security.Principal.SecurityIdentifier])
    } catch {
        throw "Could not find the account '$account'. Use DOMAIN\user, .\user for a local account, or a built-in name such as 'NT AUTHORITY\NetworkService'."
    }
}

# Built-in service accounts authenticate by identity, not password. Asking for one - or passing an
# empty one - is what makes 'sc config obj=' fail with a bad-credentials error for these.
function Test-AccountNeedsPassword([string] $account) {
    $normalized = $account.Trim()
    if ($normalized -like 'NT AUTHORITY\*') { return $false }
    if ($normalized -like 'NT SERVICE\*') { return $false }
    if ($normalized -in @('LocalSystem', 'LocalService', 'NetworkService')) { return $false }
    if ($normalized.EndsWith('$')) { return $false }   # gMSA / computer account
    return $true
}

# A service account that cannot log on as a service fails to start with error 1069, which reads as
# a bad password and sends people hunting in the wrong place.
function Grant-LogonAsService([System.Security.Principal.SecurityIdentifier] $sid) {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("dbsync-rights-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $exported = Join-Path $work 'export.inf'
        $updated  = Join-Path $work 'update.inf'
        $database = Join-Path $work 'secedit.sdb'

        & secedit.exe /export /areas USER_RIGHTS /cfg $exported | Out-Null
        if (-not (Test-Path $exported)) { throw 'secedit could not export the current user rights.' }

        $line = (Get-Content $exported | Where-Object { $_ -match '^SeServiceLogonRight' })
        $holders = if ($line) { ($line -split '=', 2)[1].Trim() } else { '' }
        # secedit exports a holder it can resolve as a plain account name and one it cannot as
        # *SID, so comparing against *SID alone would miss an account that already holds the right
        # and add it a second time.
        $alreadyHeld = $false
        foreach ($holder in ($holders -split ',')) {
            $entry = $holder.Trim()
            if (-not $entry) { continue }
            if ($entry -eq "*$($sid.Value)") { $alreadyHeld = $true; break }
            if ($entry.StartsWith('*')) { continue }
            try {
                $resolved = (New-Object System.Security.Principal.NTAccount($entry)).Translate(
                    [System.Security.Principal.SecurityIdentifier]).Value
                if ($resolved -eq $sid.Value) { $alreadyHeld = $true; break }
            } catch {
                # An entry naming an account that no longer exists. Left alone: it is not ours to
                # tidy, and dropping it would silently change policy the installer never set.
            }
        }

        if ($alreadyHeld) {
            Write-Host '  already allowed to log on as a service.' -ForegroundColor DarkGray
            return
        }

        $holders = if ($holders) { "$holders,*$($sid.Value)" } else { "*$($sid.Value)" }
        @(
            '[Unicode]'
            'Unicode=yes'
            '[Version]'
            'signature="$CHICAGO$"'
            'Revision=1'
            '[Privilege Rights]'
            "SeServiceLogonRight = $holders"
        ) | Set-Content -Path $updated -Encoding Unicode

        & secedit.exe /configure /db $database /cfg $updated /areas USER_RIGHTS | Out-Null
        Write-Host '  granted the right to log on as a service.' -ForegroundColor DarkGray
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

# Config, the SQLite database and its journals all live here. BUILTIN\Users gets Write but not
# Delete, which is not enough for SQLite to clean up its -wal and -shm files.
function Grant-DataAccess([System.Security.Principal.SecurityIdentifier] $sid, [string] $path) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    $acl = Get-Acl $path
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $sid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -Path $path -AclObject $acl
    Write-Host "  granted Modify on $path." -ForegroundColor DarkGray
}

$serviceSid = $null
if ($ServiceAccount) {
    $ServiceAccount = Resolve-AccountName $ServiceAccount
    $serviceSid = Resolve-AccountSid $ServiceAccount
    if ((Test-AccountNeedsPassword $ServiceAccount) -and -not $ServicePassword) {
        $ServicePassword = Read-Host -AsSecureString "Password for $ServiceAccount"
    }
}

# Before publishing, not after: the running service and tray hold their own DLLs open, and
# msbuild simply fails to copy over them.
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host 'Stopping the existing service ...' -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

$runningTray = Get-Process -Name 'DBsync.Tray' -ErrorAction SilentlyContinue
if ($runningTray) {
    Write-Host 'Stopping the running tray app ...' -ForegroundColor Yellow
    $runningTray | Stop-Process -Force
    Start-Sleep -Seconds 1
}

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

Write-Host 'Registering the service ...' -ForegroundColor Cyan
& sc.exe create $serviceName binPath= "`"$exe`"" start= auto DisplayName= 'DBsync' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'sc.exe create failed.' }

& sc.exe description $serviceName 'Keeps local folders in sync with network shares.' | Out-Null

# Folders left un-synced are invisible to the user, so always come back after a crash.
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

if ($ServiceAccount) {
    Write-Host "Configuring the service to run as $ServiceAccount ..." -ForegroundColor Cyan
    Grant-LogonAsService $serviceSid
    Grant-DataAccess $serviceSid (Join-Path $env:ProgramData 'DBsync')

    # Set the account through WMI rather than 'sc config obj= password= ...'. sc.exe would put the
    # password on a command line, where it is visible to anything that can list processes.
    $plain = ''
    if ($ServicePassword) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServicePassword)
        try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }

    $result = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" |
        Invoke-CimMethod -MethodName Change -Arguments @{
            StartName     = $ServiceAccount
            StartPassword = $plain
        }
    $plain = $null

    if ($result.ReturnValue -ne 0) {
        throw "Could not set the service account (Win32_Service.Change returned $($result.ReturnValue)). 21 means the password or account name was rejected; 15 means the account is not allowed to log on as a service."
    }
}

Write-Host 'Starting the service ...' -ForegroundColor Cyan
Start-Service -Name $serviceName
Get-Service -Name $serviceName | Format-Table -AutoSize

$trayExe = Join-Path $InstallPath 'DBsync.Tray.exe'
if (Test-Path $trayExe) {
    Write-Host 'Registering the tray app ...' -ForegroundColor Cyan

    # Sign-in launch is the tray app's own business now, kept per-user under HKCU so the settings
    # window can turn it off without administrator rights (#8). A machine-wide entry cannot be
    # switched off by the person it launches for, which would make that setting a lie - so clear
    # any left by an earlier install, or the tray would be started twice.
    $machineRunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    if (Get-ItemProperty -Path $machineRunKey -Name $serviceName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $machineRunKey -Name $serviceName
        Write-Host '  removed the old machine-wide startup entry.' -ForegroundColor DarkGray
    }

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

# Without this DBsync cannot be uninstalled by any normal means: it does not appear in Settings,
# and the only uninstall script lives in a source tree the installed machine may not even have.
Write-Host 'Registering the uninstaller ...' -ForegroundColor Cyan
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-DBsyncService.ps1') $InstallPath -Force

$uninstaller = Join-Path $InstallPath 'Uninstall-DBsyncService.ps1'
$arp = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$serviceName"
New-Item -Path $arp -Force | Out-Null
Set-ItemProperty -Path $arp -Name 'DisplayName'     -Value 'DBsync'
Set-ItemProperty -Path $arp -Name 'DisplayVersion'  -Value '1.0.0'
Set-ItemProperty -Path $arp -Name 'Publisher'       -Value 'DBsync'
Set-ItemProperty -Path $arp -Name 'InstallLocation' -Value $InstallPath
Set-ItemProperty -Path $arp -Name 'DisplayIcon'     -Value $trayExe
Set-ItemProperty -Path $arp -Name 'NoModify'        -Value 1 -Type DWord
Set-ItemProperty -Path $arp -Name 'NoRepair'        -Value 1 -Type DWord
Set-ItemProperty -Path $arp -Name 'UninstallString' `
    -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`""

Write-Host ''
Write-Host 'Installed. Next steps:' -ForegroundColor Green
Write-Host '  To remove DBsync later: Settings > Apps > Installed apps > DBsync.'
Write-Host ''
Write-Host '  The DBsync icon is in the notification area. Windows 11 hides new tray icons by'
Write-Host '  default - click the ^ chevron, then drag DBsync onto the taskbar to pin it.'
Write-Host ''
Write-Host "  $InstallPath\dbsync.exe status"
Write-Host "  $InstallPath\dbsync.exe add --local C:\Work --share \\server\share"
Write-Host ''
$runsAs = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartName
Write-Host "  The service runs as: $runsAs"
Write-Host ''
if ($runsAs -eq 'LocalSystem' -or $runsAs -like 'NT AUTHORITY\*') {
    Write-Host 'NOTE: this account has no network identity of its own, so a share that authenticates'
    Write-Host 'sees the computer rather than a person. For a UNC destination that requires a sign-in,'
    Write-Host 'pass --user/--password when adding the pair (filed in Windows Credential Manager), or'
    Write-Host 're-run this script with -ServiceAccount to run as an account that already has access:'
    Write-Host ''
    Write-Host "  .\Install-DBsyncService.ps1 -ServiceAccount '$env:USERDOMAIN\$env:USERNAME'"
} else {
    Write-Host 'The service now carries that account''s network identity, so any share that account'
    Write-Host 'can already reach needs no stored credentials. Two things to know: the service will'
    Write-Host 'fail to start after that account''s password changes until this script is re-run, and'
    Write-Host 'volume shadow copies (--vss) need the account to be an administrator.'
}
