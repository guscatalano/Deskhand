using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using DImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Deskhand.Core.Services;

/// <summary>What to record and how. <see cref="MaxDurationMs"/> is a hard safety cap: the recording
/// auto-stops and finalizes when it elapses, so a forgotten/failed stop can't record forever.</summary>
public record RecordingOptions(
    int? Monitor,            // null = whole virtual desktop (all monitors)
    string Format,           // "gif" | "avi" (mjpeg video)
    int Fps,                 // frames per second (1..30)
    int Scale,               // output scale percent (10..100)
    int Quality,             // jpeg/avi quality (1..100); ignored for gif
    int MaxDurationMs);      // hard auto-stop ceiling

public record RecordingStatus(
    string Id, string State, string Format, int? Monitor, int Fps, int Scale,
    int Width, int Height, int Frames, long ElapsedMs, int MaxDurationMs,
    long SizeBytes, string? File, string? Error);

/// <summary>
/// Records the screen (one monitor or the whole virtual desktop) to an animated GIF or an MJPEG AVI.
/// Frames are grabbed on a background timer via GDI (no UIA STA needed) and encoded with self-contained
/// writers — no external tools or codecs. Every session carries a hard <c>MaxDurationMs</c> auto-stop.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    public const int MaxAllowedDurationMs = 300_000;   // 5 min absolute ceiling

    private sealed class Session
    {
        public required string Id;
        public required RecordingOptions Opt;
        public Rectangle Rect;
        public int OutW, OutH;
        public readonly List<byte[]> JpegFrames = new();   // per-frame JPEG (both formats capture JPEG; GIF re-quantizes)
        public System.Threading.Timer? Timer;
        public System.Threading.Timer? AutoStop;
        public readonly object Gate = new();
        public long StartedTicks;
        public volatile string State = "recording";       // recording | completed | error
        public string? File;
        public long SizeBytes;
        public string? Error;
        public bool Finalized;
    }

    public const int RetentionHours = 24;   // saved media is auto-deleted after this many hours

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly string _dir;
    private readonly Deskhand.Core.Governance.AuditLog? _audit;
    private readonly System.Threading.Timer _janitor;

    public ScreenRecorder(Deskhand.Core.Governance.AuditLog? audit = null)
    {
        _audit = audit;
        // One predefined location for all saved media, shared with screenshots the dashboard downloads.
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Deskhand", "recordings");
        System.IO.Directory.CreateDirectory(_dir);
        CleanupExpired();
        _janitor = new System.Threading.Timer(_ => CleanupExpired(), null,
            TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    public string Directory => _dir;

    /// <summary>Delete saved media older than <see cref="RetentionHours"/>, auditing each removal.</summary>
    private void CleanupExpired()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        File.Delete(f);
                        _audit?.Record("recording_expired", Path.GetFileName(f), $"deleted (>{RetentionHours}h)");
                    }
                }
                catch { /* file may be in use; skip this pass */ }
            }
        }
        catch { }
    }

    public RecordingStatus Start(RecordingOptions o)
    {
        string fmt = o.Format?.ToLowerInvariant() switch { "avi" or "mjpeg" or "video" => "avi", _ => "gif" };
        int fps = Math.Clamp(o.Fps <= 0 ? 10 : o.Fps, 1, 30);
        int scale = Math.Clamp(o.Scale <= 0 ? 100 : o.Scale, 10, 100);
        int quality = Math.Clamp(o.Quality <= 0 ? 75 : o.Quality, 1, 100);
        int maxMs = o.MaxDurationMs <= 0 ? 30_000 : Math.Min(o.MaxDurationMs, MaxAllowedDurationMs);

        var rect = RectFor(o.Monitor);
        int outW = Math.Max(2, rect.Width * scale / 100) & ~1;   // even dims (MJPEG/most decoders prefer)
        int outH = Math.Max(2, rect.Height * scale / 100) & ~1;

        var s = new Session
        {
            Id = "rec_" + Guid.NewGuid().ToString("N")[..12],
            Opt = o with { Format = fmt, Fps = fps, Scale = scale, Quality = quality, MaxDurationMs = maxMs },
            Rect = rect, OutW = outW, OutH = outH,
            StartedTicks = Environment.TickCount64,
        };
        _sessions[s.Id] = s;

        int periodMs = Math.Max(1000 / fps, 20);
        s.Timer = new System.Threading.Timer(_ => CaptureFrame(s), null, 0, periodMs);
        s.AutoStop = new System.Threading.Timer(_ => { try { Stop(s.Id); } catch { } }, null, maxMs, System.Threading.Timeout.Infinite);
        return StatusOf(s);
    }

    private void CaptureFrame(Session s)
    {
        if (s.State != "recording") return;
        // Cap frames to the duration budget so a stuck stop can't grow unbounded before auto-stop fires.
        int cap = s.Opt.Fps * (s.Opt.MaxDurationMs / 1000 + 2);
        lock (s.Gate) if (s.JpegFrames.Count >= cap) return;
        try
        {
            using var full = new Bitmap(s.Rect.Width, s.Rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(s.Rect.X, s.Rect.Y, 0, 0, s.Rect.Size, CopyPixelOperation.SourceCopy);

            Bitmap frame = full;
            if (s.OutW != s.Rect.Width || s.OutH != s.Rect.Height)
            {
                frame = new Bitmap(s.OutW, s.OutH, PixelFormat.Format32bppArgb);
                using var g2 = Graphics.FromImage(frame);
                g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g2.DrawImage(full, 0, 0, s.OutW, s.OutH);
            }
            byte[] jpeg = EncodeJpeg(frame, s.Opt.Quality);
            if (!ReferenceEquals(frame, full)) frame.Dispose();
            lock (s.Gate) if (s.State == "recording") s.JpegFrames.Add(jpeg);
        }
        catch { /* a dropped frame must not kill the recording */ }
    }

    public RecordingStatus Stop(string id)
    {
        var s = Get(id);
        lock (s.Gate)
        {
            if (s.Finalized) return StatusOf(s);
            s.Finalized = true;
            s.State = "encoding";
        }
        try { s.Timer?.Dispose(); } catch { }
        try { s.AutoStop?.Dispose(); } catch { }

        try
        {
            byte[][] frames;
            lock (s.Gate) frames = s.JpegFrames.ToArray();
            if (frames.Length == 0) throw new InvalidOperationException("No frames were captured.");

            string ext = s.Opt.Format;
            byte[] outBytes = s.Opt.Format == "avi"
                ? AviMjpegWriter.Write(s.OutW, s.OutH, s.Opt.Fps, frames)
                : GifWriter.Write(s.OutW, s.OutH, s.Opt.Fps, frames);

            string path = Path.Combine(_dir, $"{s.Id}.{ext}");
            File.WriteAllBytes(path, outBytes);
            s.File = path; s.SizeBytes = outBytes.LongLength; s.State = "completed";
            _audit?.Record("recording_saved", path, $"{s.Opt.Format} {frames.Length}f {outBytes.LongLength}B (auto-delete {RetentionHours}h)");
        }
        catch (Exception ex) { s.State = "error"; s.Error = ex.Message; }
        finally { lock (s.Gate) s.JpegFrames.Clear(); }
        return StatusOf(s);
    }

    public RecordingStatus GetStatus(string id) => StatusOf(Get(id));

    public IReadOnlyList<RecordingStatus> List() => _sessions.Values.Select(StatusOf).ToList();

    /// <summary>The encoded file bytes for a completed recording (for HTTP download / dashboard save).</summary>
    public (byte[] bytes, string mime, string name) Read(string id)
    {
        var s = Get(id);
        if (s.File is null || !File.Exists(s.File)) throw new InvalidOperationException("Recording is not finished.");
        string mime = s.Opt.Format == "avi" ? "video/avi" : "image/gif";
        return (File.ReadAllBytes(s.File), mime, Path.GetFileName(s.File));
    }

    private Session Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : throw new ArgumentException($"No recording '{id}'.");

    private RecordingStatus StatusOf(Session s) => new(
        s.Id, s.State, s.Opt.Format, s.Opt.Monitor, s.Opt.Fps, s.Opt.Scale, s.OutW, s.OutH,
        s.JpegFrames.Count == 0 && s.State != "recording" ? -1 : s.JpegFrames.Count,
        Environment.TickCount64 - s.StartedTicks, s.Opt.MaxDurationMs, s.SizeBytes, s.File, s.Error);

    private static Rectangle RectFor(int? monitor)
    {
        if (monitor is null)
        {
            var v = DesktopInfo.VirtualScreen();
            return new Rectangle(v.X, v.Y, v.Width, v.Height);
        }
        var m = DesktopInfo.Monitors().FirstOrDefault(mm => mm.Index == monitor.Value)
                ?? throw new ArgumentException($"No monitor with index {monitor}.");
        return new Rectangle(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height);
    }

    private static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        using var ms = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == DImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bmp.Save(ms, codec, ep);
        return ms.ToArray();
    }

    public void Dispose()
    {
        try { _janitor.Dispose(); } catch { }
        foreach (var s in _sessions.Values) { try { s.Timer?.Dispose(); s.AutoStop?.Dispose(); } catch { } }
        _sessions.Clear();
    }
}
