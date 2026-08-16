# 14 — Events, Hooks, Recording

Four related capabilities let an agent *observe* the machine (not just drive it): a live event feed,
process/window hooks, screen recording, and user-input recording. All are wired into the same HTTP host
(dashboard + MCP over `/mcp`) and the stdio MCP host.

## Event feed (`EventHub`)

`Deskhand.Core.Events.EventHub` is a bounded ring buffer (500) plus live channels. Everything that
"happens" is published here with a monotonic `id`, so a poller resumes from a cursor and a streamer gets
a push. Event types:

| type | source | payload highlights |
|------|--------|--------------------|
| `focus_changed` | UIA focus event (`UiaService.StartEvents`) | name, controlType, processId |
| `window_opened` | UIA `WindowOpenedEvent` | name, processId |
| `process_started` | `ProcessWatcher` | name, processId |
| `process_exited` | `ProcessWatcher` | name, processId |

Consume it two ways:
- **Poll** — `GET /events/poll?since=<id>` / `deskhand_get_events(sinceId)` → `{lastId, events}`.
- **Stream** — `GET /events` (Server-Sent Events) via `EventHub.Subscribe()`.

The dashboard's **events drawer** polls `/events/poll` and renders all four types.

## Process hooks (`ProcessWatcher`)

Polls the running-process set every 1s (no elevation; in-session) and diffs it, publishing
`process_started` / `process_exited` into the `EventHub`. It reports pid + name (not full image paths —
those need elevation for many processes, so matching is by name).

For a **blocking** wait (an agent workflow: "launch installer → wait for it to exit → continue"):

```
POST /process/wait  { event: "start"|"exit", name?, pid?, timeoutMs }
deskhand_wait_for_process(event, name?, pid?, timeoutMs)
```

- `event:"start"` returns when a *new* process matching name-substring/pid launches after the call.
- `event:"exit"` returns when a matching process exits (returns immediately if the given pid is already gone).
- Returns `{event, processId, name}`, or a `wait_timeout` on timeout.

**Window-open hooks already exist** via `window_opened` events; to *block* for a specific window use
`deskhand_wait_for_element` (poll for a window by name/controlType).

## Screen recording (`ScreenRecorder`)

Records a monitor (by index) or the whole virtual desktop (all monitors) to an animated **GIF** or an
**MJPEG AVI** video. Frames are grabbed on a background timer via GDI (no UIA STA needed), scaled, and
JPEG-encoded per frame; at stop they're wrapped by self-contained writers — no external codecs:

- `GifWriter` — hands each frame to GDI+ (quantize ≤256 colours + LZW), parses the single-image GIF back
  out, and re-wraps all frames into one looping GIF89a with proper per-frame delays.
- `AviMjpegWriter` — writes a minimal RIFF/AVI (`avih`/`strh`/`strf`/`movi`/`idx1`) with `MJPG` frames.

Every session carries a hard **`MaxDurationMs`** (≤ 5 min): a timer auto-stops and finalizes the file, so
a forgotten/failed `stop` can't record forever. Capture is gated on the kill switch (armed +
capture-enabled), audited, and fires the capture toast.

```
POST /record/start  { monitor?, format:"gif"|"avi", fps, scale, quality, maxDurationMs }
POST /record/stop   { reference: <id> }
GET  /record/status/{id}   ·   GET /record/list   ·   GET /recordings/{id}  (download)
deskhand_record_start / deskhand_record_stop / deskhand_record_status
```

**Storage & retention:** all saved media land in one predefined dir,
`%LOCALAPPDATA%\Deskhand\recordings`. Each save is audited (`recording_saved` + path), and files older
than **24h** are auto-deleted (audited `recording_expired`) on startup and every 6h. The dashboard's
Screen tab has a **Record** panel with live `frames · elapsed/max` status and preview/download links.

## User-input recording (`InputRecorder`)

Records the **user's own** physical mouse + keyboard via global low-level hooks
(`WH_MOUSE_LL` / `WH_KEYBOARD_LL`), and — crucially — resolves the UIA element under each click via
`FromPoint`, so the log shows *what* was clicked, not just coordinates. This is distinct from
`MacroRecorder`, which records the *agent's* own actions.

- The hook callback only enqueues raw events (Windows drops slow LL hooks); a **worker thread** resolves
  elements and coalesces printable keystrokes into `text` runs. Special keys (Enter, Tab, arrows…) are
  named; injected Unicode (`VK_PACKET`) is decoded too.
- Event kinds: `click` (with `element`), `scroll`, `text` (a typed run), `key` (a special key).

```
POST /input/record/start  { captureText }      (captureText=false → mouse-only)
POST /input/record/stop    → { status, events }
GET  /input/record/status  ·  GET /input/record/events?since=<id>
deskhand_user_input_start(captureText) / deskhand_user_input_stop / deskhand_user_input_get(sinceId)
```

