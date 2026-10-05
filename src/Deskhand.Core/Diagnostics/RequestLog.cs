namespace Deskhand.Core.Diagnostics;

/// <summary>One recorded HTTP request: when it started, what it was, and how long it took.
/// Bodies are never captured — only metadata — so this is safe to keep always-on.</summary>
public sealed record RequestLogEntry(
    long StartedUnixMs,
    string Method,
    string Path,
    string? Query,
    int Status,
    double ElapsedMs,
    string? RemoteIp,
    long? RequestBytes);

/// <summary>A fixed-size, thread-safe ring buffer of the most recent HTTP requests with timing,
/// so lag can be investigated after the fact without external tooling. Oldest entries drop off.</summary>
public sealed class RequestLog
{
    private readonly RequestLogEntry[] _buf;
    private readonly object _gate = new();
    private int _next;      // index to write next
    private int _count;     // number of valid entries
    private long _total;    // lifetime count (survives ring wrap)

    public RequestLog(int capacity = 2000)
    {
        if (capacity < 1) capacity = 1;
        _buf = new RequestLogEntry[capacity];
    }

    public int Capacity => _buf.Length;
    public long Total { get { lock (_gate) return _total; } }

    public void Add(RequestLogEntry e)
    {
        lock (_gate)
        {
            _buf[_next] = e;
            _next = (_next + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
            _total++;
        }
    }

    /// <summary>Most recent first. Optionally keep only requests at or above <paramref name="minMs"/>
    /// and/or whose path contains <paramref name="pathContains"/>.</summary>
    public IReadOnlyList<RequestLogEntry> Recent(int limit = 200, double? minMs = null, string? pathContains = null)
    {
        lock (_gate)
        {
            var list = new List<RequestLogEntry>(_count);
            int start = (_next - _count + _buf.Length) % _buf.Length;
            for (int i = _count - 1; i >= 0; i--)   // newest → oldest
            {
                var e = _buf[(start + i) % _buf.Length];
                if (e is null) continue;
                if (minMs is double m && e.ElapsedMs < m) continue;
                if (!string.IsNullOrEmpty(pathContains)
                    && !e.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(e);
                if (limit > 0 && list.Count >= limit) break;
            }
            return list;
        }
    }
}
