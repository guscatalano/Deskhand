# Deskhand — local HTTP automation server

A **localhost-only HTTP server** that exposes Windows **UI Automation**, **screen capture**, and
**synthetic input** for the machine it runs on. It is the HTTP surface of the Deskhand design
(see `Deskhand.html` for the full architecture doc). Built on **.NET 9 + FlaUI (UIA3)**.

This is the **Phase 1** deliverable: the single-machine, in-session **Default desktop** path.
The secure desktop (UAC / lock / logon) is *reported* by `/desktop/state` but driving it requires
the SYSTEM "Secure Helper" (Phase 2), which is not in this build.

## Layout

```
Deskhand.slnx
src/
  Deskhand.Core/          # backend: IAutomationBackend + LocalAutomationBackend
    Services/             #   UiaService (FlaUI), ScreenCapture (GDI), InputInjector (SendInput),
                          #   DesktopInfo, SecureCapture (input-desktop attach — Phase 2)
    Elements/             #   ElementRegistry — opaque refs + re-resolution recipe
    Interop/              #   P/Invoke (SendInput, PrintWindow, monitors, desktop attach, DPI)
    StaExecutor.cs        #   single STA thread that serializes all COM/UIA work
  Deskhand.Http/          # ASP.NET Core minimal API + web dashboard (wwwroot/index.html)
  Deskhand.Mcp/           # MCP server (stdio) — same IAutomationBackend, exposed as MCP tools
  Deskhand.SecureHelper/  # Phase 2: runs as SYSTEM in the console session to capture the secure desktop
  Deskhand.Broker/        # Phase 2: elevated launcher that starts the Secure Helper as SYSTEM
```

Both hosts — **HTTP** (`Deskhand.Http`) and **MCP** (`Deskhand.Mcp`) — are thin shells over the
same `IAutomationBackend`, so they expose identical capabilities.

The whole surface routes through `IAutomationBackend`, so a future gRPC **fleet-agent** backend or a
protocol-level **RDP** backend can implement the same contract without changing the HTTP layer.

## Build & run

```powershell
dotnet build Deskhand.slnx -c Release

# optional: pin a token and port (otherwise a random token is generated and printed)
$env:DESKHAND_TOKEN = "your-secret"
$env:DESKHAND_PORT  = "8791"          # default 8791

dotnet run --project src/Deskhand.Http -c Release
```

On start it prints the URL. **Just open it in a browser** — no token needed.

## Web dashboard

Open **http://127.0.0.1:8791** in any browser. The single-page console lets you:

- see live machine / desktop / monitor status (auto-refreshing);
- capture a monitor (or the whole virtual desktop) and click the screenshot to read desktop
  coordinates — or, in *control mode*, to move/click there for real;
- explore the UIA tree (foreground / focused / desktop root, lazy-expand), and per element
  **Invoke / Focus / Capture / Highlight** it on the screenshot;
- drive mouse and keyboard from the Input panel;
- watch an activity log of every request.

The dashboard talks to the API same-origin, so it needs no token.

## Security

No bearer token is required for the browser dashboard. The server is protected by:

- **Loopback only** — Kestrel binds `127.0.0.1` / `::1`; no external interface is ever exposed.
- **Host check** — non-loopback `Host` headers are rejected (DNS-rebinding defense).
- **Cross-site block** — any request whose `Origin` isn't this server is rejected `403`, so other
  web pages in your browser cannot reach it. No CORS headers are emitted.
- **Optional token** — set `DESKHAND_TOKEN` to require `Authorization: Bearer <token>` from
  *non-browser* clients (curl / scripts). The same-origin dashboard still needs none.
- **No stealth** — input is honest `SendInput`; there is no anti-detection behavior.
- Run **unelevated** to automate normal apps. Elevated / secure-desktop targets are refused with a
  clear error (UIPI / secure desktop), by design.

## Endpoints

