# Deskhand installers

Two installers, matching how Deskhand is meant to run.

## Which one?

Deskhand's normal apps — the **dashboard/HTTP server** and the **MCP server** — run **as the
logged-in user, no SYSTEM and no elevation** (they drive the Default desktop with your own rights).
Elevation is only needed to automate *elevated* windows; SYSTEM is only for the optional
secure-desktop helper and the fleet launcher service.

| Installer | Scope | Contains | Services? |
|---|---|---|---|
| **MSIX** (`Deskhand.msix`) | per-user | Dashboard app (full-trust, runs as user) | No (MSIX can't install services) |
| **MSI** (`Deskhand.msi`) | machine-wide | Dashboard + MCP server, Start-menu shortcut | Can add the fleet launcher / secure services |

Both bundle a **self-contained** .NET runtime — no prerequisite install.

## Build

```powershell
pwsh installer/build-msi.ps1      # -> installer/out/Deskhand.msi
pwsh installer/build-msix.ps1     # -> installer/out/Deskhand.msix  (+ self-signed DeskhandDev.cer)
```

`build-msi.ps1` publishes the apps self-contained and packs with **WiX 5** (`dotnet tool install --global wix`).
`build-msix.ps1` publishes the dashboard, generates logos, packs with **makeappx**, and **signs** —
locating `makeappx`/`signtool` from the Windows SDK, or fetching `Microsoft.Windows.SDK.BuildTools`.

## Install

**MSI** (admin): `msiexec /i Deskhand.msi` — installs to `%ProgramFiles%\Deskhand`, adds a
"Deskhand Console" Start-menu shortcut, and an Add/Remove-Programs entry. Silent: `/qn`.

**MSIX**: the package is signed with a **self-signed dev cert**, so first trust it, then install:

```powershell
Import-Certificate -FilePath DeskhandDev.cer -CertStoreLocation Cert:\LocalMachine\Root   # admin, once
Add-AppxPackage Deskhand.msix
```

For real distribution, sign the MSIX with a trusted code-signing certificate:
`pwsh build-msix.ps1 -CertPfx mycert.pfx -CertPassword ****`.

## Notes

- The MSI/MSIX ship the **per-user** apps. The fleet **launcher** (a Windows Service) and the
  **secure-desktop** helper/broker run as SYSTEM and are installed separately (`sc create …`) — see
  the top-level README.
- Artifacts (`installer/stage`, `installer/out`) are git-ignored; CI builds them (see
  `.github/workflows/build.yml`).
