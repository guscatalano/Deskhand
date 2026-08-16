# 15 — Test Plan (how to know you built it correctly)

This is the acceptance checklist for a from-scratch (or post-change) build. Each subsystem lists a concrete
procedure and the **PASS** condition. An agent that runs these top-to-bottom and hits every PASS has a
working Deskhand. Expected values below are real ones observed on a reference build — yours will differ in
counts/ids but must match the *shape* and the assertions.

Conventions: HTTP examples assume `deskhand-http` on `:8791` with header `Origin: http://localhost:8791`
(loopback auth). Fleet examples assume `deskhand-fleet` on `:8799`. "PASS" = the stated assertion holds.

## 0. Build & smoke

1. `dotnet build Deskhand.slnx -c Release` → **PASS:** `Build succeeded. 0 Error(s)`.
2. Start `deskhand-http`; `GET /health` → **PASS:** `{ ok: true, service: "deskhand-http" }`.
3. `GET /` → **PASS:** 200 and HTML containing the dashboard (not 404 — the ContentRoot/StaticWebAssets
   fix). A 404 here means `ContentRootPath`/`StaticWebAssetsEnabled=false`/`Content Update wwwroot` is wrong.

## 1. Orientation & UIA

| Test | Procedure | PASS |
|------|-----------|------|
| Windows | `GET /windows` | non-empty; each has `ref`, `controlType`, `boundingRect`, `processId` |
| Foreground | `GET /foreground` | a window DTO with a `boundingRect` |
| Tree | `POST /uia/tree {rootRef:<a window>, depth:3}` | nested `element`/`children`; **≤ 4000 nodes total** (budget) |
| Find | `POST /uia/find {controlType:"Button", scope:"descendants"}` on a window with buttons | returns matching elements |
| Element-from-point | `POST /uia/element-from-point {x,y}` at the centre of a known window | returns the element under it; a **second call resolves again fresh** (no stale-ref error) |
| Stale ref | resolve a window, close it, re-`GET /uia/element/{ref}` | **404 `stale_element`** (not a crash) |

**Gotcha (not a bug):** passing an *array* of refs as `rootRef` → empty-body **400** (JSON bind failure).
Chromium top-level window refs go stale within ms — `get_tree` on them may **404**; that's expected (see
`04-uia.md`). Use `element_from_point` for Chromium/Electron.

## 2. Capture & input

| Test | Procedure | PASS |
|------|-----------|------|
| Screen | `POST /capture/screen {monitor:0}` | base64 PNG/JPEG; `rect` matches the monitor |
| Window (WGC) | `POST /capture/window {reference:<a window>}` | image of that window even if occluded/unfocused, without raising it |
| Coordinate accuracy | `POST /mouse/move {x,y}`; read cursor pos | lands within ±1px (Per-Monitor-v2 mapping) |
| Type | focus an edit, `POST /keyboard/type {text:"abc"}` | "abc" appears |

## 3. Governance & safety

1. `GET /control` → armed/input/capture flags present.
2. Disarm (`POST /control {armed:false}`), then `POST /mouse/click …` → **PASS:** `403` refused, audited.
3. Re-arm; capture once → **PASS:** a toast appears on screen, `screenshot_toast` in the audit.
4. **Toast debounce:** capture 5× within 6s → **PASS:** at most **one** toast fires (every capture still
   audited). This is what stops the fleet screenshot storm.
5. Audit is durable: `AuditLog` JSONL file exists and grows; killing the host and re-reading it preserves lines.

## 4. MCP

1. `POST /mcp` `initialize` → `tools/list` → **PASS:** the tool set incl. `deskhand_list_processes`,
   `deskhand_element_from_point`, `deskhand_wait_for_process`, `deskhand_record_start`,
   `deskhand_user_input_start` (~34 tools).
2. `tools/call deskhand_capture_screen` → **PASS:** an `ImageContentBlock` with real bytes.
3. Register in a client (`claude mcp add --transport http deskhand http://127.0.0.1:8791/mcp`) → **PASS:**
   client shows ✔ Connected.

## 5. Processes (process → windows → tree)

1. `GET /processes` → **PASS:** many processes; windowed ones first; each has `processId`, `name`,
   `workingSet`, and a `windows[]` of top-level windows (empty for background procs).
2. Take a windowed process's `windows[0].ref`, `POST /uia/tree {rootRef:that}` → **PASS:** expands into its
   UIA subtree (proves the window ref is live).
3. Dashboard **Processes** tab → expand a process → a window → its elements; select one → **Open in
   Explorer** loads it as the Explorer root. **PASS:** all three levels navigate.

## 6. Events & hooks

1. `GET /events/poll?since=0` → `{lastId, events}`; change focus / open a window → new `focus_changed` /
   `window_opened` events. **PASS.**
2. **Process events:** in one call `POST /process/wait {event:"start", name:"notepad", timeoutMs:15000}`,
   launch Notepad in another → **PASS:** returns `{event:"process_started", processId, name:"Notepad"}`.
   `GET /events/poll` shows `process_started`/`process_exited`.
3. **Exit wait:** `POST /process/wait {event:"exit", pid:<notepad pid>}`, kill it → **PASS:**
   `process_exited` returned.

