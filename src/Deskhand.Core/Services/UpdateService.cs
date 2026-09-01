using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Deskhand.Core.Services;

public record UpdateCheckDto(
    string Current, string? Latest, bool UpdateAvailable, string? Name, string? Notes,
    string? PublishedAt, string? AssetName, long AssetSize, bool Enabled, string? Error = null);

public record UpdateApplyDto(bool Ok, string? From, string? To, string? Message, string? Error = null);

/// <summary>
/// Self-update against the project's GitHub Releases. <see cref="CheckAsync"/> is read-only (compares the
/// running <see cref="BuildInfo.Version"/> to the latest release tag). <see cref="ApplyAsync"/> downloads the
/// self-contained <c>deskhand.zip</c>, stages it, and hands off to a tiny detached updater that stops this
/// process, copies the new files over the install directory, and relaunches — so it only works on a
/// zip/self-contained install, and it runs downloaded code, hence it's opt-in (<c>DESKHAND_ENABLE_SELF_UPDATE</c>).
/// </summary>
public static class UpdateService
{
    private const string Asset = "deskhand.zip";
    private static readonly HttpClient Http = CreateClient();

    public static bool Enabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DESKHAND_ENABLE_SELF_UPDATE")?.Trim().ToLowerInvariant();
            return v is "1" or "true" or "yes" or "on";
        }
    }

    /// <summary>The most recent check result, cached for a fast /update/status (populated at startup).</summary>
    public static UpdateCheckDto? Cached { get; private set; }

    public static async Task<UpdateCheckDto> CheckAsync()
    {
        string cur = BuildInfo.Version;
        try
        {
            var rel = await LatestReleaseAsync();
            if (rel is null)
                return Cache(new UpdateCheckDto(cur, null, false, null, null, null, null, 0, Enabled, "Could not reach GitHub Releases."));

            var (tag, name, notes, published, assetName, assetSize) = rel.Value;
            string latest = tag.TrimStart('v', 'V');
            bool newer = CompareVersions(latest, cur) > 0;
            return Cache(new UpdateCheckDto(cur, latest, newer, name, notes, published, assetName, assetSize, Enabled));
        }
        catch (Exception ex) { return Cache(new UpdateCheckDto(cur, null, false, null, null, null, null, 0, Enabled, ex.Message)); }
    }

    private static UpdateCheckDto Cache(UpdateCheckDto d) { Cached = d; return d; }

    public static async Task<UpdateApplyDto> ApplyAsync()
    {
        if (!Enabled)
            return new UpdateApplyDto(false, BuildInfo.Version, null, null, "Self-update is disabled. Set DESKHAND_ENABLE_SELF_UPDATE=1.");
        try
        {
            var rel = await LatestReleaseAsync();
            if (rel is null) return new UpdateApplyDto(false, BuildInfo.Version, null, null, "Could not reach GitHub Releases.");
            var (tag, _, _, _, _, _) = rel.Value;
            string latest = tag.TrimStart('v', 'V');
            if (CompareVersions(latest, BuildInfo.Version) <= 0)
                return new UpdateApplyDto(true, BuildInfo.Version, latest, "Already up to date; nothing to do.");

            string? url = await AssetUrlAsync();
            if (url is null) return new UpdateApplyDto(false, BuildInfo.Version, latest, null, $"Release {tag} has no {Asset} asset.");

            string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string? exe = SafeExePath();
            if (exe is null) return new UpdateApplyDto(false, BuildInfo.Version, latest, null, "Could not resolve the running executable path.");

            string work = Path.Combine(Path.GetTempPath(), "deskhand-update-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, Asset);
            string staging = Path.Combine(work, "staging");

            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs);
            }
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            if (!File.Exists(Path.Combine(staging, Path.GetFileName(exe))))
                return new UpdateApplyDto(false, BuildInfo.Version, latest, null, $"Downloaded {Asset} doesn't contain {Path.GetFileName(exe)} — not a matching build.");

            // Detached updater: wait a beat, stop THIS process, copy staged files over the install dir, relaunch.
            string script = Path.Combine(work, "apply.ps1");
            await File.WriteAllTextAsync(script, UpdaterScript());
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                            $"-AppPid {Environment.ProcessId} -Staging \"{staging}\" -AppDir \"{appDir}\" -Exe \"{exe}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return new UpdateApplyDto(true, BuildInfo.Version, latest,
                $"Downloaded {latest}. The server will stop and relaunch on the new version in a few seconds.");
        }
        catch (Exception ex) { return new UpdateApplyDto(false, BuildInfo.Version, null, null, ex.Message); }
    }

    private static string UpdaterScript() => """
        param([int]$AppPid, [string]$Staging, [string]$AppDir, [string]$Exe)
        Start-Sleep -Seconds 1
        try { Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Seconds 2
        try {
          Copy-Item -Path (Join-Path $Staging '*') -Destination $AppDir -Recurse -Force
          Start-Process -FilePath $Exe -WorkingDirectory $AppDir
        } catch {
          # Leave the old install intact on failure; the operator can extract the staged zip manually.
        }
        """;

    private static async Task<(string tag, string? name, string? notes, string? published, string? asset, long size)?> LatestReleaseAsync()
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{BuildInfo.Repository}/releases/latest");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        string? name = Str(r, "name");
        string? notes = Str(r, "body");
        string? published = Str(r, "published_at");
        string? asset = null; long size = 0;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(Str(a, "name"), Asset, StringComparison.OrdinalIgnoreCase))
                { asset = Asset; size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0; break; }
        return (tag, name, notes, published, asset, size);
    }

    private static async Task<string?> AssetUrlAsync()
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{BuildInfo.Repository}/releases/latest");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(Str(a, "name"), Asset, StringComparison.OrdinalIgnoreCase))
                    return Str(a, "browser_download_url");
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? SafeExePath()
    {
        try { return Process.GetCurrentProcess().MainModule?.FileName; } catch { return null; }
    }

    // Numeric dotted compare; ignores any pre-release suffix. >0 means a is newer than b.
    public static int CompareVersions(string a, string b)
    {
        int[] pa = Parse(a), pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;

        static int[] Parse(string v) => (v ?? "").Split('-', '+')[0].Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Deskhand-Updater", BuildInfo.Version));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }
}
