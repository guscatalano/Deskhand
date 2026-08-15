using Deskhand.Core.Interop;
using static Deskhand.Core.Interop.NativeMethods;

namespace Deskhand.Core.Services;

/// <summary>
/// Bring a window to the foreground from a background process. A plain SetForegroundWindow
/// is refused by Windows' foreground lock (it just flashes the taskbar button), so we
/// temporarily zero the lock timeout and attach our input queue to the current foreground
/// thread — the standard technique for reliably raising a window.
/// </summary>
public static class WindowService
{
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
}
