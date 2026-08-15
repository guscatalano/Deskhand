# 12 — Recreation Walkthrough

A concrete, ordered recipe to rebuild Deskhand from an empty folder. Follow the phases in order; each
milestone is independently verifiable. Cross-references point at the detail docs.

## Prerequisites

- Windows 10 build 19041 (2004) or newer, x64.
- .NET 9 SDK (or newer). No `global.json`.
- An MCP client (Claude Desktop / Claude Code) for Phase 1's MCP verification (optional).

## Step 0 — Solution and projects

Create the repo root and a `src/` folder, then a `.slnx` solution with six projects. Create them **in
dependency order** so references resolve:

1. `src/Deskhand.Core` — `Microsoft.NET.Sdk`, library.
2. `src/Deskhand.Ui` — `Microsoft.NET.Sdk`, library, `UseWindowsForms=true`, refs Core.
3. `src/Deskhand.Http` — `Microsoft.NET.Sdk.Web`, refs Core + Ui.
4. `src/Deskhand.Mcp` — `Microsoft.NET.Sdk`, `Exe`, refs Core + Ui.
5. `src/Deskhand.SecureHelper` — `Microsoft.NET.Sdk`, `Exe`, refs Core.
6. `src/Deskhand.Broker` — `Microsoft.NET.Sdk`, `Exe`, no refs.

Every project: `net9.0-windows10.0.19041.0`, `ImplicitUsings=enable`, `Nullable=enable`, `Platforms=x64`.
Add `AllowUnsafeBlocks=true` to Core. Set `AssemblyName` to `deskhand-http`/`deskhand-mcp`/`deskhand-secure`/
`deskhand-broker` on those four. See `02-environment-and-dependencies.md` for the exact csproj contents and
package versions.

Add packages to Core (`FlaUI.Core` 4.0.0, `FlaUI.UIA3` 4.0.0, `System.Drawing.Common` 9.0.0,
`Vortice.Direct3D11` 3.8.3) and to Mcp (`ModelContextProtocol` 2.2.0, `Microsoft.Extensions.Hosting` 9.0.0).

**Verify:** `dotnet build Deskhand.slnx` succeeds with empty projects.

---

## Phase 1 — Prove the spine (Default desktop, end to end)

Goal: HTTP + MCP hosts driving the local Default desktop through `IAutomationBackend`.

### 1.1 Interop and DPI
- `Interop/NativeMethods.cs` — the P/Invoke surface: DPI (`SetProcessDpiAwarenessContext`, `GetDpiForMonitor`),
  system metrics (`SM_*VIRTUALSCREEN`), windows (`GetForegroundWindow`, `GetWindowRect`, `PrintWindow`+
  `PW_RENDERFULLCONTENT`), foreground (`SetForegroundWindow`, `BringWindowToTop`, `ShowWindow`, `IsIconic`,
  `GetWindowThreadProcessId`, `AttachThreadInput`, both `SystemParametersInfo` overloads), desktop
  (`OpenInputDesktop`, `CloseDesktop`, `SetThreadDesktop`, `GetThreadDesktop`, `GetCurrentThreadId`,
  `GetUserObjectInformation`), monitors (`EnumDisplayMonitors`, `GetMonitorInfo`), `VkKeyScan`, and
  `SendInput` with the `INPUT`/`MOUSEINPUT`/`KEYBDINPUT`/`INPUTUNION` structs. Exact signatures/constants in
  `06-input-and-dpi.md` and `11-secure-desktop.md`.
- `DpiHelper.EnablePerMonitorV2()` (03/06).

### 1.2 DTOs and exceptions
- `Models.cs` — all records (03).
- `Exceptions.cs` — the six typed exceptions (03).

### 1.3 STA discipline and element registry
- `StaExecutor.cs` — one STA thread + `BlockingCollection` queue + `TaskCompletionSource` marshalling (03).
- `Elements/ElementRegistry.cs` — opaque `el_…` refs, capped dictionary, re-resolution recipe fields (03).

**Verify:** unit-call `StaExecutor.Invoke(() => 2+2)` returns 4 from the STA thread.

### 1.4 Services
- `Services/DesktopInfo.cs` — machine info, monitors (+ per-monitor DPI), virtual screen, desktop state (11).
- `Services/UiaService.cs` — FlaUI automation, GetDesktop, ConditionFactory, tree walk, find, patterns,
  all-properties, resolve/re-resolve, `Register`/`BuildInfo`, `SupportedPatterns`. Mind the **TrueCondition
  gotcha** (04).
