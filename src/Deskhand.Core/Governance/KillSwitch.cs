using System.Runtime.InteropServices;

namespace Deskhand.Core.Governance;

/// <summary>
/// Global hotkey kill switch: <b>Ctrl+Alt+Pause</b> toggles the armed state from anywhere, so a
/// user can instantly cut off input and capture without touching the dashboard. Runs a tiny
/// message loop on its own thread; thread-targeted hotkeys need no window.
/// </summary>
public sealed class KillSwitch : IDisposable
{
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const uint VK_PAUSE = 0x13;
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xB0B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern void PostThreadMessage(uint idThread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }

    private readonly Thread _thread;
    private uint _threadId;
    private volatile bool _stop;

    public KillSwitch(ControlState state, AuditLog audit, Action<bool>? onToggle = null)
    {
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_PAUSE))
                return; // hotkey unavailable (already taken) — dashboard toggle still works
            try
            {
                while (!_stop && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_HOTKEY)
                    {
                        bool nowArmed = !state.Armed;
                        state.Armed = nowArmed;
                        audit.Record("kill_switch", "Ctrl+Alt+Pause", nowArmed ? "armed" : "disarmed");
                        onToggle?.Invoke(nowArmed);
                    }
                }
            }
            finally { UnregisterHotKey(IntPtr.Zero, HOTKEY_ID); }
        })
        { IsBackground = true, Name = "Deskhand-KillSwitch" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }
}
