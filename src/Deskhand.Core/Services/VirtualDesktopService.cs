using System.Runtime.InteropServices;
using System.Text;

namespace Deskhand.Core.Services;

public record DesktopWindowDto(long Hwnd, string? Title, int ProcessId, bool OnCurrent);
public record VirtualDesktopDto(string Id, bool IsCurrent, IReadOnlyList<DesktopWindowDto> Windows);

/// <summary>
/// Windows Virtual Desktops via the <b>documented</b> <c>IVirtualDesktopManager</c> — stable across builds,
/// but limited: it can tell which desktop a window is on and move a window between desktops; it CANNOT
/// list/switch/create/remove desktops (that needs undocumented per-build COM). So we group the visible
/// top-level windows by their desktop GUID (marking the current one) and can move a window to the current
/// desktop or to another window's desktop.
/// </summary>
public static class VirtualDesktopService
{
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid desktopId);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManager { }

    private static IVirtualDesktopManager Mgr() => (IVirtualDesktopManager)new VirtualDesktopManager();

    /// <summary>Visible top-level windows grouped by the virtual desktop they're on (the group with the
    /// on-current windows is flagged <c>IsCurrent</c>). Current desktop first.</summary>
    public static IReadOnlyList<VirtualDesktopDto> ListByWindow()
    {
        var mgr = Mgr();
        var groups = new Dictionary<Guid, List<DesktopWindowDto>>();
        var currentIds = new HashSet<Guid>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsVisibleAppWindow(hwnd)) return true;
            try
            {
                if (mgr.GetWindowDesktopId(hwnd, out var id) != 0 || id == Guid.Empty) return true;
                mgr.IsWindowOnCurrentVirtualDesktop(hwnd, out var onCur);
                if (onCur != 0) currentIds.Add(id);
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (!groups.TryGetValue(id, out var l)) groups[id] = l = new();
                l.Add(new DesktopWindowDto(hwnd.ToInt64(), Title(hwnd), (int)pid, onCur != 0));
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return groups
            .Select(g => new VirtualDesktopDto(g.Key.ToString(), currentIds.Contains(g.Key), g.Value))
            .OrderByDescending(d => d.IsCurrent)
            .ToList();
    }

    /// <summary>The GUID of the current virtual desktop (from the foreground window, which is always on it).</summary>
    public static Guid? CurrentDesktopId()
    {
        try
        {
            var mgr = Mgr();
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && mgr.GetWindowDesktopId(fg, out var id) == 0 && id != Guid.Empty) return id;
        }
        catch { }
        return null;
    }

    /// <summary>Move a window to the current desktop (bring it here).</summary>
    public static bool MoveWindowToCurrent(IntPtr hwnd)
    {
        var cur = CurrentDesktopId();
        if (cur is null) return false;
        return Move(hwnd, cur.Value);
    }

    /// <summary>Move a window to the desktop identified by a GUID string (e.g. one from <see cref="ListByWindow"/>).</summary>
    public static bool MoveWindowToDesktop(IntPtr hwnd, string desktopId)
    {
        if (!Guid.TryParse(desktopId, out var g)) throw new ArgumentException($"Not a desktop GUID: '{desktopId}'.");
        return Move(hwnd, g);
    }

    private static bool Move(IntPtr hwnd, Guid id)
    {
        try { return Mgr().MoveWindowToDesktop(hwnd, id) == 0; }
        catch { return false; }
    }

    // ---- window enumeration ----
    private static bool IsVisibleAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x00000080;
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        return true;
    }

    private static string? Title(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return null;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
