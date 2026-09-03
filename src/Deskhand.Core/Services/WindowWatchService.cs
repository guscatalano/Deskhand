using System.Collections.Concurrent;

namespace Deskhand.Core.Services;

public record WindowSnapshotDto(string BaselineId, int Count, IReadOnlyList<Win32Window> Windows);
public record WindowChangesDto(
    string Baseline, bool BaselineJustCreated, int AppearedCount, IReadOnlyList<Win32Window> Appeared,
    int ClosedCount, IReadOnlyList<Win32Window> Closed, long? ForegroundHwnd, string? ForegroundTitle, string Note);

/// <summary>
/// Report-only window-appearance detector, built on the COMPLETE <see cref="Win32Windows"/> enumeration (so it
/// isn't blind to owned nag windows). Take a <b>baseline</b>, then after an action ask what <b>changed</b> —
/// which windows appeared (and which closed). It never clicks or closes anything; it just turns "my click went
/// somewhere baffling" into "a window titled X (class Y, process Z) appeared over your target — handle it." A new
/// window that is foreground and from a different process is the classic focus-stealer.
/// </summary>
public static class WindowWatchService
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Win32Window>> Baselines = new();
    private static readonly ConcurrentQueue<string> Order = new();
    private static volatile string _latest = "";

    public static WindowSnapshotDto Baseline()
    {
        var wins = Win32Windows.List();
        string id = "wb_" + Guid.NewGuid().ToString("N")[..8];
        Baselines[id] = wins; _latest = id; Order.Enqueue(id);
        while (Order.Count > 20 && Order.TryDequeue(out var old)) Baselines.TryRemove(old, out _);
        return new WindowSnapshotDto(id, wins.Count, wins);
    }

    /// <summary>What appeared / closed since a baseline. If none exists (or the id is unknown), a baseline is
    /// established now and nothing is reported as appeared.</summary>
    public static WindowChangesDto Changes(string? baselineId = null)
    {
        var current = Win32Windows.List();
        var fgWin = current.FirstOrDefault(w => w.Foreground);

        string key = string.IsNullOrEmpty(baselineId) ? _latest : baselineId!;
        if (!Baselines.TryGetValue(key, out var baseWins))
        {
            var snap = Baseline();
            return new WindowChangesDto(snap.BaselineId, true, 0, Array.Empty<Win32Window>(), 0, Array.Empty<Win32Window>(),
                fgWin?.Hwnd, fgWin?.Title, "No baseline existed — established one now; call again after an action to see what appeared.");
        }

        var baseHwnds = baseWins.Select(w => w.Hwnd).ToHashSet();
        var curHwnds = current.Select(w => w.Hwnd).ToHashSet();
        var appeared = current.Where(w => !baseHwnds.Contains(w.Hwnd)).ToList();
        var closed = baseWins.Where(w => !curHwnds.Contains(w.Hwnd)).ToList();

        string note = appeared.Count == 0 ? "No new windows since the baseline."
            : appeared.Any(w => w.Foreground)
                ? $"{appeared.Count} new window(s); one is now FOREGROUND — likely what stole focus. Handle it, then re-baseline."
                : $"{appeared.Count} new window(s) appeared (none foreground).";
        return new WindowChangesDto(key, false, appeared.Count, appeared, closed.Count, closed, fgWin?.Hwnd, fgWin?.Title, note);
    }

    public static IReadOnlyList<Win32Window> List() => Win32Windows.List();
}