- `Services/WindowService.cs` — `ForceForeground` (06).
- `Services/InputInjector.cs` — mouse (absolute+VIRTUALDESK) and keyboard (Unicode + VkKeyScan chords) (06).
- `Services/ScreenCapture.cs` — GDI screen/region/bounds + window (WGC-then-PrintWindow) (05).
- `Services/WgcCapture.cs` — the full Direct3D11/WGC pipeline. Mind the **uint texture dims** and
  **PrintWindow-black** gotchas (05).

**Verify (Core only, temporary console):** capture the primary monitor to a PNG; walk the foreground
window's tree; `MouseMove` to screen center. Confirm the PNG opens and coordinates land correctly on a
second monitor (validates VIRTUALDESK + DPI).

### 1.5 The seam and the local backend
- `IAutomationBackend.cs` — the interface (03).
- `LocalAutomationBackend.cs` — STA marshalling for UIA/capture/input; direct calls for `DesktopInfo` and
  `SecureCapture`; off-STA `WaitForElement` poll loop (03).

### 1.6 HTTP host (minimal, ungoverned first)
- `Deskhand.Http/Program.cs` — DPI first; Kestrel `ListenLocalhost(port)`; camelCase JSON; register
  `IAutomationBackend`; map the endpoints (07); add the loopback/Origin/token security middleware and the
  error→status middleware.
- `wwwroot/index.html` — the dashboard (08) — can be stubbed now, fleshed out later.

**Verify:** `dotnet run --project src/Deskhand.Http`, open `http://127.0.0.1:8791`, list windows, expand a
tree, capture a monitor, click the image in control mode to move the mouse. Hit `/health` with `curl`.

### 1.7 MCP host
- `Deskhand.Mcp/DeskhandTools.cs` — `[McpServerToolType]` + all `[McpServerTool]` methods; `AsImage` returns
  a text summary + `ImageContentBlock` with **raw bytes** (09).
- `Deskhand.Mcp/Program.cs` — DPI first; Generic Host; **logging to STDERR**; register backend; `AddMcpServer
  ().WithStdioServerTransport().WithToolsFromAssembly()` (09).

**Verify:** register via `mcp.json` (09) and call `deskhand_list_windows`, `deskhand_capture_screen`
(image comes back), `deskhand_get_tree`, `deskhand_invoke` on a button.

**Milestone: Phase 1 done** — both hosts drive the Default desktop through the one seam.

---

## Phase 2 — Secure desktop coverage

Goal: capture whichever desktop owns input, including `Winsta0\Winlogon` when run as SYSTEM.

### 2.1 The primitive
- `Services/SecureCapture.cs` — MTA attach thread (`OpenInputDesktop` + `SetThreadDesktop` + GDI), returning
  `InputDesktopResult`. Mind the **STA-vs-MTA / ERROR_BUSY gotcha** (11).
- Wire `CaptureInputDesktop` through `IAutomationBackend`/`LocalAutomationBackend` (calling `SecureCapture`
  **directly**, never via STA), the HTTP `POST /capture/input-desktop` endpoint, and MCP
  `deskhand_capture_input_desktop`.

**Verify:** call it as a normal user → it returns `Default`. Add the dashboard Secure-desktop panel.

### 2.2 Secure Helper
- `Deskhand.SecureHelper/Program.cs` — `capture <out.png> [--jpeg]`, reports identity/`IsSystem`.

**Verify:** run as a user → saves Default. Run `psexec -s -i <consoleSession> deskhand-secure.exe capture …`
and trigger a UAC prompt → saves the secure desktop.

### 2.3 Broker
- `Deskhand.Broker/Interop.cs` + `Program.cs` — winlogon-token duplication + `CreateProcessAsUser` (11).

**Verify (real elevated console):** `deskhand-broker deskhand-secure.exe capture C:\temp\secure.png` launches
the helper as SYSTEM and captures the secure desktop. (Not exercisable in a sandbox — needs elevation +
SeDebugPrivilege.)

**Milestone: Phase 2 done** — secure-desktop *capture* proven; input remains gated (needs signed uiAccess).

---

## Phase 3 — Reliability & safety

