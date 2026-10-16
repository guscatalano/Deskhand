using System.Diagnostics;
using System.Runtime.InteropServices;
using Deskhand.Fleet.Launcher;
using static Deskhand.Fleet.Launcher.Interop;

// Deskhand Fleet Launcher: a Windows Service (LocalSystem) that keeps a Deskhand agent running in the
// active console session, launched AS THE LOGGED-IN USER (design §03). Because a Session-0 service has
// no desktop, it spawns the agent into Winsta0\Default via the session's user token.
//
// Requires install as a service running as LocalSystem (needs SeTcbPrivilege for WTSQueryUserToken):
//   sc create DeskhandLauncher binPath= "C:\path\deskhand-launcher.exe" start= auto obj= LocalSystem
//   sc start DeskhandLauncher
// Configure via machine env: DESKHAND_FLEET_URL, DESKHAND_FLEET_TOKEN, DESKHAND_AGENT_EXE.
// NOT exercised in the build sandbox (needs service install + SYSTEM).

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "DeskhandLauncher");
// DESKHAND_LAUNCH_EXE set → keep that exe (deskhand-http.exe) in the active CONSOLE session so Deskhand follows a
// fast user switch (the switch-user model). Otherwise → the fleet-agent launcher (an agent in every session).
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKHAND_LAUNCH_EXE")))
    builder.Services.AddHostedService<SessionFollowWorker>();
else
    builder.Services.AddHostedService<AgentLauncherWorker>();
builder.Build().Run();

namespace Deskhand.Fleet.Launcher
{
    public sealed class AgentLauncherWorker(ILogger<AgentLauncherWorker> log) : BackgroundService
    {
        private readonly string _agentExe = Environment.GetEnvironmentVariable("DESKHAND_AGENT_EXE")
            ?? Path.Combine(AppContext.BaseDirectory, "deskhand-agent.exe");
        private readonly string _wsUrl = Environment.GetEnvironmentVariable("DESKHAND_FLEET_URL")
            ?? "ws://127.0.0.1:8799/agent/connect";

        private readonly Dictionary<uint, Process> _agents = new();
        private Process? _systemAgent;                       // the logon-screen agent (SYSTEM, console session)
        private uint _systemAgentSession = INVALID_SESSION;

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            log.LogInformation("Deskhand launcher started; agent={agent} url={url}", _agentExe, _wsUrl);
            while (!ct.IsCancellationRequested)
            {
                try { EnsureAgents(); }
                catch (Exception ex) { log.LogError(ex, "ensure-agents failed"); }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        // Spawn/keep an agent in EVERY active user session — console AND RDP — so Deskhand covers
        // remote-desktop sessions too (each RDP session is just another interactive session).
        private void EnsureAgents()
        {
            var active = ActiveUserSessions();
            foreach (uint session in active)
            {
                if (_agents.TryGetValue(session, out var p) && !p.HasExited) continue;
                EnsureAgent(session);
            }
            // forget sessions that ended
            foreach (var gone in _agents.Keys.Where(s => !active.Contains(s)).ToList())
                _agents.Remove(gone);

            EnsureConsoleSystemAgent();
        }

        // When NO user is logged on at the console (logon / lock / switch-user / UAC-secure screen), run a SYSTEM
        // agent in the console session on winsta0\winlogon so that desktop stays driveable (capturable) before a
        // user signs in. When a user logs on, stop it — the per-user agent (above) covers that session instead.
        private void EnsureConsoleSystemAgent()
        {
            uint console = WTSGetActiveConsoleSessionId();
            if (console == INVALID_SESSION || console == 0) { StopSystemAgent(); return; }
            if (WTSQueryUserToken(console, out IntPtr probe)) { CloseHandle(probe); StopSystemAgent(); return; }  // user present → user agent covers it
            if (_systemAgent is { HasExited: false } && _systemAgentSession == console) return;
            StopSystemAgent();
            LaunchSystemAgent(console);
        }

        private void StopSystemAgent()
        {
            if (_systemAgent is null) { _systemAgentSession = INVALID_SESSION; return; }
            try { if (!_systemAgent.HasExited) { _systemAgent.Kill(entireProcessTree: true); _systemAgent.WaitForExit(3000); } } catch { }
            _systemAgent = null; _systemAgentSession = INVALID_SESSION;
        }

        // Launch the agent AS SYSTEM into the console session's Winlogon desktop: duplicate the service's own
        // LocalSystem token and retarget TokenSessionId at the console (SeTcbPrivilege, held by LocalSystem).
        private void LaunchSystemAgent(uint session)
        {
            if (!OpenProcessToken(GetCurrentProcess(), MAXIMUM_ALLOWED, out IntPtr selfTok))
            { log.LogWarning("logon-agent OpenProcessToken failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
            try
            {
                if (!DuplicateTokenEx(selfTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr primary))
                { log.LogWarning("logon-agent DuplicateTokenEx failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
                try
                {
                    uint sess = session;
                    if (!SetTokenInformation(primary, TokenSessionId, ref sess, sizeof(uint)))
                    { log.LogWarning("logon-agent SetTokenInformation(session) failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
                    CreateEnvironmentBlock(out IntPtr env, primary, false);   // machine env: DESKHAND_FLEET_URL/TOKEN
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\winlogon" };
                    string agentId = $"{Environment.MachineName}-S{session}";
                    string cmd = $"\"{_agentExe}\" {_wsUrl} {agentId}";
                    bool ok = CreateProcessAsUser(primary, _agentExe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(_agentExe), ref si, out var pi);
                    if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                    if (!ok) { log.LogWarning("logon-agent CreateProcessAsUser failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    try { _systemAgent = Process.GetProcessById(pi.dwProcessId); } catch { _systemAgent = null; }
                    _systemAgentSession = session;
                    log.LogInformation("launched SYSTEM logon agent (pid {pid}) into console session {s}", pi.dwProcessId, session);
                }
                finally { CloseHandle(primary); }
            }
            finally { CloseHandle(selfTok); }
        }

        private static HashSet<uint> ActiveUserSessions()
        {
            var result = new HashSet<uint>();
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr info, out int count)) return result;
            try
            {
                int size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (int i = 0; i < count; i++)
                {
                    var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(info + i * size);
                    if (si.State == WTS_ACTIVE && si.SessionId != 0) result.Add(si.SessionId);
                }
            }
            finally { WTSFreeMemory(info); }
            return result;
        }

        private void EnsureAgent(uint session)
        {
            if (!WTSQueryUserToken(session, out IntPtr userToken))
            {
                log.LogWarning("WTSQueryUserToken failed for session {s} (Win32 {e})", session, Marshal.GetLastWin32Error());
                return;
            }
            try
            {
                if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr primary))
                { log.LogWarning("DuplicateTokenEx failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }
                try
                {
                    CreateEnvironmentBlock(out IntPtr env, primary, false);
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                    string agentId = $"{Environment.MachineName}-S{session}";
                    string cmd = $"\"{_agentExe}\" {_wsUrl} {agentId}";
                    bool ok = CreateProcessAsUser(primary, _agentExe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(_agentExe), ref si, out var pi);
                    if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                    if (!ok) { log.LogWarning("CreateProcessAsUser failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }

                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    try { _agents[session] = Process.GetProcessById(pi.dwProcessId); } catch { }
                    log.LogInformation("launched agent (pid {pid}, id {aid}) into session {s}", pi.dwProcessId, agentId, session);
                }
                finally { CloseHandle(primary); }
            }
            finally { CloseHandle(userToken); }
        }
    }
}
