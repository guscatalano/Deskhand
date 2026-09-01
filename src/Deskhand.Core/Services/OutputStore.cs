using System.Text;

namespace Deskhand.Core.Services;

public record OutputSliceDto(string OutputId, int Offset, int Limit, int TotalChars, int NextOffset, bool Done, string Text, string? Error = null);

/// <summary>
/// A spill buffer for over-budget tool results. When a tool result would exceed the size a client's tool
/// channel can carry, the full text is written here and the caller gets back a small envelope (a head preview
/// plus an id/URL). The caller then pages the full text with <see cref="ReadSlice"/> (in-channel) or downloads
/// it from <c>/outputs/{id}</c>. This means Deskhand never emits an oversized blob that a client would truncate
/// mid-token (which corrupts JSON/base64) — it bounds its own output and points at the rest.
/// </summary>
public static class OutputStore
{
    private static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskhand-outputs");

    public const int FloorChars = 8_000;
    public const int CeilingChars = 20_000_000;
    private static volatile int _override;   // 0 = use env/default; set at runtime by a client that knows its budget

    /// <summary>Per-result char budget for MCP tool output. A runtime override (set via <see cref="SetBudget"/>)
    /// wins; otherwise DESKHAND_MAX_TOOL_CHARS; otherwise 200,000. Floored at 8,000 so it can't be uselessly small.</summary>
    public static int MaxChars
    {
        get
        {
            if (_override > 0) return _override;
            var v = Environment.GetEnvironmentVariable("DESKHAND_MAX_TOOL_CHARS");
            return int.TryParse(v, out var n) && n >= FloorChars ? n : 200_000;
        }
    }

    /// <summary>Whether the current budget comes from a runtime override vs env/default.</summary>
    public static string BudgetSource => _override > 0 ? "runtime"
        : (int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_MAX_TOOL_CHARS"), out var n) && n >= FloorChars ? "env" : "default");

    /// <summary>Set the runtime char budget for tool output (clamped to [8k, 20M]); pass 0 or less to clear the
    /// override and fall back to env/default. Returns the now-effective budget.</summary>
    public static int SetBudget(int chars)
    {
        _override = chars <= 0 ? 0 : Math.Clamp(chars, FloorChars, CeilingChars);
        return MaxChars;
    }

    /// <summary>Save an over-budget result; returns its id. Old spills (>6h) are swept on write.</summary>
    public static string Save(string text)
    {
        System.IO.Directory.CreateDirectory(Dir);
        Sweep();
        string id = "out_" + Guid.NewGuid().ToString("N")[..12];
        System.IO.File.WriteAllText(PathFor(id), text, new UTF8Encoding(false));
        return id;
    }

    public static string PathFor(string id)
    {
        // Guard against path traversal — ids are our own tokens, but never trust an id blindly.
        var safe = System.IO.Path.GetFileName(id ?? "");
        return System.IO.Path.Combine(Dir, safe + ".txt");
    }

    public static OutputSliceDto ReadSlice(string id, int offset, int limit)
    {
        try
        {
            var path = PathFor(id);
            if (!System.IO.File.Exists(path)) return new OutputSliceDto(id, offset, limit, 0, offset, true, "", "No such output id (it may have expired).");
            string all = System.IO.File.ReadAllText(path);
            offset = Math.Clamp(offset, 0, all.Length);
            limit = Math.Clamp(limit <= 0 ? MaxChars : limit, 1, MaxChars);
            int take = Math.Min(limit, all.Length - offset);
            string slice = all.Substring(offset, take);
            int next = offset + take;
            return new OutputSliceDto(id, offset, limit, all.Length, next, next >= all.Length, slice);
        }
        catch (Exception ex) { return new OutputSliceDto(id, offset, limit, 0, offset, true, "", ex.Message); }
    }

    private static void Sweep()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var f in System.IO.Directory.GetFiles(Dir, "out_*.txt"))
                try { if (System.IO.File.GetLastWriteTimeUtc(f) < cutoff) System.IO.File.Delete(f); } catch { }
        }
        catch { }
    }
}
