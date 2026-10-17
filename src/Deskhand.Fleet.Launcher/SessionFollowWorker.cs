using System.Diagnostics;
using System.Runtime.InteropServices;
using static Deskhand.Fleet.Launcher.Interop;

namespace Deskhand.Fleet.Launcher;

/// <summary>
/// Keeps ONE instance of a target exe (normally deskhand-http.exe) running in whichever session currently owns the
/// physical console — so Deskhand <b>follows a fast user switch</b>. A LocalSystem service in session 0 has no
/// desktop, so it spawns the exe into winsta0\default of the console session. Two modes:
/// <list type="bullet">
/// <item>a user is logged on → launch AS THAT USER, <b>elevated</b> (the full linked token);</item>
/// <item>no user (logon / switch-user screen) → launch AS <b>SYSTEM</b> in the console session, so the secure /
/// lock / logon desktop stays <b>capturable</b> (OpenInputDesktop on the Winlogon desktop needs SYSTEM).</item>
/// </list>
/// When the console session or the required mode changes, the old instance is stopped (freeing the port) and a
/// new one launched. Selected by setting DESKHAND_LAUNCH_EXE; otherwise the fleet-agent worker runs.
/// </summary>
public sealed class SessionFollowWorker(ILogger<SessionFollowWorker> log) : BackgroundService
{
    private enum Mode { None, User, System }

    private readonly string _exe = Environment.GetEnvironmentVariable("DESKHAND_LAUNCH_EXE")
        ?? Path.Combine(AppContext.BaseDirectory, "deskhand-http.exe");
    private readonly string _args = Environment.GetEnvironmentVariable("DESKHAND_LAUNCH_ARGS") ?? "--hidden";

