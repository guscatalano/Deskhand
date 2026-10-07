<#
  Deskhand secure-desktop input provisioning.

  Makes the bundled uiAccess helper (deskhand-uia.exe) able to drive the SECURE desktop (UAC / lock /
  logon) on THIS machine, using the documented accessibility path: a signed, uiAccess=true binary in a
  secure location whose signature chains to a trusted root on this machine.

  Security: this deliberately weakens UAC on this machine (a signed binary can auto-approve prompts). It
  is opt-in, audited by Deskhand, and reversible (-Deprovision). To keep it from leaving a reusable
  "sign anything as trusted" key behind, the per-machine code-signing key is DELETED right after signing —
  the already-signed helper stays valid, but no key remains to mint new uiAccess binaries.

  Run elevated (Administrator).
#>
param(
    [string]$ExeDir = "C:\Program Files\Deskhand\uia",
    [string]$SourceDir = "",          # folder holding the published deskhand-uia.exe to install into $ExeDir
    [switch]$Deprovision
)
$ErrorActionPreference = 'Stop'
$subject = 'CN=Deskhand Secure-Input (per-machine)'

function Test-Admin {
    (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) { Write-Error 'provision-uia must run elevated (Administrator).'; exit 1 }

function Remove-OurCerts {
    foreach ($store in 'My','Root','TrustedPublisher') {
        Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $subject } | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

if ($Deprovision) {
    Remove-OurCerts
    if (Test-Path $ExeDir) { Remove-Item $ExeDir -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Output 'deprovisioned: removed cert(s) and helper.'
    exit 0
}

# Install the helper into a SECURE location (Program Files is admin-only, which is what uiAccess requires).
if ($SourceDir -and (Test-Path $SourceDir)) {
    New-Item -ItemType Directory -Force $ExeDir | Out-Null
    Copy-Item (Join-Path $SourceDir '*') $ExeDir -Recurse -Force
}
$exe = Join-Path $ExeDir 'deskhand-uia.exe'
if (-not (Test-Path $exe)) { Write-Error "deskhand-uia.exe not found at $exe (pass -SourceDir)."; exit 1 }

# Start clean (idempotent), then mint a fresh per-machine code-signing cert with a non-exportable key.
Remove-OurCerts
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
    -CertStoreLocation Cert:\LocalMachine\My -KeyUsage DigitalSignature `
    -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(5)

$sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
if ($sig.Status -ne 'Valid') { Remove-OurCerts; Write-Error "signing failed: $($sig.Status) - $($sig.StatusMessage)"; exit 1 }

# Trust it on THIS machine only: public cert into the machine Root + TrustedPublisher stores.
$pub = Join-Path $env:TEMP 'deskhand-uia-pub.cer'
Export-Certificate -Cert $cert -FilePath $pub -Type CERT | Out-Null
Import-Certificate -FilePath $pub -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $pub -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Remove-Item $pub -Force -ErrorAction SilentlyContinue

# Destroy the signing key: delete the cert (and its non-exportable key) from the My store. The embedded
# signature stays valid (it chains to the trusted Root), but there is no longer a key to sign new binaries.
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $subject } | Remove-Item -Force

$v = Get-AuthenticodeSignature $exe
Write-Output "provisioned: signStatus=$($v.Status) exe=$exe secureUiaPaths=$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableSecureUIAPaths -ErrorAction SilentlyContinue).EnableSecureUIAPaths)"
