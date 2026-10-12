# Deskhand — AI feedback backlog

Field feedback from AI agents driving Deskhand, triaged. Three rounds so far.

## Shipped (v0.2.58–v0.2.66)

| Item | What shipped |
|---|---|
| Recordings that survive a reboot | `record_start resumable:true` (mp4) persists a `<base>.rec.json` state file + wall-clock start and records into numbered segments (`<base>.pN.mp4`). On an orderly shutdown the open segment is finalized (`FinalizeForShutdown`, wired to `ApplicationStopping` + `SessionEnding`); on the next startup `ResumeAll` (wired into both hosts) resumes the recording under its **original id** into a new segment, discarding any unfinalized crash tail. `record_stop` returns `segments` (ordered, each a playable mp4); download any with `deskhand_record_read(segment:)` or `GET /recordings/seg/{name}`. First cut delivers a segment list (no auto-stitch). Verified via a simulated reboot: finalize → fresh recorder → resume same id → 2 valid segments (v0.2.66). |
| Recording markers | `deskhand_record_mark(id, label)` captions the moment on-screen (~2.5s) and records it in the marker timeline; `deskhand_record_speed(id, factor)` fast-forwards from here to the next change by shrinking each frame's MP4 presentation duration (0.25–8×, mp4 only — gif/avi are fixed-fps). Both timelines (`{atMs,label}` / `{atMs,factor}`) are returned by `record_stop`. Also `POST /record/mark` + `/record/speed`. (v0.2.65) |
| Click / key overlays on recordings | `record_start overlays:true` (and `Overlays` on `POST /record/start`) draws the agent's own clicks as fading highlight ripples and its typed keys/chords as a bottom key-press strip, composited onto each frame before encode — works for mp4/avi/gif. Sourced from a new `InputInjector.Injected` event; cosmetic failures never break a frame. (v0.2.64) |
| `find_elements` misses some Start tiles | New `deskhand_find_start_apps(query, max)` (and `GET /uia/start-apps`) searches the Start host's (`StartMenuExperienceHost`) **and** the search host's own UIA tree, matching tiles on **visible text** — their Name **or** a visible descendant label — as a case-insensitive substring, so pinned tiles with an empty/odd UIA Name (which exact-Name `find_elements` misses) are found. Returns invokable refs, ordered exact-label → prefix → alphabetical (v0.2.63). |
| H.264 MP4 recordings + longer clips | New `Mp4SinkWriter` streams frames straight to an **H.264 MP4 on disk** via in-box Media Foundation (hardware MFT when present, SW otherwise) — no bundled codec. Because it's not RAM-bound, mp4 clips may run up to **30 min** (gif/avi stay capped at 5 min). `record_start` defaults to `format:"mp4"` and takes an optional sanitized `outputName`; falls back to AVI if Media Foundation is unavailable (N/Server SKU). Verified: finalized, playable MP4 (ftyp/mdat/moov, avc1) (v0.2.62). |
| `launch_process elevated:false` | De-elevated launch using the logged-on user's shell token (v0.2.59). |
| `run_script` (verbatim) | Takes a script body + args, writes a temp file, runs with `-File` — nothing interpolates `$_`/`$PID`/`$env:` (v0.2.58). |
| Target windows / captures by title or pid | By-title/pid convenience wrappers over the hwnd-based window/capture tools (v0.2.58). |
| `record_read` chunked / download handle | `record_stop` now returns `{status, downloadUrl: /recordings/{id}, readVia}`; new `deskhand_record_read(id, offset, length)` downloads a finished recording in ~6 MB chunks over the MCP connection (no direct network needed), returning `{totalSize, nextOffset, done, base64}` — call until `done=true` (v0.2.60). |
| Non-visual waits | `deskhand_wait_for(target, match, expect, timeoutMs, pollMs)` (and `GET /wait`) blocks until a window (title substring) / process (name or pid) / file (path exists) / registry (`…\Key!Name=expected`) condition holds, `expect="gone"` to invert — **never captures, so no toast into recordings**. Returns `{met, timedOut, elapsedMs, polls, detail}` (v0.2.61). |

## Shipped (v0.2.57)

| Item | What shipped |
|---|---|
| `run_command` timed out at the caller despite a big `timeoutMs` | **Auto-detach** — still running after `autoDetachMs` (60s) → returns `{running, detached, jobId, pid, stdout}`; command keeps going, `timeoutMs` enforced in the background, `timedOut` surfaced. Poll `deskhand_shell_result(jobId)`. |
| `write_file` failed opaquely; had to go through `run_command` base64 | `append=true` to stream big files across calls; underlying error surfaced; description documents the per-message base64 limit and points to `POST /fs/upload` and `deskhand_fetch_url`. |
| `type_text` doesn't reach console windows | Documented — use `deskhand_paste_text` for PowerShell/cmd/Terminal. |
| No plain Windows key | `send_keys "win"` (also `start`, `apps`/`menu`) works on its own. |
| `record_status` frames `-1` after completion | Reports the real frame count + duration (the frame list is cleared post-encode; now remembered). |

