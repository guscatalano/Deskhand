<#
.SYNOPSIS
  Install deskhand-http as an auto-start Windows service. Run elevated (Administrator).

.DESCRIPTION
  Creates a "Deskhand" service that runs deskhand-http.exe. Configuration is passed via machine-scope
  environment variables (the service, running as LocalSystem, reads them at startup): DESKHAND_PORT,
  and optionally DESKHAND_TOKEN / DESKHAND_BIND / DESKHAND_TLS / capability flags.

  NOTE: a service has no interactive desktop (Session 0), so UI Automation / capture / input will target
  Session 0, not your logged-in session. For driving the logged-in user's desktop, run Deskhand in that
  session (or use the Fleet Launcher, which spawns agents into each interactive session). This service mode
  is best for the read-only inventory, files, shell, firewall, metrics, and fleet-server style uses.

.EXAMPLE
  ./install-service.ps1 -Port 8791 -Token "a-strong-secret" -Bind any
#>
param(
  [string]$ExePath = (Join-Path $PSScriptRoot 'deskhand-http.exe'),
  [string]$Name = 'Deskhand',
  [int]$Port = 8791,
  [string]$Token = '',
  [string]$Bind = '',
  [string]$Tls = ''
)
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) {
  throw 'Run this elevated (Administrator).'
}
if (-not (Test-Path $ExePath)) { throw "deskhand-http.exe not found at $ExePath (point -ExePath at your install)." }

Write-Host "Setting machine environment for the service..."
[Environment]::SetEnvironmentVariable('DESKHAND_PORT', "$Port", 'Machine')
if ($Token) { [Environment]::SetEnvironmentVariable('DESKHAND_TOKEN', $Token, 'Machine') }
if ($Bind)  { [Environment]::SetEnvironmentVariable('DESKHAND_BIND',  $Bind,  'Machine') }
if ($Tls)   { [Environment]::SetEnvironmentVariable('DESKHAND_TLS',   $Tls,   'Machine') }

if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
  Write-Host "Service '$Name' exists — stopping and removing it first..."
  sc.exe stop $Name | Out-Null
  Start-Sleep -Seconds 2
  sc.exe delete $Name | Out-Null
  Start-Sleep -Seconds 1
}

Write-Host "Creating service '$Name'..."
& sc.exe create $Name binPath= "`"$ExePath`"" start= auto DisplayName= "Deskhand automation server" | Out-Null
& sc.exe description $Name "Deskhand — local Windows desktop-automation HTTP/MCP server." | Out-Null
& sc.exe start $Name | Out-Null

Write-Host "Done. 'Deskhand' is installed and started (auto-start)."
Write-Host "  Health: http://127.0.0.1:$Port/health"
Write-Host "  Manage: sc.exe stop Deskhand / start Deskhand / delete Deskhand"
