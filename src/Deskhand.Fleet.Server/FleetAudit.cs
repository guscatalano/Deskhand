using System.Text.Json;

namespace Deskhand.Fleet.Server;

/// <summary>
/// Server-side record of fleet activity: which agents connected/disconnected and every action routed
/// to them (from the web dashboard, the HTTP API, or MCP), with the caller's address. Kept in a ring
/// buffer for the dashboard + MCP, and appended to a dated JSONL file.
/// </summary>
public sealed class FleetAudit
{
    public sealed record Entry(long Id, string Ts, string Kind, string? Client, string? Agent, string? Detail);

    private const int Cap = 2000;
    private readonly object _lock = new();
    private long _seq;
    private readonly Queue<Entry> _buffer = new();
    public string Directory { get; }

    public FleetAudit()
    {
        Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskhand", "fleet-audit");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public Entry Record(string kind, string? client, string? agent, string? detail)
    {
        Entry e;
        lock (_lock)
        {
            e = new Entry(++_seq, DateTimeOffset.Now.ToString("o"), kind, client, agent, detail);
            _buffer.Enqueue(e);
            while (_buffer.Count > Cap) _buffer.Dequeue();
        }
        try { File.AppendAllText(Path.Combine(Directory, $"fleet-{DateTime.Now:yyyyMMdd}.jsonl"), JsonSerializer.Serialize(e) + Environment.NewLine); }
        catch { }
        return e;
    }

    public IReadOnlyList<Entry> Since(long cursor) { lock (_lock) return _buffer.Where(e => e.Id > cursor).ToList(); }
    public long LastId { get { lock (_lock) return _seq; } }
}
