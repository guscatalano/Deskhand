using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

/// <summary>
/// A global low-level mouse/keyboard hook (WH_MOUSE_LL / WH_KEYBOARD_LL on a dedicated message-pump thread) that
/// raises an event for each click and each shortcut/navigation key — the source for drawing the HUMAN's input on a
/// recording's overlays (distinct from the agent's injected input, which comes from <see cref="InputInjector"/>).
/// Reference-counted: the hooks are installed on the first <see cref="AddRef"/> and removed on the last
/// <see cref="Release"/>. PRIVACY: plain typed characters are deliberately NOT surfaced (only clicks, modified
/// shortcuts like Ctrl+S, and non-character keys like Enter/Tab/arrows), so an overlay can't leak passwords.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    public readonly record struct HookEvent(bool IsKey, int X, int Y, string Label);
    public event Action<HookEvent>? Input;

    private readonly object _gate = new();
    private int _refs;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyHook;
    private HookProc? _mouseProc, _keyProc;

    public void AddRef() { lock (_gate) { if (_refs++ == 0) StartHooks(); } }
    public void Release() { lock (_gate) { if (_refs > 0 && --_refs == 0) StopHooks(); } }

    private void StartHooks()
    {
        _thread = new Thread(HookLoop) { IsBackground = true, Name = "deskhand-overlay-hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void StopHooks()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null; _threadId = 0;
    }

    private void HookLoop()
    {
        _threadId = GetCurrentThreadId();
        _mouseProc = MouseProc; _keyProc = KeyProc;
        IntPtr mod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, mod, 0);
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { /* pump until WM_QUIT */ }
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            string? button = (int)wParam switch { WM_LBUTTONDOWN => "left", WM_RBUTTONDOWN => "right", WM_MBUTTONDOWN => "middle", _ => null };
            if (button is not null)
                try { Input?.Invoke(new HookEvent(false, ms.pt.x, ms.pt.y, button)); } catch { }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            string? label = LabelFor(kb.vkCode);
            if (label is not null)
                try { Input?.Invoke(new HookEvent(true, 0, 0, label)); } catch { }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    // Decide what (if anything) to surface for a key. Plain typed characters are dropped for privacy; a key is
    // surfaced only when a Ctrl/Alt/Win modifier is held (a shortcut) or it's a non-character key.
    private static string? LabelFor(uint vk)
    {
        bool ctrl = Down(VK_CONTROL), alt = Down(VK_MENU), win = Down(VK_LWIN) || Down(VK_RWIN), shift = Down(VK_SHIFT);
        bool isChar = (vk >= 0x30 && vk <= 0x5A) || vk == 0x20 || (vk >= 0x60 && vk <= 0x6F) || (vk >= 0xBA && vk <= 0xE2);
        bool modified = ctrl || alt || win;
        if (isChar && !modified) return null;   // plain typing — not surfaced
        string key = KeyName(vk);
        if (key.Length == 0) return null;
        var mods = "";
        if (ctrl) mods += "Ctrl+";
        if (alt) mods += "Alt+";
        if (win) mods += "Win+";
        if (shift && modified) mods += "Shift+";
        return mods + key;
    }

    private static string KeyName(uint vk) => vk switch
    {
        0x0D => "Enter", 0x09 => "Tab", 0x1B => "Esc", 0x20 => "Space", 0x08 => "Backspace",
        0x2E => "Del", 0x2D => "Ins", 0x24 => "Home", 0x23 => "End", 0x21 => "PgUp", 0x22 => "PgDn",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2C => "PrtSc",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num" + (char)('0' + (vk - 0x60)),
        _ => "",
    };

    private static bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    public void Dispose() { lock (_gate) { _refs = 0; StopHooks(); } }

    private const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

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
}
