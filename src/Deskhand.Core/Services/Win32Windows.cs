using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Deskhand.Core.Services;

public record Win32Window(long Hwnd, string? Title, string? Class, int Pid, string? Process,
    int X, int Y, int Width, int Height, bool Owned, bool Foreground);

/// <summary>
/// Complete top-level window enumeration via the raw Win32 <c>EnumWindows</c> — it catches windows the UIA tree
/// misses (owned pop-ups, VCL/Delphi nag windows like <c>TInAppShopForm</c>, tool windows), which is exactly the
/// class of "a window appeared over my app and my click went somewhere baffling" the agent needs to see. Cloaked
/// (suspended UWP) and empty junk windows are filtered out. Read-only.
/// </summary>
public static class Win32Windows
{
    public static long Foreground() { try { return (long)GetForegroundWindow(); } catch { return 0; } }

    public static IReadOnlyList<Win32Window> List()
    {
        long fg = Foreground();
        var list = new List<Win32Window>();
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                if (IsCloaked(h)) return true;                       // suspended UWP / hidden shell windows
                string title = Text(h);
                string cls = ClassOf(h);
                GetWindowRect(h, out RECT r);
                int w = r.Right - r.Left, hgt = r.Bottom - r.Top;
                if (title.Length == 0 && (w <= 1 || hgt <= 1)) return true;   // junk: no title and no size
                if (IsShell(cls)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                list.Add(new Win32Window((long)h,
                    title.Length == 0 ? null : title, cls, (int)pid, ProcName((int)pid),
                    r.Left, r.Top, w, hgt,
                    GetWindow(h, GW_OWNER) != IntPtr.Zero, (long)h == fg && fg != 0));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool IsCloaked(IntPtr h)
    {
        try { return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0; }
        catch { return false; }
    }

    private static bool IsShell(string cls) => cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Button" or "DummyDWMListenerWindow";

    private static string Text(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
    private static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    private static string? ProcName(int pid) { try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return null; } }

    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
}
