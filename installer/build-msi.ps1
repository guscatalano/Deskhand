<#
  Builds Deskhand.msi: publishes the per-user apps (dashboard + MCP) self-contained, then WiX-packs.
  Usage:  pwsh installer/build-msi.ps1 [-Config Release]
#>
param([string]$Config = "Release")
$ErrorActionPreference = "Stop"

$here  = $PSScriptRoot
$root  = Split-Path $here -Parent
$stage = Join-Path $here "stage"
$out   = Join-Path $here "out"
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Publishing self-contained apps..."
dotnet publish (Join-Path $root "src/Deskhand.Http") -c $Config -r win-x64 --self-contained true -o (Join-Path $stage "http") --nologo
dotnet publish (Join-Path $root "src/Deskhand.Mcp")  -c $Config -r win-x64 --self-contained true -o (Join-Path $stage "mcp")  --nologo

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "Installing WiX tool..."
    dotnet tool install --global wix | Out-Null
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

Write-Host "Building MSI..."
wix build (Join-Path $here "Deskhand.wxs") -arch x64 -d "StageDir=$stage" -o (Join-Path $out "Deskhand.msi")
Write-Host "-> $(Join-Path $out 'Deskhand.msi')"
