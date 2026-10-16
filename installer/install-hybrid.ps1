<#
Install Deskhand in HYBRID local-fleet mode.

A stable front end (deskhand-http) runs as a LocalSystem service in session 0 and hosts the flat API/MCP on the
port below, but routes DESKTOP operations (UIA / capture / input / the pixels) to a PER-SESSION agent living in
each interactive session. Session-agnostic work (files, registry, shell by default, events, disk) runs in the
front end. A second LocalSystem service (the launcher, in agent mode) spawns a deskhand-agent into each session;
each agent dials back to the front end at ws://127.0.0.1:<port>/agent/connect.

Desktop ops default to the ACTIVE CONSOLE session; target another with ?session=<id> (or X-Deskhand-Session: <id>)
— see deskhand_sessions / GET /hybrid/sessions for the connected sessions. run_command defaults to the session-0
front end; ?session=<id> runs it in that session as that user.

Run elevated (creates LocalSystem services). Requires the hybrid payload (deskhand-http.exe + launcher\ + agent\),
which ships in deskhand.zip.
#>
param(
    [string]$InstallDir = $PSScriptRoot,                                    # folder with deskhand-http.exe + launcher\ + agent\
    [string]$Token = $env:DESKHAND_TOKEN,
    [int]$Port = $(if ($env:DESKHAND_PORT) { [int]$env:DESKHAND_PORT } else { 8791 }),
    [ValidateSet('loopback','any')][string]$Bind = 'loopback',
    [switch]$EnableShell                                                    # allow run_command (front end AND agents)
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this elevated (Administrator) — it creates LocalSystem services.'
}
$http     = Join-Path $InstallDir 'deskhand-http.exe'
$launcher = Join-Path $InstallDir 'launcher\deskhand-launcher.exe'
$agent    = Join-Path $InstallDir 'agent\deskhand-agent.exe'
if (-not (Test-Path $http))     { throw "deskhand-http.exe not found in $InstallDir" }
if (-not (Test-Path $launcher)) { throw "launcher\deskhand-launcher.exe not found in $InstallDir" }
if (-not (Test-Path $agent))    { throw "agent\deskhand-agent.exe not found in $InstallDir" }
if ($Bind -eq 'any' -and -not $Token) { throw "Binding to the network (any) requires -Token." }

# Machine env the services (and the agents they spawn) read. The agents authenticate to /agent/connect with the
# fleet token (= the Deskhand token here); the launcher runs in AGENT mode (DESKHAND_LAUNCH_EXE unset).
[Environment]::SetEnvironmentVariable('DESKHAND_HYBRID',    '1',           'Machine')
[Environment]::SetEnvironmentVariable('DESKHAND_PORT',      "$Port",       'Machine')
[Environment]::SetEnvironmentVariable('DESKHAND_BIND',      $Bind,         'Machine')
if ($Token) { [Environment]::SetEnvironmentVariable('DESKHAND_TOKEN', $Token, 'Machine')
              [Environment]::SetEnvironmentVariable('DESKHAND_FLEET_TOKEN', $Token, 'Machine') }
[Environment]::SetEnvironmentVariable('DESKHAND_FLEET_URL', "ws://127.0.0.1:$Port/agent/connect", 'Machine')
[Environment]::SetEnvironmentVariable('DESKHAND_AGENT_EXE', $agent,        'Machine')
[Environment]::SetEnvironmentVariable('DESKHAND_LAUNCH_EXE', $null,        'Machine')   # agent mode, NOT session-follow
if ($EnableShell) { [Environment]::SetEnvironmentVariable('DESKHAND_ENABLE_SHELL', '1', 'Machine') }

# Don't let the old per-user logon task / session-follow service also bind the port.
try { Disable-ScheduledTask -TaskName 'Deskhand' -ErrorAction SilentlyContinue | Out-Null } catch {}
& sc.exe stop DeskhandLauncher *> $null; & sc.exe delete DeskhandLauncher *> $null

# Front end (deskhand-http) as a LocalSystem service in session 0 — it only routes + runs session-agnostic work.
& sc.exe stop   Deskhand *> $null
& sc.exe delete Deskhand *> $null
Start-Sleep -Milliseconds 700
& sc.exe create Deskhand binPath= "`"$http`"" start= auto obj= LocalSystem DisplayName= "Deskhand (hybrid front end)" | Out-Null
& sc.exe description Deskhand "Deskhand hybrid front end: hosts the API on port $Port and routes desktop ops to per-session agents." | Out-Null
& sc.exe failure Deskhand reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
& sc.exe start  Deskhand | Out-Null

# Launcher (agent mode) as a LocalSystem service — spawns a deskhand-agent into each interactive session.
& sc.exe create DeskhandAgents binPath= "`"$launcher`"" start= auto obj= LocalSystem DisplayName= "Deskhand Agents" | Out-Null
& sc.exe description DeskhandAgents "Spawns a Deskhand agent into each interactive session for the hybrid front end." | Out-Null
& sc.exe failure DeskhandAgents reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
& sc.exe start  DeskhandAgents | Out-Null

Write-Host "Hybrid installed. Front end on port $Port (session 0); a deskhand-agent runs in each interactive session."
Write-Host "Desktop ops default to the active console; GET /hybrid/sessions lists connectable sessions."
