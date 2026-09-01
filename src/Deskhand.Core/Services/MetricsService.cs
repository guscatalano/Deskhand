using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Deskhand.Core.Services;

/// <summary>A tiny Prometheus text-format exposition for /metrics — cheap enough to scrape frequently (process
/// stats + machine memory via GlobalMemoryStatusEx; no WMI). Lets a fleet be monitored with standard tooling.</summary>
public static class MetricsService
{
    private static readonly DateTime Start = Process.GetCurrentProcess().StartTime;

    public static string Render(bool armed, bool captureEnabled, string version, int? fleetAgents = null)
    {
        var p = Process.GetCurrentProcess();
        var sb = new StringBuilder();
        void G(string name, string help, double value, string? labels = null)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
            sb.Append(name);
            if (labels is not null) sb.Append('{').Append(labels).Append('}');
            sb.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        G("deskhand_up", "1 if the server is responding.", 1, $"version=\"{version}\"");
        G("deskhand_uptime_seconds", "Seconds since the server process started.", (DateTime.Now - Start).TotalSeconds);
        G("deskhand_armed", "1 if the kill switch is armed (input/capture allowed).", armed ? 1 : 0);
        G("deskhand_capture_enabled", "1 if capture is enabled.", captureEnabled ? 1 : 0);
        G("deskhand_process_working_set_bytes", "Working set of the server process.", p.WorkingSet64);
        G("deskhand_process_threads", "Thread count of the server process.", SafeThreads(p));
        G("deskhand_gc_heap_bytes", "Managed heap in use.", GC.GetTotalMemory(false));

        if (TryMemStatus(out var m))
        {
            G("deskhand_memory_total_bytes", "Total physical memory.", m.ullTotalPhys);
            G("deskhand_memory_available_bytes", "Available physical memory.", m.ullAvailPhys);
            G("deskhand_memory_load_percent", "Percent physical memory in use.", m.dwMemoryLoad);
        }
        if (fleetAgents is int n)
            G("deskhand_fleet_agents", "Connected fleet agents.", n);

        return sb.ToString();
    }

    private static double SafeThreads(Process p) { try { return p.Threads.Count; } catch { return 0; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    private static bool TryMemStatus(out MEMORYSTATUSEX m)
    { m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() }; return GlobalMemoryStatusEx(ref m); }
}
