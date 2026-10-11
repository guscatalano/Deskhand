using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using DImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Deskhand.Core.Services;

/// <summary>What to record and how. <see cref="MaxDurationMs"/> is a hard safety cap: the recording
/// auto-stops and finalizes when it elapses, so a forgotten/failed stop can't record forever.</summary>
public record RecordingOptions(
    int? Monitor,            // null = whole virtual desktop (all monitors)
    string Format,           // "gif" | "avi" (mjpeg video) | "mp4" (h.264, streamed to disk)
    int Fps,                 // frames per second (1..30)
    int Scale,               // output scale percent (10..100)
    int Quality,             // jpeg/avi quality (1..100); ignored for gif
    int MaxDurationMs,       // hard auto-stop ceiling
    string? OutputName = null,    // optional caller-chosen base filename (sanitized; extension is forced to match Format)
    bool Overlays = false,        // draw the agent's click highlights + a key-press strip onto the recording
    bool Resumable = false,       // mp4 only: persist state + survive a reboot, resuming under the same id into a new segment
    bool OverlaysUser = false);   // also draw the HUMAN's clicks + shortcut keys (global hook); plain typing is not shown

public record RecordingStatus(
    string Id, string State, string Format, int? Monitor, int Fps, int Scale,
    int Width, int Height, int Frames, long ElapsedMs, int MaxDurationMs,
    long SizeBytes, string? File, string? Error);

