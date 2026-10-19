# Hybrid local-fleet mode

**Goal.** Keep `deskhand-http` as a stable front end on **port 8791 with the identical flat `deskhand_*` tools**,
running in **session 0** as a LocalSystem service, but route **desktop operations (pixels / UIA / input) to a
per-session `deskhand-agent`** living in each interactive session. Session-agnostic operations keep running
locally in the front end. Opt-in behind `DESKHAND_HYBRID=1`; with it unset, 8791 behaves exactly as today.

## Why it's tractable

`IAutomationBackend` is exactly the "needs a desktop" boundary:

- **On the backend seam → route to the session agent:** UIA (`GetTree`/`Find`/`Invoke`/`SetValue`/`SetFocus`/…),
  capture (`CaptureScreen`/`Region`/`Window`/`Element`/`InputDesktop`), input (`Mouse*`/`Type*`/`SendKeys`),
  `GetDesktopState`/`GetForegroundWindow`/`GetFocusedElement`/`GetTopLevelWindows`/`LaunchProcess`.
- **Direct Core services → stay local in the front end:** files, registry, shell, event log, disk/SMART, recycle
  bin, inventory. These don't need a desktop.

So the core change is: in hybrid mode, resolve the host's `IAutomationBackend` to a **`RoutingBackend`** (wrapped
by `GovernedBackend` as today) instead of `LocalAutomationBackend`. One swap covers the whole desktop surface.

## Topology

```
deskhand-http  (session 0, LocalSystem service)          port 8791, flat deskhand_* tools (+ /mcp)
   ├─ direct services (files / registry / shell / events / disk / recycle / inventory)  → run here, locally
   ├─ GovernedBackend( RoutingBackend )  → forwards IAutomationBackend calls to the target session's agent
   └─ agent-connect WebSocket endpoint + AgentRegistry (hosted in deskhand-http; Deskhand.Core.Fleet primitives)
          ▲ ws  (loopback, shared-token auth)
deskhand-agent  (one per interactive session)            = LocalAutomationBackend + GovernedBackend (exists)
   spawned into each session by the launcher; does the pixels / UIA / input IN its session
```

## Decisions (confirmed)

1. **Build all three phases**, validating each on a 2-session sandbox.
2. **`run_command` (shell) defaults to session 0** (the LocalSystem front end); an opt-in **`session=`** runs it in
   another session (as that user) by routing to that session's agent.
3. **Concurrent multi-session is required.** Keep per-session addressing. Default target = **active console
   session**, auto-following switches; a sticky/per-call **`session=`** override pins another session (e.g. RDP).
4. **Stay on 8791 with the identical flat tools**; hybrid is behind `DESKHAND_HYBRID=1`.

## Phases

- **Phase 1 — core routing.** `RoutingBackend : IAutomationBackend`; host the agent endpoint + registry inside
  deskhand-http behind the flag; default routing to the active console agent. Validate: non-streaming UIA / capture
  / input tool calls route to the console agent; direct services still run locally.
- **Phase 2 — streaming + recording.** `/capture/stream`, `/capture/stream.mp4`, and `ScreenRecorder` currently
  grab frames via `ScreenCapture` directly (blind from session 0); make them pull frames from the target agent.
- **Phase 3 — session targeting + shell routing + installer.** Per-request **`?session=<id>`** (or
  `X-Deskhand-Session`) sets `RoutingBackend.TargetSession` for that request/MCP-connection — concurrent callers
  stay independent, no tool signatures change; default = active console. `GET /hybrid/sessions` + `deskhand_sessions`
  list the connected session agents. **`run_command` routing:** defaults to the session-0 front end; `?session=N`
  routes it to that session's agent (runs as that user) via `RemoteAgentObserver`. `install-hybrid.ps1` installs the
  front end as a session-0 service (`DESKHAND_HYBRID=1`) + the launcher in agent mode (`DeskhandAgents` service)
  spawning a `deskhand-agent` into each session; the agent is now published into `deskhand.zip` (agent/).

### Status
- Phases 1 & 2 & 3 implemented and shipping (v0.2.91 → v0.2.94). Phase 1 routing, Phase 2 MJPEG streaming, and
  Phase 3 session-targeting + shell routing validated locally end-to-end.
- **Logon-screen SYSTEM agent (v0.2.94):** `AgentLauncherWorker` now also runs a SYSTEM agent in the console
  session on `winsta0\winlogon` whenever no user is logged on at the console (logon / lock / UAC-secure screen),
  and stops it when a user signs in (the per-user agent takes over). So the console is driveable/capturable before
  login. Driving *input* on the secure desktop stays the separate Windows boundary (uiAccess/SeTcb — gated).
- **Known limit:** routed `run_command` is synchronous (the agent handles its own timeout); the host's
  auto-detach/job polling applies only to the local (session-0) path.

## Governance & edge cases

- The **front end is the authoritative kill switch** — gate in the host's `GovernedBackend` before the RPC; the
  agent trusts the host.
- `el_…` element refs resolve on the agent that owns them — fine within a session; stale if the target session
  changes (documented).
- Before any agent connects (fresh boot), desktop ops return a clear "no agent in session N yet"; non-desktop ops
  still work.
- Streaming = a local-ws RPC capture per frame; the agent captures+scales+returns JPEG.