## Open — bounded / high value (next batch)

- _(empty — batch cleared through v0.2.61)_

## Open — larger (need a design decision)

- _(empty — all triaged items shipped through v0.2.66)_

## Round 3 — "record a driven session, replay it after a code change" (triaged)

Goal framing from the field: capture a driven session, replay it against the current machine after a code change,
and re-cut the video. Much of this round **already shipped earlier this session** — flagged below so we don't rebuild it.

**Already shipped (this session) — mostly a discoverability gap now:**

- *wait_for_\* primitives (window/process/text, with timeouts)* → `deskhand_wait_for` (window title / process name·pid /
  file / registry, `expect=gone`, timeout+poll) shipped v0.2.61; `deskhand_wait_for_element` already existed. **Gap:**
  `wait_for_text` (OCR-based) is the one condition not yet covered.
- *Long-running / async command mode (watchdog kills ~18s of sleeps)* → `run_command` **auto-detach** + `timeoutMs=0`
  + `deskhand_shell_result(jobId)` shipped v0.2.57.
- *`$` / `$_` / `$PID` stripping in inline `run_command`* → `deskhand_run_script` (verbatim body, run with `-File`)
  shipped v0.2.58; the inline mangling itself is a caller/transport (sandboxctl/MCP) artifact, not Deskhand internals.
- *Direct artifact retrieval for recordings; native .mp4* → H.264 **mp4** is the default format (v0.2.62) and
  `deskhand_record_read` streams a finished recording back **in chunks over the MCP connection** (v0.2.60) — no more
  ~85-chunk base64 of a 1 GB AVI; also `/recordings/{id}` and `/recordings/seg/{name}`.

**Shipped (v0.2.67):**

- **Semantic click/type convenience** — `deskhand_click` and `deskhand_type` (and `POST /uia/click` · `/uia/type`)
  find a target by UIA automationId / name / controlType (or on-screen **text** via OCR for click) and act in one call —
  Invoke when supported, else a real click at the element centre; type focuses the target (or sets the Value pattern
  with `setValue=true`) — coordinate-free, so replays survive DPI/layout changes.
- **`wait_for_text`** — `deskhand_wait_for target:"text"` OCRs the screen each poll until the text appears (or is
  gone); no capture toast.
- **`launch_process` follows stubs to the real process** — returns the real pid/name when the window is handed off by
  a launcher/stub that exits (e.g. Store Notepad), not the stub's pid.
- **Window ops on a maximized window** — `deskhand_window move/resize` restore out of maximized/minimized first, so
  they no longer silently no-op.
- **`write_file` creates missing parent directories** — already handled (`WriteFileBase64` creates the parent tree);
  the field report predated it.

**Shipped (v0.2.68):**

- **Session record/replay as a first-class thing** — built on the existing macro recorder. `deskhand_macro_assert`
  adds a non-element assertion (window / process / file / registry / text-via-OCR, `present`|`gone`) alongside the
  element `macro_expect`; `deskhand_macro_save` / `_list` / `_delete` persist **named** macros to disk
  (`%LOCALAPPDATA%\Deskhand\macros`); `deskhand_macro_replay(name)` replays as a **regression test**, returning a
  pass/fail report `{passed, steps, failures, results:[{index,kind,method,ok,detail}]}` — assertions are checked
  (via `WaitService`) and an element that can't re-resolve is a **loud failure**, not a silent abort. Also
  `POST /macro/assert|save|replay|delete` + `GET /macro/list`. UIA steps still re-resolve by selector so a replay
  survives DPI/layout churn. Verified against a sandbox Notepad: record→save→load→replay passes; a broken
  assertion fails loudly.

**Field-test fixes (v0.2.72) — from the user running the replay feature end to end (two clean runs):**

- **Cross-session / repo-stored replay** — `deskhand_macro_replay` now accepts an inline `macroJson` (the exact object
  `macro_stop` emits), and `deskhand_macro_import(name, json)` loads a repo-stored macro JSON into the named store — so
  "record now, replay after I rebuild next week" works (also `POST /macro/import` and an inline macro on `/macro/replay`).
  (Named save/replay already shipped v0.2.68; this adds feeding the JSON blob straight back.) Verified: the JSON from
  `macro_stop` replays and passes, both inline and via import→replay-by-name.
