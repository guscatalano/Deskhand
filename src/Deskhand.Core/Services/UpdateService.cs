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
/// Self-update from a configurable source. By default it checks the project's <b>GitHub Releases</b>; set
/// <c>DESKHAND_UPDATE_MANIFEST_URL</c> to point a whole fleet at one internal source instead (see
/// <see cref="ManifestUrl"/>) — faster, no per-box GitHub dependency, and it works where GitHub is unreachable.
/// <see cref="CheckAsync"/> is read-only (compares the running <see cref="BuildInfo.Version"/> to the latest the
/// source advertises). <see cref="ApplyAsync"/> downloads the self-contained <c>deskhand.zip</c>, verifies it
/// against the sha256 digest and size the source publishes (failing closed on any mismatch — an incomplete or
/// tampered download), stages it, and hands off to a tiny detached updater that stops the service(s)/process,
/// copies the new files over the install directory, and relaunches — so it only works on a zip/self-contained
/// install, and it runs downloaded code, hence it's opt-in (<c>DESKHAND_ENABLE_SELF_UPDATE</c>).
/// </summary>
public static class UpdateService
{
    private const string Asset = "deskhand.zip";
    private static readonly HttpClient Http = CreateClient();

    public static bool Enabled
    {
        // Self-update is allowed when explicitly enabled, OR when auto-update is on (auto-update implies it).
        get => Flag("DESKHAND_ENABLE_SELF_UPDATE") || AutoUpdate;
    }

    /// <summary>Auto-update mode: periodically check GitHub Releases and apply a newer version on its own
    /// (<c>DESKHAND_AUTO_UPDATE=1</c>). Implies <see cref="Enabled"/>.</summary>
    public static bool AutoUpdate => Flag("DESKHAND_AUTO_UPDATE");

