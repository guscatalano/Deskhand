# Deskhand — AI feedback backlog

Field feedback from AI agents driving Deskhand, triaged. Two rounds so far.

## Shipped (v0.2.58–v0.2.60)

| Item | What shipped |
|---|---|
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

- **H.264 MP4 recordings + longer clips** — current MJPEG-AVI is ~1.7 MB/s (~1 GB per 4–5 min) and capped at 5 min; H.264 needs an encoder (Media Foundation hardware encode, or bundling ffmpeg) — a real dependency call. Let the caller name the output file.
- **Recordings that survive a reboot** — resume under the same id after sign-in (persist recorder state + logon re-attach), so a demo with a restart isn't two clips.
- **`find_elements` misses some Start tiles** — search `StartMenuExperienceHost`'s own UIA tree and match on visible text, not just the UIA Name.
- **Click / key overlays on recordings** — optional click highlights + a key-press strip.
- **Recording markers** — `record_mark {label}` and `record_speed {factor}` from here to the next marker, so Deskhand can render captions/speed-ups itself.

## Already supported (reported, but it exists — may just need surfacing / a convenience wrapper)

- **Window management** — `deskhand_window` already does `activate | minimize | maximize | restore | close | move | resize | bounds | topmost | notopmost` by `nativeWindowHandle`; `deskhand_list_windows` and `deskhand_list_windows_all` give title/pid/rect. Gap: targeting by **title/pid** directly instead of hwnd.
- **Paste long strings** — `deskhand_paste_text` sets the clipboard and sends Ctrl+V (no 160-char chunking). `deskhand_type_text` already accepts a `reference` to focus the target window first.
- **Per-window screenshot** — `deskhand_capture_window` captures one window (by element ref, incl. occluded via WGC). Gap: a by-**title/pid** convenience.
- **Long-running commands / kill reason** — addressed by auto-detach (v0.2.57) + `timeoutMs=0` (no cap) + `timedOut` in the result.
- **Recording fps / scale** — `deskhand_record_start` already takes `format`, `fps`, `scale`, `quality`. Still MJPEG/GIF only (see MP4 above).

## Notes

- Several "missing" features were really **discoverability** gaps — the agents hand-rolled window moves and per-window captures that Deskhand already exposes. Worth improving tool descriptions / a quick "capabilities" index so agents find them.
- The `$`-mangling and the "~160-char chunking" both smell like **caller/transport** limits (sandboxctl, MCP message size), not Deskhand internals — but `run_script` and the documented `paste_text` path make them non-issues from the agent's side.
