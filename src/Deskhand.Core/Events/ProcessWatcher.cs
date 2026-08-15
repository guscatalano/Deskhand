using System.Diagnostics;

namespace Deskhand.Core.Events;

/// <summary>Result of a <see cref="ProcessWatcher.WaitForProcess"/> — the process that started/exited.</summary>
public record ProcessEventDto(string Event, int ProcessId, string? Name);

/// <summary>
/// Watches the running-process set and turns changes into <c>process_started</c> / <c>process_exited</c>
/// events on the <see cref="EventHub"/> — so any client polling <c>get_events</c> (or the dashboard's
/// events drawer) is "notified" when a process launches or exits. Also offers a blocking
/// <see cref="WaitForProcess"/> so an agent can await a launch/exit by name or pid.
///
/// Detection is by polling + diff (default 1s): no elevation, works in-session. It reports pid + name;
/// it does not resolve full image paths (that needs elevation for many processes), so matching is by
/// process name. WMI <c>Win32_ProcessStartTrace</c> would give lower latency but needs admin — a future option.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly EventHub _hub;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();
    private Dictionary<int, string> _known = new();
    private bool _primed;

    public ProcessWatcher(EventHub hub, int pollMs = 1000)
    {
        _hub = hub;
        _timer = new System.Threading.Timer(_ => Tick(), null, 0, Math.Max(200, pollMs));
    }

    private static Dictionary<int, string> Snapshot()
    {
        var d = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { d[p.Id] = p.ProcessName; } catch { } finally { p.Dispose(); }
        }
        return d;
    }

    private void Tick()
    {
        Dictionary<int, string> current;
        try { current = Snapshot(); } catch { return; }

        Dictionary<int, string> prev;
        bool primed;
        lock (_lock) { prev = _known; _known = current; primed = _primed; _primed = true; }
        if (!primed) return;   // never fire the world on the first snapshot

        foreach (var kv in current)
            if (!prev.ContainsKey(kv.Key)) _hub.Publish("process_started", kv.Value, null, kv.Key);
        foreach (var kv in prev)
            if (!current.ContainsKey(kv.Key)) _hub.Publish("process_exited", kv.Value, null, kv.Key);
    }

    private static string Norm(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    private static bool NameMatch(string procName, string query) =>
        procName.Contains(Norm(query), StringComparison.OrdinalIgnoreCase);

    /// <summary>Block until a process matching (<paramref name="name"/> substring and/or
    /// <paramref name="pid"/>) fires the given event ("start" or "exit"), or the timeout elapses.
    /// "start" waits for a *new* launch after the call begins; "exit" also returns immediately if the
    /// given pid is already gone. Returns null on timeout.</summary>
    public ProcessEventDto? WaitForProcess(string ev, string? name, int? pid, int timeoutMs)
    {
        bool exit = ev.Equals("exit", StringComparison.OrdinalIgnoreCase)
                 || ev.Equals("process_exited", StringComparison.OrdinalIgnoreCase);
        var baseline = Snapshot();               // for "start": only launches after this count
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var now = Snapshot();
            if (exit)
            {
                if (pid is int pv)
                {
                    if (!now.ContainsKey(pv))
                        return new ProcessEventDto("process_exited", pv, baseline.TryGetValue(pv, out var n) ? n : name);
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    // any previously-seen matching process that is now gone
                    var gone = baseline.FirstOrDefault(kv => NameMatch(kv.Value, name) && !now.ContainsKey(kv.Key));
                    if (gone.Value is not null) return new ProcessEventDto("process_exited", gone.Key, gone.Value);
                }
            }
            else
            {
                foreach (var kv in now)
                {
                    if (baseline.ContainsKey(kv.Key)) continue;               // not new
                    if (pid is int pv && kv.Key != pv) continue;
                    if (!string.IsNullOrEmpty(name) && !NameMatch(kv.Value, name)) continue;
                    return new ProcessEventDto("process_started", kv.Key, kv.Value);
                }
            }
            if (sw.ElapsedMilliseconds >= Math.Max(0, timeoutMs)) return null;
            Thread.Sleep(200);
        }
    }

    public void Dispose() => _timer.Dispose();
}
