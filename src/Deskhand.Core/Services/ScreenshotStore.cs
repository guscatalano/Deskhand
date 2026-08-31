using Deskhand.Core.Governance;

namespace Deskhand.Core.Services;

public record ScreenshotSaveDto(string FileName, string File, long SizeBytes, string Format, string Ts);

/// <summary>
/// Saves capture bytes to a predefined folder on the machine (so a caller can choose "save on the box + get
/// a link" instead of the image inline). Same discipline as recordings/dumps: audited on write, and
/// auto-deleted after <see cref="RetentionHours"/> hours. Directory: %LOCALAPPDATA%\Deskhand\screenshots.
/// </summary>
public sealed class ScreenshotStore : IDisposable
{
    public const int RetentionHours = 24;

    private readonly string _dir;
    private readonly AuditLog? _audit;
    private readonly System.Threading.Timer _janitor;

    public ScreenshotStore(AuditLog? audit = null)
    {
        _audit = audit;
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskhand", "screenshots");
        System.IO.Directory.CreateDirectory(_dir);
        CleanupExpired();
        _janitor = new System.Threading.Timer(_ => CleanupExpired(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    public string Directory => _dir;

    public ScreenshotSaveDto Save(byte[] bytes, string format)
    {
        var ext = format == "jpeg" ? "jpg" : "png";
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.{ext}";
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        _audit?.Record("screenshot_saved", path, $"{bytes.LongLength}B (auto-delete {RetentionHours}h)");
        return new ScreenshotSaveDto(name, path, bytes.LongLength, format, DateTimeOffset.Now.ToString("o"));
    }

    public string PathFor(string fileName) => Path.Combine(_dir, Path.GetFileName(fileName));

    public IEnumerable<object> List()
    {
        try
        {
            return System.IO.Directory.EnumerateFiles(_dir)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => (object)new { name = f.Name, sizeBytes = f.Length, saved = f.LastWriteTime.ToString("o") })
                .ToList();
        }
        catch { return Array.Empty<object>(); }
    }

    private void CleanupExpired()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir))
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) { File.Delete(f); _audit?.Record("screenshot_expired", Path.GetFileName(f), $"deleted (>{RetentionHours}h)"); } }
                catch { }
        }
        catch { }
    }

    public void Dispose() => _janitor.Dispose();
}
