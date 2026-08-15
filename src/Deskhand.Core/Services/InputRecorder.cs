using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Deskhand.Core.Services;

/// <summary>One thing the user did: a click (with the element it landed on), a scroll, a run of typed
/// text, or a special key.</summary>
public record UserInputEventDto(
    long Seq, string Ts, string Kind, string? Button, int? X, int? Y,
    string? Key, string? Text, ElementInfoDto? Element);

/// <summary>
/// Records the <b>user's</b> physical mouse + keyboard input via global low-level hooks
/// (WH_MOUSE_LL / WH_KEYBOARD_LL), and — for each click — resolves the UIA element under the cursor so
/// the log shows <i>what</i> was clicked, not just coordinates. Complements <c>MacroRecorder</c>, which
/// records the agent's own actions.
///
/// Privacy: this captures real keystrokes (which can include passwords). It is off by default, must be
/// started explicitly, and typing capture can be disabled (mouse-only). Element resolution runs on a
/// worker thread so the hook callback stays fast (Windows drops slow LL hooks).
/// </summary>
public sealed class InputRecorder : IDisposable
{
    private readonly Func<int, int, ElementInfoDto?> _resolveAt;
    private readonly object _gate = new();
    private readonly List<UserInputEventDto> _events = new();
    private long _seq;

    private bool _recording;
    private bool _captureText;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _mouseHook, _keyHook;
    private HookProc? _mouseProc, _keyProc;          // held to prevent GC of the delegates
    private BlockingCollection<Raw>? _queue;
    private Thread? _worker;
    private readonly StringBuilder _textRun = new();

    public InputRecorder(Func<int, int, ElementInfoDto?> resolveAt) => _resolveAt = resolveAt;

    public bool IsRecording { get { lock (_gate) return _recording; } }
    public long LastId { get { lock (_gate) return _seq; } }

    public object Start(bool captureText = true)
    {
        lock (_gate)
        {
            if (_recording) return Status();
            _recording = true; _captureText = captureText; _events.Clear(); _seq = 0; _textRun.Clear();
        }
        _queue = new BlockingCollection<Raw>();
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "deskhand-input-worker" };
        _worker.Start();
        _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "deskhand-input-hooks" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        return Status();
    }

    public object Stop()
    {
        lock (_gate) { if (!_recording) return Status(); _recording = false; }
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _queue?.CompleteAdding();
        try { _worker?.Join(1500); } catch { }
        FlushText();
        return Status();
    }

    public IReadOnlyList<UserInputEventDto> Since(long cursor) { lock (_gate) return _events.Where(e => e.Seq > cursor).ToList(); }

    public object Status()
    {
        lock (_gate) return new { recording = _recording, captureText = _captureText, count = _events.Count, lastId = _seq };
    }

    // ---- hook thread: install hooks + pump messages ----
    private void HookLoop()
    {
        _hookThreadId = GetCurrentThreadId();
        _mouseProc = MouseProc; _keyProc = KeyProc;
        IntPtr mod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, mod, 0);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { /* pump until WM_QUIT */ }

        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _recording && _queue is { IsAddingCompleted: false })
        {
            int msg = (int)wParam;
            var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            string? button = msg switch { WM_LBUTTONDOWN => "left", WM_RBUTTONDOWN => "right", WM_MBUTTONDOWN => "middle", _ => null };
            if (button is not null)
                _queue.TryAdd(new Raw(RawKind.Click, ms.pt.x, ms.pt.y, button, 0, 0, false, false));
            else if (msg == WM_MOUSEWHEEL)
                _queue.TryAdd(new Raw(RawKind.Scroll, ms.pt.x, ms.pt.y, (short)((ms.mouseData >> 16) & 0xFFFF) > 0 ? "up" : "down", 0, 0, false, false));
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _recording && _captureText && _queue is { IsAddingCompleted: false })
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                bool caps = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
                _queue.TryAdd(new Raw(RawKind.Key, 0, 0, null, kb.vkCode, kb.scanCode, shift, caps));
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    // ---- worker thread: resolve elements + coalesce text (keeps the hook callback fast) ----
    private void WorkerLoop()
    {
        foreach (var r in _queue!.GetConsumingEnumerable())
        {
            try
            {
                switch (r.Kind)
                {
                    case RawKind.Click:
                        FlushText();
                        Add(new UserInputEventDto(0, Now(), "click", r.S, r.X, r.Y, null, null, Safe(r.X, r.Y)));
                        break;
                    case RawKind.Scroll:
                        FlushText();
                        Add(new UserInputEventDto(0, Now(), "scroll", r.S, r.X, r.Y, null, null, null));
                        break;
                    case RawKind.Key:
                        HandleKey(r);
                        break;
                }
            }
            catch { }
        }
    }

    private void HandleKey(Raw r)
    {
        string? special = SpecialName(r.Vk);
        string? ch = special is null ? Translate(r.Vk, r.Scan, r.Shift, r.Caps) : null;
        if (ch is { Length: > 0 })
        {
            lock (_gate) _textRun.Append(ch);
        }
        else
        {
            FlushText();
            Add(new UserInputEventDto(0, Now(), "key", null, null, null, special ?? ("VK_" + r.Vk), null, null));
        }
    }

    private void FlushText()
    {
        string text;
        lock (_gate) { if (_textRun.Length == 0) return; text = _textRun.ToString(); _textRun.Clear(); }
        Add(new UserInputEventDto(0, Now(), "text", null, null, null, null, text, null));
    }

    private ElementInfoDto? Safe(int x, int y) { try { return _resolveAt(x, y); } catch { return null; } }

    private void Add(UserInputEventDto e)
    {
        lock (_gate)
        {
            _events.Add(e with { Seq = ++_seq });
            if (_events.Count > 5000) _events.RemoveRange(0, 1000);   // bound memory for a long session
        }
    }

    private static string Now() => DateTimeOffset.Now.ToString("o");

    private const uint VK_PACKET = 0xE7;

    private static string? Translate(uint vk, uint scan, bool shift, bool caps)
    {
        // Injected Unicode (SendInput KEYEVENTF_UNICODE) arrives as VK_PACKET with the char in the scan code.
        if (vk == VK_PACKET) { char c = (char)scan; return c == '\0' ? null : c.ToString(); }
        var state = new byte[256];
        if (shift) state[VK_SHIFT] = 0x80;
        if (caps) state[VK_CAPITAL] = 0x01;
        var sb = new StringBuilder(8);
        int n = ToUnicode(vk, scan, state, sb, sb.Capacity, 0);
        return n > 0 ? sb.ToString(0, n) : null;
    }

    private static string? SpecialName(uint vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc", 0x20 => "Space",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x10 or 0xA0 or 0xA1 => "Shift", 0x11 or 0xA2 or 0xA3 => "Ctrl", 0x12 or 0xA4 or 0xA5 => "Alt",
        0x5B or 0x5C => "Win", 0x14 => "CapsLock",
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        _ => null,
    };

    public void Dispose() { try { Stop(); } catch { } }

    private enum RawKind { Click, Scroll, Key }
    private readonly record struct Raw(RawKind Kind, int X, int Y, string? S, uint Vk, uint Scan, bool Shift, bool Caps);

    // ---- native ----
    private const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_MOUSEWHEEL = 0x020A;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;
    private const int VK_SHIFT = 0x10, VK_CAPITAL = 0x14;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags);
}
