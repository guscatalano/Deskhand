# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Deskhand is a Windows desktop-automation server for AI agents: it exposes UI Automation, screen capture,
synthetic input, OCR, on-screen vision, screen recording, and system control over **MCP** (stdio + Streamable-HTTP)
and a **localhost HTTP API**, plus a multi-PC **fleet** and protocol-level **RDP** reach, behind a kill switch and
an append-only audit log. .NET 9 + FlaUI (UIA3). Windows-only.

## Build, run, test

```powershell
dotnet build Deskhand.slnx -c Release          # whole solution
dotnet build src/Deskhand.Core/Deskhand.Core.csproj -c Release   # one project (fast inner loop)

dotnet run --project src/Deskhand.Http -c Release   # HTTP + HTTP-MCP host; prints the URL, open it in a browser
# src/Deskhand.Mcp is the stdio-MCP host (same backend); launched by an MCP client, not run by hand

dotnet test tests/Deskhand.Rdp.Tests -c Release
dotnet test tests/Deskhand.Rdp.Tests -c Release --filter "FullyQualifiedName~WindowWatch"   # a single test/class
```

- **Target framework is `net9.0-windows10.0.19041.0`, x64 only.** Everything is Windows-specific (Win32 P/Invoke,
  WinRT media/OCR, UIA3) — it does not build or run cross-platform.
