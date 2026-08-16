using System.Collections.Concurrent;
using System.Diagnostics;

namespace Deskhand.Fleet.Server;

/// <summary>
/// Spawns and tracks <c>deskhand-rdp --fleet</c> connector processes so an operator can add a machine to
/// the fleet over RDP straight from the web UI. Each connector opens an RDP session to the target and
/// dials back into this server as a capture+input agent. The password is passed to the child via an
/// environment variable (not the command line, so it isn't visible in the process list).
/// </summary>
public sealed class RdpConnectorManager
{
    public record Conn(string Id, string Host, string User, int Pid, string StartedTs);

    private readonly ConcurrentDictionary<string, (Process proc, Conn info)> _conns = new();
    private readonly string _wsUrl;
    private readonly string? _token;

    public RdpConnectorManager(int fleetPort, string? token)
    {
        _wsUrl = $"ws://127.0.0.1:{fleetPort}/agent/connect";
        _token = token;
    }

    private static string RdpExe()
    {
        var env = Environment.GetEnvironmentVariable("DESKHAND_RDP_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        return Path.Combine(AppContext.BaseDirectory, "deskhand-rdp.exe");
    }

    public Conn Connect(string host, string user, string password, string? domain, string? size, string? id)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
            throw new ArgumentException("host and user are required.");
        id = string.IsNullOrWhiteSpace(id) ? host : id;
        if (_conns.TryGetValue(id, out var ex) && SafeAlive(ex.proc))
            throw new InvalidOperationException($"An RDP connector '{id}' is already running.");
        _conns.TryRemove(id, out _);

        var exe = RdpExe();
        if (!File.Exists(exe))
            throw new FileNotFoundException($"deskhand-rdp.exe not found (looked at '{exe}'). Set DESKHAND_RDP_PATH to its full path.");

        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add(user);
        psi.ArgumentList.Add("-");                       // password comes from the env var below
        psi.ArgumentList.Add("--fleet"); psi.ArgumentList.Add(_wsUrl);
        psi.ArgumentList.Add("--id"); psi.ArgumentList.Add(id);
        if (!string.IsNullOrWhiteSpace(domain)) { psi.ArgumentList.Add("--domain"); psi.ArgumentList.Add(domain); }
        if (!string.IsNullOrWhiteSpace(size)) { psi.ArgumentList.Add("--size"); psi.ArgumentList.Add(size); }
        psi.Environment["DESKHAND_RDP_PASSWORD"] = password;
        if (!string.IsNullOrEmpty(_token)) psi.Environment["DESKHAND_FLEET_TOKEN"] = _token;

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start deskhand-rdp.");
        var info = new Conn(id, host, user, proc.Id, DateTimeOffset.Now.ToString("o"));
        _conns[id] = (proc, info);
        return info;
    }

    public bool Disconnect(string id)
    {
        if (!_conns.TryRemove(id, out var c)) return false;
        try { if (!c.proc.HasExited) c.proc.Kill(true); } catch { }
        return true;
    }

    public IEnumerable<object> List() => _conns.Values.Select(c => new
    {
        c.info.Id, c.info.Host, c.info.User, c.info.Pid, c.info.StartedTs, alive = SafeAlive(c.proc)
    });

    private static bool SafeAlive(Process p) { try { return !p.HasExited; } catch { return false; } }

    public void DisposeAll()
    {
        foreach (var c in _conns.Values) { try { if (!c.proc.HasExited) c.proc.Kill(true); } catch { } }
        _conns.Clear();
    }
}