Goal: robust element handles, full patterns, DPI correctness, and the governance controls.

Much of the reliability is already in Phase 1 (element re-resolution in `ElementRegistry`/`UiaService`,
Per-Monitor-v2 DPI, defensive `Safe*` property reads, full pattern coverage). Add the governance layer:

### 3.1 Governance types (`Deskhand.Core/Governance/`)
- `ICaptureNotifier.cs` — the toast interface.
- `ControlState.cs` — the four switches + `FromEnvironment()` (10).
- `AuditLog.cs` — JSONL under `%LOCALAPPDATA%\Deskhand\audit`, `HashImage` (10).
- `KillSwitch.cs` — Ctrl+Alt+Pause global hotkey on its own message-loop thread (10).
- `GovernedBackend.cs` — the decorator: `RequireInput`/`RequireCapture` gates, `Audited`/`Capture` wrappers,
  content hashing, `NotifyCapture` (10).

### 3.2 The toast (`Deskhand.Ui/ToastNotifier.cs`)
- WinForms non-activating bottom-right toast on a private STA thread (10).

### 3.3 Wire governance into both hosts
- Replace the raw `LocalAutomationBackend` registration with
  `new GovernedBackend(new LocalAutomationBackend(), controlState, auditLog, captureNotifier)`.
- Register `ControlState`/`AuditLog` singletons; construct `using var killSwitch = new KillSwitch(...)`.
- HTTP: add `GET/POST /control`. MCP: add `deskhand_control_status`/`deskhand_arm`/`deskhand_disarm`.
- Flesh out the dashboard Safety panel + arm button (08).

**Verify:** every action appears in today's `audit-*.jsonl`. Disarm (button, `POST /control`, MCP tool, or
Ctrl+Alt+Pause) → input/capture return `403 disarmed`, reads still work. Each capture pops the on-screen
toast; `DESKHAND_DISABLE_CAPTURE_TOAST=1` suppresses it. `DESKHAND_START_DISARMED=1` starts disarmed.

**Milestone: Phase 3 done** — the system is safe, auditable, and interruptible.

---

## Recreation checklist (files, by project)

- **Core:** `IAutomationBackend.cs`, `LocalAutomationBackend.cs`, `StaExecutor.cs`, `Models.cs`,
  `Exceptions.cs`, `DpiHelper.cs`, `Interop/NativeMethods.cs`, `Elements/ElementRegistry.cs`,
  `Services/{UiaService,ScreenCapture,WgcCapture,SecureCapture,InputInjector,DesktopInfo,WindowService}.cs`,
  `Governance/{ICaptureNotifier,ControlState,AuditLog,KillSwitch,GovernedBackend}.cs`.
- **Ui:** `ToastNotifier.cs`.
- **Http:** `Program.cs`, `wwwroot/index.html`.
- **Mcp:** `Program.cs`, `DeskhandTools.cs`.
- **SecureHelper:** `Program.cs`.
- **Broker:** `Program.cs`, `Interop.cs`.

## Pitfalls to avoid (collected)

1. **DPI first.** `DpiHelper.EnablePerMonitorV2()` must be the first line of each host's `Main`/top-level
   statements, before any pixel/window/coordinate work.
2. **UIA is single-threaded COM.** Everything UIA/capture/input goes through the one STA thread — except the
   secure-desktop attach, which needs its own MTA thread.
3. **`SetThreadDesktop` needs MTA** (ERROR_BUSY on STA). Never run `SecureCapture` on the UIA STA thread.
4. **`TrueCondition` has no parameterless ctor** — use argument-less `FindAllChildren/FindAllDescendants`.
5. **PrintWindow is black on GPU windows** — try WGC first; PrintWindow is the pre-1903 fallback.
6. **`ImageContentBlock.Data` is raw bytes** (MCP), but the HTTP JSON body needs base64.
7. **Vortice texture dims are `uint`** — cast to `int`.
8. **MCP logs to stderr only** — stdout is the protocol.
9. **`MOUSEEVENTF_VIRTUALDESK`** on absolute mouse events, or secondary monitors are unreachable.
10. **Foreground lock** — plain `SetForegroundWindow` only flashes the taskbar; use the
    `AttachThreadInput` + zero-`SPI_SETFOREGROUNDLOCKTIMEOUT` dance.
11. **"Foreground window" is the tool when it's focused** — prefer `/windows` to target a specific app.
