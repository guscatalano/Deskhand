using Microsoft.Win32;

namespace Deskhand.Core.Services;

public record RegValueDto(string Name, string Kind, string? Value);
public record RegKeyDto(string Path, string? Hive, IReadOnlyList<string> SubKeys, IReadOnlyList<RegValueDto> Values, string? Error = null);

/// <summary>
/// Read-only Windows Registry browsing: list the subkeys and values of a key. Hives are addressed by short
/// name (HKLM/HKCU/HKCR/HKU/HKCC) or full name; the rest of the path is backslash-separated. Reading is
/// bounded to what the host's token allows — some keys (e.g. HKLM\SECURITY) need elevation and return a
/// clear access error rather than throwing.
/// </summary>
public static class RegistryService
{
    private const int MaxBinaryBytes = 512;   // truncate huge REG_BINARY blobs for display

    public static readonly string[] Hives = { "HKLM", "HKCU", "HKCR", "HKU", "HKCC" };

    private static RegistryKey OpenHive(string h) => h.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Registry.Users,
        "HKCC" or "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
        _ => throw new ArgumentException($"Unknown hive '{h}'. Use HKLM, HKCU, HKCR, HKU or HKCC."),
    };

    /// <summary>Browse a key. <paramref name="path"/> is "HKLM" or "HKLM\SOFTWARE\Microsoft\…". Empty/null
    /// lists the hive roots.</summary>
    public static RegKeyDto Browse(string? path)
    {
        path = (path ?? "").Trim().Trim('\\');
        if (path.Length == 0)
            return new RegKeyDto("", null, Hives, Array.Empty<RegValueDto>());   // the root: the hives

        int slash = path.IndexOf('\\');
        string hiveName = slash < 0 ? path : path[..slash];
        string sub = slash < 0 ? "" : path[(slash + 1)..];

        RegistryKey hive;
        try { hive = OpenHive(hiveName); }
        catch (ArgumentException ex) { return new RegKeyDto(path, null, Array.Empty<string>(), Array.Empty<RegValueDto>(), ex.Message); }

        try
        {
            using var key = sub.Length == 0 ? hive : hive.OpenSubKey(sub);
            if (key is null) return new RegKeyDto(path, hiveName, Array.Empty<string>(), Array.Empty<RegValueDto>(), $"Key not found: {path}");

            List<string> subKeys;
            try { subKeys = key.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { subKeys = new(); }

            var values = new List<RegValueDto>();
            foreach (var n in SafeNames(key))
            {
                try
                {
                    var kind = key.GetValueKind(n);
                    var raw = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    values.Add(new RegValueDto(n.Length == 0 ? "(Default)" : n, kind.ToString(), Stringify(raw, kind)));
                }
                catch { values.Add(new RegValueDto(n.Length == 0 ? "(Default)" : n, "Unknown", "<unreadable>")); }
            }

            return new RegKeyDto(hiveName + (sub.Length > 0 ? "\\" + sub : ""), hiveName, subKeys, values);
        }
        catch (System.Security.SecurityException) { return new RegKeyDto(path, hiveName, Array.Empty<string>(), Array.Empty<RegValueDto>(), "Access denied — this key needs elevation."); }
        catch (UnauthorizedAccessException) { return new RegKeyDto(path, hiveName, Array.Empty<string>(), Array.Empty<RegValueDto>(), "Access denied — this key needs elevation."); }
    }

    private static string[] SafeNames(RegistryKey key) { try { return key.GetValueNames(); } catch { return Array.Empty<string>(); } }

    private static string? Stringify(object? v, RegistryValueKind kind) => v switch
    {
        null => null,
        string s => s,
        int i => i.ToString(),
        long l => l.ToString(),
        string[] arr => string.Join(" | ", arr),
        byte[] b => (b.Length > MaxBinaryBytes ? Convert.ToHexString(b, 0, MaxBinaryBytes) + $"… (+{b.Length - MaxBinaryBytes} bytes)" : Convert.ToHexString(b)),
        _ => v.ToString(),
    };
}
