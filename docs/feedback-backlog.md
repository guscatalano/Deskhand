# Deskhand — AI feedback backlog

Field feedback from AI agents driving Deskhand, triaged. Two rounds so far.

## Shipped (v0.2.58–v0.2.65)

| Item | What shipped |
|---|---|
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

- **Recordings that survive a reboot** — resume under the same id after sign-in (persist recorder state + logon re-attach), so a demo with a restart isn't two clips.

## Already supported (reported, but it exists — may just need surfacing / a convenience wrapper)

- **Window management** — `deskhand_window` already does `activate | minimize | maximize | restore | close | move | resize | bounds | topmost | notopmost` by `nativeWindowHandle`; `deskhand_list_windows` and `deskhand_list_windows_all` give title/pid/rect. Gap: targeting by **title/pid** directly instead of hwnd.
- **Paste long strings** — `deskhand_paste_text` sets the clipboard and sends Ctrl+V (no 160-char chunking). `deskhand_type_text` already accepts a `reference` to focus the target window first.
- **Per-window screenshot** — `deskhand_capture_window` captures one window (by element ref, incl. occluded via WGC). Gap: a by-**title/pid** convenience.
- **Long-running commands / kill reason** — addressed by auto-detach (v0.2.57) + `timeoutMs=0` (no cap) + `timedOut` in the result.
- **Recording fps / scale** — `deskhand_record_start` already takes `format`, `fps`, `scale`, `quality`. Still MJPEG/GIF only (see MP4 above).

## Notes

- Several "missing" features were really **discoverability** gaps — the agents hand-rolled window moves and per-window captures that Deskhand already exposes. Worth improving tool descriptions / a quick "capabilities" index so agents find them.
- The `$`-mangling and the "~160-char chunking" both smell like **caller/transport** limits (sandboxctl, MCP message size), not Deskhand internals — but `run_script` and the documented `paste_text` path make them non-issues from the agent's side.
