using System.Text;
using static Deskhand.Core.Interop.NativeMethods;

namespace Deskhand.Core.Services;

public record WindowBoundsDto(int X, int Y, int Width, int Height);
public record WindowActionResultDto(
    bool Ok, long Hwnd, string Action, string? Title = null, string? State = null,
    WindowBoundsDto? Bounds = null, string? Error = null);

/// <summary>
/// Window management by native handle (the <c>nativeWindowHandle</c> from <c>/windows</c>): raise/activate,
/// minimize/maximize/restore, move/resize, or close a top-level window. Handle-based (no UIA), so it works for
/// any HWND, including apps UI Automation can't see well.
/// </summary>
public static class WindowService
{
    /// <summary>
    /// Bring a window to the foreground from a background process. A plain SetForegroundWindow is refused by
    /// Windows' foreground lock (it just flashes the taskbar button), so we temporarily zero the lock timeout
    /// and attach our input queue to the current foreground thread — the standard technique for reliably
    /// raising a window.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();

        uint oldTimeout = 0;
        SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);

        bool attached = false;
        if (fgThread != 0 && fgThread != thisThread)
            attached = AttachThreadInput(thisThread, fgThread, true);

        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            else ShowWindow(hwnd, SW_SHOW);
            BringWindowToTop(hwnd);
            bool ok = SetForegroundWindow(hwnd);
            return ok;
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
            SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)oldTimeout, SPIF_SENDCHANGE);
        }
    }

    // ---- handle-addressed window management (the /window/* surface) ----

    public static WindowActionResultDto Activate(long hwnd) => Do(hwnd, "activate", h => ForceForeground(h));
    public static WindowActionResultDto Minimize(long hwnd) => Do(hwnd, "minimize", h => ShowWindow(h, SW_MINIMIZE));
    public static WindowActionResultDto Maximize(long hwnd) => Do(hwnd, "maximize", h => ShowWindow(h, SW_MAXIMIZE));
    public static WindowActionResultDto Restore(long hwnd) => Do(hwnd, "restore", h => ShowWindow(h, SW_RESTORE));
    public static WindowActionResultDto Close(long hwnd) => Do(hwnd, "close", h => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero));

    public static WindowActionResultDto Move(long hwnd, int x, int y) => Do(hwnd, "move", h =>
        SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE));

    public static WindowActionResultDto Resize(long hwnd, int width, int height) => Do(hwnd, "resize", h =>
        SetWindowPos(h, IntPtr.Zero, 0, 0, Math.Max(0, width), Math.Max(0, height), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE));

    public static WindowActionResultDto SetBounds(long hwnd, int x, int y, int width, int height) => Do(hwnd, "set_bounds", h =>
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetWindowPos(h, IntPtr.Zero, x, y, Math.Max(0, width), Math.Max(0, height), SWP_NOZORDER | SWP_NOACTIVATE);
    });

    private static WindowActionResultDto Do(long hwnd, string action, Action<IntPtr> act)
    {
        var h = (IntPtr)hwnd;
        if (h == IntPtr.Zero || !IsWindow(h))
            return new WindowActionResultDto(false, hwnd, action, Error: $"No window with handle {hwnd} (get a fresh one from /windows).");
        try
        {
            act(h);
            string? title = Title(h);
            var b = Bounds(h);
            string state = !IsWindow(h) ? "closed" : IsIconic(h) ? "minimized" : IsZoomed(h) ? "maximized" : "normal";
            return new WindowActionResultDto(true, hwnd, action, title, state, b);
        }
        catch (Exception ex) { return new WindowActionResultDto(false, hwnd, action, Error: ex.Message); }
    }

    private static string? Title(IntPtr h)
    {
        try
        {
            int len = GetWindowTextLength(h);
            if (len <= 0) return null;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.Length == 0 ? null : sb.ToString();
        }
        catch { return null; }
    }

    private static WindowBoundsDto? Bounds(IntPtr h)
    {
        try { return GetWindowRect(h, out RECT r) ? new WindowBoundsDto(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null; }
        catch { return null; }
    }
}
