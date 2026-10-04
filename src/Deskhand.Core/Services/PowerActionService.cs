using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record PowerActionResultDto(bool Ok, string Action, string? Error = null);

/// <summary>
/// Whole-machine power actions: shut down, restart, sign out, lock, sleep, hibernate. These are disruptive and
/// irreversible-in-progress, so the host layer gates them on the kill switch (armed) AND an explicit
/// confirm — a power action is never taken by accident. Shutdown/restart/sign-out go through ExitWindowsEx
/// (after enabling SeShutdownPrivilege); sleep/hibernate through SetSuspendState; lock through LockWorkStation.
/// </summary>
public static class PowerActionService
{
    public static PowerActionResultDto Lock() => Do("lock", () => LockWorkStation());
    public static PowerActionResultDto Sleep(bool force = false) => Do("sleep", () => SetSuspendState(false, force, false));
    public static PowerActionResultDto Hibernate(bool force = false) => Do("hibernate", () => SetSuspendState(true, force, false));
    public static PowerActionResultDto Shutdown(bool force = false) => ExitWindows("shutdown", EWX_SHUTDOWN | (force ? EWX_FORCE : 0));
    public static PowerActionResultDto Restart(bool force = false) => ExitWindows("restart", EWX_REBOOT | (force ? EWX_FORCE : 0));
    public static PowerActionResultDto SignOut(bool force = false) => ExitWindows("signout", EWX_LOGOFF | (force ? EWX_FORCE : 0));

    private static PowerActionResultDto Do(string action, Func<bool> op)
    {
        try { return op() ? new PowerActionResultDto(true, action) : new PowerActionResultDto(false, action, LastErr()); }
        catch (Exception ex) { return new PowerActionResultDto(false, action, ex.Message); }
    }

    private static PowerActionResultDto ExitWindows(string action, uint flags)
    {
        try
        {
            if (!EnableShutdownPrivilege()) return new PowerActionResultDto(false, action, "Could not acquire SeShutdownPrivilege.");
            return ExitWindowsEx(flags, SHTDN_REASON) ? new PowerActionResultDto(true, action) : new PowerActionResultDto(false, action, LastErr());
        }
        catch (Exception ex) { return new PowerActionResultDto(false, action, ex.Message); }
    }

    private static string LastErr() => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out LUID luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(tok); }
    }

    // ---- interop ----
    private const uint EWX_LOGOFF = 0x00, EWX_SHUTDOWN = 0x01, EWX_REBOOT = 0x02, EWX_FORCE = 0x04;
    private const uint SHTDN_REASON = 0x80000000;   // SHTDN_REASON_FLAG_PLANNED | MAJOR_OTHER(0) | MINOR_OTHER(0)
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int bufferLen, IntPtr prev, IntPtr prevLen);

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }
}