    /// <summary>How often auto-update checks, in minutes (<c>DESKHAND_AUTO_UPDATE_INTERVAL_MIN</c>, default 60;
    /// clamped to 5 min – 1 week so it can't hammer the GitHub API).</summary>
    public static int AutoUpdateIntervalMinutes
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DESKHAND_AUTO_UPDATE_INTERVAL_MIN");
            return int.TryParse(v, out var m) ? Math.Clamp(m, 5, 7 * 24 * 60) : 60;
        }
    }

    private static bool Flag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant();
        return v is "1" or "true" or "yes" or "on";
    }

    /// <summary>The most recent check result, cached for a fast /update/status (populated at startup).</summary>
    public static UpdateCheckDto? Cached { get; private set; }

    // ---- failed-apply back-off ----
    // A self-update that can't complete (disk full, flaky network, a file still locked) would otherwise
    // re-download the ~260 MB zip every interval forever. We record each apply attempt per target version;
    // after a few failures at the same version the auto-updater holds off until a back-off window passes. A
    // successful update (the new version is now running) clears the record on the next check.
    private sealed record UpdateState(string? Target, int Attempts, DateTime LastUtc);

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskhand", "update-state.json");

    private static UpdateState ReadState()
    {
        try { return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath)) ?? new(null, 0, default); }
        catch { return new(null, 0, default); }
    }

    private static void WriteState(UpdateState s)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!); File.WriteAllText(StatePath, JsonSerializer.Serialize(s)); }
        catch { /* best-effort */ }
    }

    private static void ClearStateIfUpdated()
    {
        var s = ReadState();
        if (s.Target is not null && CompareVersions(BuildInfo.Version, s.Target) >= 0)
            try { File.Delete(StatePath); } catch { }
    }

    private const int MaxAutoAttempts = 3;
    private static readonly TimeSpan BackoffWindow = TimeSpan.FromHours(24);

    /// <summary>Whether the auto-updater should attempt <paramref name="latest"/> now, or hold off because it has
    /// already failed to apply this same version several times recently (so it doesn't re-download every interval).
    /// A manual apply ignores this.</summary>
    public static bool ShouldAutoApply(string latest)
    {
        var s = ReadState();
        return !(s.Target == latest && s.Attempts >= MaxAutoAttempts && DateTime.UtcNow - s.LastUtc < BackoffWindow);
    }

    private static void RecordApplyAttempt(string latest)
    {
        var s = ReadState();
        WriteState(new UpdateState(latest, s.Target == latest ? s.Attempts + 1 : 1, DateTime.UtcNow));
    }

    /// <summary>Back-off state for /update/status: the version recent apply attempts targeted, how many
    /// consecutive attempts have been made at it, and whether the auto-updater is currently holding off (so an
    /// operator can see WHY a reachable box isn't advancing instead of guessing). Null target = no failed run.</summary>
    public static (string? target, int attempts, bool holdingOff) BackoffStatus()
    {
        var s = ReadState();
        bool holding = s.Target is not null && s.Attempts >= MaxAutoAttempts && DateTime.UtcNow - s.LastUtc < BackoffWindow;
        return (s.Target, s.Attempts, holding);
    }

    public static async Task<UpdateCheckDto> CheckAsync()
    {
        _ = Task.Run(SweepStaleWorkDirs);   // opportunistically clear leftover staging folders (non-blocking)
        ClearStateIfUpdated();              // a prior update that landed clears its back-off record
        string cur = BuildInfo.Version;
        try
        {
            var rel = await ResolveLatestAsync();
            if (rel is null)
                return Cache(new UpdateCheckDto(cur, null, false, null, null, null, null, 0, Enabled, $"Could not reach the update source ({Source})."));

            var v = rel.Value;
            bool newer = CompareVersions(v.Version, cur) > 0;
            return Cache(new UpdateCheckDto(cur, v.Version, newer, v.Name, v.Notes, v.Published, v.Url is null ? null : Asset, v.Size, Enabled));
        }
        catch (Exception ex) { return Cache(new UpdateCheckDto(cur, null, false, null, null, null, null, 0, Enabled, ex.Message)); }
    }

    private static UpdateCheckDto Cache(UpdateCheckDto d) { Cached = d; return d; }

    public static async Task<UpdateApplyDto> ApplyAsync()
    {
        if (!Enabled)
            return new UpdateApplyDto(false, BuildInfo.Version, null, null, "Self-update is disabled. Set DESKHAND_ENABLE_SELF_UPDATE=1.");
        SweepStaleWorkDirs();   // clear leftovers from prior (incl. failed) applies before staging a fresh one
        try
        {
            var rel = await ResolveLatestAsync();
            if (rel is null) return new UpdateApplyDto(false, BuildInfo.Version, null, null, $"Could not reach the update source ({Source}).");
            var v = rel.Value;
            string latest = v.Version;
            if (CompareVersions(latest, BuildInfo.Version) <= 0)
                return new UpdateApplyDto(true, BuildInfo.Version, latest, "Already up to date; nothing to do.");
            if (string.IsNullOrEmpty(v.Url))
                return new UpdateApplyDto(false, BuildInfo.Version, latest, null, $"The update source has version {latest} but no {Asset} download URL.");
            string url = v.Url; string? expectedSha = v.Sha256; long expectedSize = v.Size;

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

            // Verify the download before we trust its contents — this path extracts and RUNS downloaded code.
            // Check against the size and sha256 the source publishes for the asset, and fail closed on any mismatch
            // (an incomplete download, or a tampered one). If the source gave no digest we fall through to the
            // zip-validity + exe-presence checks below.
            long gotSize = new FileInfo(zipPath).Length;
            if (expectedSize > 0 && gotSize != expectedSize)
                return new UpdateApplyDto(false, BuildInfo.Version, latest, null,
                    $"Downloaded {Asset} is {gotSize:N0} bytes, expected {expectedSize:N0} — aborting (incomplete or tampered download).");
            if (expectedSha is not null)
            {
                string gotSha = await Sha256HexAsync(zipPath);
                if (!string.Equals(gotSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                    return new UpdateApplyDto(false, BuildInfo.Version, latest, null,
                        $"Downloaded {Asset} sha256 {gotSha} does not match the published {expectedSha} — aborting (corrupt or tampered download).");
            }

            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            if (!File.Exists(Path.Combine(staging, Path.GetFileName(exe))))
                return new UpdateApplyDto(false, BuildInfo.Version, latest, null, $"Downloaded {Asset} doesn't contain {Path.GetFileName(exe)} — not a matching build.");

            // Record the attempt so a repeatedly-failing apply backs off instead of re-downloading hourly; a
            // successful update clears this on the next check (ClearStateIfUpdated), once the new version runs.
            RecordApplyAttempt(latest);

            // Detached updater: wait a beat, stop THIS process (or the services), copy staged files over the
            // install dir, relaunch.
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
        $ErrorActionPreference = 'SilentlyContinue'
        Start-Sleep -Seconds 1

        function Stop-Svc($n) {
          if (Get-Service $n -ErrorAction SilentlyContinue) {
            & sc.exe stop $n | Out-Null
            for ($i = 0; $i -lt 40; $i++) { if ((Get-Service $n).Status -eq 'Stopped') { break }; Start-Sleep -Milliseconds 500 }
          }
        }

        # A service install (the hybrid default, or UseWindowsService) can't be updated by killing one PID:
        # the SCM restarts the service on a crash, and the launcher + per-session agents keep their binaries
        # locked. So GRACEFULLY stop the services (sc stop doesn't trigger the SCM's restart action), kill any
        # lingering Deskhand processes to release file handles, copy, then start the services back up.
        if (Get-Service Deskhand -ErrorAction SilentlyContinue) {
          Stop-Svc 'DeskhandAgents'
          Stop-Svc 'DeskhandLauncher'
          Stop-Svc 'Deskhand'
          Get-Process deskhand-agent,deskhand-launcher,deskhand-http,deskhand-secure,deskhand-broker,deskhand-uia -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
          Start-Sleep -Seconds 2
          for ($i = 0; $i -lt 5; $i++) {
            try { Copy-Item -Path (Join-Path $Staging '*') -Destination $AppDir -Recurse -Force -ErrorAction Stop; break }
            catch { Start-Sleep -Seconds 2 }   # a handle may be slow to release; retry
          }
          if (Get-Service Deskhand -ErrorAction SilentlyContinue)       { & sc.exe start Deskhand | Out-Null }
          if (Get-Service DeskhandAgents -ErrorAction SilentlyContinue) { & sc.exe start DeskhandAgents | Out-Null }
          if (Get-Service DeskhandLauncher -ErrorAction SilentlyContinue) { & sc.exe start DeskhandLauncher | Out-Null }
        } else {
          # Plain self-contained install: stop this process and relaunch the exe directly.
          try { Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue } catch {}
          Start-Sleep -Seconds 2
          try {
            Copy-Item -Path (Join-Path $Staging '*') -Destination $AppDir -Recurse -Force
            Start-Process -FilePath $Exe -WorkingDirectory $AppDir
          } catch {
            # Leave the old install intact on failure; the operator can extract the staged zip manually.
          }
        }
        # Delete the staging work dir (the ~260 MB zip + its extraction) so updates don't fill the drive.
        # Deletes staging + zip even if the running .ps1 itself can't be removed; the remnant is swept next check.
        try { Remove-Item -Path (Split-Path -Parent $Staging) -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        """;

    /// <summary>Delete leftover <c>%TEMP%\deskhand-update-*</c> staging folders from past updates. Each holds
    /// the downloaded <c>deskhand.zip</c> (~260 MB) plus its extraction, so a failed/interrupted apply — or an
    /// older build that never cleaned up — can quietly fill the drive. The updater script removes its own folder
    /// after a successful apply; this is the safety net that also clears an accumulated backlog. Skips anything
    /// touched in the last 10 min so it can't race an in-flight apply's handoff. Best-effort; never throws.</summary>
    public static void SweepStaleWorkDirs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "deskhand-update-*"))
            {
                try { if (Directory.GetLastWriteTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true); }
                catch { /* in use, partially gone, or racing another delete — skip it */ }
            }
        }
        catch { /* temp not enumerable — ignore */ }
    }

    // ---- update source (GitHub, or a custom manifest) ----

    /// <summary>A custom update source: the URL of a small manifest JSON that points at a <c>deskhand.zip</c>
    /// anywhere (an internal mirror, the controller's payload server, S3…). Set via
    /// <c>DESKHAND_UPDATE_MANIFEST_URL</c>. When set, it fully replaces the GitHub Releases check — so a fleet
    /// updates from ONE internal source instead of every box hitting GitHub, and boxes without GitHub reachability
    /// can still update. Manifest shape: <c>{"version":"0.2.108","url":"https://…/deskhand.zip",
    /// "sha256":"&lt;hex&gt;","size":274198858,"name":"…","notes":"…"}</c> (sha256/size optional but recommended —
    /// the download is verified against them, failing closed on mismatch).</summary>
    public static string? ManifestUrl =>
        Environment.GetEnvironmentVariable("DESKHAND_UPDATE_MANIFEST_URL")?.Trim() is { Length: > 0 } u ? u : null;

    /// <summary>Human-readable update source for /update/status (the manifest URL, or the GitHub repo).</summary>
    public static string Source => ManifestUrl ?? $"github:{BuildInfo.Repository}";

    private readonly record struct ReleaseInfo(string Version, string? Url, string? Sha256, long Size, string? Name, string? Notes, string? Published);

    /// <summary>Resolve the latest available release from the configured source (custom manifest, else GitHub).
    /// Returns null if the source is unreachable.</summary>
    private static async Task<ReleaseInfo?> ResolveLatestAsync()
        => ManifestUrl is { } m ? await FromManifestAsync(m) : await FromGitHubAsync();

    private static async Task<ReleaseInfo?> FromManifestAsync(string url)
    {
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        string? version = Str(r, "version");
        if (string.IsNullOrWhiteSpace(version)) return null;
        string? dl = Str(r, "url") ?? Str(r, "download_url");
        string? sha = Str(r, "sha256");
        long size = r.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        return new ReleaseInfo(version.TrimStart('v', 'V'), dl, sha, size, Str(r, "name"), Str(r, "notes"), Str(r, "published"));
    }

    // GitHub Releases: the tag is the version; the deskhand.zip asset gives the download URL plus the sha256
    // digest (arrives as "sha256:<hex>") and size GitHub publishes, used to verify the download before it runs.
    private static async Task<ReleaseInfo?> FromGitHubAsync()
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{BuildInfo.Repository}/releases/latest");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        string? url = null, sha = null; long size = 0;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(Str(a, "name"), Asset, StringComparison.OrdinalIgnoreCase))
                {
                    url = Str(a, "browser_download_url");
                    string? digest = Str(a, "digest");
                    sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : null;
                    size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    break;
                }
        return new ReleaseInfo(tag.TrimStart('v', 'V'), url, sha, size, Str(r, "name"), Str(r, "body"), Str(r, "published_at"));
    }

    private static async Task<string> Sha256HexAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(fs)).ToLowerInvariant();
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