All bodies and responses are JSON (camelCase). `reference` values (`el_…`) come from any read call.

| Method & path | Body | Purpose |
|---|---|---|
| `GET /health` | — | Liveness (no auth) |
| `GET /machine` | — | Machine, monitors, virtual screen, desktop state |
| `GET /desktop/state` | — | `default` / `secure` / `screensaver` + input availability |
| `GET /control` | — | Kill-switch / capability state + audit directory |
| `POST /control` | `{armed?, inputEnabled?, captureEnabled?}` | Arm/disarm; toggle input/capture |
| `GET /events` | — | SSE stream of live UIA events (focus, window-open) |
| `GET /events/poll` | `?since=N` | Buffered UIA events newer than id N |
| `GET /foreground` | — | Foreground window element |
| `GET /focused` | — | Focused element |
| `GET /windows` | — | All top-level windows (the reliable way to enter a specific app) |
| `POST /process/launch` | `{path, args?, workingDir?, waitForWindowMs?}` | Launch a program; returns its window if it appears |
| `POST /uia/tree` | `{rootRef?, depth?, maxChildren?}` | Element subtree |
| `POST /uia/find` | `{rootRef?, name?, automationId?, controlType?, className?, scope?, max?}` | Query elements |
| `POST /uia/wait` | `{…conditions, timeoutMs?}` | Poll until a matching element appears (or `404 wait_timeout`) |
| `GET /uia/element/{ref}` | — | Re-read one element |
| `GET /uia/element/{ref}/properties` | — | **Every** UIA property of an element (name → value) |
| `POST /uia/invoke` | `{reference}` | Invoke pattern (click a button, etc.) |
| `POST /uia/set-value` | `{reference, text}` | Set a value (text boxes) |
| `POST /uia/toggle` | `{reference}` | Toggle a checkbox/switch |
| `POST /uia/expand-collapse` | `{reference, expand}` | Expand/collapse a tree item |
| `POST /uia/select` | `{reference}` | Select a list/tab item |
| `POST /uia/set-focus` | `{reference}` | Focus an element |
| `POST /capture/screen` | `{monitor?, format?, quality?}` | Whole virtual desktop, or one monitor |
| `POST /capture/region` | `{x, y, width, height, format?, quality?}` | Arbitrary rectangle |
| `POST /capture/window` | `{reference? \| hwnd?, format?, quality?}` | One window (PrintWindow) |
| `POST /capture/element` | `{reference, format?, quality?}` | One element's bounds |
| `POST /capture/input-desktop` | `{format?, quality?}` | **Phase 2** — the desktop currently owning input (secure desktop when run as SYSTEM) |
| `POST /mouse/move` | `{x, y}` | Move cursor (virtual-desktop pixels) |
| `POST /mouse/click` | `{button?, x?, y?, count?}` | Click (`left`/`right`/`middle`) |
| `POST /mouse/down` · `/mouse/up` | `{button?, x?, y?}` | Press / release |
| `POST /mouse/scroll` | `{dx, dy}` | Wheel notches (dy up+, dx right+) |
| `POST /keyboard/type` | `{text}` | Type a literal string (Unicode) |
| `POST /keyboard/keys` | `{chord}` | Chord, e.g. `"ctrl+shift+s"`, `"alt+F4"`, `"enter"` |

**Capture responses** default to JSON `{desktop, rect, monitor, dpiScale, format, imageBase64}`.
Add `?raw=true` (or send `Accept: image/png`) to get raw image bytes instead.

`format` is `png` (default) or `jpeg`; `quality` (1–100) applies to JPEG.

## Governance & safety (Phase 3)

Both hosts wrap the backend in a `GovernedBackend`, so a single seam enforces safety and records
history for HTTP and MCP alike:

- **Audit log** — every action (reads, input, capture, refusals) is written as a JSON line to
  `%LOCALAPPDATA%\Deskhand\audit\audit-YYYYMMDD.jsonl`, with timestamp, user, action, detail, and a
  content hash for captures.
