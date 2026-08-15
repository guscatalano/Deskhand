using System.Threading.Channels;

namespace Deskhand.Core.Events;

/// <summary>
/// Collects UIA automation events (focus changes, windows opening) and fans them out two ways:
/// a bounded ring buffer for polling clients (<see cref="Since"/>), and live channels for streaming
/// clients (SSE via <see cref="Subscribe"/>). Events carry a monotonic id so pollers can resume.
/// </summary>
public sealed class EventHub
{
    public sealed record UiEvent(long Id, string Ts, string Type, string? Name, string? ControlType, int? ProcessId);

    private const int BufferCap = 500;
    private readonly object _lock = new();
    private long _seq;
    private readonly Queue<UiEvent> _buffer = new();
    private readonly List<Channel<UiEvent>> _subscribers = new();

    public long LastId { get { lock (_lock) return _seq; } }

    public UiEvent Publish(string type, string? name, string? controlType, int? processId)
    {
        UiEvent ev;
        Channel<UiEvent>[] subs;
        lock (_lock)
        {
            ev = new UiEvent(++_seq, DateTimeOffset.Now.ToString("o"), type, name, controlType, processId);
            _buffer.Enqueue(ev);
            while (_buffer.Count > BufferCap) _buffer.Dequeue();
            subs = _subscribers.ToArray();
        }
        foreach (var c in subs) c.Writer.TryWrite(ev);
        return ev;
    }

    public IReadOnlyList<UiEvent> Since(long cursor)
    {
        lock (_lock) return _buffer.Where(e => e.Id > cursor).ToList();
    }

    public (ChannelReader<UiEvent> reader, Action dispose) Subscribe()
    {
        var ch = Channel.CreateUnbounded<UiEvent>();
        lock (_lock) _subscribers.Add(ch);
        return (ch.Reader, () =>
        {
            lock (_lock) _subscribers.Remove(ch);
            ch.Writer.TryComplete();
        });
    }
}
