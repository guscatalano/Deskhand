<#
  Builds and signs Deskhand.msix (packaged full-trust desktop app: the dashboard, runs as the user).
  Publishes the http app, generates logos, packs with makeappx, and signs.
  Usage:  pwsh installer/build-msix.ps1 [-Config Release] [-CertPfx path -CertPassword pw]
          (no cert args -> a self-signed dev cert is created; install requires trusting it)
#>
param([string]$Config = "Release", [string]$CertPfx, [string]$CertPassword)
$ErrorActionPreference = "Stop"

$here   = $PSScriptRoot
$root   = Split-Path $here -Parent
$stage  = Join-Path $here "stage"
$out    = Join-Path $here "out"
$layout = Join-Path $here "msix\layout"
$assets = Join-Path $layout "Assets"
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Publishing dashboard app..."
dotnet publish (Join-Path $root "src/Deskhand.Http") -c $Config -r win-x64 --self-contained true -o (Join-Path $stage "http") --nologo

# --- locate makeappx / signtool (Windows SDK, else fetch SDK BuildTools) ---
function Find-SdkTool([string]$name) {
    $kits = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $t = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($t) { return $t.FullName }
    }
    $dl = Join-Path $env:TEMP "dh-sdk"
    if (-not (Test-Path (Join-Path $dl "extracted"))) {
        $ver = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json").versions[-1]
        New-Item -ItemType Directory -Force $dl | Out-Null
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$ver/microsoft.windows.sdk.buildtools.$ver.nupkg" -OutFile (Join-Path $dl "sdk.zip")
        Expand-Archive (Join-Path $dl "sdk.zip") -DestinationPath (Join-Path $dl "extracted") -Force
    }
    return (Get-ChildItem (Join-Path $dl "extracted") -Recurse -Filter $name |
            Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1).FullName
}
$makeappx = Find-SdkTool "makeappx.exe"
$signtool = Find-SdkTool "signtool.exe"

# --- logos ---
Add-Type -AssemblyName System.Drawing
if ([IO.Directory]::Exists($layout)) { [IO.Directory]::Delete($layout, $true) }
New-Item -ItemType Directory -Force $assets | Out-Null
function New-Logo([string]$path, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml("#10161D"))
    $f = New-Object System.Drawing.Font("Segoe UI", [float]($size * 0.5), [System.Drawing.FontStyle]::Bold)
    $b = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml("#24BABF"))
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString("D", $f, $b, (New-Object System.Drawing.RectangleF(0, 0, $size, $size)), $sf)
    $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
New-Logo (Join-Path $assets "Square44x44Logo.png") 44
New-Logo (Join-Path $assets "Square150x150Logo.png") 150
New-Logo (Join-Path $assets "StoreLogo.png") 50

Copy-Item (Join-Path $here "msix\AppxManifest.xml") (Join-Path $layout "AppxManifest.xml")
Copy-Item (Join-Path $stage "http") (Join-Path $layout "http") -Recurse

Write-Host "Packing MSIX..."
& $makeappx pack /d $layout /p (Join-Path $out "Deskhand.msix") /o

# --- sign ---
if (-not $CertPfx) {
    Write-Host "Creating self-signed dev cert (CN=Deskhand Dev)..."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Deskhand Dev" -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(2)
    $CertPfx = Join-Path $out "DeskhandDev.pfx"; $CertPassword = "deskhand"
    $sp = ConvertTo-SecureString $CertPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $CertPfx -Password $sp | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $out "DeskhandDev.cer") | Out-Null
}
& $signtool sign /fd SHA256 /f $CertPfx /p $CertPassword (Join-Path $out "Deskhand.msix")
Write-Host "-> $(Join-Path $out 'Deskhand.msix') (signed)"
