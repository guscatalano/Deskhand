using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record DisplayModeDto(int Width, int Height, int RefreshHz, int Bpp);
public record DisplayDto(string Device, string? Adapter, bool Primary, DisplayModeDto? Current, IReadOnlyList<DisplayModeDto> Modes);
public record DisplaySetResultDto(bool Ok, string Device, DisplayModeDto? Applied, string? Error = null);

/// <summary>
/// List connected displays and their available modes, and change a display's resolution / refresh rate
/// (<c>ChangeDisplaySettingsEx</c> with <c>CDS_UPDATEREGISTRY</c>, so it persists). Changing the mode is a
/// system-altering action — the host layer gates it on the kill switch (armed) and audits it. Read (list) is
/// harmless. Unknown/invalid modes are validated with a <c>CDS_TEST</c> pass first and return a clear error
/// instead of leaving the desktop in a bad state.
/// </summary>
public static class DisplayService
{
    public static IReadOnlyList<DisplayDto> List()
    {
        var list = new List<DisplayDto>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            if (!attached) continue;
            bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            string device = dd.DeviceName;
            string adapter = dd.DeviceString;

            DisplayModeDto? current = ReadMode(device, ENUM_CURRENT_SETTINGS);
            var modes = new List<DisplayModeDto>();
            var seen = new HashSet<(int, int, int)>();
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            for (int m = 0; EnumDisplaySettings(device, m, ref dm); m++)
            {
                dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
                if (dm.dmBitsPerPel < 24) continue;   // ignore legacy low-colour modes
                var key = (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
                if (seen.Add(key))
                    modes.Add(new DisplayModeDto(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel));
            }
            modes = modes
                .OrderByDescending(x => x.Width).ThenByDescending(x => x.Height).ThenByDescending(x => x.RefreshHz)
                .ToList();
            list.Add(new DisplayDto(device, adapter, primary, current, modes));
        }
        return list;
    }

    /// <summary>Change a display's mode. <paramref name="device"/> empty/null targets the primary display.
    /// <paramref name="refreshHz"/> 0 keeps the current refresh for the chosen resolution.</summary>
    public static DisplaySetResultDto SetResolution(string? device, int width, int height, int refreshHz = 0)
    {
        device = (device ?? "").Trim();
        if (string.IsNullOrEmpty(device))
            device = List().FirstOrDefault(d => d.Primary)?.Device ?? List().FirstOrDefault()?.Device ?? "";
        if (string.IsNullOrEmpty(device)) return new DisplaySetResultDto(false, device, null, "No display found.");
        if (width <= 0 || height <= 0) return new DisplaySetResultDto(false, device, null, "width and height must be positive.");

        // Start from the device's current mode so we keep bpp/orientation, then override the fields we're setting.
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm))
            return new DisplaySetResultDto(false, device, null, $"Unknown display '{device}'.");
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        if (refreshHz > 0) { dm.dmDisplayFrequency = refreshHz; dm.dmFields |= DM_DISPLAYFREQUENCY; }

        int test = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != DISP_CHANGE_SUCCESSFUL)
            return new DisplaySetResultDto(false, device, null,
                $"{width}x{height}{(refreshHz > 0 ? "@" + refreshHz : "")} is not a supported mode for this display ({ChangeResult(test)}). Use the modes from the list.");

        int apply = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (apply != DISP_CHANGE_SUCCESSFUL)
            return new DisplaySetResultDto(false, device, null, $"Failed to apply the mode ({ChangeResult(apply)}).");

        return new DisplaySetResultDto(true, device, ReadMode(device, ENUM_CURRENT_SETTINGS));
    }

    private static DisplayModeDto? ReadMode(string device, int which)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(device, which, ref dm)) return null;
        return new DisplayModeDto(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel);
    }

    private static string ChangeResult(int code) => code switch
    {
        0 => "successful",
        1 => "restart required",
        -1 => "failed",
        -2 => "bad mode",
        -4 => "not updated",
        -5 => "bad flags",
        -6 => "bad parameter",
        _ => $"code {code}",
    };

    // ---- interop ----
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x01;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x04;
    private const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    private const int CDS_UPDATEREGISTRY = 0x01, CDS_TEST = 0x02;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;           // POINTL (union with print fields)
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }
}
