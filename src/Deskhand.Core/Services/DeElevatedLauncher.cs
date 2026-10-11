using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

/// <summary>
/// Launch a process as the LOGGED-ON USER at normal (medium) integrity, from an elevated Deskhand. Because
/// Deskhand itself runs elevated, a plain launch inherits elevation ("Administrator: …") — which shows in
/// demos and makes some per-user installers behave differently. This borrows the shell's (explorer.exe)
/// primary token and starts the process with it via CreateProcessWithTokenW (needs SeImpersonatePrivilege,
/// which an admin has), so the child runs de-elevated in the user's session.
/// </summary>
public static class DeElevatedLauncher
{
    public static (bool ok, int pid, string? error) Launch(string path, string? args, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, 0, "No path given.");
        IntPtr shellHwnd = GetShellWindow();
        if (shellHwnd == IntPtr.Zero) return (false, 0, "No shell (explorer) window — cannot borrow the user's token (is anyone signed in?).");
        GetWindowThreadProcessId(shellHwnd, out uint shellPid);
        if (shellPid == 0) return (false, 0, "Could not find the shell process id.");

        IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, shellPid);
        if (hProc == IntPtr.Zero) return (false, 0, $"OpenProcess(explorer) failed (Win32 {Marshal.GetLastWin32Error()}).");
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID, out IntPtr hTok))
                return (false, 0, $"OpenProcessToken(explorer) failed (Win32 {Marshal.GetLastWin32Error()}).");
            try
            {
                if (!DuplicateTokenEx(hTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr hDup))
                    return (false, 0, $"DuplicateTokenEx failed (Win32 {Marshal.GetLastWin32Error()}).");
                try
                {
                    CreateEnvironmentBlock(out IntPtr env, hDup, false);
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                    string cmd = args is { Length: > 0 } ? $"\"{path}\" {args}" : $"\"{path}\"";
                    string? dir = string.IsNullOrWhiteSpace(workingDir) ? System.IO.Path.GetDirectoryName(path) : workingDir;
                    bool ok = CreateProcessWithTokenW(hDup, 0, path, cmd,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE, env, dir, ref si, out var pi);
                    int err = Marshal.GetLastWin32Error();
                    if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                    if (!ok) return (false, 0, $"CreateProcessWithTokenW failed (Win32 {err})"
                        + (err == 1314 ? " — de-elevated launch needs Deskhand running elevated (SeImpersonatePrivilege)." : "."));
                    int pid = pi.dwProcessId;
                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    return (true, pid, null);
                }
                finally { CloseHandle(hDup); }
            }
            finally { CloseHandle(hTok); }
        }
        catch (Exception ex) { return (false, 0, ex.Message); }
        finally { CloseHandle(hProc); }
    }

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, TOKEN_ASSIGN_PRIMARY = 0x0001,
        TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_ADJUST_SESSIONID = 0x0100, MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400, CREATE_NEW_CONSOLE = 0x00000010;

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? appName, string cmdLine, uint creationFlags, IntPtr env, string? curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
}
