<#
Install Deskhand in SESSION-FOLLOW mode.

Instead of a per-user logon task, this installs a LocalSystem Windows service (DeskhandLauncher) that keeps
deskhand-http.exe running in whichever session currently owns the physical console. When you do a fast user
switch, the service stops the old instance (freeing the port) and launches a new one in the new user's session,
so Deskhand follows the switch. At the logon / switch-user screen (no user) nothing runs — there's nothing to
drive. Requires elevation (it creates a LocalSystem service, which needs SeTcbPrivilege to spawn as the user).
#>
param(
    [string]$InstallDir = $PSScriptRoot,                                    # folder with deskhand-http.exe + launcher\
    [string]$Token = $env:DESKHAND_TOKEN,
    [int]$Port = $(if ($env:DESKHAND_PORT) { [int]$env:DESKHAND_PORT } else { 8791 })
)
$ErrorActionPreference = 'Stop'
$http     = Join-Path $InstallDir 'deskhand-http.exe'
$launcher = Join-Path $InstallDir 'launcher\deskhand-launcher.exe'
if (-not (Test-Path $http))     { throw "deskhand-http.exe not found in $InstallDir" }
if (-not (Test-Path $launcher)) { throw "launcher\deskhand-launcher.exe not found in $InstallDir" }

# Machine env the service — and the deskhand-http it spawns — read.
[Environment]::SetEnvironmentVariable('DESKHAND_LAUNCH_EXE',  $http,      'Machine')
[Environment]::SetEnvironmentVariable('DESKHAND_LAUNCH_ARGS', '--hidden', 'Machine')
if ($Token) { [Environment]::SetEnvironmentVariable('DESKHAND_TOKEN', $Token, 'Machine') }
[Environment]::SetEnvironmentVariable('DESKHAND_PORT', "$Port", 'Machine')

# Stop the logon-task model so it doesn't also launch a deskhand-http (which would fight over the port).
try { Disable-ScheduledTask -TaskName 'Deskhand' -ErrorAction SilentlyContinue | Out-Null } catch {}

# (Re)create the service as LocalSystem, auto-start, with restart-on-failure.
& sc.exe stop   DeskhandLauncher *> $null
& sc.exe delete DeskhandLauncher *> $null
Start-Sleep -Milliseconds 700
& sc.exe create DeskhandLauncher binPath= "`"$launcher`"" start= auto obj= LocalSystem DisplayName= "Deskhand Launcher" | Out-Null
& sc.exe description DeskhandLauncher "Keeps Deskhand in the active console session (follows a fast user switch)." | Out-Null
& sc.exe failure DeskhandLauncher reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
& sc.exe start  DeskhandLauncher | Out-Null

Write-Host "DeskhandLauncher installed (session-follow). deskhand-http will track the active console session on port $Port."