## 7. Screen recording

1. `POST /record/start {monitor:0, format:"gif", fps:8, scale:40, maxDurationMs:20000}` → id + `state:"recording"`.
2. wait ~3s, `GET /record/status/{id}` → `frames` climbing.
3. `POST /record/stop {reference:id}` → `state:"completed"`, a `file`, `sizeBytes>0`.
4. `GET /recordings/{id}` → **PASS (GIF):** magic bytes `GIF8`; opens as an animated image with **>1 frame
   and per-frame delays**. **PASS (AVI):** repeat with `format:"avi"` → magic `RIFF`, valid `avih`/`movi`/`idx1`.
5. **Hard auto-stop:** start with `maxDurationMs:3000`, don't stop, wait 5s, `GET /record/status/{id}` →
   **PASS:** `state:"completed"` on its own.
6. **Retention/location:** file is under `%LOCALAPPDATA%\Deskhand\recordings`; `recording_saved` (+path) in
   audit; a file back-dated >24h is deleted on next startup (`recording_expired` audited). **PASS.**

## 8. User-input recording (+ consent)

1. `POST /input/record/start {captureText:true}` → **PASS:** a **persistent red banner** appears
   top-centre + a start toast.
2. Physically (or via injected input) click a control and type → `POST /input/record/stop` → **PASS:**
   events include a `click` with a resolved `element` (controlType/name), a coalesced `text` run, and any
   special `key` (e.g. `Enter`).
3. **PASS:** the banner disappears on stop. With `captureText:false` the banner still shows (mouse-only).

## 9. Fleet

Start `deskhand-fleet` (:8799) and one `deskhand-agent` pointing at `ws://127.0.0.1:8799/agent/connect`.

1. `GET /agents` → **PASS:** the agent appears with the **correct machine name** (its own, not the server's).
2. Drive it: `POST /agents/{id}/capture/screen`, `/mouse/click`, `/uia/tree` → **PASS:** act on that PC.
3. **Observation over fleet:** `GET /agents/{id}/events`, `POST /agents/{id}/process/wait`,
   `POST /agents/{id}/record/start`+`/stop` then `GET /agents/{id}/recordings/{recId}` (downloads the
   agent's file through the server), `POST /agents/{id}/input/record/start`+`/stop`. **PASS:** each works
   against the agent; recording a fleet PC's user shows the banner on **that** PC.
4. **Live view is OFF by default:** open the fleet dashboard → **PASS:** no screenshots taken until you
   click **Go live**; it also auto-pauses when the page is hidden. Opening one agent fetches a single frame.

## 10. RDP (zero-install target)

1. CLI capture: `deskhand-rdp <host> <user> <pass> --capture out.png` → **PASS:** `CONNECTED` and a PNG of
   the remote desktop (needs a reachable target + creds).
2. **Join fleet over RDP:** `deskhand-rdp <host> <user> <pass> --fleet ws://127.0.0.1:8799/agent/connect
   --id NAME` → **PASS:** a tile named after the **remote host** appears; capture + click/type work;
   UIA/process/observation calls return a clean "not available over RDP" error.
3. **From the web:** dashboard **＋ RDP** → fill the form → Connect → **PASS:** `POST /fleet/rdp/connect`
   spawns a `deskhand-rdp` process (`GET /fleet/rdp/list` shows it), `rdp_connect` audited.
4. **Remove:** **✕ Disconnect RDP** (or `POST /fleet/rdp/disconnect {id}`) → **PASS:** connector process
   killed, entry gone from `/fleet/rdp/list`, tile drops off, `rdp_disconnect` audited.

5. **Install native agent over RDP** (needs a reachable target): the RDP tile's **Install native agent**
   button (`POST /fleet/rdp/install {id}`) opens the remote Run dialog and launches the self-contained
   `deskhand-agent.exe` from `\\tsclient`, pointed at the fleet → **PASS:** the machine reappears as a
   **native** agent (full console). Prereq: run `installer/publish-agent.ps1` first.

Without a real target you can still verify steps 3–5 structurally: connect spawns a real process and
tracks it; a bogus/unreachable host makes the connector exit after its RDP timeout; and
`POST /fleet/rdp/install` on a **native** agent returns a clean "not an RDP connector" error — proving the
endpoint → observer → fleet RPC → agent-dispatch → install-delegate chain is wired.

## 11. Regression traps (things that previously looked broken but weren't)

- **Empty-body 400 on `/uia/tree`** → you passed an array `rootRef` (multiple matched windows). Pass one scalar ref.
- **Stale build / wrong exe** → single-project `dotnet build` outputs to `bin\x64\Release` vs `bin\Release`;
  pick the newest `deskhand-*.exe`, or build the solution.
- **File-lock build errors (MSB3021/3027)** → a running host holds the DLLs; stop it before rebuilding.
- **Fleet exe names** are `deskhand-fleet.exe` / `deskhand-agent.exe` (not `-fleet-server`/`-fleet-agent`).
- **`--force-renderer-accessibility` seems to do nothing** → the browser was already running with that
  profile (Chromium is single-instance); use a fresh `--user-data-dir`.