/// <summary>
/// Records the screen (one monitor or the whole virtual desktop) to an animated GIF, an MJPEG AVI, or an
/// H.264 MP4. Frames are grabbed on a background timer via GDI (no UIA STA needed). GIF/AVI buffer every
/// JPEG frame in memory and are wrapped at stop, so they carry the short <see cref="MaxAllowedDurationMs"/>
/// ceiling; MP4 is streamed straight to disk via <see cref="Mp4SinkWriter"/> (in-box Media Foundation, no
/// external tools), so it is bounded by disk and allowed the longer <see cref="MaxMp4DurationMs"/> ceiling.
/// Every session carries a hard auto-stop so a forgotten/failed stop can't record forever.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    public const int MaxAllowedDurationMs = 300_000;     // 5 min — GIF/AVI are held in RAM until encode
    public const int MaxMp4DurationMs = 1_800_000;       // 30 min — MP4 streams to disk, so it can run far longer
    private const long CaptionMs = 2500;                 // how long a record_mark caption stays on screen
    // resumable: finalize a segment every N s, so a hard crash loses ≤ this. Override via DESKHAND_ROLL_SECONDS (min 2).
    public static readonly int RollSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_ROLL_SECONDS"), out var _rs) && _rs >= 2 ? _rs : 60;

    private sealed class Session
    {
        public required string Id;
        public required RecordingOptions Opt;
        public Rectangle Rect;
        public int OutW, OutH;
        public readonly List<byte[]> JpegFrames = new();   // GIF/AVI: per-frame JPEG buffered in RAM (GIF re-quantizes)
        public Mp4SinkWriter? Mp4;                          // MP4: streaming encoder (JpegFrames stays empty)
        public string? Mp4Path;                             // MP4 file path, assigned at Start (exists but unfinalized while recording)
        public volatile int Mp4Frames;                      // MP4 frames written so far
        public volatile bool Busy;                          // MP4: drop a tick rather than queue if an encode is in flight
        public double Speed = 1.0;                           // current playback-speed factor (mp4 only); >1 = faster
        public readonly List<object> Marks = new();         // timeline of { atMs, label } markers (for the result)
        public readonly List<object> Speeds = new();        // timeline of { atMs, factor } speed changes
        public volatile string? Caption;                    // transient on-screen caption (the latest marker label)
        public long CaptionUntil;                           // TickCount64 after which the caption stops rendering
        public bool HookReffed;                             // this session holds a ref on the global user-input hook
        public System.Threading.Timer? Timer;
        public System.Threading.Timer? AutoStop;
        public System.Threading.Timer? Roll;          // resumable: periodically finalize the current segment and roll to the next
        public readonly object WriterLock = new();    // serializes mp4 WriteFrame vs a segment roll
        public readonly object Gate = new();
        public long StartedTicks;
        public volatile string State = "recording";       // recording | completed | error
        public string? File;
        public long SizeBytes;
        public string? Error;
        public bool Finalized;
        public int FinalFrames = -1;       // frame count captured at encode time (JpegFrames is cleared afterward)
        public long FinalElapsedMs = -1;   // wall-clock duration captured at stop time
        // --- resumable (reboot-surviving) recordings ---
        public bool Resumable;
        public string Base = "";           // base filename (id or sanitized OutputName); segments are <Base>.pN.mp4
        public int Part;                   // current segment index
        public readonly List<string> Segments = new();   // finalized segment filenames, in order
        public DateTime StartedUtc;        // wall-clock start (survives reboot, unlike TickCount64)
    }

    /// <summary>On-disk state for a resumable recording (<c>&lt;Base&gt;.rec.json</c>), so a reboot can resume it.</summary>
    public sealed record ResumeState(string Id, string Base, int? Monitor, int Fps, int Scale, int Quality,
        bool Overlays, int MaxDurationMs, string StartedUtc, int Part, List<string> Segments, string Status);

    public const int RetentionHours = 24;   // saved media is auto-deleted after this many hours

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly string _dir;
    private readonly Deskhand.Core.Governance.AuditLog? _audit;
    private readonly System.Threading.Timer _janitor;

    // Recent injected input (for optional on-screen overlays). Kept small and trimmed by age; shared across
    // sessions but only drawn into those started with Overlays=true.
    private readonly object _marksGate = new();
    private readonly LinkedList<InputInjector.InputMark> _marks = new();
    private readonly Action<InputInjector.InputMark> _onInput;
    private readonly GlobalInputHook _userHook = new();         // the HUMAN's input source (ref-counted per OverlaysUser session)
    private const long OverlayClickMs = 650;   // a click ripple lives this long
    private const long OverlayKeyMs = 2500;    // a key stays in the strip this long

    public ScreenRecorder(Deskhand.Core.Governance.AuditLog? audit = null)
    {
        _audit = audit;
        _onInput = OnInput;
        InputInjector.Injected += _onInput;
        _userHook.Input += OnUserInput;
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
        string fmt = o.Format?.ToLowerInvariant() switch
        {
            "mp4" or "h264" or "h.264" => "mp4",
            "avi" or "mjpeg" or "video" => "avi",
            _ => "gif",
        };
        int fps = Math.Clamp(o.Fps <= 0 ? 10 : o.Fps, 1, 30);
        int scale = Math.Clamp(o.Scale <= 0 ? 100 : o.Scale, 10, 100);
        int quality = Math.Clamp(o.Quality <= 0 ? 75 : o.Quality, 1, 100);
        int ceiling = fmt == "mp4" ? MaxMp4DurationMs : MaxAllowedDurationMs;
        int maxMs = o.MaxDurationMs <= 0 ? 30_000 : Math.Min(o.MaxDurationMs, ceiling);

        var rect = RectFor(o.Monitor);
        int outW = Math.Max(2, rect.Width * scale / 100) & ~1;   // even dims (encoders/most decoders require)
        int outH = Math.Max(2, rect.Height * scale / 100) & ~1;
        string id = "rec_" + Guid.NewGuid().ToString("N")[..12];
        string baseName = SafeBase(o.OutputName) ?? id;
        bool resumable = o.Resumable && fmt == "mp4";

        return BeginSession(id, baseName, o with { Format = fmt, Fps = fps, Scale = scale, Quality = quality, MaxDurationMs = maxMs },
            rect, outW, outH, DateTime.UtcNow, part: 0, new List<string>(), maxMs, resumable);
    }

    // Build the Session + encoder + timers for a fresh recording or a post-reboot resume (new segment). For a
    // resumable mp4, segment files are <Base>.p{Part}.mp4 and a <Base>.rec.json state file is written so a
    // reboot can pick it up; the hard auto-stop uses the supplied remaining budget.
    private RecordingStatus BeginSession(string id, string baseName, RecordingOptions o, Rectangle rect,
        int outW, int outH, DateTime startedUtc, int part, List<string> segments, int remainingMs, bool resumable)
    {
        string fmt = o.Format;
        Mp4SinkWriter? mp4 = null;
        string? mp4Path = null;
        if (fmt == "mp4")
        {
            try
            {
                mp4Path = resumable ? Path.Combine(_dir, $"{baseName}.p{part}.mp4") : Path.Combine(_dir, $"{baseName}.mp4");
                int bitrate = (int)Math.Clamp((long)outW * outH * o.Fps / 20, 1_000_000, 24_000_000);
                mp4 = new Mp4SinkWriter(mp4Path, outW, outH, o.Fps, bitrate);
            }
            catch (Exception ex)
            {
                _audit?.Record("recording_mp4_fallback", ex.Message, "Media Foundation unavailable → recording as AVI");
                fmt = "avi"; mp4 = null; mp4Path = null; resumable = false;
                remainingMs = Math.Min(remainingMs, MaxAllowedDurationMs);
            }
        }

        var s = new Session
        {
            Id = id,
            Opt = o with { Format = fmt },
            Rect = rect, OutW = outW, OutH = outH,
            Mp4 = mp4, Mp4Path = mp4Path,
            StartedTicks = Environment.TickCount64,
            Resumable = resumable, Base = baseName, Part = part, StartedUtc = startedUtc,
        };
        s.Segments.AddRange(segments);
        _sessions[s.Id] = s;

        if (o.OverlaysUser) { try { _userHook.AddRef(); s.HookReffed = true; } catch { } }
        if (resumable) WriteState(s, "recording");

        int periodMs = Math.Max(1000 / o.Fps, 20);
        s.Timer = new System.Threading.Timer(_ => CaptureFrame(s), null, 0, periodMs);
        s.AutoStop = new System.Threading.Timer(_ => { try { Stop(s.Id); } catch { } }, null, Math.Max(0, remainingMs), System.Threading.Timeout.Infinite);
        if (resumable)   // roll to a fresh segment periodically so a hard crash loses at most one roll interval
            s.Roll = new System.Threading.Timer(_ => { try { RollSegment(s); } catch { } },
                null, RollSeconds * 1000, RollSeconds * 1000);
        return StatusOf(s);
    }

    /// <summary>Resolve the output file path (non-resumable, single-file). A caller-supplied name is reduced to a
    /// safe basename inside the recordings dir; otherwise the recording id is used.</summary>
    private string PathFor(string id, string? outputName, string ext)
        => Path.Combine(_dir, $"{SafeBase(outputName) ?? id}.{ext}");

    /// <summary>A caller-supplied name reduced to a safe basename (no path, no traversal, ≤80 chars), or null.</summary>
    private static string? SafeBase(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string cand = Path.GetFileNameWithoutExtension(name.Trim());
        foreach (char c in Path.GetInvalidFileNameChars()) cand = cand.Replace(c, '_');
        return cand.Length == 0 ? null : cand[..Math.Min(cand.Length, 80)];
    }

    private void CaptureFrame(Session s)
    {
        if (s.State != "recording") return;
        bool mp4 = s.Mp4 is not null;

        // Cap frames to the duration budget so a stuck stop can't grow unbounded before auto-stop fires.
        int cap = s.Opt.Fps * (s.Opt.MaxDurationMs / 1000 + 2);
        if (mp4) { if (s.Mp4Frames >= cap) return; }
        else { lock (s.Gate) if (s.JpegFrames.Count >= cap) return; }

        // MP4 encodes on this timer thread; if the previous frame is still encoding, drop this tick rather than
        // let timer callbacks pile up (a dropped frame is harmless — timestamps keep playback at the right speed).
        if (mp4) { if (s.Busy) return; s.Busy = true; }
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

            if (s.Opt.Overlays || s.Opt.OverlaysUser) DrawOverlays(frame, s);
            if (s.Caption is not null && Environment.TickCount64 < s.CaptionUntil) DrawCaption(frame, s);

            if (mp4)
            {
                // Speed segments: a frame's presentation duration is the 1x frame time divided by the factor,
                // so a 2x stretch plays back in half the time.
                long baseDur = 10_000_000L / s.Opt.Fps;
                long dur = Math.Max(1, (long)Math.Round(baseDur / Math.Clamp(s.Speed, 0.25, 8.0)));
                lock (s.WriterLock)   // don't write while a segment roll is swapping the encoder
                    if (s.State == "recording" && s.Mp4 is not null && s.Mp4.WriteFrame(frame, dur)) s.Mp4Frames++;
            }
            else
            {
                byte[] jpeg = EncodeJpeg(frame, s.Opt.Quality);
                lock (s.Gate) if (s.State == "recording") s.JpegFrames.Add(jpeg);
            }
            if (!ReferenceEquals(frame, full)) frame.Dispose();
        }
        catch { /* a dropped frame must not kill the recording */ }
        finally { if (mp4) s.Busy = false; }
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
        try { s.Roll?.Dispose(); } catch { }

        try
        {
            s.FinalElapsedMs = Environment.TickCount64 - s.StartedTicks;

            if (s.Mp4 is not null)   // MP4: frames were already streamed to disk; just finalize the file.
            {
                // Let the in-flight timer tick (if any) finish so Mp4Frames is settled before we finalize.
                for (int i = 0; i < 50 && s.Busy; i++) Thread.Sleep(10);
                s.FinalFrames = s.Mp4Frames;
                // With rolling, Mp4Frames is per-segment; a recording with prior segments is still valid.
                if (s.Mp4Frames == 0 && s.Segments.Count == 0) throw new InvalidOperationException("No frames were captured.");
                lock (s.WriterLock) s.Mp4.Finish();
                if (s.Resumable)
                {
                    if (s.Mp4Frames > 0 && s.Mp4Path is not null && File.Exists(s.Mp4Path) && new FileInfo(s.Mp4Path).Length > 0)
                        s.Segments.Add(Path.GetFileName(s.Mp4Path));
                    s.File = s.Segments.Count > 0 ? Path.Combine(_dir, s.Segments[^1]) : s.Mp4Path;   // last segment
                    s.SizeBytes = s.Segments.Sum(n => { try { return new FileInfo(Path.Combine(_dir, n)).Length; } catch { return 0L; } });
                    WriteState(s, "completed");
                }
                else
                {
                    s.File = s.Mp4Path;
                    s.SizeBytes = s.Mp4Path is not null && File.Exists(s.Mp4Path) ? new FileInfo(s.Mp4Path).Length : 0;
                }
                s.State = "completed";
                _audit?.Record("recording_saved", s.File, $"mp4 {s.FinalFrames}f {s.SizeBytes}B{(s.Resumable ? $" {s.Segments.Count} segment(s)" : "")} (auto-delete {RetentionHours}h)");
            }
            else
            {
                byte[][] frames;
                lock (s.Gate) frames = s.JpegFrames.ToArray();
                s.FinalFrames = frames.Length;                              // remember before JpegFrames is cleared
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
        }
        catch (Exception ex) { s.State = "error"; s.Error = ex.Message; try { s.Mp4?.Dispose(); } catch { } }
        finally { lock (s.Gate) s.JpegFrames.Clear(); ReleaseHook(s); }
        return StatusOf(s);
    }

    public RecordingStatus GetStatus(string id) => StatusOf(Get(id));

    /// <summary>Add a labelled marker at the current point of a recording. The label is captioned on-screen for
    /// a couple of seconds and recorded in the recording's marker timeline (returned by stop).</summary>
    public RecordingStatus Mark(string id, string label)
    {
        var s = Get(id);
        label = (label ?? "").Trim();
        long atMs = Environment.TickCount64 - s.StartedTicks;
        lock (s.Gate) s.Marks.Add(new { atMs, label });
        if (label.Length > 0) { s.Caption = label; s.CaptionUntil = Environment.TickCount64 + CaptionMs; }
        return StatusOf(s);
    }

    /// <summary>Set the playback-speed factor from now until the next change (mp4 only; &gt;1 plays faster). Frames
    /// keep being captured at the same rate, but their presentation durations shrink so the segment plays sped-up.
    /// Clamped to 0.25x–8x. Recorded in the speed timeline (returned by stop).</summary>
    public RecordingStatus Speed(string id, double factor)
    {
        var s = Get(id);
        factor = Math.Clamp(double.IsFinite(factor) && factor > 0 ? factor : 1.0, 0.25, 8.0);
        s.Speed = factor;
        long atMs = Environment.TickCount64 - s.StartedTicks;
        lock (s.Gate) s.Speeds.Add(new { atMs, factor });
        return StatusOf(s);
    }

    /// <summary>The marker and speed-change timelines for a recording (for the stop/mark result).</summary>
    public (IReadOnlyList<object> marks, IReadOnlyList<object> speeds) Timeline(string id)
    {
        var s = Get(id);
        lock (s.Gate) return (s.Marks.ToArray(), s.Speeds.ToArray());
    }

    /// <summary>The ordered segment filenames of a (resumable) recording — empty for a single-file recording.
    /// Each name is downloadable via deskhand_record_read(segment:) or GET /recordings/seg/{name}.</summary>
    public IReadOnlyList<string> Segments(string id)
    {
        var s = Get(id);
        lock (s.Gate) return s.Segments.ToArray();
    }

    /// <summary>Concatenate a finished resumable recording's segments into one continuous MP4
    /// (<c>&lt;base&gt;.stitched.mp4</c>) via <see cref="Mp4Stitcher"/>. Returns the stitched filename, or null if
    /// there was nothing to stitch. Call after <see cref="Stop"/>.</summary>
    public string? StitchSegments(string id)
    {
        var s = Get(id);
        if (!s.Resumable) return null;
        var paths = s.Segments.Select(n => Path.Combine(_dir, n)).Where(File.Exists).ToList();
        if (paths.Count == 0) return null;
        string outName = $"{s.Base}.stitched.mp4";
        string outPath = Path.Combine(_dir, outName);
        int bitrate = (int)Math.Clamp((long)s.OutW * s.OutH * s.Opt.Fps / 20, 1_000_000, 24_000_000);
        int frames = Mp4Stitcher.Stitch(paths, outPath, s.Opt.Fps, bitrate);
        _audit?.Record("recording_stitched", outName, $"{paths.Count} segment(s) -> {frames} frames");
        return frames > 0 && File.Exists(outPath) ? outName : null;
    }

    // ---------- reboot-surviving (resumable) recordings ----------

    private static readonly JsonSerializerOptions _stateJson = new() { WriteIndented = false };
    private string StateFile(string baseName) => Path.Combine(_dir, $"{baseName}.rec.json");

    private void WriteState(Session s, string status)
    {
        try
        {
            var st = new ResumeState(s.Id, s.Base, s.Opt.Monitor, s.Opt.Fps, s.Opt.Scale, s.Opt.Quality,
                s.Opt.Overlays, s.Opt.MaxDurationMs, s.StartedUtc.ToString("o"), s.Part, s.Segments.ToList(), status);
            File.WriteAllText(StateFile(s.Base), JsonSerializer.Serialize(st, _stateJson));
        }
        catch { /* state persistence is best-effort */ }
    }

    // Finalize the current segment and start the next one, so closed segments are always playable and a hard
    // crash loses at most one roll interval. Serialized against frame writes via WriterLock.
    private void RollSegment(Session s)
    {
        lock (s.WriterLock)
        {
            if (s.State != "recording" || s.Mp4 is null || !s.Resumable) return;
            if (s.Mp4Frames == 0) return;   // nothing captured this segment yet — don't roll an empty file
            try
            {
                s.Mp4.Finish();
                if (s.Mp4Path is not null && File.Exists(s.Mp4Path) && new FileInfo(s.Mp4Path).Length > 0)
                    s.Segments.Add(Path.GetFileName(s.Mp4Path));
                s.Part++;
                string path = Path.Combine(_dir, $"{s.Base}.p{s.Part}.mp4");
                int bitrate = (int)Math.Clamp((long)s.OutW * s.OutH * s.Opt.Fps / 20, 1_000_000, 24_000_000);
                s.Mp4 = new Mp4SinkWriter(path, s.OutW, s.OutH, s.Opt.Fps, bitrate);
                s.Mp4Path = path;
                s.Mp4Frames = 0;
                WriteState(s, "recording");
            }
            catch { /* a failed roll shouldn't kill the recording; keep the (now-finished) writer null-guarded */ }
        }
    }

    /// <summary>Finalize the current segment of every in-progress resumable recording and mark its state
    /// "interrupted", so a reboot can resume it. Call on an orderly shutdown (service stop / session end).</summary>
    public void FinalizeForShutdown()
    {
        foreach (var s in _sessions.Values)
        {
            if (!s.Resumable || s.State != "recording" || s.Mp4 is null) continue;
            try
            {
                lock (s.Gate) { if (s.State != "recording") continue; s.State = "encoding"; }
                try { s.Timer?.Dispose(); } catch { }
                try { s.AutoStop?.Dispose(); } catch { }
                try { s.Roll?.Dispose(); } catch { }
                for (int i = 0; i < 50 && s.Busy; i++) Thread.Sleep(10);
                lock (s.WriterLock) s.Mp4.Finish();
                if (s.Mp4Frames > 0 && s.Mp4Path is not null && File.Exists(s.Mp4Path) && new FileInfo(s.Mp4Path).Length > 0)
                    s.Segments.Add(Path.GetFileName(s.Mp4Path));
                WriteState(s, "interrupted");
                s.State = "interrupted";
                ReleaseHook(s);
                _audit?.Record("recording_interrupted", s.Base, $"segment {s.Part} finalized for shutdown ({s.Segments.Count} total)");
            }
            catch { /* best-effort at shutdown */ }
        }
    }

    /// <summary>On startup, resume every interrupted resumable recording under its original id, capturing into a
    /// new segment. Orphaned (unfinalized) segment files from a hard crash are discarded. Returns the resumed ids.</summary>
    public IReadOnlyList<string> ResumeAll()
    {
        var resumed = new List<string>();
        List<string> files;
        try { files = System.IO.Directory.EnumerateFiles(_dir, "*.rec.json").ToList(); } catch { return resumed; }
        foreach (var f in files)
        {
            ResumeState? st = null;
            try { st = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(f)); } catch { }
            if (st is null || (st.Status != "recording" && st.Status != "interrupted")) continue;
            if (_sessions.ContainsKey(st.Id)) continue;
            try { if (ResumeOne(st)) resumed.Add(st.Id); } catch { }
        }
        return resumed;
    }

    private bool ResumeOne(ResumeState st)
    {
        // Keep only segments that still exist on disk; delete every other <Base>.pN.mp4 (unfinalized crash tails).
        var segments = st.Segments.Where(n => File.Exists(Path.Combine(_dir, n))).ToList();
        try
        {
            foreach (var pf in System.IO.Directory.EnumerateFiles(_dir, $"{st.Base}.p*.mp4"))
                if (!segments.Contains(Path.GetFileName(pf))) try { File.Delete(pf); } catch { }
        }
        catch { }

        var startedUtc = DateTime.TryParse(st.StartedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow;
        int elapsed = (int)Math.Clamp((DateTime.UtcNow - startedUtc).TotalMilliseconds, 0, int.MaxValue);
        int remaining = st.MaxDurationMs - elapsed;
        if (remaining <= 1000)   // budget already spent → mark completed, don't resume
        {
            try { File.WriteAllText(StateFile(st.Base), JsonSerializer.Serialize(st with { Segments = segments, Status = "completed" }, _stateJson)); } catch { }
            return false;
        }

        int maxPart = -1;
        foreach (var seg in segments) { int p = ParsePart(seg); if (p > maxPart) maxPart = p; }
        int nextPart = maxPart + 1;

        var rect = RectFor(st.Monitor);
        int scale = Math.Clamp(st.Scale, 10, 100);
        int outW = Math.Max(2, rect.Width * scale / 100) & ~1;
        int outH = Math.Max(2, rect.Height * scale / 100) & ~1;
        var opt = new RecordingOptions(st.Monitor, "mp4", st.Fps, st.Scale, st.Quality, st.MaxDurationMs, st.Base, st.Overlays, Resumable: true);
        BeginSession(st.Id, st.Base, opt, rect, outW, outH, startedUtc, nextPart, segments, remaining, resumable: true);
        _audit?.Record("recording_resumed", st.Base, $"resumed into segment {nextPart}; {segments.Count} prior segment(s)");
        return true;
    }

    // Extract N from "<base>.pN.mp4"; -1 if not matched.
    private static int ParsePart(string fileName)
    {
        int dot = fileName.LastIndexOf(".p", StringComparison.Ordinal);
        if (dot < 0) return -1;
        int start = dot + 2, end = fileName.IndexOf(".mp4", start, StringComparison.OrdinalIgnoreCase);
        return end > start && int.TryParse(fileName.AsSpan(start, end - start), out int n) ? n : -1;
    }

    /// <summary>Read a chunk of any finished recording file (or segment) in the recordings dir by name — the
    /// segment-aware sibling of <see cref="ReadChunk"/>. The name is sanitized to a basename (no path traversal).</summary>
    public (byte[] chunk, long total, string name) ReadSegmentChunk(string fileName, long offset, int length)
    {
        string safe = Path.GetFileName((fileName ?? "").Trim());
        if (safe.Length == 0) throw new ArgumentException("No file name.");
        string path = Path.Combine(_dir, safe);
        if (!File.Exists(path)) throw new InvalidOperationException($"No such recording file '{safe}'.");
        long total = new FileInfo(path).Length;
        if (offset < 0) offset = 0;
        if (offset >= total) return (Array.Empty<byte>(), total, safe);
        int want = (int)Math.Min(length <= 0 ? 6 * 1024 * 1024 : length, total - offset);
        var buf = new byte[want];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < want) { int n = fs.Read(buf, read, want - read); if (n <= 0) break; read += n; }
            if (read != want) Array.Resize(ref buf, read);
        }
        return (buf, total, safe);
    }

    public IReadOnlyList<RecordingStatus> List() => _sessions.Values.Select(StatusOf).ToList();

    /// <summary>The encoded file bytes for a completed recording (for HTTP download / dashboard save).</summary>
    public (byte[] bytes, string mime, string name) Read(string id)
    {
        var s = Get(id);
        if (s.File is null || !File.Exists(s.File)) throw new InvalidOperationException("Recording is not finished.");
        return (File.ReadAllBytes(s.File), MimeFor(s.Opt.Format), Path.GetFileName(s.File));
    }

    /// <summary>Read one chunk of a finished recording's bytes, so large files come back over MCP in pieces
    /// (the generic file-read tool caps at ~25 MB). Returns the chunk plus the total size and file name.</summary>
    public (byte[] chunk, long total, string name) ReadChunk(string id, long offset, int length)
    {
        var s = Get(id);
        if (s.File is null || !File.Exists(s.File)) throw new InvalidOperationException("Recording is not finished.");
        long total = new FileInfo(s.File).Length;
        if (offset < 0) offset = 0;
        if (offset >= total) return (Array.Empty<byte>(), total, Path.GetFileName(s.File));
        int want = (int)Math.Min(length <= 0 ? 6 * 1024 * 1024 : length, total - offset);
        var buf = new byte[want];
        using (var fs = new FileStream(s.File, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < want) { int n = fs.Read(buf, read, want - read); if (n <= 0) break; read += n; }
            if (read != want) Array.Resize(ref buf, read);
        }
        return (buf, total, Path.GetFileName(s.File));
    }

    private Session Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : throw new ArgumentException($"No recording '{id}'.");

    private RecordingStatus StatusOf(Session s) => new(
        s.Id, s.State, s.Opt.Format, s.Opt.Monitor, s.Opt.Fps, s.Opt.Scale, s.OutW, s.OutH,
        // While recording, the live count; once finalized, the remembered count (JpegFrames is cleared post-encode).
        s.State == "recording" ? (s.Mp4 is not null ? s.Mp4Frames : s.JpegFrames.Count)
                               : (s.FinalFrames >= 0 ? s.FinalFrames : s.JpegFrames.Count),
        s.FinalElapsedMs >= 0 ? s.FinalElapsedMs : Environment.TickCount64 - s.StartedTicks,
        s.Opt.MaxDurationMs, s.SizeBytes, s.File, s.Error);

    private static string MimeFor(string format) => format switch
    {
        "mp4" => "video/mp4",
        "avi" => "video/avi",
        _ => "image/gif",
    };

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

    // Release the global user-input hook ref this session holds (idempotent).
    private void ReleaseHook(Session s)
    {
        if (!s.HookReffed) return;
        s.HookReffed = false;
        try { _userHook.Release(); } catch { }
    }

    public void Dispose()
    {
        try { InputInjector.Injected -= _onInput; } catch { }
        try { _userHook.Input -= OnUserInput; _userHook.Dispose(); } catch { }
        try { _janitor.Dispose(); } catch { }
        foreach (var s in _sessions.Values) { try { s.Timer?.Dispose(); s.AutoStop?.Dispose(); s.Roll?.Dispose(); s.Mp4?.Dispose(); } catch { } }
        _sessions.Clear();
    }

    private void OnInput(InputInjector.InputMark m) => AddMark(m);

    // The human's input from the global hook — fed into the same overlay ring as the agent's injected input.
    private void OnUserInput(GlobalInputHook.HookEvent e)
        => AddMark(new InputInjector.InputMark(e.IsKey, e.X, e.Y, e.Label, Environment.TickCount64));

    private void AddMark(InputInjector.InputMark m)
    {
        long now = Environment.TickCount64;
        lock (_marksGate)
        {
            _marks.AddLast(m);
            // Trim anything older than the longest overlay lifetime, and cap the list.
            while (_marks.First is { } f && now - f.Value.Tick > OverlayKeyMs) _marks.RemoveFirst();
            while (_marks.Count > 64) _marks.RemoveFirst();
        }
    }

    /// <summary>Draw the agent's recent clicks (fading ripples) and typed keys (a bottom strip) onto a frame.
    /// Best-effort — any failure is swallowed so it can never break the recording.</summary>
    private void DrawOverlays(Bitmap frame, Session s)
    {
        InputInjector.InputMark[] marks;
        lock (_marksGate) marks = _marks.ToArray();
        if (marks.Length == 0) return;
        long now = Environment.TickCount64;
        double sx = (double)s.OutW / s.Rect.Width, sy = (double)s.OutH / s.Rect.Height;
        try
        {
            using var g = Graphics.FromImage(frame);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Click ripples.
            foreach (var m in marks)
            {
                if (m.IsKey) continue;
                long age = now - m.Tick;
                if (age < 0 || age > OverlayClickMs) continue;
                double t = age / (double)OverlayClickMs;             // 0..1
                int cx = (int)Math.Round((m.X - s.Rect.X) * sx), cy = (int)Math.Round((m.Y - s.Rect.Y) * sy);
                if (cx < 0 || cy < 0 || cx > s.OutW || cy > s.OutH) continue;
                int r = (int)(10 + 34 * t);
                int alpha = (int)(200 * (1 - t));
                using var pen = new Pen(Color.FromArgb(alpha, 255, 210, 40), 3f);
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                using var dot = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 40), 255, 120, 20));
                g.FillEllipse(dot, cx - 4, cy - 4, 8, 8);
            }

            // Key strip: the most recent keys/typed text, newest last.
            var keys = marks.Where(m => m.IsKey && now - m.Tick <= OverlayKeyMs).Select(m => m.Label).ToList();
            if (keys.Count > 0)
            {
                string text = string.Join("  ", keys);
                if (text.Length > 48) text = "…" + text[^47..];
                int fontPx = Math.Clamp(s.OutH / 28, 11, 22);
                using var font = new Font("Consolas", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                var size = g.MeasureString(text, font);
                float pad = fontPx * 0.6f;
                float boxW = size.Width + pad * 2, boxH = size.Height + pad;
                float x = (s.OutW - boxW) / 2, y = s.OutH - boxH - pad;
                using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                g.FillRectangle(bg, x, y, boxW, boxH);
                using var fg = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
                g.DrawString(text, font, fg, x + pad, y + pad / 2);
            }
        }
        catch { /* overlays are cosmetic; never let them kill a frame */ }
    }

    /// <summary>Draw the current marker caption centred near the top of the frame. Best-effort.</summary>
    private static void DrawCaption(Bitmap frame, Session s)
    {
        string? text = s.Caption;
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 80) text = text[..79] + "…";
        try
        {
            using var g = Graphics.FromImage(frame);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int fontPx = Math.Clamp(s.OutH / 22, 13, 28);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(text, font);
            float pad = fontPx * 0.7f;
            float boxW = size.Width + pad * 2, boxH = size.Height + pad;
            float x = (s.OutW - boxW) / 2, y = Math.Max(pad, s.OutH * 0.04f);
            using var bg = new SolidBrush(Color.FromArgb(185, 20, 20, 20));
            g.FillRectangle(bg, x, y, boxW, boxH);
            using var accent = new Pen(Color.FromArgb(220, 255, 210, 40), 2f);
            g.DrawRectangle(accent, x, y, boxW, boxH);
            using var fg = new SolidBrush(Color.White);
            g.DrawString(text, font, fg, x + pad, y + pad / 2);
        }
        catch { /* cosmetic */ }
    }
}
