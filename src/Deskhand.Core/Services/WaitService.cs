using System.Diagnostics;

namespace Deskhand.Core.Services;

public record WaitResultDto(
    bool Met, string Target, string Match, string Expect,
    long ElapsedMs, int Polls, bool TimedOut, string? Detail = null, string? Error = null);

/// <summary>
/// Poll a non-visual condition until it holds (or a timeout). Unlike the capture/vision tools this never takes a
/// screenshot, so it does not fire the capture toast into a recording — an agent can wait for an install to finish,
/// a window to appear, a file to drop, or a registry value to flip, all off-screen. Supported targets:
/// <list type="bullet">
/// <item><b>window</b> — a window whose title contains <c>match</c> (case-insensitive).</item>
/// <item><b>process</b> — a process by name (with or without .exe) or by numeric pid.</item>
/// <item><b>file</b> — a file or directory path exists.</item>
/// <item><b>registry</b> — <c>HKCU\Path\Key</c> (key exists), <c>…\Key!ValueName</c> (value exists), or
/// <c>…\Key!ValueName=expected</c> (value equals, string-compared case-insensitively).</item>
/// </list>
/// <c>expect="gone"</c> inverts the wait (block until the thing is absent). Report-only: it never clicks, launches,
/// or modifies anything.
/// </summary>
public static class WaitService
{
    public const int MaxTimeoutMs = 300_000;   // 5 min — an agent should not wedge a session longer than this.
    public const int MinPollMs = 100;
    public const int MaxPollMs = 10_000;

    public static async Task<WaitResultDto> WaitAsync(
        string? target, string? match, string? expect, int timeoutMs, int pollMs, CancellationToken ct = default)
    {
        target = (target ?? "").Trim().ToLowerInvariant();
        match = (match ?? "").Trim();
        bool wantGone = string.Equals((expect ?? "present").Trim(), "gone", StringComparison.OrdinalIgnoreCase);
        string expectLabel = wantGone ? "gone" : "present";

        Func<(bool present, string? detail)>? probe = target switch
        {
            "window" => () => ProbeWindow(match),
            "process" => () => ProbeProcess(match),
            "file" => () => ProbeFile(match),
            "registry" or "reg" => () => ProbeRegistry(match),
            _ => null,
        };
        if (probe is null)
            return new WaitResultDto(false, target, match, expectLabel, 0, 0, false, null,
                "Unknown target — use window | process | file | registry.");
        if (match.Length == 0)
            return new WaitResultDto(false, target, match, expectLabel, 0, 0, false, null, "Empty match.");

        timeoutMs = timeoutMs <= 0 ? 30_000 : Math.Min(timeoutMs, MaxTimeoutMs);
        pollMs = Math.Clamp(pollMs <= 0 ? 500 : pollMs, MinPollMs, MaxPollMs);

        var sw = Stopwatch.StartNew();
        int polls = 0;
        string? lastDetail = null;
        while (true)
        {
            polls++;
            bool present;
            try { (present, lastDetail) = probe(); }
            catch (Exception ex) { return new WaitResultDto(false, target, match, expectLabel, sw.ElapsedMilliseconds, polls, false, null, ex.Message); }

            if (present != wantGone)   // met when (present && want-present) or (!present && want-gone)
                return new WaitResultDto(true, target, match, expectLabel, sw.ElapsedMilliseconds, polls, false, lastDetail);

            if (sw.ElapsedMilliseconds >= timeoutMs)
                return new WaitResultDto(false, target, match, expectLabel, sw.ElapsedMilliseconds, polls, true, lastDetail);

            try { await Task.Delay(pollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new WaitResultDto(false, target, match, expectLabel, sw.ElapsedMilliseconds, polls, false, lastDetail, "Cancelled."); }
        }
    }

    private static (bool, string?) ProbeWindow(string match)
    {
        var win = Win32Windows.List().FirstOrDefault(w =>
            !string.IsNullOrEmpty(w.Title) && w.Title!.Contains(match, StringComparison.OrdinalIgnoreCase));
        return win is null ? (false, null) : (true, $"hwnd={win.Hwnd} pid={win.Pid} title=\"{win.Title}\"");
    }

    private static (bool, string?) ProbeProcess(string match)
    {
        if (int.TryParse(match, out int pid))
        {
            try { using var p = Process.GetProcessById(pid); return (true, $"pid={pid} name={p.ProcessName}"); }
            catch { return (false, null); }
        }
        string name = match.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? match[..^4] : match;
        var procs = Process.GetProcessesByName(name);
        try
        {
            if (procs.Length == 0) return (false, null);
            return (true, $"{procs.Length} running; pid={procs[0].Id}");
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private static (bool, string?) ProbeFile(string match)
    {
        string full;
        try { full = Path.GetFullPath(match.Trim('"')); }
        catch { full = match; }
        if (File.Exists(full)) return (true, $"file, {new FileInfo(full).Length} bytes");
        if (Directory.Exists(full)) return (true, "directory");
        return (false, null);
    }

    private static (bool, string?) ProbeRegistry(string match)
    {
        // HKCU\Path\Key        → key exists
        // HKCU\Path\Key!Name   → value 'Name' exists
        // HKCU\Path\Key!Name=X → value 'Name' equals X (case-insensitive string compare)
        string keyPath = match;
        string? valName = null, expected = null;
        int bang = match.IndexOf('!');
        if (bang >= 0)
        {
            keyPath = match[..bang];
            string rest = match[(bang + 1)..];
            int eq = rest.IndexOf('=');
            if (eq >= 0) { valName = rest[..eq]; expected = rest[(eq + 1)..]; }
            else valName = rest;
        }

        var key = RegistryService.Browse(keyPath);
        if (key.Error is not null) return (false, null);           // missing key / access denied → not present
        if (valName is null) return (true, $"key, {key.SubKeys.Count} subkeys, {key.Values.Count} values");

        var v = key.Values.FirstOrDefault(x => string.Equals(x.Name, valName, StringComparison.OrdinalIgnoreCase));
        if (v is null) return (false, null);
        if (expected is null) return (true, $"{v.Name} ({v.Kind}) = {v.Value}");
        return string.Equals(v.Value ?? "", expected, StringComparison.OrdinalIgnoreCase)
            ? (true, $"{v.Name} = {v.Value}")
            : (false, $"{v.Name} = {v.Value} (waiting for {expected})");
    }
}
