using System.Management;

namespace Deskhand.Core.Services;

public record VolumeDto(string? DriveLetter, string? Label, string? FileSystem, long? SizeBytes, long? FreeBytes);
public record PartitionDto(string? Name, long? SizeBytes, string? Type, bool Bootable, IReadOnlyList<VolumeDto> Volumes);
public record PhysicalDiskDto(uint Index, string? Model, string? InterfaceType, string? MediaType, string? Serial, long? SizeBytes, uint Partitions, IReadOnlyList<PartitionDto> PartitionList);
public record HotfixDto(string? HotFixId, string? Description, string? InstalledOn, string? InstalledBy);
public record DeviceDto(string? Name, string? Class, string? Manufacturer, string? Status, string? DeviceId);
public record DriverDto(string? Device, string? Provider, string? Version, string? Date, string? InfName, bool? Signed);
public record AudioDeviceDto(string? Name, string? Manufacturer, string? Status);

/// <summary>
/// Read-only hardware / software inventory via WMI (CIM): physical disks + partitions + volumes, installed
/// Windows updates (KBs), PnP devices, installed drivers, and audio devices. Nothing is changed. Some queries
/// (devices, drivers) enumerate hundreds of entries and can take a second or two; results are capped.
/// </summary>
public static class HardwareInfoService
{
    private const int MaxRows = 4000;

    // ---- disks: physical disk -> partitions -> logical volumes (via WMI associators) ----
    public static IReadOnlyList<PhysicalDiskDto> Disks()
    {
        var disks = new List<PhysicalDiskDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
            foreach (ManagementObject d in s.Get())
            {
                var parts = new List<PartitionDto>();
                try
                {
                    foreach (ManagementObject p in d.GetRelated("Win32_DiskPartition"))
                    {
                        var vols = new List<VolumeDto>();
                        try
                        {
                            foreach (ManagementObject ld in p.GetRelated("Win32_LogicalDisk"))
                                vols.Add(new VolumeDto(
                                    Str(ld["DeviceID"]), Str(ld["VolumeName"]), Str(ld["FileSystem"]),
                                    L(ld["Size"]), L(ld["FreeSpace"])));
                        }
                        catch { }
                        parts.Add(new PartitionDto(Str(p["Name"]), L(p["Size"]), Str(p["Type"]), B(p["Bootable"]), vols));
                    }
                }
                catch { }
                disks.Add(new PhysicalDiskDto(
                    U(d["Index"]), Str(d["Model"]), Str(d["InterfaceType"]), Str(d["MediaType"]),
                    Str(d["SerialNumber"])?.Trim(), L(d["Size"]), U(d["Partitions"]), parts));
            }
        }
        catch { }
        return disks;
    }

    // ---- installed Windows updates (hotfixes / KBs) ----
    public static IReadOnlyList<HotfixDto> WindowsUpdates()
    {
        var list = new List<HotfixDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering");
            foreach (ManagementObject o in s.Get())
                list.Add(new HotfixDto(Str(o["HotFixID"]), Str(o["Description"]), Str(o["InstalledOn"]), Str(o["InstalledBy"])));
        }
        catch { }
        // Newest first when the date parses; unknown dates sort last.
        return list.OrderByDescending(h => DateTime.TryParse(h.InstalledOn, out var dt) ? dt : DateTime.MinValue).ToList();
    }

    // ---- PnP devices (Device Manager-like). Optional class filter (e.g. "Net", "Display", "Media"). ----
    public static IReadOnlyList<DeviceDto> Devices(string? classFilter = null)
    {
        var list = new List<DeviceDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPClass, Manufacturer, Status, DeviceID FROM Win32_PnPEntity");
            foreach (ManagementObject o in s.Get())
            {
                var cls = Str(o["PNPClass"]);
                if (!string.IsNullOrEmpty(classFilter) && !string.Equals(cls, classFilter, StringComparison.OrdinalIgnoreCase)
                    && !(cls?.Contains(classFilter, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                list.Add(new DeviceDto(Str(o["Name"]), cls, Str(o["Manufacturer"]), Str(o["Status"]), Str(o["DeviceID"])));
                if (list.Count >= MaxRows) break;
            }
        }
        catch { }
        return list.OrderBy(d => d.Class).ThenBy(d => d.Name).ToList();
    }

    // ---- installed drivers ----
    public static IReadOnlyList<DriverDto> Drivers()
    {
        var list = new List<DriverDto>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in s.Get())
            {
                var name = Str(o["DeviceName"]);
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new DriverDto(name, Str(o["DriverProviderName"]), Str(o["DriverVersion"]),
                    WmiDate(o["DriverDate"]), Str(o["InfName"]), B(o["IsSigned"])));
                if (list.Count >= MaxRows) break;
            }
        }
        catch { }
        return list.OrderBy(d => d.Device).ToList();
    }

    // ---- audio devices ----
    public static IReadOnlyList<AudioDeviceDto> Audio()
    {
        var list = new List<AudioDeviceDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice");
            foreach (ManagementObject o in s.Get())
                list.Add(new AudioDeviceDto(Str(o["Name"]), Str(o["Manufacturer"]), Str(o["Status"])));
        }
        catch { }
        return list;
    }

    // ---- WMI value helpers ----
    private static string? Str(object? v) => v?.ToString();
    private static long? L(object? v) => v is null ? null : (long.TryParse(v.ToString(), out var l) ? l : null);
    private static uint U(object? v) => v is null ? 0 : (uint.TryParse(v.ToString(), out var u) ? u : 0);
    private static bool B(object? v) => v is bool b ? b : (bool.TryParse(v?.ToString(), out var bb) && bb);

    // WMI dates are CIM_DATETIME (yyyymmddHHMMSS.mmmmmm±UUU); return the yyyy-MM-dd date part.
    private static string? WmiDate(object? v)
    {
        var s = v?.ToString();
        if (string.IsNullOrEmpty(s) || s.Length < 8) return s;
        try { return ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd"); }
        catch { return s.Length >= 8 ? $"{s[..4]}-{s.Substring(4, 2)}-{s.Substring(6, 2)}" : s; }
    }
}
