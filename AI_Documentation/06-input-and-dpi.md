# 06 — Input and DPI

## DPI awareness (do this first)

`DpiHelper.EnablePerMonitorV2()` is the **very first line** of both `Deskhand.Http/Program.cs` and
`Deskhand.Mcp/Program.cs`. It opts the process into **Per-Monitor-v2** DPI awareness:

```csharp
public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
```

**Why it must come first:** without it, Windows virtualizes coordinates and pixels for the process. Captured
pixels would be in one space and injected mouse coordinates in another, drifting on mixed-DPI monitors. With
Per-Monitor-v2, everything Deskhand reasons about is **physical (device) pixels on the virtual desktop** —
the same space `CopyFromScreen` reads and `SendInput` targets. It must be set *before* any window, capture,
or coordinate work touches the process. The WinForms toast additionally calls
`Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` on its own thread.

Monitor DPI itself is read per-monitor in `DesktopInfo.Monitors()` via
`GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out dpiX, out _)` → `scale = dpiX / 96.0`, and travels with
every `MonitorDto` and `CaptureResultDto`.

## Input engine — `SendInput`

All synthetic input is in `Deskhand.Core.Services.InputInjector` (static). It builds `INPUT` structs and
calls `SendInput`, producing real `WM_INPUT` events indistinguishable from hardware. There is **no**
"human-like" jitter or randomized timing — motion is functional and honest, by design.

The `INPUT` marshalling (in `NativeMethods`) is the standard union layout:

```csharp
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { int dx, dy; uint mouseData, dwFlags, time; IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { ushort wVk, wScan; uint dwFlags, time; IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit)]   public struct INPUTUNION { [FieldOffset(0)] MOUSEINPUT mi; [FieldOffset(0)] KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { int type; INPUTUNION u; }
[DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
```

Every send is checked and, on a short count, throws with the Win32 error:

```csharp
uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
if (sent != inputs.Length)
    throw new DesktopUnavailableException($"SendInput injected {sent}/{inputs.Length} events (Win32 error {err}). ...");
```

> **Gotcha — Win32 error 5 (access denied) means the input was blocked, not a bug.** A medium-integrity
> Deskhand process cannot send input to a higher-integrity (elevated) foreground window — that is User
> Interface Privilege Isolation (UIPI) — nor to the secure desktop. The message says so explicitly.

### Mouse — absolute over the virtual desktop

Coordinates are physical pixels on the virtual desktop, normalized to the **0..65535** absolute range using
`MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK` so secondary monitors are addressable:

```csharp
private static (int nx, int ny) ToAbsolute(int x, int y)
{
    int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1);
    int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);
    int nx = (int)Math.Round((x - vx) * 65535.0 / vw);
    int ny = (int)Math.Round((y - vy) * 65535.0 / vh);
    return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
}
public static void MouseMove(int x, int y) {
    var (nx, ny) = ToAbsolute(x, y);
    Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
}
```

> **Gotcha — `MOUSEEVENTF_VIRTUALDESK` is mandatory for multi-monitor.** Without it, absolute mouse
> coordinates map to the *primary* monitor only, so any point on a secondary monitor lands on the wrong
> screen. With it, 0..65535 spans the whole virtual desktop. Note the `-1` in `vw`/`vh`: the normalization
> denominator is `virtualSize - 1` so the far edge maps exactly to 65535.

- **Click** — optionally moves first, then sends `down`+`up` pairs `count` times. Buttons: `right`→
  `MOUSEEVENTF_RIGHTDOWN/UP`, `middle`→`MOUSEEVENTF_MIDDLEDOWN/UP`, default `left`→`MOUSEEVENTF_LEFTDOWN/UP`.
- **Down / Up** — single half-click, optional pre-move.
- **Scroll** — `MOUSEEVENTF_WHEEL` for `dy` (positive = up), `MOUSEEVENTF_HWHEEL` for `dx` (positive =
  right); the wheel data is `notches * WHEEL_DELTA` where `WHEEL_DELTA = 120`, cast `unchecked((uint)...)`.

