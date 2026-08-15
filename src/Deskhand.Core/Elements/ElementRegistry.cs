using System.Collections.Concurrent;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Deskhand.Core.Elements;

/// <summary>
/// Maps opaque, model-facing refs to live UIA elements plus a re-resolution recipe.
/// UIA elements are volatile COM references, so a ref that goes stale is re-resolved
/// from its recipe (see <see cref="Services.UiaService"/>) before we give up on it.
/// </summary>
public sealed class ElementRegistry
{
    public sealed class Entry(AutomationElement element)
    {
        public AutomationElement Element { get; set; } = element;
        public string? AutomationId { get; init; }
        public string? Name { get; init; }
        public string? ClassName { get; init; }
        public ControlType ControlType { get; init; }
        public IntPtr Hwnd { get; init; }
    }

    private const int MaxEntries = 20_000;
    private readonly ConcurrentDictionary<string, Entry> _map = new();
    private readonly ConcurrentQueue<string> _order = new();

    public string Add(Entry entry)
    {
        string refId = "el_" + Guid.NewGuid().ToString("N")[..16];
        _map[refId] = entry;
        _order.Enqueue(refId);

        while (_order.Count > MaxEntries && _order.TryDequeue(out var old))
            _map.TryRemove(old, out _);

        return refId;
    }

    public Entry? Get(string reference) => _map.TryGetValue(reference, out var e) ? e : null;

    public void Clear()
    {
        _map.Clear();
        while (_order.TryDequeue(out _)) { }
    }
}
