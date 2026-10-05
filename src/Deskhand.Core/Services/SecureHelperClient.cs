using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Deskhand.Core.Services;

/// <summary>
/// Talks to the SYSTEM secure helper (deskhand-secure.exe serve) over a named pipe, and starts it
/// (via deskhand-broker.exe, which relaunches it as SYSTEM in the console session) when it isn't
/// already running. The main server delegates secure-desktop capture + input here, since an elevated
/// user-session process cannot touch Winsta0\Winlogon itself. A single connection is reused and
/// serialized; it reconnects on error. All methods throw on failure so callers surface a clear error.
/// </summary>
public sealed class SecureHelperClient : IDisposable
{
    private readonly string _pipe;
    private readonly object _gate = new();
    private NamedPipeClientStream? _conn;
    private DateTime _lastSpawn = DateTime.MinValue;

    public SecureHelperClient(string? pipe = null) => _pipe = pipe ?? SecureHelperProtocol.DefaultPipeName;

    /// <summary>Ensure the helper is reachable: connect, else spawn it and wait briefly for the pipe.</summary>
    public bool EnsureStarted(int waitMs = 6000)
    {
        lock (_gate)
        {
            if (TryConnectLocked(250)) return true;
            Spawn();
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (DateTime.UtcNow < until)
            {
                if (TryConnectLocked(250)) return true;
                Thread.Sleep(250);
            }
            return false;
        }
    }

    public bool IsReady { get { lock (_gate) return TryConnectLocked(150); } }

    /// <summary>{running as, desktop} — proves the helper is alive and whether it's SYSTEM.</summary>
    public JsonElement Ping() => Call(new { op = "ping" }).json;

    /// <summary>Capture the current input desktop (the secure desktop when the helper is SYSTEM).</summary>
    public (string desktopName, string kind, byte[] bytes) Capture(string format, int quality)
    {
        var (json, blob) = Call(new { op = "capture", format, quality });
        return (Str(json, "desktopName"), Str(json, "kind"), blob);
    }

    public void Input(object inputRequest) => Call(inputRequest);

    private (JsonElement json, byte[] blob) Call(object request)
    {
        byte[] req = JsonSerializer.SerializeToUtf8Bytes(request);
        lock (_gate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_conn is null || !_conn.IsConnected)
                {
                    if (!TryConnectLocked(500) && !RespawnAndConnectLocked())
                        throw new DesktopUnavailableException("Secure helper is not running and could not be started (needs elevation + SeDebugPrivilege).");
                }
                try
                {
                    SecureHelperProtocol.WriteMessage(_conn!, req, null);
                    var (rjson, rblob) = SecureHelperProtocol.ReadMessage(_conn!);
                    var doc = JsonDocument.Parse(rjson).RootElement;
                    if (doc.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                        throw new DesktopUnavailableException(Str(doc, "error"));
                    return (doc, rblob);
                }
                catch (DesktopUnavailableException) { throw; }
                catch (Exception) when (attempt == 0) { try { _conn?.Dispose(); } catch { } _conn = null; } // pipe dropped; retry once
            }
            throw new DesktopUnavailableException("Secure helper connection failed.");
        }
    }

    private bool TryConnectLocked(int timeoutMs)
    {
        if (_conn is { IsConnected: true }) return true;
        try
        {
            var c = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.None);
            c.Connect(timeoutMs);
            _conn = c;
            return true;
        }
        catch { return false; }
    }

    private bool RespawnAndConnectLocked()
    {
        Spawn();
        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until)
        {
            if (TryConnectLocked(250)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Launch the helper as SYSTEM via the broker (detached; the broker keeps it alive).
    /// Rate-limited so a storm of failing calls doesn't spawn brokers in a tight loop.</summary>
    private void Spawn()
    {
        if ((DateTime.UtcNow - _lastSpawn).TotalSeconds < 3) return;
        _lastSpawn = DateTime.UtcNow;

        string dir = AppContext.BaseDirectory;
        string broker = Path.Combine(dir, "deskhand-broker.exe");
        string helper = Path.Combine(dir, "deskhand-secure.exe");
        if (!File.Exists(broker) || !File.Exists(helper))
            throw new DesktopUnavailableException(
                $"Secure helper not deployed: expected deskhand-broker.exe and deskhand-secure.exe beside the server in '{dir}'.");

        var psi = new ProcessStartInfo(broker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        psi.ArgumentList.Add(helper);
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(_pipe);
        try { Process.Start(psi); } catch (Exception ex) { throw new DesktopUnavailableException($"Could not start the secure helper broker: {ex.Message}"); }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    public void Dispose() { lock (_gate) { try { _conn?.Dispose(); } catch { } _conn = null; } }
}
