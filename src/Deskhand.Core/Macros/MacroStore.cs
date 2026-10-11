using System.Text.Json;
using Deskhand.Core.Fleet;

namespace Deskhand.Core.Macros;

public sealed record SavedMacroDto(string Name, int Steps, int Asserts, string SavedUtc);

/// <summary>
/// Durable, named macros on disk (<c>%LOCALAPPDATA%\Deskhand\macros\&lt;name&gt;.json</c>) — so a recorded,
/// assertion-bearing session survives a restart and can be replayed later as a regression test. Names are
/// sanitized to a safe basename (no path traversal). Modelled on the other on-disk stores.
/// </summary>
public static class MacroStore
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskhand", "macros");

    static MacroStore() { try { System.IO.Directory.CreateDirectory(_dir); } catch { } }

    public static string Directory => _dir;

    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = Path.GetFileNameWithoutExtension(name.Trim());
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Length == 0 ? null : n[..Math.Min(n.Length, 100)];
    }

    private static string? PathFor(string? name) => SafeName(name) is { } n ? Path.Combine(_dir, n + ".json") : null;

    /// <summary>Save a macro under a name (overwrites). Returns the sanitized name actually used, or null on bad input.</summary>
    public static string? Save(string name, Macro macro)
    {
        if (SafeName(name) is not { } safe) return null;
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, safe + ".json"), JsonSerializer.Serialize(macro, FleetJson.Options));
            return safe;
        }
        catch { return null; }
    }

    public static Macro? Load(string name)
    {
        if (PathFor(name) is not { } p || !File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<Macro>(File.ReadAllText(p), FleetJson.Options); }
        catch { return null; }
    }

    public static IReadOnlyList<SavedMacroDto> List()
    {
        var list = new List<SavedMacroDto>();
        try
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<Macro>(File.ReadAllText(f), FleetJson.Options);
                    int steps = m?.Steps.Count ?? 0;
                    int asserts = m?.Steps.Count(s => s.Kind is "assert" or "wait") ?? 0;
                    list.Add(new SavedMacroDto(Path.GetFileNameWithoutExtension(f), steps, asserts,
                        File.GetLastWriteTimeUtc(f).ToString("o")));
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(m => m.SavedUtc).ToList();
    }

    public static bool Delete(string name)
    {
        if (PathFor(name) is not { } p || !File.Exists(p)) return false;
        try { File.Delete(p); return true; } catch { return false; }
    }
}