- **`type_text` long-input fidelity** — `InputInjector.TypeText` sends in ~10-char chunks with a brief pause instead of
  one giant `SendInput` batch (which can overflow the target's input queue and drop the tail of a long prompt). Verified:
  a 171-char string types in full, direct and via macro replay.
- **`deskhand_get_text`** (new; `GET /uia/text`) — reads an element's Value/Text pattern: the read-back counterpart to
  `set_value`/`type`, so a replay can **assert a field/document holds the expected text**. _Note: the reported garbled
  `type_text` tail did NOT reproduce on current `main` once read back correctly via get_text — typing is faithful;
  the earlier observation was the older deployed build and/or a read-too-early race. Chunking is kept as hardening._

**Sandbox finding (v0.2.74) — the type_text bug is REAL on a VM, and the fix is to not synthesize keystrokes.**
Validated v0.2.73 on an actual disposable Win11 VM (sandboxctl, vmid 903), driving the real Deskhand MCP. On the VM,
`type_text` still corrupts a long prompt even with chunking (`"the Q3 earnings"` → `"the eeeearnings"`) — synthesized
keystrokes race under VM input timing and no chunking fully fixes it. `paste_text` and `set_value` are **lossless**
(118/118 both). So `deskhand_type` now **prefers the UIA Value pattern by default** (lossless, and records as a
re-resolvable step for replay), falling back to real keystrokes only when there's no Value pattern or `keystrokes=true`.
The `type_text` primitive keeps chunking but is documented best-effort; prefer `deskhand_type` / `paste_text` for
reliable text entry. (`assert_text` did its job here — it caught the garbled text and failed the replay loudly.)
- **Reliable element-text assertion (v0.2.73)** — `deskhand_macro_assert_text(expected, contains, selector…)` re-resolves
  an element at replay and checks its text via the Value/Text pattern (`get_text`), not OCR — the dependable way to
  assert a field/document holds the prompt. An end-to-end **two-process** sandbox test (record+save in one process,
  replay in a fresh one) now passes fully: load-by-name and inline-JSON replays both `passed=true` (UIA focus
  re-resolve + type_text + window assert + element-text assert), and a broken assertion fails loudly. _(The OCR `text`
  assert remains for on-screen/canvas text where no UIA element exists, but element-text is preferred for fields.)_

## Follow-ups — shipped (v0.2.69)

- **Reboot-survival hardening** — resumable mp4 now **rolls** to a fresh segment every `RollSeconds` (default 60; `DESKHAND_ROLL_SECONDS`), so a hard crash loses ≤ one interval; `record_stop stitch:true` **auto-stitches** the segments into one continuous mp4 (`<base>.stitched.mp4`) via `Mp4Stitcher` (MF SourceReader decode → `Mp4SinkWriter` re-encode).
- **Overlays for user-driven input** — `record_start overlaysUser:true` draws the **human's** clicks + shortcut keys via a global LL hook (`GlobalInputHook`), into the same overlay ring as the agent's injected input; plain typed text is not shown (privacy).
- **`wait_for_text` region/monitor scoping** — `deskhand_wait_for target:"text"` takes optional `monitor` / `region:"x,y,w,h"` to OCR less and avoid matching the same text elsewhere.
- **Forgiving `deskhand_click` name-matching** — exact UIA name → case-insensitive substring scan → OCR visible-text fallback (also uses `name` as OCR text), so a slightly-off/differently-cased name still lands.

_Verified in-process (sandbox): rolling→3 valid segments, auto-stitch→1 valid mp4, global hook captures real injected keys; semantic click/type and region text exercised against a throwaway Notepad._

_All listed follow-ups shipped. Stitch orientation verified upright (v0.2.70); lossless remux is the default stitch (v0.2.71)._

- **Stitch orientation** — ✅ verified upright (v0.2.70) via a deterministic top-white/bottom-black encode→decode round-trip; `Mp4Stitcher.FirstFramePng` added as the reusable poster-frame decoder used for the check.
- **True remux stitch** — ✅ shipped (v0.2.71): `Mp4Stitcher.StitchRemux` copies the compressed H.264 samples straight into one MP4 (SourceReader native → SinkWriter passthrough) with rebased timestamps — lossless and fast; `record_stop stitch:true` uses it, falling back to the decode/re-encode path only if passthrough can't be set up. Verified: 3 segments → one valid mp4 of the exact summed duration, byte size ≈ the segments' total (a copy, not a re-encode).

## Already supported (reported, but it exists — may just need surfacing / a convenience wrapper)

- **Window management** — `deskhand_window` already does `activate | minimize | maximize | restore | close | move | resize | bounds | topmost | notopmost` by `nativeWindowHandle`; `deskhand_list_windows` and `deskhand_list_windows_all` give title/pid/rect. Gap: targeting by **title/pid** directly instead of hwnd.
- **Paste long strings** — `deskhand_paste_text` sets the clipboard and sends Ctrl+V (no 160-char chunking). `deskhand_type_text` already accepts a `reference` to focus the target window first.
- **Per-window screenshot** — `deskhand_capture_window` captures one window (by element ref, incl. occluded via WGC). Gap: a by-**title/pid** convenience.
- **Long-running commands / kill reason** — addressed by auto-detach (v0.2.57) + `timeoutMs=0` (no cap) + `timedOut` in the result.
- **Recording fps / scale** — `deskhand_record_start` already takes `format`, `fps`, `scale`, `quality`. Still MJPEG/GIF only (see MP4 above).

## Notes

- Several "missing" features were really **discoverability** gaps — the agents hand-rolled window moves and per-window captures that Deskhand already exposes. Worth improving tool descriptions / a quick "capabilities" index so agents find them.
- The `$`-mangling and the "~160-char chunking" both smell like **caller/transport** limits (sandboxctl, MCP message size), not Deskhand internals — but `run_script` and the documented `paste_text` path make them non-issues from the agent's side.