    private uint _session = INVALID_SESSION;
    private Mode _mode = Mode.None;
    private Process? _proc;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Enable SeTcbPrivilege once so we can set TokenUIAccess on a child's token (born uiAccess). LocalSystem
        // holds SeTcb but services get it DISABLED by default; without this the UIAccess set silently fails and
        // SendInput on the secure desktop stays ACCESS_DENIED (UIAccess is latched by win32k at process creation,
        // so a runtime self-grant in the child is ineffective for injection).
        EnableSeTcb();
        log.LogInformation("Deskhand session-follow launcher started; exe={exe} args={args}", _exe, _args);
        while (!ct.IsCancellationRequested)
        {
            try { Follow(); }
            catch (Exception ex) { log.LogError(ex, "follow tick failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        StopCurrent("service stopping");
    }

    private void Follow()
    {
        uint console = WTSGetActiveConsoleSessionId();
        if (console == INVALID_SESSION || console == 0) { StopCurrent("no console session"); return; }

        // Is a user logged on at the console? Fails at the logon / switch-user screen (no user session) → run as
        // SYSTEM there so the secure desktop stays capturable, instead of running nothing.
        bool hasUser = WTSQueryUserToken(console, out IntPtr probe);
        if (hasUser) CloseHandle(probe);
        Mode want = hasUser ? Mode.User : Mode.System;

        if (console == _session && _mode == want && _proc is { HasExited: false }) return;   // already correct

        // Session changed (switch user), mode changed (user ↔ logon screen), or our instance died → relaunch.
        StopCurrent($"(re)launch: console session {console}, mode {want}");
        KillStray();                 // make sure the port is free before the new instance binds it
        if (want == Mode.User) LaunchAsUser(console);
        else                   LaunchAsSystem(console);
    }

    private void StopCurrent(string why)
    {
        if (_proc is null && _session == INVALID_SESSION) return;
        try { if (_proc is { HasExited: false }) { log.LogInformation("stopping Deskhand in session {s}: {why}", _session, why); _proc.Kill(entireProcessTree: true); _proc.WaitForExit(4000); } }
        catch { }
        _proc = null; _session = INVALID_SESSION; _mode = Mode.None;
    }

    // Kill every deskhand-http so only the instance we launch owns the port — covers one left by a prior session,
    // the logon task, or a crash-restart.
    private void KillStray()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_exe)))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(4000); } catch { }
            }
        }
        catch { }
    }

    // A user is logged on → launch deskhand AS THAT USER. WTSQueryUserToken hands back the UAC-FILTERED (medium-IL)
    // token for an admin when UAC is on, so launch with the FULL (linked) token → ELEVATED (parity with the logon
    // task's RunLevel=Highest; needed for admin ops and secure-desktop/UAC handling). Standard user / UAC off: as-is.
    private void LaunchAsUser(uint session)
    {
        if (!WTSQueryUserToken(session, out IntPtr userToken))
        { log.LogWarning("WTSQueryUserToken failed for session {s} (Win32 {e})", session, Marshal.GetLastWin32Error()); return; }
        IntPtr linked = ElevatedLinkedToken(userToken);
        IntPtr source = linked != IntPtr.Zero ? linked : userToken;
        try
        {
            if (!DuplicateTokenEx(source, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr primary))
            { log.LogWarning("DuplicateTokenEx(user) failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
            try { StartWithToken(primary, session, Mode.User, @"winsta0\default"); }
            finally { CloseHandle(primary); }
        }
        finally { if (linked != IntPtr.Zero) CloseHandle(linked); CloseHandle(userToken); }
    }

    // No user is logged on (logon / switch-user screen) → launch deskhand AS SYSTEM in the console session, so the
    // secure/lock/logon desktop stays capturable (OpenInputDesktop on the Winlogon desktop requires SYSTEM). The
    // service is already LocalSystem: duplicate its own token and retarget it at the console session (SeTcbPrivilege,
    // which LocalSystem holds, lets us set TokenSessionId).
    private void LaunchAsSystem(uint session)
    {
        if (!OpenProcessToken(GetCurrentProcess(), MAXIMUM_ALLOWED, out IntPtr selfTok))
        { log.LogWarning("OpenProcessToken failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
        try
        {
            if (!DuplicateTokenEx(selfTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr primary))
            { log.LogWarning("DuplicateTokenEx(system) failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
            try
            {
                uint sess = session;
                if (!SetTokenInformation(primary, TokenSessionId, ref sess, sizeof(uint)))
                { log.LogWarning("SetTokenInformation(session) failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
                uint ui = 1;   // born uiAccess (SeTcbPrivilege, held by LocalSystem): lets SendInput cross UIPI to elevated
                               // windows on a NORMAL desktop. NOTE: this does NOT unlock the SECURE desktop — a SYSTEM +
                               // born-uiAccess + Winlogon-attached process still gets SendInput ACCESS_DENIED on the
                               // lock/logon/UAC screen (proven on sandbox 903, v0.2.103). See docs/secure-desktop-input.md.
                if (!SetTokenInformation(primary, TokenUIAccess, ref ui, sizeof(uint)))
                    log.LogWarning("SetTokenInformation(UIAccess) failed (Win32 {e})", Marshal.GetLastWin32Error());
                // Launch ON the Winlogon (secure) desktop — like Deskhand.SecureHelper — so a uiAccess SYSTEM
                // process can inject input there, not only capture it.
                StartWithToken(primary, session, Mode.System, @"winsta0\winlogon");
            }
            finally { CloseHandle(primary); }
        }
        finally { CloseHandle(selfTok); }
    }

    // Shared: start the target exe with an already-prepared primary token on the given desktop of the console session.
    private void StartWithToken(IntPtr primary, uint session, Mode mode, string desktop)
    {
        CreateEnvironmentBlock(out IntPtr env, primary, false);   // machine env (DESKHAND_TOKEN/PORT) + user env
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
        string cmd = $"\"{_exe}\" {_args}".Trim();
        bool ok = CreateProcessAsUser(primary, _exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(_exe), ref si, out var pi);
        if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
        if (!ok) { log.LogWarning("CreateProcessAsUser ({mode}) failed (Win32 {e})", mode, Marshal.GetLastWin32Error()); return; }

        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        try { _proc = Process.GetProcessById(pi.dwProcessId); } catch { _proc = null; }
        _session = session; _mode = mode;
        log.LogInformation("launched {exe} (pid {pid}) into console session {s} as {mode}", Path.GetFileName(_exe), pi.dwProcessId, session, mode);
    }

    // Enable SeTcbPrivilege on our own process token (LocalSystem has it present but disabled) so SetTokenInformation
    // (TokenUIAccess) on a child's primary token is permitted.
    private void EnableSeTcb()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok))
        { log.LogWarning("EnableSeTcb: OpenProcessToken failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
        try
        {
            if (!LookupPrivilegeValue(null, "SeTcbPrivilege", out long luid))
            { log.LogWarning("EnableSeTcb: LookupPrivilegeValue failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                log.LogWarning("EnableSeTcb: AdjustTokenPrivileges (Win32 {e})", Marshal.GetLastWin32Error());
            else
                log.LogInformation("EnableSeTcb: SeTcbPrivilege enabled");
        }
        finally { CloseHandle(tok); }
    }

    // Returns the FULL (elevated) linked token for a UAC-filtered admin token, or IntPtr.Zero when there is
    // none (standard user, or UAC disabled so the token is already full). Caller owns the returned handle.
    private static IntPtr ElevatedLinkedToken(IntPtr wtsToken)
    {
        IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(wtsToken, TokenElevationType, buf, sizeof(int), out _)) return IntPtr.Zero;
            if (Marshal.ReadInt32(buf) != TokenElevationTypeLimited) return IntPtr.Zero; // already full/default
        }
        finally { Marshal.FreeHGlobal(buf); }

        IntPtr link = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            // TOKEN_LINKED_TOKEN is a single handle; LocalSystem (SeTcbPrivilege) may read it. Caller CloseHandles it.
            if (GetTokenInformation(wtsToken, TokenLinkedToken, link, IntPtr.Size, out _))
                return Marshal.ReadIntPtr(link);
        }
        finally { Marshal.FreeHGlobal(link); }
        return IntPtr.Zero;
    }
}
