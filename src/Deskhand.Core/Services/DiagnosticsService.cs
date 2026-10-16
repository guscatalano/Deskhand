using System.Diagnostics.Eventing.Reader;
using System.Management;

namespace Deskhand.Core.Services;

public record EventEntryDto(string Log, string Level, int EventId, string? Source, string? Time, string? Message);
public record DiskHealthDto(string? Model, string? Serial, string? Status, bool? PredictFailure);

/// <summary>Read-only diagnostics: recent error/warning events from the System + Application logs, and disk
/// health (WMI status + SMART failure-prediction). Nothing is changed.</summary>
public static class DiagnosticsService
{
    /// <summary>The newest error/warning events across the System + Application logs (Level 1–3), most recent
    /// first. Capped at <paramref name="count"/> per log.</summary>
    public static IReadOnlyList<EventEntryDto> RecentErrors(int count = 50)
    {
        var list = new List<EventEntryDto>();
        foreach (var log in new[] { "System", "Application" }) Read(log, Math.Clamp(count, 1, 500), list);
        return list.OrderByDescending(e => e.Time).ToList();
    }

    private static void Read(string log, int count, List<EventEntryDto> list)
    {
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, "*[System[(Level=1 or Level=2 or Level=3)]]")
            { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int n = 0;
            for (EventRecord? r = reader.ReadEvent(); r is not null && n < count; r = reader.ReadEvent(), n++)
            {
                using (r)
                {
                    string? msg = null;
                    try { msg = r.FormatDescription(); } catch { }
                    if (msg is { Length: > 400 }) msg = msg[..400] + "…";
                    list.Add(new EventEntryDto(log, LevelName(r.Level), r.Id, r.ProviderName,
                        r.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss"), msg?.Replace("\r\n", " ").Trim()));
                }
            }
        }
        catch { }
    }

    /// <summary>General read-only event-log query. <paramref name="log"/> is any log/channel name — System,
    /// Application, Security, Setup, or an operational channel like "Microsoft-Windows-TaskScheduler/Operational".
    /// Filters (all optional): <paramref name="provider"/> (source name, exact), <paramref name="eventId"/>,
    /// <paramref name="level"/> as a severity FLOOR (critical|error|warning|information|all — that severity or
    /// worse; "information" also includes classic Level-0 Info), and <paramref name="sinceMinutes"/> (only events
    /// newer than now − N minutes). Newest first, capped at <paramref name="count"/> (1–1000).</summary>
    public static IReadOnlyList<EventEntryDto> Query(string log = "Application", string? provider = null,
        int? eventId = null, string level = "warning", int? sinceMinutes = null, int count = 50)
    {
        count = Math.Clamp(count, 1, 1000);
        var conds = new List<string>();
        string lv = LevelClause(level);
        if (lv.Length > 0) conds.Add(lv);
        if (eventId is int id) conds.Add($"EventID={id}");
        if (sinceMinutes is int m && m > 0) conds.Add($"TimeCreated[timediff(@SystemTime) <= {(long)m * 60_000}]");
        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (provider.Contains('\'')) throw new ArgumentException("provider must not contain a single quote");
            conds.Add($"Provider[@Name='{provider.Trim()}']");
        }
        string xpath = conds.Count > 0 ? $"*[System[{string.Join(" and ", conds)}]]" : "*";
        var list = new List<EventEntryDto>();
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int n = 0;
            for (EventRecord? r = reader.ReadEvent(); r is not null && n < count; r = reader.ReadEvent(), n++)
            {
                using (r)
                {
                    string? msg = null;
                    try { msg = r.FormatDescription(); } catch { }
                    if (msg is { Length: > 1000 }) msg = msg[..1000] + "…";
                    string lvlName; try { lvlName = r.LevelDisplayName ?? LevelName(r.Level); } catch { lvlName = LevelName(r.Level); }
                    list.Add(new EventEntryDto(log, lvlName, r.Id, r.ProviderName,
                        r.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss"), msg?.Replace("\r\n", " ").Trim()));
                }
            }
        }
        catch (EventLogNotFoundException) { throw new InvalidOperationException($"event log/channel '{log}' not found"); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException($"access denied reading '{log}' — the Security log needs elevation (SeSecurityPrivilege)"); }
        catch (EventLogException ex) { throw new InvalidOperationException($"cannot read '{log}': {ex.Message}"); }
        return list;
    }

    // Severity FLOOR → XPath Level clause (Windows levels: 1 Critical, 2 Error, 3 Warning, 4 Information, 5 Verbose;
    // classic System/Application logs tag Information as Level 0, so "information" includes 0 too).
    private static string LevelClause(string? level) => (level ?? "warning").Trim().ToLowerInvariant() switch
    {
        "critical" => "(Level=1)",
        "error" => "(Level=1 or Level=2)",
        "warning" => "(Level=1 or Level=2 or Level=3)",
        "information" or "info" => "(Level=0 or Level=1 or Level=2 or Level=3 or Level=4)",
        "verbose" or "all" or "" => "",
        _ => "(Level=1 or Level=2 or Level=3)"
    };

    private static string LevelName(byte? lvl) => lvl switch { 1 => "Critical", 2 => "Error", 3 => "Warning", 4 => "Information", 5 => "Verbose", 0 => "Information", _ => "?" };

    // ---- disk health: WMI drive status + SMART failure prediction ----
    public static IReadOnlyList<DiskHealthDto> DiskHealth()
    {
        var list = new List<DiskHealthDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Model, SerialNumber, Status FROM Win32_DiskDrive");
            foreach (ManagementObject o in s.Get())
                list.Add(new DiskHealthDto(o["Model"]?.ToString(), o["SerialNumber"]?.ToString()?.Trim(), o["Status"]?.ToString(), null));
        }
        catch { }
        // Overlay SMART predict-fail if readable (needs elevation for some drivers).
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            var predicts = s.Get().Cast<ManagementObject>().Select(o => o["PredictFailure"] as bool?).ToList();
            if (predicts.Count == list.Count)
                for (int i = 0; i < list.Count; i++) list[i] = list[i] with { PredictFailure = predicts[i] };
            else if (predicts.Count > 0 && list.Count > 0)
                list[0] = list[0] with { PredictFailure = predicts.Any(p => p == true) ? true : predicts.All(p => p == false) ? false : null };
        }
        catch { }
        return list;
    }
}