- **Kill switch** — `Armed` is the master switch; disarmed, all input and capture are refused
  (`403 disarmed`) while read-only introspection still works. Toggle it from the dashboard, the
  `POST /control` endpoint, the MCP tools `deskhand_disarm` / `deskhand_arm`, or the global hotkey
  **Ctrl+Alt+Pause**.
- **Capability gates** — disable input or capture independently (dashboard switches, `/control`, or
  env at startup: `DESKHAND_DISABLE_INPUT`, `DESKHAND_DISABLE_CAPTURE`, `DESKHAND_START_DISARMED`).
- **Screenshot toast** — an on-screen toast pops up in the bottom-right corner **every time a
  screenshot is taken**, so the user always knows their screen was captured. On by default; toggle
  via the dashboard Safety panel, `POST /control {notifyOnCapture}`, or `DESKHAND_DISABLE_CAPTURE_TOAST`.

The dashboard shows armed state in the top bar and a **Safety** panel (Screen & Input tab) with the
switches and the audit-log path. The Explorer also has a **↻ Refresh** button that reloads the current
tree and keeps your selection, for when the app under inspection changes.

## MCP server

`Deskhand.Mcp` exposes the same capabilities as **MCP tools** over stdio (25 tools:
`deskhand_list_windows`, `deskhand_get_tree`, `deskhand_get_all_properties`, `deskhand_invoke`,
`deskhand_capture_window`, `deskhand_mouse_click`, …). Screenshots are returned as real MCP image
content, so a model sees them directly.

```powershell
dotnet build src/Deskhand.Mcp -c Release
```

Register it with an MCP client (e.g. Claude Desktop / Claude Code) — `mcp.json`:

```json
{
  "mcpServers": {
    "deskhand": {
      "command": "C:\\Users\\crimson\\source\\repos\\uia_mcp\\src\\Deskhand.Mcp\\bin\\x64\\Release\\net9.0-windows10.0.19041.0\\deskhand-mcp.exe"
    }
  }
}
```

The MCP server runs in your user session and covers the Default desktop, exactly like the HTTP one.

### Two ways to run MCP

- **stdio** — the standalone `deskhand-mcp.exe`; your client launches it on demand. No port, no
  dashboard.
- **HTTP (unified)** — the **dashboard server also serves MCP** over Streamable HTTP at
  **`http://127.0.0.1:8791/mcp`**. Run `deskhand-http` once and you get the browser dashboard *and*
  the MCP endpoint in **one process sharing one backend** — so the dashboard shows and governs
  exactly what your MCP client does (same audit, events, kill switch, macros, screenshot toast).

For Claude Code: `claude mcp add --transport http deskhand http://127.0.0.1:8791/mcp` (keep
`deskhand-http` running). Clients that support HTTP MCP take a URL instead of a command
(`{ "type": "http", "url": "http://127.0.0.1:8791/mcp" }`).

### Configure your MCP client (stdio)

For the stdio server, point your client at `deskhand-mcp.exe`. The examples use the MSI path
`C:\Program Files\Deskhand\mcp\deskhand-mcp.exe` — swap in your build path if running from source.

Most clients share the **`mcpServers`** shape:

```json
{
  "mcpServers": {
    "deskhand": { "command": "C:\\Program Files\\Deskhand\\mcp\\deskhand-mcp.exe" }
  }
}
```