### Keyboard — Unicode text and VK chords

**Typing literal text** uses `KEYEVENTF_UNICODE` with the char in `wScan` and `wVk = 0`, one down+up pair
per char (surrogate pairs handled by iterating `char`s):

```csharp
private static INPUT KeyUnicode(char c, bool up) => new() {
    type = INPUT_KEYBOARD,
    u = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = c,
        dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } } };
```

**Chords** (`SendKeys("ctrl+shift+s")`) are parsed by splitting on `+` (also stripping `{ }`). All but the
last token are modifiers; the last is the key:

- Modifiers → VK codes: `ctrl`/`control`→`0x11`, `alt`/`menu`→`0x12`, `shift`→`0x10`,
  `win`/`meta`/`cmd`→`0x5B`. Unknown modifier throws `ArgumentException`.
- The key token is resolved by `ResolveKey`:
  - Named keys: `enter`/`return`=0x0D, `tab`=0x09, `esc`=0x1B, `space`=0x20, `backspace`=0x08,
    `delete`=0x2E, `insert`=0x2D, `home`=0x24, `end`=0x23, `pageup`/`pagedown`=0x21/0x22, arrows=0x25–0x28,
    `printscreen`=0x2C (plus common aliases).
  - `f1`..`f24` → `0x70 + n - 1`.
  - A single character → **`VkKeyScan(ch)`**: low byte is the VK, bit `0x100` means Shift is required.
- Emit order: modifiers down → (Shift down if needed and not already a modifier) → key down → key up →
  (Shift up) → modifiers up (reverse order).

```csharp
short scan = VkKeyScan(token[0]);            // -1 => cannot map, throws
ushort vk = (ushort)(scan & 0xFF);
bool shift = (scan & 0x100) != 0;
```

`[DllImport("user32.dll")] public static extern short VkKeyScan(char ch);`

## Defeating the foreground lock — `WindowService.ForceForeground`

`SetForegroundWindow` alone is refused by Windows' foreground lock when called from a background process —
it just flashes the taskbar button instead of raising the window. `WindowService.ForceForeground(hwnd)` uses
the standard technique to actually raise a window:

```csharp
IntPtr fg = GetForegroundWindow();
uint fgThread = GetWindowThreadProcessId(fg, out _);
uint thisThread = GetCurrentThreadId();

uint oldTimeout = 0;
SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);   // lock timeout -> 0

bool attached = false;
if (fgThread != 0 && fgThread != thisThread)
    attached = AttachThreadInput(thisThread, fgThread, true);     // share input queue with current fg thread
try {
    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE); else ShowWindow(hwnd, SW_SHOW);
    BringWindowToTop(hwnd);
    return SetForegroundWindow(hwnd);
}
finally {
    if (attached) AttachThreadInput(thisThread, fgThread, false);
    SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)oldTimeout, SPIF_SENDCHANGE);  // restore
}
```

Constants: `SPI_GETFOREGROUNDLOCKTIMEOUT=0x2000`, `SPI_SETFOREGROUNDLOCKTIMEOUT=0x2001`,
`SPIF_SENDCHANGE=0x0002`, `SW_RESTORE=9`, `SW_SHOW=5`. Two overloads of `SystemParametersInfo` are declared
— one `ref uint` (to read the timeout) and one `IntPtr` (to write it).

> **Why the two-part trick:** zeroing `SPI_SETFOREGROUNDLOCKTIMEOUT` removes the "an app took focus
> recently, deny the steal" window, and `AttachThreadInput` makes Windows treat our thread and the current
> foreground thread as one input context, which is the condition under which `SetForegroundWindow`
> succeeds. The original timeout is always restored in `finally`. This is what `SetFocus` (04) calls before
> `element.Focus()`.
