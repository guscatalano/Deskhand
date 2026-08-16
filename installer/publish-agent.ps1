# Publishes a self-contained, single-file deskhand-agent.exe so it can run on a remote machine that may
# not have the .NET runtime. Required for "Install native agent over RDP" (the ＋RDP tile's install
# button / POST /fleet/rdp/install): the connector launches this exe on the remote via \\tsclient.
#
# Usage:
#   .\installer\publish-agent.ps1            # publishes next to the built deskhand-rdp.exe (auto-found)
#   .\installer\publish-agent.ps1 -OutDir C:\some\dir
param([string]$OutDir = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $OutDir) {
  $rdp = Get-ChildItem "$root\src\Deskhand.Rdp\bin" -Recurse -Filter deskhand-rdp.exe -EA SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
  $OutDir = if ($rdp) { Split-Path $rdp.FullName } else { "$root\publish" }
}

Write-Host "Publishing self-contained deskhand-agent.exe -> $OutDir"
dotnet publish "$root\src\Deskhand.Fleet.Agent" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $OutDir
Write-Host "Done. The RDP connector will find it as $OutDir\deskhand-agent.exe (or set DESKHAND_AGENT_PATH)."
