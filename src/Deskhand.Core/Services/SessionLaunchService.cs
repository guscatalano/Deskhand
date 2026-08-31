using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

/// <summary>The user context a launched process runs as.</summary>
public enum LaunchAs
{
    /// <summary>Run as whoever is interactively logged into the target session (WTSQueryUserToken).</summary>
    SessionUser,
    /// <summary>Run as an explicit account via LogonUser(user, domain, password).</summary>
    Credentials,
    /// <summary>Run as NT AUTHORITY\SYSTEM in the target session (borrows winlogon's token).</summary>
    System,
}

public record SessionLaunchResultDto(
    bool Ok, int ProcessId, uint SessionId, string Desktop, string As, string? User,
    string? Error = null, int Win32 = 0, string? Hint = null);

/// <summary>
/// Launch a process into a specific Terminal-Services <b>session</b> (X), on a specific window-station\<b>desktop</b>
/// (Y, e.g. <c>winsta0\default</c>), running as a specific <b>user</b> (Z). Consolidates the three axes over
/// <c>CreateProcessAsUser</c>.
///
/// <para><b>Privilege.</b> Crossing a session boundary or changing user requires <c>SeTcbPrivilege</c> —
/// in practice the host must run as <b>LocalSystem</b> (e.g. via the Deskhand Fleet Launcher service, or a
/// SYSTEM-hosted agent). The one exception is the fast path — target session == current session AND
/// <see cref="LaunchAs.SessionUser"/> — which only changes the <i>desktop</i> and needs no token or elevation,
/// so it works from an ordinary user process. Everything else returns a crisp Win32 error + hint when the
/// privilege isn't held rather than failing opaquely.</para>
///
/// <para>Most powerful capability after the shell: OFF unless <c>DESKHAND_ENABLE_SESSION_LAUNCH</c> is set;
/// the host layer additionally requires the kill switch armed and audits every launch (never the password).</para>
/// </summary>
public static class SessionLaunchService
{
    /// <summary>Opt-in: set DESKHAND_ENABLE_SESSION_LAUNCH=1 (or true/yes/on) to allow it.</summary>
    public static bool Enabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DESKHAND_ENABLE_SESSION_LAUNCH")?.Trim().ToLowerInvariant();
            return v is "1" or "true" or "yes" or "on";
        }
    }

    public const string DefaultDesktop = @"winsta0\default";

    public static SessionLaunchResultDto Launch(
        string path, string? args, string? workingDir, int? sessionId, string? desktop, LaunchAs asUser,
        string? user, string? domain, string? password, bool createNoWindow)
    {
        path = (path ?? "").Trim().Trim('"');
        desktop = string.IsNullOrWhiteSpace(desktop) ? DefaultDesktop : desktop.Trim();
        workingDir = (workingDir ?? "").Trim().Trim('"');

        if (!Enabled)
            return Err(0, desktop, asUser, user, "Session launch is disabled. Set DESKHAND_ENABLE_SESSION_LAUNCH=1.");
        if (string.IsNullOrWhiteSpace(path))
            return Err(0, desktop, asUser, user, "No program path given.");

        uint session = sessionId is int s && s >= 0 ? (uint)s : WTSGetActiveConsoleSessionId();
        if (session == INVALID_SESSION)
            return Err(0, desktop, asUser, user, "No active console session and no sessionId given.");

        // Best-effort: enable the privileges the token paths need. Silently no-ops if not held (e.g. not SYSTEM);
        // the actual API call below then reports the real ERROR_PRIVILEGE_NOT_HELD with a hint.
        foreach (var p in new[] { "SeTcbPrivilege", "SeAssignPrimaryTokenPrivilege", "SeIncreaseQuotaPrivilege", "SeDebugPrivilege" })
            TryEnablePrivilege(p);

        uint currentSession = SafeCurrentSession();
        // CreateProcess[AsUser] with lpApplicationName does NOT search PATH — it needs a full path. Resolve a
        // bare name ("cmd.exe", "notepad") against PATH/PATHEXT; if we can't, pass appName=null and let the
        // command line's first token drive the OS PATH search (in the target user's environment).
        string? appName = ResolveExe(path);
        string cmdLine = BuildCommandLine(appName ?? path, args);
        if (workingDir.Length == 0 && appName is not null) workingDir = Path.GetDirectoryName(appName) ?? "";

        // Fast path: same session, run as the session's user, only the desktop differs. No token, no elevation.
        if (asUser == LaunchAs.SessionUser && session == currentSession)
            return LaunchPlain(appName, cmdLine, workingDir, desktop, session, createNoWindow, user);

        // Token paths (cross-session and/or different user) — need SeTcbPrivilege (LocalSystem) in practice.
        IntPtr token = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            switch (asUser)
            {
                case LaunchAs.SessionUser:
                    if (!WTSQueryUserToken(session, out token))
                        return Win32Err(session, desktop, asUser, user, "WTSQueryUserToken",
                            "Requires running as LocalSystem (SeTcbPrivilege), and session must have an interactive user logged on.");
                    break;

                case LaunchAs.System:
                    token = OpenSessionSystemToken(session, out string? sysErr);
                    if (token == IntPtr.Zero)
                        return Err(session, desktop, asUser, "SYSTEM", sysErr ?? "Could not obtain a SYSTEM token in the target session.");
                    break;

                case LaunchAs.Credentials:
                    if (string.IsNullOrWhiteSpace(user))
                        return Err(session, desktop, asUser, user, "as=credentials requires a user (and password).");
                    if (!LogonUser(user, string.IsNullOrWhiteSpace(domain) ? "." : domain, password ?? "",
                                   LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out token))
                        return Win32Err(session, desktop, asUser, user, "LogonUser",
                            "Check the username/domain/password. Batch/interactive logon rights may be required.");
                    break;
            }

            if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                return Win32Err(session, desktop, asUser, user, "DuplicateTokenEx", null);

            // Place the token in the requested session when it differs (needs SeTcbPrivilege). WTSQueryUserToken
            // already returns a token in `session`; LogonUser/SYSTEM tokens may not, so retarget them.
            if (asUser != LaunchAs.SessionUser)
            {
                uint sid = session;
                if (!SetTokenInformation(primary, TokenSessionId, ref sid, sizeof(uint)))
                    return Win32Err(session, desktop, asUser, user, "SetTokenInformation(session)",
                        "Requires running as LocalSystem (SeTcbPrivilege) to move a token into another session.");
            }

            CreateEnvironmentBlock(out env, primary, false);
            uint flags = CREATE_UNICODE_ENVIRONMENT | (createNoWindow ? CREATE_NO_WINDOW : 0);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };

            bool ok = CreateProcessAsUser(primary, appName, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                flags, env, NullIfEmpty(workingDir), ref si, out var pi);
            if (!ok)
                return Win32Err(session, desktop, asUser, DisplayUser(asUser, user), "CreateProcessAsUser",
                    "Verify the path exists for that user, the desktop name is valid, and the session is active.");

            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return new SessionLaunchResultDto(true, pi.dwProcessId, session, desktop, asUser.ToString(),
                DisplayUser(asUser, user));
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    // Same-session, same-user: a plain CreateProcess with STARTUPINFO.lpDesktop redirects the new process to
    // another desktop in the caller's window station. No privileged token needed.
    private static SessionLaunchResultDto LaunchPlain(
        string? appName, string cmdLine, string workingDir, string desktop, uint session, bool createNoWindow, string? user)
    {
        uint flags = CREATE_UNICODE_ENVIRONMENT | (createNoWindow ? CREATE_NO_WINDOW : 0);
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
        bool ok = CreateProcess(appName, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
            NullIfEmpty(workingDir), ref si, out var pi);
        if (!ok)
            return Win32Err(session, desktop, LaunchAs.SessionUser, user, "CreateProcess",
                "Check the path exists and the desktop name is valid (create it first if it doesn't exist).");
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return new SessionLaunchResultDto(true, pi.dwProcessId, session, desktop, LaunchAs.SessionUser.ToString(),
            user ?? SafeCurrentUser());
    }

    // Borrow winlogon.exe's SYSTEM token from the target session (winlogon runs as SYSTEM in every interactive
    // session). Needs SeDebugPrivilege to open it — i.e. an elevated admin or SYSTEM host.
    private static IntPtr OpenSessionSystemToken(uint session, out string? error)
    {
        error = null;
        var winlogon = Process.GetProcessesByName("winlogon")
            .FirstOrDefault(p => { try { return (uint)p.SessionId == session; } catch { return false; } });
        if (winlogon is null) { error = $"winlogon.exe not found in session {session} (is a user logged on there?)."; return IntPtr.Zero; }
        try
        {
            if (!OpenProcessToken(winlogon.Handle, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out IntPtr tok))
            {
                error = $"OpenProcessToken(winlogon) failed (Win32 {Marshal.GetLastWin32Error()}); requires elevation + SeDebugPrivilege.";
                return IntPtr.Zero;
            }
            return tok;
        }
        catch (Exception ex) { error = "Could not access winlogon: " + ex.Message; return IntPtr.Zero; }
    }

    // ---- helpers ----

    // Resolve a program to a full path so lpApplicationName works (CreateProcess doesn't PATH-search it).
    // Returns null if we can't find it — caller then passes appName=null and lets the OS search via cmdLine.
    private static string? ResolveExe(string path)
    {
        try
        {
            if (Path.IsPathRooted(path)) return File.Exists(path) ? Path.GetFullPath(path) : null;
            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            bool hasExt = Path.HasExtension(path);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var d = dir.Trim().Trim('"');
                if (d.Length == 0) continue;
                if (hasExt) { var c = Path.Combine(d, path); if (File.Exists(c)) return c; }
                else foreach (var e in exts) { var c = Path.Combine(d, path + e); if (File.Exists(c)) return c; }
            }
        }
        catch { }
        return null;
    }

    private static string BuildCommandLine(string path, string? args) =>
        string.IsNullOrWhiteSpace(args) ? Quote(path) : $"{Quote(path)} {args}";
    private static string Quote(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static uint SafeCurrentSession() { try { return (uint)Process.GetCurrentProcess().SessionId; } catch { return INVALID_SESSION; } }
    private static string SafeCurrentUser() { try { return Environment.UserName; } catch { return "(current)"; } }
    private static string? DisplayUser(LaunchAs a, string? user) => a switch
    {
        LaunchAs.System => "NT AUTHORITY\\SYSTEM",
        LaunchAs.Credentials => user,
        _ => user,
    };

    private static SessionLaunchResultDto Err(uint session, string desktop, LaunchAs a, string? user, string error) =>
        new(false, 0, session, desktop, a.ToString(), user, error);
    private static SessionLaunchResultDto Win32Err(uint session, string desktop, LaunchAs a, string? user, string api, string? hint)
    {
        int e = Marshal.GetLastWin32Error();
        return new(false, 0, session, desktop, a.ToString(), user,
            $"{api} failed (Win32 {e}: {Win32Name(e)}).", e, hint);
    }
    private static string Win32Name(int e) => e switch
    {
        1314 => "ERROR_PRIVILEGE_NOT_HELD",
        1326 => "ERROR_LOGON_FAILURE (bad user/password)",
        2 => "ERROR_FILE_NOT_FOUND",
        5 => "ERROR_ACCESS_DENIED",
        1385 => "ERROR_LOGON_TYPE_NOT_GRANTED",
        _ => "see Win32 error",
    };

    private static void TryEnablePrivilege(string name)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok)) return;
            try
            {
                if (!LookupPrivilegeValue(null, name, out LUID luid)) return;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }
        catch { }
    }

    // ---- interop ----
    private const uint INVALID_SESSION = 0xFFFFFFFF;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12; // TOKEN_INFORMATION_CLASS
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, TOKEN_ASSIGN_PRIMARY = 0x0001,
                       TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int LOGON32_LOGON_INTERACTIVE = 2, LOGON32_PROVIDER_DEFAULT = 0;

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int tokenInfoClass, ref uint info, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, string cmdLine, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string? curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? appName, string cmdLine, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string? curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    /// <summary>Parse the <c>as</c> string from a request into <see cref="LaunchAs"/> (default session user).</summary>
    public static LaunchAs ParseAs(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "system" or "localsystem" => LaunchAs.System,
        "credentials" or "creds" or "user" => LaunchAs.Credentials,
        _ => LaunchAs.SessionUser,
    };
}
