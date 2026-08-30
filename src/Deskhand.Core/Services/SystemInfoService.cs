using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Deskhand.Core.Services;

public record OsInfoDto(string Name, string Edition, string DisplayVersion, string Build, string BuildLab, string Version, string Architecture, bool Is64Bit);
public record TimeInfoDto(DateTime BootTime, long UptimeSeconds, string Uptime, DateTime Now);
public record CpuInfoDto(string Name, int LogicalProcessors, string Architecture, double? LoadPercent);
public record MemoryInfoDto(long TotalPhysical, long AvailablePhysical, long UsedPhysical, int LoadPercent, long TotalPageFile, long AvailablePageFile);
public record DiskInfoDto(string Name, string? Label, string Format, string DriveType, long TotalSize, long FreeSpace, long UsedSize);
public record NetInfoDto(string Name, string Description, string Type, string Status, string? Mac, long SpeedBps,
    IReadOnlyList<string> IPv4, IReadOnlyList<string> IPv6, IReadOnlyList<string> Gateways, IReadOnlyList<string> DnsServers);
public record FirewallInfoDto(bool? DomainEnabled, bool? PrivateEnabled, bool? PublicEnabled, string? Note = null);
public record SystemInfoDto(OsInfoDto Os, string MachineName, string UserName, string? Domain,
    TimeInfoDto Time, CpuInfoDto Cpu, MemoryInfoDto Memory,
    IReadOnlyList<DiskInfoDto> Disks, IReadOnlyList<NetInfoDto> Network, FirewallInfoDto Firewall);

/// <summary>
/// Read-only "about this machine" snapshot: Windows version (incl. BuildLab), uptime, CPU, memory, disks,
/// network interfaces, and Windows Firewall profile state. All from public OS APIs / HKLM — nothing is
/// changed. Sampling the live CPU load adds ~250 ms to the call.
/// </summary>
public static class SystemInfoService
{
    public static SystemInfoDto Get() => new(
        Os(), Environment.MachineName, Environment.UserName, DomainName(),
        Time(), Cpu(), Memory(), Disks(), Network(), Firewall());

