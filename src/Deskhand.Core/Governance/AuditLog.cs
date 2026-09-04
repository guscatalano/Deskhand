using System.Security.Cryptography;
using System.Text.Json;

namespace Deskhand.Core.Governance;

/// <summary>
/// Append-only audit trail. Every action taken through the governed backend is written as one
/// JSON line to a dated file under %LOCALAPPDATA%\Deskhand\audit (or a supplied directory), so
/// there is a durable record of what was read, driven, and captured — and by whom.
/// </summary>
public sealed class AuditLog
{
    private readonly object _lock = new();
    public string Directory { get; }

    /// <summary>Raised for every recorded action (action, detail, status). Lets the episode recorder capture a
    /// trajectory step per governed action without threading a dependency through every call site. Handlers
    /// must not throw and should be cheap/non-reentrant (they run inline on the acting thread).</summary>
    public event Action<string, string?, string>? Recorded;

    public AuditLog(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskhand", "audit");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public void Record(string action, string? detail, string status)
    {
        var entry = new
        {
            ts = DateTimeOffset.Now.ToString("o"),
            user = Environment.UserName,
            action,
            detail,
            status,
        };
        string line = JsonSerializer.Serialize(entry);
        string file = Path.Combine(Directory, $"audit-{DateTime.Now:yyyyMMdd}.jsonl");
        lock (_lock)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
        try { Recorded?.Invoke(action, detail, status); } catch { /* a subscriber must never break auditing */ }
    }

    /// <summary>Short content hash for logging a capture without storing the image.</summary>
    public static string HashImage(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
}
