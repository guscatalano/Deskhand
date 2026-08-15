# 10 — Governance and Safety (Phase 3)

All safety lives in `Deskhand.Core.Governance` and is enforced by **one decorator** wrapping the real
backend, so HTTP and MCP inherit it identically. Both hosts build:

```csharp
new GovernedBackend(new LocalAutomationBackend(), controlState, auditLog, captureNotifier)
```

## `GovernedBackend` — the decorator

`GovernedBackend(IAutomationBackend inner, ControlState state, AuditLog audit, ICaptureNotifier? notifier)`
implements `IAutomationBackend`. Every method: (a) gates, (b) delegates to `inner`, (c) audits.

- **Reads are always allowed and audited.** `Audited<T>(action, detail, op)` runs `op`, records `ok`, and
  on exception records `error:<ExceptionTypeName>` then rethrows.
- **Input-class actions are gated by `RequireInput`** — throws `DisarmedException` if `!Armed`
  (audited `refused:disarmed`) or `CapabilityDisabledException` if `!InputEnabled`
  (audited `refused:input-disabled`). Applies to all UIA *act* methods (invoke/set-value/toggle/
  expand-collapse/select/set-focus) and all mouse/keyboard methods.
- **Capture is gated by `RequireCapture`** — same shape, keyed on `CaptureEnabled`. On success the audit
  detail includes the image dimensions and a **content hash** (`AuditLog.HashImage`), and the screenshot
  toast fires:

```csharp
private CaptureResultDto Capture(string action, string? detail, Func<CaptureResultDto> op) {
    RequireCapture(action);
    try {
        var r = op();
        audit.Record(action, $"{detail} {r.Rect.Width}x{r.Rect.Height} sha={AuditLog.HashImage(r.Bytes)}", "ok");
        NotifyCapture(action, r.Desktop, r.Rect.Width, r.Rect.Height);
        return r;
    } catch (Exception ex) { audit.Record(action, detail, "error:" + ex.GetType().Name); throw; }
}
private void NotifyCapture(string action, string desktop, int w, int h) {
    if (!state.NotifyOnCapture || notifier is null) return;
    try { notifier.Notify($"Deskhand took a screenshot  ·  {desktop}  ·  {w}×{h}");
          audit.Record("screenshot_toast", action, "shown"); }
    catch { /* a notifier failure must never break capture */ }
}
```

`CaptureInputDesktop` is handled specially (records `desktopName`, `ok`/`empty`, toasts only on success).
`Dispose()` forwards to `inner`.

## `ControlState` — the switches

Four `volatile bool`s with `InputAllowed`/`CaptureAllowed` = `Armed && (Input|Capture)`:

```csharp
public bool Armed          { get; set; } = true;   // master kill switch
public bool InputEnabled   { get; set; } = true;
public bool CaptureEnabled { get; set; } = true;
public bool NotifyOnCapture{ get; set; } = true;   // screenshot toast, default ON
```

`ControlState.FromEnvironment()` reads env vars at startup (value `"1"` or `"true"`, case-insensitive, means
"on"):

| Env var | Effect |
|---|---|
| `DESKHAND_DISABLE_INPUT` | `InputEnabled = false` |
| `DESKHAND_DISABLE_CAPTURE` | `CaptureEnabled = false` |
| `DESKHAND_START_DISARMED` | `Armed = false` |
| `DESKHAND_DISABLE_CAPTURE_TOAST` | `NotifyOnCapture = false` |

Runtime toggles: `POST /control` (HTTP), the MCP tools `deskhand_arm`/`deskhand_disarm`, the dashboard
Safety panel, and the global hotkey.

**Disarmed behavior:** all input and capture are refused (`403 disarmed` over HTTP) while read-only
introspection still works.

## `AuditLog` — JSONL trail

Append-only, one JSON object per line. Directory defaults to
`%LOCALAPPDATA%\Deskhand\audit` (`Environment.SpecialFolder.LocalApplicationData` + `Deskhand\audit`),
created on construction. Files are dated: `audit-YYYYMMDD.jsonl`. Each line:

```json
{ "ts": "2026-08-15T10:22:33.4567890-07:00", "user": "crimson", "action": "capture_screen",
  "detail": "monitor=0 1920x1080 sha=9f3a1c...", "status": "ok" }
```

`ts` is `DateTimeOffset.Now` round-trip (`"o"`), `user` is `Environment.UserName`. Writes are serialized by
a `lock`. `HashImage(byte[])` = first 16 hex chars of `SHA256.HashData(bytes)`, lowercased — a capture is
logged by hash, never stored.

## `KillSwitch` — global hotkey Ctrl+Alt+Pause

A tiny message loop on its own background thread (`Deskhand-KillSwitch`) registers a **thread-targeted**
hotkey (no window needed):

```csharp
RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_PAUSE);
// MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_NOREPEAT=0x4000, VK_PAUSE=0x13, WM_HOTKEY=0x0312
while (!_stop && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
    if (msg.message == WM_HOTKEY) {
        bool nowArmed = !state.Armed; state.Armed = nowArmed;
        audit.Record("kill_switch", "Ctrl+Alt+Pause", nowArmed ? "armed" : "disarmed");
        onToggle?.Invoke(nowArmed);
    }
```

Pressing **Ctrl+Alt+Pause** anywhere flips `Armed`, instantly cutting off (or restoring) input and capture.
If the hotkey is already taken by another app, registration fails silently and the dashboard/`/control`
toggle still works. `Dispose()` posts `WM_QUIT` (0x0012) to the thread and `UnregisterHotKey` runs in the
loop's `finally`. Both hosts construct `using var killSwitch = new KillSwitch(controlState, auditLog);`.

## Screenshot toast (`Deskhand.Ui.ToastNotifier`)

Implements `ICaptureNotifier` (from Core). The `GovernedBackend` calls `notifier.Notify(...)` after **every**
capture unless `NotifyOnCapture` is off — so the user always knows their screen was captured. This is the
one project that sets `<UseWindowsForms>true</UseWindowsForms>`.

- Runs a private **STA** thread with `Application.SetHighDpiMode(PerMonitorV2)` + `Application.Run()`.
- Draws a borderless, non-activating, top-most, tool-window `Form` (`ToastForm`) in the **bottom-right** of
  the primary working area: dark rounded card, an accent bar, a 📷 glyph, and the message text
  (`"Deskhand took a screenshot · <desktop> · <w>×<h>"`).
- **Non-activating** so it never steals focus from what's being automated: overrides
  `ShowWithoutActivation => true` and adds `WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW` in
  `CreateParams`.
- Shows at ~0.96 opacity, holds ~2.6s, then fades over ~0.7s via a WinForms timer, then hides.
- `Notify` marshals onto the form thread with `BeginInvoke`; failures are swallowed (a toast failure must
  never break capture). `Dispose` exits the message loop and joins.

> **Not to be confused with** the dashboard's own in-page `#toast` (a `<div>` status message). The WinForms
> toast is the *server-side, on-screen* consent signal that fires regardless of which host or client
> triggered the capture.