    // ---- OS / Windows version ----
    private static OsInfoDto Os()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string S(string n) => k?.GetValue(n)?.ToString() ?? "";
        string product = S("ProductName");
        string currentBuild = S("CurrentBuildNumber");
        int ubr = (k?.GetValue("UBR") as int?) ?? 0;
        int.TryParse(currentBuild, out int buildNum);
        // The registry ProductName still says "Windows 10" on 11; correct it by build number (11 = 22000+).
        if (buildNum >= 22000 && product.Contains("Windows 10")) product = product.Replace("Windows 10", "Windows 11");
        string build = ubr > 0 ? $"{currentBuild}.{ubr}" : currentBuild;
        return new OsInfoDto(
            Name: product,
            Edition: S("EditionID"),
            DisplayVersion: S("DisplayVersion") is { Length: > 0 } dv ? dv : S("ReleaseId"),
            Build: build,
            BuildLab: S("BuildLabEx") is { Length: > 0 } bl ? bl : S("BuildLab"),
            Version: Environment.OSVersion.Version.ToString(),
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            Is64Bit: Environment.Is64BitOperatingSystem);
    }

    private static string? DomainName()
    {
        try { var d = IPGlobalProperties.GetIPGlobalProperties().DomainName; return string.IsNullOrWhiteSpace(d) ? null : d; }
        catch { return null; }
    }

    // ---- uptime ----
    private static TimeInfoDto Time()
    {
        long ms = Environment.TickCount64;
        var up = TimeSpan.FromMilliseconds(ms);
        var boot = DateTime.Now - up;
        return new TimeInfoDto(boot, (long)up.TotalSeconds, FormatUptime(up), DateTime.Now);
    }

    private static string FormatUptime(TimeSpan t) =>
        (t.Days > 0 ? $"{t.Days}d " : "") + $"{t.Hours}h {t.Minutes}m";

    // ---- CPU ----
    private static CpuInfoDto Cpu()
    {
        string name = "";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (k?.GetValue("ProcessorNameString")?.ToString() ?? "").Trim();
        }
        catch { }
        return new CpuInfoDto(name, Environment.ProcessorCount, RuntimeInformation.ProcessArchitecture.ToString(), LoadPercent());
    }

    // Sample system times over a short window to estimate current busy %.
    private static double? LoadPercent()
    {
        try
        {
            if (!Native.GetSystemTimes(out long i0, out long k0, out long u0)) return null;
            Thread.Sleep(250);
            if (!Native.GetSystemTimes(out long i1, out long k1, out long u1)) return null;
            long idle = i1 - i0, kernel = k1 - k0, user = u1 - u0;
            long total = kernel + user;               // kernel time already includes idle
            if (total <= 0) return null;
            double busy = (total - idle) * 100.0 / total;
            return Math.Round(Math.Clamp(busy, 0, 100), 1);
        }
        catch { return null; }
    }

    // ---- memory ----
    private static MemoryInfoDto Memory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return new MemoryInfoDto(0, 0, 0, 0, 0, 0);
        long total = (long)m.ullTotalPhys, avail = (long)m.ullAvailPhys;
        return new MemoryInfoDto(total, avail, total - avail, (int)m.dwMemoryLoad,
            (long)m.ullTotalPageFile, (long)m.ullAvailPageFile);
    }

    // ---- disks ----
    private static IReadOnlyList<DiskInfoDto> Disks()
    {
        var list = new List<DiskInfoDto>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { return list; }
        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady) { list.Add(new DiskInfoDto(d.Name, null, "", d.DriveType.ToString(), 0, 0, 0)); continue; }
                long total = d.TotalSize, free = d.TotalFreeSpace;
                list.Add(new DiskInfoDto(d.Name, string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                    d.DriveFormat, d.DriveType.ToString(), total, free, total - free));
            }
            catch { /* skip a drive we can't read */ }
        }
        return list;
    }

    // ---- network ----
    private static IReadOnlyList<NetInfoDto> Network()
    {
        var list = new List<NetInfoDto>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }
        foreach (var ni in nics)
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var p = ni.GetIPProperties();
                var v4 = new List<string>(); var v6 = new List<string>();
                foreach (var ua in p.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) v4.Add(ua.Address.ToString());
                    else if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) v6.Add(ua.Address.ToString());
                }
                if (v4.Count == 0 && v6.Count == 0) continue;   // skip interfaces with no address
                var mac = ni.GetPhysicalAddress()?.ToString();
                mac = string.IsNullOrEmpty(mac) ? null : string.Join(":", Enumerable.Range(0, mac.Length / 2).Select(i => mac.Substring(i * 2, 2)));
                list.Add(new NetInfoDto(ni.Name, ni.Description, ni.NetworkInterfaceType.ToString(), ni.OperationalStatus.ToString(),
                    mac, ni.Speed,
                    v4, v6,
                    p.GatewayAddresses.Select(g => g.Address.ToString()).ToList(),
                    p.DnsAddresses.Select(a => a.ToString()).ToList()));
            }
            catch { }
        }
        return list;
    }

    // ---- Windows Firewall (per-profile enabled state, read from HKLM) ----
    private static FirewallInfoDto Firewall()
    {
        bool? Read(string profile)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}");
                return k?.GetValue("EnableFirewall") is int v ? v != 0 : (bool?)null;
            }
            catch { return null; }
        }
        var dom = Read("DomainProfile");
        var priv = Read("StandardProfile");   // "Standard" = the Private profile
        var pub = Read("PublicProfile");
        string? note = (dom is null && priv is null && pub is null)
            ? "Firewall state unreadable (policy may be managed elsewhere)." : null;
        return new FirewallInfoDto(dom, priv, pub, note);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);
    }
}