**Privacy & consent:** this captures real keystrokes, which can include passwords. It is **off by default**,
must be started explicitly, `user_input_record_start` is audited, and `captureText=false` records mouse
only. Element resolution uses the *raw* backend (unaudited) so per-click resolution doesn't flood the audit
log. **While recording is active, the user sees a persistent, always-on-top red banner**
(`Deskhand.Ui.RecordingIndicator`, driven via the Core `IActivityIndicator` seam) plus a start/stop toast —
so no one can be observed silently. The banner shows even in mouse-only mode.

## Over the fleet

All four capabilities are routed per-agent, not just on the local host:

- The agent bundles its backend + observation services into `AgentServices`; `AgentConnection` /
  `AgentDispatcher` carry it, so new `FleetMethods` (`get_events`, `wait_for_process`,
  `record_start|stop|status|read`, `input_start|stop|get`) execute against the agent's own services.
- Server side, `RemoteAgentObserver(link)` forwards each call; the fleet server exposes
  `GET /agents/{id}/events`, `POST /agents/{id}/process/wait`, `/agents/{id}/record/start|stop`,
  `GET /agents/{id}/record/status`, **`GET /agents/{id}/recordings/{recId}`** (streams the agent's saved
  file back through the server), and `/agents/{id}/input/record/start|stop|events`. MCP mirrors:
  `deskhand_agent_get_events`, `deskhand_agent_wait_for_process`, `deskhand_agent_record_*`,
  `deskhand_agent_user_input_*`.
- **Consent still holds remotely:** recording a fleet PC's user shows the banner + toast on *that* PC.

## Adding a machine over RDP (no agent on the target)

`Deskhand.Rdp/RdpBackend` implements the same `IAutomationBackend` seam over the RDP wire, so a machine can
join the fleet with nothing installed on it. `deskhand-rdp <host> <user> <pass> --fleet <ws-url> [--id NAME]`
opens the RDP session, wraps it as `AgentServices { Backend = RdpBackend }` (observation services null),
and calls `AgentConnection.RunForeverAsync` — the target appears as a normal agent.

- **From the web:** the fleet dashboard's **＋ RDP** button posts to `POST /fleet/rdp/connect`
  `{host,user,password,domain?,size?,id?}`; `RdpConnectorManager` spawns `deskhand-rdp --fleet` (password via
  the `DESKHAND_RDP_PASSWORD` env, not the command line). `GET /fleet/rdp/list` tracks connectors;
  `POST /fleet/rdp/disconnect {id}` (the **✕ Disconnect RDP** button) kills one. Both are audited.
- **What works:** screen capture + coordinate mouse/keyboard over the fleet. **What doesn't:** UIA
  (tree/find/invoke/element-from-point), process list, and the observation services — pure RDP exposes no
  accessibility tree, and those services are local-machine features; the calls return a clean
  "not available over RDP" error.
- **Naming:** the agent registers under the remote target's host (`AgentConnection` uses the backend's
  `GetMachineInfo().MachineName`), not the connector box. RDP agents are tagged with an `RDP` pill.

### Upgrading an RDP target to a full native agent

An RDP tile has an **Install native agent** button (`POST /fleet/rdp/install {id}`) that bootstraps a
real agent onto the remote using only the RDP channel:

1. The connector enables **drive redirection** on connect, so the remote sees the connector's folder as
   `\\tsclient\...`, and **`KeyboardHookMode=2`** so `Win+R` routes to the remote.
2. `RdpHost.RunCommand` opens the remote **Run** dialog (a real Win+R modifier chord), types
   `"\\tsclient\C\...\deskhand-agent.exe" ws://<fleet>/agent/connect`, and runs it.
3. The self-contained agent launches on the remote and **reconnects as a native agent** (full console:
   Explorer, processes, recording, hooks). Then remove the RDP connector.

**Prerequisite:** a self-contained, single-file `deskhand-agent.exe` next to `deskhand-rdp.exe` (or
`DESKHAND_AGENT_PATH`) — publish it with `installer/publish-agent.ps1` (so the target needs no .NET runtime).
**Caveats:** it's screen-automation, so it's fragile — a UAC prompt, the login screen (not the desktop),
AV, or focus timing can break it; and drive redirection must be permitted by the target's policy.

## Reproduce quickly

1. Start `deskhand-http`. Open the dashboard.
2. **Process hook:** `POST /process/wait {event:"start", name:"notepad", timeoutMs:15000}` in one call,
   launch Notepad in another → the call returns `{event:"process_started", processId, name}`. Watch
   `process_*` in the events drawer.
3. **Record:** `POST /record/start {monitor:0, format:"gif", fps:8, scale:40, maxDurationMs:20000}`, wait,
   `POST /record/stop {reference:id}` → download `/recordings/{id}`. Confirm it's a valid animated GIF.
4. **User input:** `POST /input/record/start`, click + type anywhere, `POST /input/record/stop` → events
   list shows `click` (with the element), `text`, and `key` entries.