| Client | Where | Notes |
|---|---|---|
| **Claude Code** | `claude mcp add deskhand -s user "C:\Program Files\Deskhand\mcp\deskhand-mcp.exe"` | Or the `mcpServers` block in a project `.mcp.json`. Restart the session. |
| **Claude Desktop** | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` block above. Restart the app. |
| **Cursor** | `.cursor/mcp.json` (project) or `%USERPROFILE%\.cursor\mcp.json` (global) | `mcpServers` block above. |
| **Windsurf** | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` | `mcpServers` block above. |
| **Cline** (VS Code) | MCP Servers panel → *Configure* → `cline_mcp_settings.json` | `mcpServers` block above (add `"args": []`). |
| **VS Code** (Copilot agent) | `.vscode/mcp.json` | Uses **`servers`** + **`type`**: `{ "servers": { "deskhand": { "type": "stdio", "command": "…deskhand-mcp.exe" } } }` |
| **Zed** | `settings.json` → `context_servers` | `{ "context_servers": { "deskhand": { "command": { "path": "…deskhand-mcp.exe", "args": [] } } } }` |
| **Continue** | `~/.continue/config.yaml` | `mcpServers:` list — `- name: deskhand` / `command: …deskhand-mcp.exe` |

**Try it without a client** with the MCP Inspector:

```powershell
npx @modelcontextprotocol/inspector "C:\Program Files\Deskhand\mcp\deskhand-mcp.exe"
```

**Safety when testing:** the tools drive your real desktop. It starts **armed**; safe first calls are
`deskhand_machine_info`, `deskhand_list_windows`, `deskhand_capture_screen`. Call `deskhand_disarm`
(or set `DESKHAND_DISABLE_INPUT=1`) for read-only, and **Ctrl+Alt+Pause** is the global kill switch.

## Phase 2 — secure desktop (UAC / lock / logon)

The HTTP server runs in your user session and covers the **Default** desktop. The **secure
desktop** (`Winsta0\Winlogon`) — where UAC prompts, the lock screen, and the logon UI live —
can only be captured by a process running as **SYSTEM inside the console session**. Three pieces
implement this:

1. **`SecureCapture`** (in Core) — the primitive: attach a throwaway thread to whichever desktop
   currently owns input (`OpenInputDesktop` + `SetThreadDesktop`) and GDI-capture it. As a normal
   user this captures Default; as SYSTEM it also captures the secure desktop (WGC/DXGI cannot).
   Exposed at `POST /capture/input-desktop` and in the dashboard's **Secure desktop** panel.
2. **`deskhand-secure`** (Secure Helper) — a standalone exe that runs the primitive:
   ```
   deskhand-secure capture C:\temp\shot.png
   ```
   Run as a normal user it saves the Default desktop (proves the mechanism). Run **as SYSTEM in
   the console session** it saves the secure desktop.
3. **`deskhand-broker`** (Broker) — the elevated launcher that starts the Secure Helper as SYSTEM
   by borrowing winlogon's token:
   ```
   deskhand-broker deskhand-secure.exe capture C:\temp\secure.png     (run elevated)
   ```

**To capture the secure desktop right now**, either run the Broker elevated (above), or use
Sysinternals PsExec: `psexec -s -i <consoleSessionId> deskhand-secure.exe capture C:\temp\secure.png`
(`query session` shows the id). To actually see secure content, trigger a UAC prompt or lock the
workstation while the SYSTEM helper runs.

> **Tested vs. not:** the capture primitive, the Secure Helper, and `/capture/input-desktop` were
> verified capturing the Default desktop. The Broker's SYSTEM-launch path needs elevation +
> `SeDebugPrivilege` and was **not exercised in the build sandbox** — run it on a real elevated
> console. Driving *input* on the secure desktop (clicking the UAC button) additionally requires a
> signed `uiAccess` binary and admin policy, and is not enabled here — capture is the reliable part.

## Record & playback

Deskhand can record what you do and replay it — **synchronized, not blind**. Recording captures
state-changing actions (mouse, keyboard, and UIA acts) at the governance seam. Playback is smart:

- **UIA action steps re-resolve (wait for) their target element** before acting — so "click Save"
  waits for the Save button to exist rather than firing at a stale ref or coordinate.
