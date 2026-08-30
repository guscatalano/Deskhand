using System.Net;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record ConnectionDto(string Protocol, string LocalAddress, string? RemoteAddress, string State, int Pid, string? Process);

/// <summary>Read-only active network connections and listening ports (IPv4), netstat-style, with the owning
/// process id + name (via GetExtendedTcpTable / GetExtendedUdpTable). Nothing is changed.</summary>
public static class NetConnectionsService
{
    private const int AF_INET = 2;

    public static IReadOnlyList<ConnectionDto> List()
    {
        var list = new List<ConnectionDto>();
        var names = new Dictionary<int, string?>();
        string? ProcName(int pid)
        {
            if (names.TryGetValue(pid, out var n)) return n;
            string? name = null;
            try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { }
            names[pid] = name; return name;
        }

        Tcp(list, ProcName);
        Udp(list, ProcName);
        return list;
    }

    private static void Tcp(List<ConnectionDto> list, Func<int, string?> proc)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;
            int count = Marshal.ReadInt32(buf);
            long p = buf.ToInt64() + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>((IntPtr)p);
                p += rowSize;
                var state = TcpState(r.state);
                list.Add(new ConnectionDto("TCP",
                    $"{new IPAddress(r.localAddr)}:{Port(r.localPort)}",
                    r.state == 2 /*LISTEN*/ ? null : $"{new IPAddress(r.remoteAddr)}:{Port(r.remotePort)}",
                    state, r.owningPid, proc(r.owningPid)));
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void Udp(List<ConnectionDto> list, Func<int, string?> proc)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0) return;
            int count = Marshal.ReadInt32(buf);
            long p = buf.ToInt64() + 4;
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>((IntPtr)p);
                p += rowSize;
                list.Add(new ConnectionDto("UDP", $"{new IPAddress(r.localAddr)}:{Port(r.localPort)}", null, "Listen", r.owningPid, proc(r.owningPid)));
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static int Port(uint p) => ((int)(p & 0xFF) << 8) | (int)((p >> 8) & 0xFF);   // network byte order

    private static string TcpState(uint s) => s switch
    { 1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established", 6 => "FinWait1",
      7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck", 11 => "TimeWait", 12 => "DeleteTcb", _ => s.ToString() };

    private const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID { public uint state; public uint localAddr; public uint localPort; public uint remoteAddr; public uint remotePort; public int owningPid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID { public uint localAddr; public uint localPort; public int owningPid; }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);
}
