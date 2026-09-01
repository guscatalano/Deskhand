using System.Net.Http;

namespace Deskhand.Core.Services;

public record FetchResultDto(bool Ok, string? Url, string? Path, long Bytes, string? ContentType, string? Error = null);

/// <summary>Download a URL to a file on this machine (get an installer/asset onto the target). http/https only,
/// size-capped, writes to an explicit path (or a temp file). This performs an OUTBOUND request from the box.</summary>
public static class FetchService
{
    private const long DefaultMaxBytes = 500L * 1024 * 1024;   // 500 MB cap
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task<FetchResultDto> DownloadAsync(string? url, string? destPath, long? maxBytes)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new FetchResultDto(false, url, null, 0, null, "Provide an absolute http/https URL.");
        long cap = maxBytes is > 0 ? Math.Min(maxBytes.Value, DefaultMaxBytes) : DefaultMaxBytes;

        string path;
        try { path = ResolvePath(destPath, uri); }
        catch (Exception ex) { return new FetchResultDto(false, url, destPath, 0, null, "Bad destination path: " + ex.Message); }

        try
        {
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return new FetchResultDto(false, url, path, 0, null, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            string? ctype = resp.Content.Headers.ContentType?.ToString();
            long len = resp.Content.Headers.ContentLength ?? -1;
            if (len > cap) return new FetchResultDto(false, url, path, 0, ctype, $"Content-Length {len} exceeds the {cap}-byte cap.");

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            long written = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    written += n;
                    if (written > cap) { dst.Close(); TryDelete(path); return new FetchResultDto(false, url, path, written, ctype, $"Download exceeded the {cap}-byte cap."); }
                    await dst.WriteAsync(buf.AsMemory(0, n));
                }
            }
            return new FetchResultDto(true, url, path, written, ctype);
        }
        catch (Exception ex) { return new FetchResultDto(false, url, path, 0, null, ex.Message); }
    }

    private static string ResolvePath(string? destPath, Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(destPath))
        {
            var p = Path.GetFullPath(destPath.Trim().Trim('"'));
            // If a directory was given, keep the URL's filename inside it.
            if (System.IO.Directory.Exists(p) || destPath!.EndsWith('\\') || destPath.EndsWith('/'))
                return Path.Combine(p, SafeName(uri));
            return p;
        }
        return Path.Combine(Path.GetTempPath(), SafeName(uri));
    }

    private static string SafeName(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "download-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