- **Explicit expectations** — insert "wait for element Y (up to a timeout)" steps between actions,
  giving *do X, expect Y, do Z*. Playback blocks until the expectation is met (or fails clearly).
- Only raw coordinate/keyboard input honors the recorded timing (scaled by `speed`, each gap capped).

UIA steps store a **re-resolvable selector** (name / automationId / controlType / className), so a
macro replays across sessions even though element refs are per-session.

HTTP: `POST /macro/start`, `POST /macro/stop` (returns the macro JSON), `GET /macro/status`,
`POST /macro/expect {…conditions, timeoutMs}` (while recording), `POST /macro/play {speed?, macro?}`.
MCP: `deskhand_macro_start/stop/status/expect/play`. Dashboard: a **Macro** panel (Screen & Input)
plus an **Expect (macro)** action on any Explorer element.

## Fleet (Phase 4)

`Deskhand.Fleet.Server` + `Deskhand.Fleet.Agent` extend the single-machine backend to many machines.
The **agent** dials *outbound* to the server over a WebSocket and serves commands against its local
desktop (no inbound port on the agent). The **server** keeps a registry of connected agents and
exposes the **full** automation surface routed to a selected agent — the server-side
`RemoteAgentBackend` implements the entire `IAutomationBackend`, so every capability (UIA read/act,
capture, input, launch) works remotely.

```powershell
$env:DESKHAND_FLEET_TOKEN="s3cret"                  # shared agent+client token (optional)
$env:DESKHAND_FLEET_BIND="any"                      # accept remote agents (default: loopback)
dotnet run --project src/Deskhand.Fleet.Server -c Release          # 8799
# on each target machine:
$env:DESKHAND_FLEET_TOKEN="s3cret"; $env:DESKHAND_FLEET_URL="ws://server:8799/agent/connect"
$env:DESKHAND_AGENT_ID="WKS-1"; dotnet run --project src/Deskhand.Fleet.Agent -c Release
```

Client API (bearer token when configured): `GET /agents`, then the same surface per agent —
`GET /agents/{id}/machine|foreground|windows`, `POST /agents/{id}/uia/tree|find|wait|invoke|set-value`,
`/capture/screen|region|window|element`, `/mouse/*`, `/keyboard/*`, `/process/launch`.

The fleet server also serves a **web dashboard** at `http://127.0.0.1:8799` — a live grid of every
connected PC (screenshot thumbnails, machine/desktop/monitor info); click a PC to open it, watch its
screen live, and drive it (click-to-click, keyboard). This is where you *see other machines* (the
single-machine dashboard on `:8791` only shows the local one). The two dashboards cross-link in the
top bar (**Fleet** ⇄ **This PC**).

**The fleet is exposed over MCP too**, at `http://127.0.0.1:8799/mcp` — fleet-aware tools so a model
can list your PCs and drive any of them by id: `deskhand_list_agents`, then `deskhand_agent_capture_screen`,
`deskhand_agent_get_tree`, `deskhand_agent_click`, `deskhand_agent_type`, `deskhand_agent_invoke`,
`deskhand_agent_launch`, etc. Register it alongside the local one:

```powershell
claude mcp add --transport http deskhand-fleet http://127.0.0.1:8799/mcp
```

So a model gets both surfaces: `deskhand_*` for the local PC (`:8791/mcp`) and `deskhand_list_agents`
+ `deskhand_agent_*` for the whole fleet (`:8799/mcp`).

**Done & tested:** outbound WebSocket transport, agent registry + routing, the full remote backend,
**shared-token auth** for agents and clients, and AnyIP binding — verified: no-token → 401,
authenticated agent routing across the whole surface.

**`Deskhand.Fleet.Launcher`** is a Windows Service (LocalSystem) that keeps an agent running in the
active console session, launched as the logged-in user via `WTSQueryUserToken` + `CreateProcessAsUser`:

