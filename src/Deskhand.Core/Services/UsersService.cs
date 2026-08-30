using System.Management;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record LocalUserDto(string? Name, string? FullName, bool Disabled, bool Lockout, bool PasswordExpires, string? Sid);
public record LocalGroupDto(string? Name, string? Description, IReadOnlyList<string> Members);

/// <summary>Read-only local users and groups (with membership). Domain accounts are not enumerated.</summary>
public static class UsersService
{
    public static IReadOnlyList<LocalUserDto> Users()
    {
        var list = new List<LocalUserDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, FullName, Disabled, Lockout, PasswordExpires, SID FROM Win32_UserAccount WHERE LocalAccount=TRUE");
            foreach (ManagementObject o in s.Get())
                list.Add(new LocalUserDto(o["Name"]?.ToString(), o["FullName"]?.ToString(),
                    o["Disabled"] is bool d && d, o["Lockout"] is bool l && l, o["PasswordExpires"] is bool pe && pe, o["SID"]?.ToString()));
        }
        catch { }
        return list.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<LocalGroupDto> Groups()
    {
        var list = new List<LocalGroupDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, Description FROM Win32_Group WHERE LocalAccount=TRUE");
            foreach (ManagementObject o in s.Get())
            {
                var name = o["Name"]?.ToString();
                list.Add(new LocalGroupDto(name, o["Description"]?.ToString(), name is null ? Array.Empty<string>() : Members(name)));
            }
        }
        catch { }
        return list.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Local group members via netapi32 (fast + won't hang the way the WMI Win32_GroupUser associator can).
    private static IReadOnlyList<string> Members(string group)
    {
        var members = new List<string>();
        IntPtr buf = IntPtr.Zero, resume = IntPtr.Zero;
        try
        {
            int rc = NetLocalGroupGetMembers(null, group, 1, out buf, -1, out int read, out _, ref resume);
            if (rc == 0 && buf != IntPtr.Zero)
            {
                int size = Marshal.SizeOf<LOCALGROUP_MEMBERS_INFO_1>();
                long p = buf.ToInt64();
                for (int i = 0; i < read; i++)
                {
                    var m = Marshal.PtrToStructure<LOCALGROUP_MEMBERS_INFO_1>((IntPtr)p);
                    p += size;
                    if (!string.IsNullOrEmpty(m.lgrmi1_name)) members.Add(m.lgrmi1_name);
                }
            }
        }
        catch { }
        finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
        return members;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOCALGROUP_MEMBERS_INFO_1
    {
        public IntPtr lgrmi1_sid;
        public int lgrmi1_sidusage;
        [MarshalAs(UnmanagedType.LPWStr)] public string lgrmi1_name;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string groupName, int level, out IntPtr buf, int prefMaxLen, out int entriesRead, out int totalEntries, ref IntPtr resume);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);
}
