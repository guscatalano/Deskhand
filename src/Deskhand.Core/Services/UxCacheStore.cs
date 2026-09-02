using System.Text.Json;

namespace Deskhand.Core.Services;

/// <summary>Persists crawled UX maps keyed by an app signature (exe · window class · title) so an agent can
/// recall the layout of an app it has explored before instead of re-crawling. Files live under temp; small JSON.</summary>
public static class UxCacheStore
{
    private static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskhand-uxcache");
    private static readonly JsonSerializerOptions J = new() { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static void Save(string appKey, object map)
    {
        try { System.IO.Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(PathFor(appKey), JsonSerializer.Serialize(map, J)); }
        catch { /* cache is best-effort */ }
    }

    public static JsonElement? Load(string appKey)
    {
        try
        {
            var p = PathFor(appKey);
            if (!System.IO.File.Exists(p)) return null;
            return JsonSerializer.Deserialize<JsonElement>(System.IO.File.ReadAllText(p));
        }
        catch { return null; }
    }

    public static IReadOnlyList<string> List()
    {
        try { return System.IO.Directory.Exists(Dir) ? System.IO.Directory.GetFiles(Dir, "*.json").Select(System.IO.Path.GetFileNameWithoutExtension).OfType<string>().ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    public static bool Delete(string appKey)
    {
        try { var p = PathFor(appKey); if (System.IO.File.Exists(p)) { System.IO.File.Delete(p); return true; } } catch { }
        return false;
    }

    private static string PathFor(string appKey)
    {
        // Sanitize the key into a filename.
        var safe = new string((appKey ?? "app").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (safe.Length > 120) safe = safe[..120];
        return System.IO.Path.Combine(Dir, safe + ".json");
    }
}
