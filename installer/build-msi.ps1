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

# Pin WiX to v5: v6/v7 gate the build behind the Open Source Maintenance Fee EULA (error WIX7015), which
# fails non-interactively (e.g. in CI). v5 uses the same v4-schema .wxs and needs no acceptance.
$wixVersion = "5.0.2"
if (Get-Command wix -ErrorAction SilentlyContinue) {
    dotnet tool update --global wix --version $wixVersion 2>&1 | Out-Null
} else {
    Write-Host "Installing WiX $wixVersion..."
    dotnet tool install --global wix --version $wixVersion 2>&1 | Out-Null
}
$env:PATH += ";$env:USERPROFILE\.dotnet\tools"

Write-Host "Building MSI (WiX $(wix --version))..."
$msi = Join-Path $out "Deskhand.msi"
wix build (Join-Path $here "Deskhand.wxs") -arch x64 -d "StageDir=$stage" -o $msi
# $ErrorActionPreference=Stop does NOT catch a native command's non-zero exit — check it, or a failed
# wix build silently "succeeds" and the MSI goes missing (which is exactly what happened in CI).
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $msi)) { throw "wix build reported success but $msi is missing" }
Write-Host "-> $msi ($([int]((Get-Item $msi).Length/1MB)) MB)"
