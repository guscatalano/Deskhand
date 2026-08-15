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
    }

    /// <summary>Short content hash for logging a capture without storing the image.</summary>
    public static string HashImage(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
}
