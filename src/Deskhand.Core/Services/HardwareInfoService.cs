using System.Management;

namespace Deskhand.Core.Services;

public record VolumeDto(string? DriveLetter, string? Label, string? FileSystem, long? SizeBytes, long? FreeBytes);
public record PartitionDto(string? Name, long? SizeBytes, string? Type, bool Bootable, IReadOnlyList<VolumeDto> Volumes);
public record PhysicalDiskDto(uint Index, string? Model, string? InterfaceType, string? MediaType, string? Serial, long? SizeBytes, uint Partitions, IReadOnlyList<PartitionDto> PartitionList);
public record HotfixDto(string? HotFixId, string? Description, string? InstalledOn, string? InstalledBy);
public record DeviceDto(string? Name, string? Class, string? Manufacturer, string? Status, string? DeviceId);
public record DriverDto(string? Device, string? Provider, string? Version, string? Date, string? InfName, bool? Signed);
public record AudioDeviceDto(string? Name, string? Manufacturer, string? Status);
public record GpuDto(string? Name, string? DriverVersion, string? DriverDate, long? VramBytes, long? SharedMemoryBytes, string? VideoProcessor, string? Resolution, uint? RefreshHz);
public record MonitorDetailDto(string? Manufacturer, string? Model, string? Serial, int? Year);
public record BiosDto(string? Manufacturer, string? Version, string? ReleaseDate, string? SerialNumber, string? SmbiosVersion);
public record BaseboardDto(string? Manufacturer, string? Product, string? Version, string? SerialNumber);
public record MemoryModuleDto(string? Slot, string? BankLabel, long? CapacityBytes, uint? SpeedMhz, string? Manufacturer, string? PartNumber, string? FormFactor, string? MemoryType);
public record HardwareDetailDto(string? ComputerManufacturer, string? ComputerModel, BiosDto? Bios, BaseboardDto? Motherboard,
    IReadOnlyList<GpuDto> Gpus, IReadOnlyList<MonitorDetailDto> Monitors, IReadOnlyList<MemoryModuleDto> Memory);

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

    // ---- detailed inventory: computer, BIOS, motherboard, GPUs, monitors, RAM sticks ----
    public static HardwareDetailDto Detail()
    {
        string? cMfr = null, cModel = null;
        First("SELECT Manufacturer, Model FROM Win32_ComputerSystem", o => { cMfr = Str(o["Manufacturer"]); cModel = Str(o["Model"]); });

        BiosDto? bios = null;
        First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS",
            o => bios = new BiosDto(Str(o["Manufacturer"]), Str(o["SMBIOSBIOSVersion"]), WmiDate(o["ReleaseDate"]),
                Str(o["SerialNumber"]), $"{Str(o["SMBIOSMajorVersion"])}.{Str(o["SMBIOSMinorVersion"])}"));

        BaseboardDto? board = null;
        First("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard",
            o => board = new BaseboardDto(Str(o["Manufacturer"]), Str(o["Product"]), Str(o["Version"]), Str(o["SerialNumber"])));

        var gpus = new List<GpuDto>();
        Each("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController",
            o => gpus.Add(new GpuDto(Str(o["Name"]), Str(o["DriverVersion"]), WmiDate(o["DriverDate"]),
                L(o["AdapterRAM"]), null, Str(o["VideoProcessor"]),
                (L(o["CurrentHorizontalResolution"]) is long w && L(o["CurrentVerticalResolution"]) is long h && w > 0) ? $"{w}×{h}" : null,
                U(o["CurrentRefreshRate"]) is uint r && r > 0 ? r : null)));
        // WMI's AdapterRAM is a 32-bit field (caps at ~4 GB). Get the TRUE dedicated VRAM + shared memory
        // from DXGI (IDXGIAdapter1.Description1) and override by matching the adapter name.
        var dxgi = DxgiAdapters();
        if (dxgi.Count > 0)
            for (int i = 0; i < gpus.Count; i++)
            {
                var g = gpus[i];
                var a = dxgi.FirstOrDefault(x => NameMatch(x.Name, g.Name));
                if (a.Name is not null && (a.Dedicated > 0 || a.Shared > 0))
                    gpus[i] = g with { VramBytes = a.Dedicated > 0 ? a.Dedicated : g.VramBytes, SharedMemoryBytes = a.Shared };
            }

        var monitors = new List<MonitorDetailDto>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture FROM WmiMonitorID");
            foreach (ManagementObject o in s.Get())
                monitors.Add(new MonitorDetailDto(U16(o["ManufacturerName"]), U16(o["UserFriendlyName"]), U16(o["SerialNumberID"]),
                    (int?)(U(o["YearOfManufacture"]) is uint y && y > 0 ? y : (uint?)null)));
        }
        catch { }

        var mem = new List<MemoryModuleDto>();
        Each("SELECT DeviceLocator, BankLabel, Capacity, Speed, Manufacturer, PartNumber, FormFactor, SMBIOSMemoryType FROM Win32_PhysicalMemory",
            o => mem.Add(new MemoryModuleDto(Str(o["DeviceLocator"]), Str(o["BankLabel"]), L(o["Capacity"]),
                U(o["Speed"]) is uint sp && sp > 0 ? sp : null, Str(o["Manufacturer"])?.Trim(), Str(o["PartNumber"])?.Trim(),
                FormFactor(U(o["FormFactor"])), MemType(U(o["SMBIOSMemoryType"])))));

        return new HardwareDetailDto(cMfr, cModel, bios, board, gpus, monitors, mem);
    }

    // True dedicated VRAM + shared system memory per adapter, via DXGI (uncapped, unlike WMI's AdapterRAM).
    private static List<(string Name, long Dedicated, long Shared)> DxgiAdapters()
    {
        var list = new List<(string, long, long)>();
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out Vortice.DXGI.IDXGIAdapter1 adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    list.Add((d.Description ?? "", (long)(ulong)d.DedicatedVideoMemory, (long)(ulong)d.SharedSystemMemory));
                }
            }
        }
        catch { }
        return list;
    }

    private static bool NameMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FormFactor(uint f) => f switch { 8 => "DIMM", 12 => "SODIMM", 0 => null, _ => $"FF{f}" };
    private static string? MemType(uint t) => t switch
    { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 0 => null, _ => $"type{t}" };

    // WmiMonitorID string fields are UInt16[] (char codes, null-terminated).
    private static string? U16(object? v)
    {
        if (v is not ushort[] arr) return null;
        var chars = arr.TakeWhile(c => c != 0).Select(c => (char)c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static void First(string query, Action<ManagementObject> use)
    {
        try { using var s = new ManagementObjectSearcher(query); foreach (ManagementObject o in s.Get()) { use(o); break; } }
        catch { }
    }
    private static void Each(string query, Action<ManagementObject> use)
    {
        try { using var s = new ManagementObjectSearcher(query); foreach (ManagementObject o in s.Get()) use(o); }
        catch { }
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
