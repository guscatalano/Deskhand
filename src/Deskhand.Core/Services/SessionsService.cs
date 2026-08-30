using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record SessionDto(int SessionId, string? Station, string State, string? User, string? Domain, string? ClientName, bool IsCurrent);

/// <summary>
/// Read-only enumeration of the machine's logon sessions via the Windows Terminal Services (WTS) APIs:
/// the console session, any RDP sessions, and the service/listener sessions — with each session's id,
/// window-station name, connect state (Active / Disconnected / Listen / …), the logged-on user/domain, and
/// the RDP client machine name (empty for local). Nothing is changed.
/// </summary>
public static class SessionsService
{
    public static IReadOnlyList<SessionDto> List()
    {
        var result = new List<SessionDto>();
        int current = -1;
        try { current = System.Diagnostics.Process.GetCurrentProcess().SessionId; } catch { }

        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr pInfo, out int count) || pInfo == IntPtr.Zero)
            return result;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            long p = pInfo.ToInt64();
            for (int i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<WTS_SESSION_INFO>((IntPtr)p);
                p += size;
                var user = QueryStr(si.SessionId, WTS_INFO_CLASS.WTSUserName);
                var domain = QueryStr(si.SessionId, WTS_INFO_CLASS.WTSDomainName);
                var client = QueryStr(si.SessionId, WTS_INFO_CLASS.WTSClientName);
                result.Add(new SessionDto(
                    si.SessionId,
                    string.IsNullOrEmpty(si.pWinStationName) ? null : si.pWinStationName,
                    si.State.ToString(),
                    string.IsNullOrEmpty(user) ? null : user,
                    string.IsNullOrEmpty(domain) ? null : domain,
                    string.IsNullOrEmpty(client) ? null : client,
                    si.SessionId == current));
            }
        }
        finally { WTSFreeMemory(pInfo); }
        return result.OrderBy(s => s.SessionId).ToList();
    }

    private static string? QueryStr(int sessionId, WTS_INFO_CLASS cls)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, cls, out IntPtr buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    private enum WTS_CONNECTSTATE_CLASS
    { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }

    private enum WTS_INFO_CLASS { WTSUserName = 5, WTSDomainName = 7, WTSClientName = 10 }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
