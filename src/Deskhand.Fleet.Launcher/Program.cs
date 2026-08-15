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

        private Process? _current;
        private uint _currentSession = INVALID_SESSION;

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            log.LogInformation("Deskhand launcher started; agent={agent} url={url}", _agentExe, _wsUrl);
            while (!ct.IsCancellationRequested)
            {
                try { EnsureAgent(); }
                catch (Exception ex) { log.LogError(ex, "ensure-agent failed"); }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        private void EnsureAgent()
        {
            uint session = WTSGetActiveConsoleSessionId();
            if (session is INVALID_SESSION or 0) return; // no interactive console session

            if (_current is { HasExited: false } && _currentSession == session) return; // still running

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
                    string cmd = $"\"{_agentExe}\" {_wsUrl}";
                    bool ok = CreateProcessAsUser(primary, _agentExe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(_agentExe), ref si, out var pi);
                    if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                    if (!ok) { log.LogWarning("CreateProcessAsUser failed (Win32 {e})", Marshal.GetLastWin32Error()); return; }

                    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                    try { _current = Process.GetProcessById(pi.dwProcessId); } catch { _current = null; }
                    _currentSession = session;
                    log.LogInformation("launched agent (pid {pid}) into session {s}", pi.dwProcessId, session);
                }
                finally { CloseHandle(primary); }
            }
            finally { CloseHandle(userToken); }
        }
    }
}
