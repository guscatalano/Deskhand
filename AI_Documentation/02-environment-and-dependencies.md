# 02 — Environment and Dependencies

## .NET SDK

- Build machine has SDK **10.0.202** installed, but there is **no `global.json`**, so any SDK able to
  build `net9.0` targets works. Install the **.NET 9 SDK or newer**.
- All projects target **`net9.0-windows10.0.19041.0`**.

### Why the `-windows10.0.19041.0` OS version matters

The TFM is not just `net9.0-windows`; it pins the **Windows SDK version `10.0.19041.0`** (Windows 10 2004 /
20H1). This is required because `WgcCapture.cs` uses **WinRT projections** for the Windows.Graphics.Capture
APIs:

- `Windows.Graphics.Capture` — `GraphicsCaptureItem`, `GraphicsCaptureSession`,
  `Direct3D11CaptureFramePool`, `Direct3D11CaptureFrame`
- `Windows.Graphics.DirectX` — `DirectXPixelFormat`
- `Windows.Graphics.DirectX.Direct3D11` — `IDirect3DDevice`, `IDirect3DSurface`
- `WinRT` — `MarshalInterface<T>`, `MarshalInspectable<T>` (from the C#/WinRT runtime)

Targeting a Windows-version TFM (`net9.0-windows10.0.19041.0`) is what makes the C#/WinRT projections for
those namespaces available to the project **without any extra WinRT NuGet package**. The `19041` floor also
matches the runtime requirement (WGC needs Windows 10 1903+; the free-threaded frame pool
`CreateFreeThreaded` needs 2004+). `WgcCapture` still checks `GraphicsCaptureSession.IsSupported()` at
runtime and falls back to PrintWindow when unavailable.

## Per-project SDK type and settings

| Project | SDK | OutputType | AssemblyName | Notable props |
|---|---|---|---|---|
| `Deskhand.Core` | `Microsoft.NET.Sdk` | (library) | Deskhand.Core | `AllowUnsafeBlocks=true`, `Platforms=x64` |
| `Deskhand.Http` | `Microsoft.NET.Sdk.Web` | (web) | `deskhand-http` | `RootNamespace=Deskhand.Http`, `InvariantGlobalization=true`, `Platforms=x64` |
| `Deskhand.Mcp` | `Microsoft.NET.Sdk` | `Exe` | `deskhand-mcp` | `Platforms=x64` |
| `Deskhand.Ui` | `Microsoft.NET.Sdk` | (library) | Deskhand.Ui | **`UseWindowsForms=true`**, `Platforms=x64` |
| `Deskhand.SecureHelper` | `Microsoft.NET.Sdk` | `Exe` | `deskhand-secure` | `Platforms=x64` |
| `Deskhand.Broker` | `Microsoft.NET.Sdk` | `Exe` | `deskhand-broker` | `Platforms=x64` |

Common to every project: `<ImplicitUsings>enable</ImplicitUsings>`, `<Nullable>enable</Nullable>`,
`<Platforms>x64</Platforms>`.

- **`AllowUnsafeBlocks=true`** is on `Deskhand.Core` because `WgcCapture.Encode` copies mapped GPU memory
  with `unsafe` pointer arithmetic (`byte*` + `Buffer.MemoryCopy`).
- **`UseWindowsForms=true`** is only on `Deskhand.Ui`, which draws the on-screen screenshot toast with a
  borderless `System.Windows.Forms.Form`.
- **`InvariantGlobalization=true`** on the HTTP host trims ICU.
- **`Platforms=x64`** everywhere: the WGC/D3D11 interop and the SendInput struct marshalling assume a
  64-bit process; mixing bitness with UIA COM proxies is a source of bugs. The `.slnx` pins the `x64`
  platform for Broker, Mcp, SecureHelper, and Ui explicitly.

## NuGet packages (exact versions)

Declared in `Deskhand.Core.csproj`:

```xml
<PackageReference Include="FlaUI.Core" Version="4.0.0" />
<PackageReference Include="FlaUI.UIA3" Version="4.0.0" />
<PackageReference Include="System.Drawing.Common" Version="9.0.0" />
<PackageReference Include="Vortice.Direct3D11" Version="3.8.3" />
```

Declared in `Deskhand.Mcp.csproj`:

```xml
<PackageReference Include="ModelContextProtocol" Version="2.2.0" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
```

| Package | Version | Used by | For |
|---|---|---|---|
| `FlaUI.Core` | 4.0.0 | Core | typed `AutomationElement`, `ConditionFactory`, `TreeScope`, patterns |
| `FlaUI.UIA3` | 4.0.0 | Core | the UIA3 provider (`UIA3Automation`) |
| `System.Drawing.Common` | 9.0.0 | Core, Ui | `Bitmap`, `Graphics.CopyFromScreen`, JPEG/PNG encoders |
| `Vortice.Direct3D11` | 3.8.3 | Core | managed D3D11/DXGI for the WGC staging-texture readback |
| `ModelContextProtocol` | 2.2.0 | Mcp | `AddMcpServer`, `[McpServerTool]`, `ImageContentBlock` |
| `Microsoft.Extensions.Hosting` | 9.0.0 | Mcp | `Host.CreateApplicationBuilder`, DI, logging |

`Deskhand.Http` pulls ASP.NET Core from the `Microsoft.NET.Sdk.Web` shared framework (no explicit HTTP
package). `Deskhand.Ui` pulls WinForms from `UseWindowsForms`. `Deskhand.SecureHelper` and
`Deskhand.Broker` reference no NuGet packages beyond the SDK; they use raw P/Invoke.

## Project references

- `Deskhand.Http` → `Deskhand.Core`, `Deskhand.Ui`
- `Deskhand.Mcp` → `Deskhand.Core`, `Deskhand.Ui`
- `Deskhand.Ui` → `Deskhand.Core`
- `Deskhand.SecureHelper` → `Deskhand.Core`
- `Deskhand.Broker` → (none — self-contained P/Invoke)

## Runtime prerequisites

- Windows 10 2004 (build 19041) or later for the WGC path; older builds fall back to PrintWindow.
- Run **unelevated** to automate normal apps. Elevated / secure-desktop targets are refused by UIPI /
  the OS and reported with a clear error (by design). See `11-secure-desktop.md`.

## Build & run

```powershell
dotnet build Deskhand.slnx -c Release

# HTTP host (opens a dashboard)
$env:DESKHAND_PORT  = "8791"      # optional, default 8791
$env:DESKHAND_TOKEN = "secret"    # optional; only affects non-browser clients
dotnet run --project src/Deskhand.Http -c Release

# MCP host (stdio) — usually launched by an MCP client, not by hand
dotnet build src/Deskhand.Mcp -c Release
```