```powershell
sc create DeskhandLauncher binPath= "C:\path\deskhand-launcher.exe" start= auto obj= LocalSystem
sc start DeskhandLauncher      # configure via machine env: DESKHAND_FLEET_URL / _TOKEN / DESKHAND_AGENT_EXE
```

The Launcher mechanism is **verified**: run as SYSTEM (e.g. `PsExec64 -s -d deskhand-launcher.exe`
with `DESKHAND_AGENT_EXE` / `DESKHAND_FLEET_URL` as machine env), it spawned the agent into the
interactive console session **as the logged-in user** (session 1, non-elevated), which then connected
to the fleet server and served routed calls.

> **Remaining hardening:** put **TLS** in front (reverse proxy or Kestrel HTTPS) for `wss://`, and
> optionally mTLS client certs instead of the shared token.

## RDP (Phase 5)

There are two ways Deskhand reaches a **Remote Desktop** machine:

1. **Agent in the RDP session (done, tested).** An RDP session is just another interactive session,
   so an agent running inside it has the **full** capability — UIA, capture, and input. The Launcher
   now enumerates **every active session** (`WTSEnumerateSessions`) — console *and* RDP — and spawns a
   per-session agent (`<machine>-S<sessionId>`), each connecting to the fleet server independently.
   Verified: run as SYSTEM, it discovered the session and registered `PORTARE-S1`; on a host with RDP
   sessions you get `…-S2`, `…-S3`, etc., side by side.

2. **Protocol-level, zero-install (`Deskhand.Rdp`, built).** A client that speaks the RDP wire
   protocol to a host with **nothing installed on the target** — capture + input only, **no UIA**
   (nothing runs in the session to read the tree). It hosts the **Microsoft RDP ActiveX control**
   (`mstscax.dll`) headlessly on an STA thread, connects with NLA credentials, captures the remote
   desktop with `PrintWindow`, and posts mouse/keyboard to the control's render window. `RdpBackend`
   implements the shared `IAutomationBackend` (capture + input; UIA members throw). CLI:

   ```powershell
   deskhand-rdp <host> <user> <password> [--domain D] [--size 1280x800] [--capture out.png]
   ```

   The ActiveX interop (`src/Deskhand.Rdp/lib/*.dll`) is generated once from `C:\Windows\System32\mstscax.dll`:
   `AxImp.exe mstscax.dll` (NETFX 4.8 Tools). **Verified:** the control hosts, the connect pipeline
   fires, and capture returns a valid PNG. Input (posted to the render window) and a live connected
   session need a reachable RDP host to validate — self-RDP to localhost would hijack the console.

Model 1 gives full fidelity (UIA + capture + input) with an agent the Launcher places in each session;
model 2 gives agentless reach when you can't install anything on the target.

## Recreation docs

`AI_Documentation/` contains a full, exact rebuild guide (architecture, dependencies with versions,
per-subsystem deep-dives, gotchas, and an ordered walkthrough) — enough for a human or an AI agent
to recreate Deskhand from scratch.

## Notes & limits

- **Coordinates** are physical pixels on the virtual desktop; the process is Per-Monitor-v2 DPI aware.
- **Element refs** are volatile UIA handles; a stale ref is re-resolved from a stored selector recipe,
  and returns `404 stale_element` if it truly cannot be found — re-query the tree.
- **Chromium/Electron** apps may expose thin UIA trees; launch with `--force-renderer-accessibility`
  or fall back to capture + coordinate input.
- **Window capture** (`/capture/window`, `deskhand_capture_window`) uses **Windows.Graphics.Capture**,
  which faithfully captures GPU/DWM-accelerated apps (Chrome, Firefox, Electron) even when the window
  is **unfocused or occluded — without raising it**. It falls back to `PrintWindow` when WGC is
  unavailable (pre-1903). Screen/region/element capture use GDI `CopyFromScreen`.

See `Deskhand.http` for ready-to-run sample requests.
