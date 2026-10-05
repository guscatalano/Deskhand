<#
.SYNOPSIS
  Register Deskhand to auto-start ELEVATED and WINDOWLESS at user logon.

.DESCRIPTION
  Deskhand drives the interactive desktop, so it must run IN the logged-in session (not as a session-0
  Windows service, which is blind to the desktop). This registers a scheduled task that, at logon, launches
  deskhand-http.exe in that session with HIGHEST privileges  -  which starts it elevated with NO UAC prompt  - 
  and with --hidden so there is no console window.

  Configure the server itself with a deskhand.json next to the exe (token, port, etc.)  -  see
  deskhand.example.json. This script only controls HOW it launches.

  Run this script elevated (registering a highest-privileges task requires admin). If the target user is a
  standard (non-admin) account, "highest privileges" cannot elevate  -  the task will still run, unelevated.

.PARAMETER ExePath
  Path to deskhand-http.exe. Auto-detected next to this script or at C:\Deskhand if omitted.

.PARAMETER TaskName
  Scheduled task name. Default "Deskhand".

.PARAMETER User
  The user to run as (DOMAIN\user). Defaults to the current user. Ignored with -AllUsers.

.PARAMETER AllUsers
  Run for whichever interactive user logs on (BUILTIN\Users), each elevated.

.PARAMETER Start
  Start the task immediately after registering (otherwise it starts at the next logon).

.PARAMETER Uninstall
  Remove the scheduled task.

.EXAMPLE
  .\install-logon-task.ps1 -ExePath C:\Deskhand\deskhand-http.exe -Start
.EXAMPLE
  .\install-logon-task.ps1 -AllUsers
.EXAMPLE
  .\install-logon-task.ps1 -Uninstall
#>
[CmdletBinding()]
param(
  [string]$ExePath,
  [string]$TaskName = "Deskhand",
  [string]$User,
  [switch]$AllUsers,
  [switch]$Start,
  [switch]$Uninstall
)
$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) {
  Write-Error "Run this script elevated  -  registering a highest-privileges scheduled task requires administrator."
  exit 1
}

if ($Uninstall) {
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
  Write-Host "Removed scheduled task '$TaskName'."
  exit 0
}

# Resolve the exe.
if (-not $ExePath) {
  $here = Split-Path -Parent $MyInvocation.MyCommand.Path
  $ExePath = @("$here\deskhand-http.exe", "$here\..\deskhand-http.exe", "C:\Deskhand\deskhand-http.exe") |
             Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
  Write-Error "deskhand-http.exe not found. Pass -ExePath C:\path\to\deskhand-http.exe."
  exit 1
}
$ExePath = (Resolve-Path $ExePath).Path
$workDir = Split-Path -Parent $ExePath

if (-not $User -and -not $AllUsers) { $User = "$env:USERDOMAIN\$env:USERNAME" }

# --hidden = no console window (same as DESKHAND_HIDE_CONSOLE=1).
$action   = New-ScheduledTaskAction -Execute $ExePath -Argument "--hidden" -WorkingDirectory $workDir
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
              -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)

if ($AllUsers) {
  # Run for whichever interactive user logs on, each elevated.
  $trigger   = New-ScheduledTaskTrigger -AtLogOn
  $principal = New-ScheduledTaskPrincipal -GroupId "S-1-5-32-545" -RunLevel Highest   # BUILTIN\Users
  $who       = "any interactive user (BUILTIN\Users)"
} else {
  $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $User
  $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
  $who       = $User
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
  -Description "Deskhand automation server - elevated, windowless, at logon (interactive session)." | Out-Null

Write-Host "Registered scheduled task '$TaskName':"
Write-Host "  launches : $ExePath --hidden"
Write-Host "  as       : $who"
Write-Host "  when     : at logon, in the interactive session, HIGHEST privileges (elevated, no UAC prompt)"
Write-Host "  config   : put a deskhand.json next to the exe for token/port/etc."

if ($Start) {
  Start-ScheduledTask -TaskName $TaskName
  Write-Host "Started now. Check http://127.0.0.1:8791/health"
} else {
  Write-Host "It starts at the next logon. To start now:  Start-ScheduledTask -TaskName '$TaskName'"
}
