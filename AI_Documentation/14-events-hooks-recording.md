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

**Privacy:** this captures real keystrokes, which can include passwords. It is **off by default**, must be
started explicitly, `user_input_record_start` is audited, and `captureText=false` records mouse only.
Element resolution uses the *raw* backend (unaudited) so per-click resolution doesn't flood the audit log.

## Reproduce quickly

1. Start `deskhand-http`. Open the dashboard.
2. **Process hook:** `POST /process/wait {event:"start", name:"notepad", timeoutMs:15000}` in one call,
   launch Notepad in another → the call returns `{event:"process_started", processId, name}`. Watch
   `process_*` in the events drawer.
3. **Record:** `POST /record/start {monitor:0, format:"gif", fps:8, scale:40, maxDurationMs:20000}`, wait,
   `POST /record/stop {reference:id}` → download `/recordings/{id}`. Confirm it's a valid animated GIF.
4. **User input:** `POST /input/record/start`, click + type anywhere, `POST /input/record/stop` → events
   list shows `click` (with the element), `text`, and `key` entries.
