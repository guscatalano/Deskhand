using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Deskhand.Core.Interop;

namespace Deskhand.Core.Services;

/// <summary>
/// Best-effort reporting of the machine, its monitors, and which desktop currently
/// owns input. From a normal user-session process the secure desktop (UAC / lock /
/// logon) is not directly inspectable — that is by design, and is what the Phase 2
/// SYSTEM "Secure Helper" would add. Here we report the state honestly instead.
/// </summary>
public static class DesktopInfo
{
    public static DesktopStateDto GetDesktopState()
    {
        IntPtr hDesktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (hDesktop == IntPtr.Zero)
        {
            // Cannot open the input desktop → the secure desktop is almost certainly active.
            return new DesktopStateDto(
                Desktop: "secure",
                RawDesktopName: "",
                InputAvailable: false,
                Note: "Input desktop is not accessible from this user session (secure desktop or locked). " +
                      "Secure-desktop coverage requires the SYSTEM Secure Helper (design Phase 2).");
        }

        try
        {
            string name = GetDesktopName(hDesktop);
            return name switch
            {
                "Default" => new DesktopStateDto("default", name, true, "Normal user desktop."),
                "Winlogon" => new DesktopStateDto("secure", name, false, "Secure desktop (UAC / lock / logon)."),
                "Screen-saver" => new DesktopStateDto("screensaver", name, false, "Screen saver desktop."),
                _ => new DesktopStateDto("unknown", name, true, "Unrecognized input desktop."),
            };
        }
        finally
        {
            NativeMethods.CloseDesktop(hDesktop);
        }
    }

    private static string GetDesktopName(IntPtr hDesktop)
    {
        NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, null, 0, out uint needed);
        if (needed == 0) return "";
        var buffer = new byte[needed];
        if (!NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, buffer, needed, out _))
            return "";
        // Unicode, null-terminated.
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static RectDto VirtualScreen()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new RectDto(x, y, w, h);
    }

    public static IReadOnlyList<MonitorDto> Monitors()
    {
        var result = new List<MonitorDto>();
        int index = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr data) =>
        {
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            bool primary = false;
            RectDto bounds;
            double scale = 1.0;
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                primary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                bounds = new RectDto(mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
            }
            else
            {
                bounds = new RectDto(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
            }
            if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
                scale = dpiX / 96.0;
            result.Add(new MonitorDto(index++, bounds, primary, scale));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static MachineInfoDto GetMachineInfo()
    {
        bool elevated = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { /* best effort */ }

        return new MachineInfoDto(
            MachineName: Environment.MachineName,
            UserName: Environment.UserName,
            OsVersion: Environment.OSVersion.VersionString,
            IsElevated: elevated,
            Monitors: Monitors(),
            VirtualScreen: VirtualScreen(),
            DesktopState: GetDesktopState());
    }
}