- Tests use xUnit; some are `SkippableFact` (skip when the required desktop/display/host isn't present), so a green
  run with skips is normal in a headless context.
- There is no linter/formatter/analyzer config and no `Directory.Build.props`/`global.json` — don't invent one.
- **Verifying runtime behavior:** this codebase is heavy on Win32/COM/Media-Foundation interop that a green build
  does not prove correct. The established pattern (see git history) is to write a throwaway console harness in the
  session scratchpad that `ProjectReference`s `Deskhand.Core` and exercises the real service, rather than trusting
  the compile. UIA work must run on an STA thread.

## Versioning convention

`src/Deskhand.Core/BuildInfo.cs` holds `Version` as the **single source of truth** (feeds `/health`, the OpenAPI
doc, and the self-update check against the latest GitHub release tag). The repo's convention is **one version bump
per change, committed to `main`** — recent history is a steady stream of `vX.Y.Z` commits straight to `main` (no
feature branches). Match that: bump `BuildInfo.Version` in the same commit as the change.

## Architecture — the one seam everything routes through

`IAutomationBackend` (`src/Deskhand.Core/IAutomationBackend.cs`) is the single contract every command flows through.
Both hosts and the fleet are **thin shells over it, so they expose identical capabilities and share one audit trail.**
Implementations:

- **`LocalAutomationBackend`** — the in-session backend. UIA/COM work is marshalled onto **one STA thread**
  (`StaExecutor` / `_sta.Invoke(...)`), which is non-negotiable for FlaUI.
- **`GovernedBackend`** — decorator that wraps the local backend: it **audits every call** and **gates input-class
  actions** on `ControlState` (the arm/kill switch). This is what the hosts actually resolve from DI.
- **`RemoteAgentBackend`** — RPC client to a fleet agent (one method per `FleetMethods.*` constant).
- **`RdpBackend`** — protocol-level RDP; UIA members throw `No<T>()` (capture/input only).

### Adding or changing a capability — the cross-cutting checklist

A new tool is **not** a one-file change. Because the surface fans out through the seam, adding a backend method
(`FindStartApps`, `wait_for`, etc.) means touching, in order:

1. `IAutomationBackend.cs` — declare the method.
2. `LocalAutomationBackend.cs` — implement (via `_sta.Invoke` for UIA).
3. `Governance/GovernedBackend.cs` — delegate, wrapped in `Audited(...)` (and a gate if it's an input-class action).
4. `Rdp/RdpBackend.cs` — `No<T>()` (or a real impl) so it still compiles.
5. `Fleet/RemoteAgentBackend.cs` + `Fleet/FleetProtocol.cs` (add the `FleetMethods` constant) + `Fleet/AgentDispatcher.cs`
   (the server-side `switch` arm) — the fleet RPC round-trip.
6. **Both** host surfaces, which must stay in lockstep: `src/Deskhand.McpTools/DeskhandTools.cs` (the MCP tool,
   `[McpServerTool]`) **and** `src/Deskhand.Http/Program.cs` (the HTTP route). Keep MCP tool descriptions and HTTP
   routes mirrored — "both hosts expose identical capabilities" is an invariant, not an aspiration.
7. The test fakes that implement `IAutomationBackend` (e.g. `tests/.../SetOfMarkTests.cs`'s `FakeBackend`) — the
   build breaks otherwise.

Many capabilities live **directly** in a `Deskhand.Core/Services/*` service (not on the backend) and are called
straight from `DeskhandTools.cs` + `Program.cs` with the service injected as a DI singleton — use this lighter path
when the feature doesn't need the STA/governance seam (e.g. `ScreenRecorder`, `WaitService`, `FolderWatchService`).
Core singletons are constructed eagerly in each host's `Program.cs` and registered with `AddSingleton(instance)`.

### Element refs

UIA elements are handed out as opaque string refs (`el_…`) via `ElementRegistry`; acts (`invoke`/`set_value`/…)
resolve the ref fresh each time so a ref survives minor tree churn. `element_from_point` is the escape hatch for
thin/unstable trees (Chromium/Electron). Prefer tree/ref targeting over coordinates.

## Hosting & process model (matters for anything session- or reboot-related)

- **Default is the hybrid local-fleet** (`installer/install-hybrid.ps1`, `DESKHAND_HYBRID=1`, `docs/hybrid-local-fleet.md`):
  deskhand-http runs as a **session-0 LocalSystem service** (`Deskhand`) hosting the flat tools on 8791, and a second
  service (`DeskhandAgents` = the launcher in agent mode) **spawns a per-session `deskhand-agent` into each interactive
  session** (plus a SYSTEM agent at the logon/secure screen). In hybrid, the host's `IAutomationBackend` is a
  `RoutingBackend` (`Deskhand.Core.Fleet`) that forwards desktop ops (UIA/capture/input) to the target session's agent
  (default = active console; `?session=<id>` / `X-Deskhand-Session` to pick another, `GET /hybrid/sessions` to list);
  direct services (files/registry/shell/events/disk) run in the session-0 host. Streaming + ScreenRecorder also pull
  frames from the agent in hybrid. This is the recommended install (stable endpoint, multi-session, reachable before login).
- **Alternative — the session-follow launcher** (`installer/install-session-follow.ps1`): a LocalSystem service
  (`DeskhandLauncher`, `SessionFollowWorker` in `Deskhand.Fleet.Launcher`, selected by `DESKHAND_LAUNCH_EXE`) that keeps
  ONE `deskhand-http.exe` in whichever session owns the physical console — as the logged-in user, elevated (full linked
  token), relocated on a fast user switch, SYSTEM at the logon/secure screen. One instance, no routing.
- **Alternative — the logon scheduled task** (`installer/install-logon-task.ps1`): runs `deskhand-http.exe --hidden`
  in the interactive session, relaunched fresh on each sign-in (the resume trigger for reboot-surviving recordings).
  No switch-user following; bound to one user.
- **Windows Service mode** (`installer/install-service.ps1`, `UseWindowsService`, `ServiceName="Deskhand"`) runs in
  **Session 0 with no interactive desktop** — explicitly *not* for desktop capture.
- **Secure desktop** (UAC/lock/logon): `Deskhand.Broker` (elevated launcher) starts `Deskhand.SecureHelper` as
  SYSTEM to *capture* the secure desktop. Driving input there is a hard Windows boundary — see
  `docs/secure-desktop-input.md`. The UAC consent prompt cannot be driven by synthetic input.
- **Fleet:** `Deskhand.Fleet.Launcher` is a SYSTEM service that spawns a `deskhand-agent.exe` into every active user
  session (`CreateProcessAsUser` onto `winsta0\default`); `Deskhand.Fleet.Server` routes tools by `agentId`.

## Configuration & governance

- Configured by `DESKHAND_*` **environment variables**, optionally declared in a **`deskhand.json`** (loaded into
  the environment at startup for keys not already set — so a real env var always wins; then the file; then the
  default). Search order: `DESKHAND_CONFIG` path → cwd → beside the exe → `%PROGRAMDATA%\Deskhand`. Example:
  `installer/deskhand.example.json`.
- **Governance is first-class:** a kill switch / arm state (`ControlState`), an append-only JSONL **audit log**
  (`%LOCALAPPDATA%\Deskhand\audit`, written by `GovernedBackend`), consent toasts on capture, and **opt-in gates**
  for dangerous surfaces (e.g. `enableShell`, `DESKHAND_ENABLE_REGISTRY_WRITE`, `DESKHAND_ENABLE_SESSION_LAUNCH`).
  When adding a surface that writes/launches/drives, follow this pattern: default-off, env-gated, audited.

## Recordings (actively evolving area)

`Deskhand.Core/Services/ScreenRecorder.cs` + `Mp4SinkWriter.cs`. Three formats: **mp4** (H.264 via in-box Media
Foundation `SinkWriter`, streamed to disk, the default and only non-RAM-bound format — up to 30 min), and **gif/avi**
(every JPEG frame buffered in RAM, wrapped at stop, capped at 5 min). Files land in `%LOCALAPPDATA%\Deskhand\recordings`
with a 24h retention janitor. Features: chunked download over MCP (`record_read`), click/key overlays, markers +
variable-speed segments, and reboot-surviving `resumable:true` recordings (segment files + `<base>.rec.json` state,
`FinalizeForShutdown` on shutdown / `ResumeAll` on startup). The AI-feedback backlog that drives this area is
`docs/feedback-backlog.md`.
