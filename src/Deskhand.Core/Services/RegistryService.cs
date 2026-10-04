using Microsoft.Win32;

namespace Deskhand.Core.Services;

public record RegValueDto(string Name, string Kind, string? Value);
public record RegKeyDto(string Path, string? Hive, IReadOnlyList<string> SubKeys, IReadOnlyList<RegValueDto> Values, string? Error = null);
public record RegWriteResultDto(bool Ok, string Op, string Path, string? Name = null, string? Error = null);

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

    /// <summary>Registry WRITE is opt-in: set DESKHAND_ENABLE_REGISTRY_WRITE=1 (or true/yes/on) to allow it.
    /// Off by default because a bad write can break the OS.</summary>
    public static bool WriteEnabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DESKHAND_ENABLE_REGISTRY_WRITE")?.Trim().ToLowerInvariant();
            return v is "1" or "true" or "yes" or "on";
        }
    }

    /// <summary>Set (create-or-update) a value. kind: string|expandstring|dword|qword|multistring|binary
    /// (multistring splits value on newlines; binary reads value as hex). An empty name targets the (Default).</summary>
    public static RegWriteResultDto SetValue(string? path, string? name, string? value, string? kind)
        => Write("set", path, k =>
        {
            string n = (name ?? "").Trim().Equals("(Default)", StringComparison.OrdinalIgnoreCase) ? "" : (name ?? "").Trim();
            var (data, rvk) = ToRegData(value ?? "", kind);
            k.SetValue(n, data, rvk);
        }, name, writable: true, create: true);

    public static RegWriteResultDto CreateKey(string? path)
        => Write("create-key", path, _ => { }, null, writable: true, create: true);

    public static RegWriteResultDto DeleteValue(string? path, string? name)
        => Write("delete-value", path, k =>
        {
            string n = (name ?? "").Trim().Equals("(Default)", StringComparison.OrdinalIgnoreCase) ? "" : (name ?? "").Trim();
            k.DeleteValue(n, throwOnMissingValue: false);
        }, name, writable: true, create: false);

    public static RegWriteResultDto DeleteKey(string? path)
    {
        path = (path ?? "").Trim().Trim('\\');
        var (hiveName, sub) = Split(path);
        if (sub.Length == 0) return new RegWriteResultDto(false, "delete-key", path, Error: "Refusing to delete a hive root. Give a subkey path.");
        try
        {
            using var hive = OpenHive(hiveName);
            hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            return new RegWriteResultDto(true, "delete-key", path);
        }
        catch (Exception ex) { return new RegWriteResultDto(false, "delete-key", path, Error: ex.Message); }
    }

    private static RegWriteResultDto Write(string op, string? path, Action<RegistryKey> act, string? name, bool writable, bool create)
    {
        path = (path ?? "").Trim().Trim('\\');
        var (hiveName, sub) = Split(path);
        if (sub.Length == 0 && op != "create-key") return new RegWriteResultDto(false, op, path, name, "Give a full key path under a hive (e.g. HKCU\\Software\\Deskhand).");
        try
        {
            using var hive = OpenHive(hiveName);
            using var key = create ? hive.CreateSubKey(sub, writable: true) : hive.OpenSubKey(sub, writable: true);
            if (key is null) return new RegWriteResultDto(false, op, path, name, $"Key not found: {path}");
            act(key);
            return new RegWriteResultDto(true, op, path, name);
        }
        catch (UnauthorizedAccessException) { return new RegWriteResultDto(false, op, path, name, "Access denied — this key needs elevation."); }
        catch (System.Security.SecurityException) { return new RegWriteResultDto(false, op, path, name, "Access denied — this key needs elevation."); }
        catch (Exception ex) { return new RegWriteResultDto(false, op, path, name, ex.Message); }
    }

    private static (string hive, string sub) Split(string path)
    {
        int slash = path.IndexOf('\\');
        return slash < 0 ? (path, "") : (path[..slash], path[(slash + 1)..]);
    }

    private static (object data, RegistryValueKind kind) ToRegData(string value, string? kind) => (kind ?? "string").Trim().ToLowerInvariant() switch
    {
        "expandstring" or "expand_sz" or "reg_expand_sz" => (value, RegistryValueKind.ExpandString),
        "dword" or "reg_dword" => ((object)(int.TryParse(value, out var i) ? i : 0), RegistryValueKind.DWord),
        "qword" or "reg_qword" => ((object)(long.TryParse(value, out var l) ? l : 0L), RegistryValueKind.QWord),
        "multistring" or "multi_sz" or "reg_multi_sz" => (value.Replace("\r\n", "\n").Split('\n'), RegistryValueKind.MultiString),
        "binary" or "reg_binary" => ((object)HexToBytes(value), RegistryValueKind.Binary),
        _ => (value, RegistryValueKind.String),
    };

    private static byte[] HexToBytes(string s)
    {
        s = s.Replace(" ", "").Replace("-", "");
        if (s.Length % 2 != 0) return Array.Empty<byte>();
        try { return System.Convert.FromHexString(s); } catch { return Array.Empty<byte>(); }
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
