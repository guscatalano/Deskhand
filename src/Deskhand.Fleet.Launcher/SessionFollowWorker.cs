using System.Diagnostics;
using System.Runtime.InteropServices;
using static Deskhand.Fleet.Launcher.Interop;

namespace Deskhand.Fleet.Launcher;

/// <summary>
/// Keeps ONE instance of a target exe (normally deskhand-http.exe) running in whichever session currently owns the
/// physical console, launched AS THE LOGGED-IN USER — so Deskhand <b>follows a fast user switch</b>. A LocalSystem
/// service in session 0 has no desktop, so it spawns the exe into winsta0\default via the active session's user
/// token. When the console switches to another user, the old instance is stopped (freeing its port) and a new one
/// is launched in the new session; when the console is at the logon / switch-user screen (no user), no instance
/// runs (there's nothing to drive). Selected by setting DESKHAND_LAUNCH_EXE; otherwise the fleet-agent worker runs.
/// </summary>
public sealed class SessionFollowWorker(ILogger<SessionFollowWorker> log) : BackgroundService
{
    private readonly string _exe = Environment.GetEnvironmentVariable("DESKHAND_LAUNCH_EXE")
        ?? Path.Combine(AppContext.BaseDirectory, "deskhand-http.exe");
    private readonly string _args = Environment.GetEnvironmentVariable("DESKHAND_LAUNCH_ARGS") ?? "--hidden";

    private uint _session = INVALID_SESSION;
    private Process? _proc;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
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

        // Is a user actually logged on at the console? (Fails at the logon / lock / switch-user screen.)
        if (!WTSQueryUserToken(console, out IntPtr probe))
        {
            StopCurrent($"no user at console session {console} (logon/switch screen)");
            return;
        }
        CloseHandle(probe);

        if (console == _session && _proc is { HasExited: false }) return;   // already running in the right session

        // Session changed (switch user) or our instance died → relocate.
        StopCurrent($"relocating to console session {console}");
        KillStray();                 // make sure the port is free before the new instance binds it
        Launch(console);
    }

    private void StopCurrent(string why)
    {
        if (_proc is null && _session == INVALID_SESSION) return;
        try { if (_proc is { HasExited: false }) { log.LogInformation("stopping Deskhand in session {s}: {why}", _session, why); _proc.Kill(entireProcessTree: true); _proc.WaitForExit(4000); } }
        catch { }
        _proc = null; _session = INVALID_SESSION;
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

    private void Launch(uint session)
    {
        if (!WTSQueryUserToken(session, out IntPtr userToken))
        { log.LogWarning("WTSQueryUserToken failed for session {s} (Win32 {e})", session, Marshal.GetLastWin32Error()); return; }
        // WTSQueryUserToken hands back the UAC-FILTERED (medium-IL) token for an admin when UAC is on. Launch
        // deskhand with the FULL (linked) token so it runs ELEVATED — parity with the logon-task (RunLevel=Highest)
        // model this replaces, and required for admin ops and the secure-desktop/UAC handling. For a standard user,
        // or with UAC off, there's no limited→full split and we use the token as-is.
        IntPtr linked = ElevatedLinkedToken(userToken);
        IntPtr source = linked != IntPtr.Zero ? linked : userToken;
        try
        {
            if (!DuplicateTokenEx(source, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr primary))
            { log.LogWarning("DuplicateTokenEx failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
            try
            {
                CreateEnvironmentBlock(out IntPtr env, primary, false);   // includes machine env (DESKHAND_TOKEN/PORT) + user env
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                string cmd = $"\"{_exe}\" {_args}".Trim();
                bool ok = CreateProcessAsUser(primary, _exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(_exe), ref si, out var pi);
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (!ok) { log.LogWarning("CreateProcessAsUser failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }

                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                try { _proc = Process.GetProcessById(pi.dwProcessId); } catch { _proc = null; }
                _session = session;
                log.LogInformation("launched {exe} (pid {pid}) into console session {s}", Path.GetFileName(_exe), pi.dwProcessId, session);
            }
            finally { CloseHandle(primary); }
        }
        finally { if (linked != IntPtr.Zero) CloseHandle(linked); CloseHandle(userToken); }
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
