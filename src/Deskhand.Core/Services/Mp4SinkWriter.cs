using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace Deskhand.Core.Services;

/// <summary>
/// Streams screen frames straight to an H.264 MP4 on disk via the in-box Media Foundation SinkWriter — so an
/// MP4 recording is bounded by disk, not RAM (unlike the GIF/AVI path, which buffers every JPEG frame in memory
/// and is therefore capped at a few minutes). One instance per recording: construct with the output path,
/// dimensions and fps, push 32bpp-ARGB frames as they are captured, then <see cref="Finish"/>. No external
/// binaries — H.264 encoding is provided by Windows (hardware MFT when present, software otherwise).
/// </summary>
public sealed class Mp4SinkWriter : IDisposable
{
    private static bool _started;
    private static readonly object _startGate = new();

    /// <summary>True if Media Foundation is usable on this machine (false on Windows "N"/Server SKUs without the
    /// Media Feature Pack — the recorder then falls back to AVI).</summary>
    public static bool IsSupported
    {
        get { try { EnsureStarted(); return true; } catch { return false; } }
    }

    private static void EnsureStarted()
    {
        if (_started) return;
        lock (_startGate) { if (!_started) { MFStartup(false).CheckError(); _started = true; } }   // left running for process lifetime
    }

    private readonly int _w, _h;
    private readonly long _frameDuration;   // one frame in 100-ns units at 1x
    private readonly IMFSinkWriter _writer;
    private readonly int _stream;
    private readonly object _gate = new();
    private long _frameIndex;
    private long _pts;                       // running presentation time (100-ns); advances by each frame's duration
    private bool _finished;

    public Mp4SinkWriter(string path, int width, int height, int fps, int bitrate)
    {
        EnsureStarted();
        _w = width; _h = height;
        _frameDuration = 10_000_000L / fps;

        _writer = MFCreateSinkWriterFromURL(path, null, null);

        // Output: H.264 in an MP4 container.
        var outType = MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
        MFSetAttributeRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        _stream = _writer.AddStream(outType);
        outType.Dispose();

        // Input: uncompressed 32bpp ARGB (what GDI LockBits gives us), top-down via a positive default stride.
        var inType = MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));   // + = top-down, matching LockBits
        MFSetAttributeSize(inType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MFSetAttributeRatio(inType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
        MFSetAttributeRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        _writer.SetInputMediaType(_stream, inType, null);
        inType.Dispose();

        _writer.BeginWriting();
    }

    /// <summary>Encode one frame (a 32bpp-ARGB bitmap at the recording's exact dimensions) at the default 1x
    /// duration.</summary>
    public bool WriteFrame(Bitmap frame) => WriteFrame(frame, _frameDuration);

    /// <summary>Encode one frame with an explicit presentation duration (100-ns units) — a duration shorter than
    /// the 1x frame time makes that stretch play back faster (speed segments). The running presentation clock
    /// advances by this duration, so playback speed is correct regardless of capture jitter or dropped frames.
    /// Thread-safe; returns false once finished.</summary>
    public bool WriteFrame(Bitmap frame, long duration)
    {
        if (duration < 1) duration = 1;
        lock (_gate)
        {
            if (_finished) return false;
            int size = _w * 4 * _h;
            var buffer = MFCreateMemoryBuffer(size);
            buffer.Lock(out IntPtr dst, out _, out _);
            try
            {
                var bits = frame.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = _w * 4;
                    for (int y = 0; y < _h; y++)   // copy per-row: the bitmap's stride may carry padding the buffer must not
                        CopyMemory(dst + y * rowBytes, bits.Scan0 + y * bits.Stride, (uint)rowBytes);
                }
                finally { frame.UnlockBits(bits); }
            }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = size;

            var sample = MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = _pts;
            sample.SampleDuration = duration;
            _pts += duration;
            _frameIndex++;
            _writer.WriteSample(_stream, sample);
            sample.Dispose();
            buffer.Dispose();
            return true;
        }
    }

    public long FrameCount { get { lock (_gate) return _frameIndex; } }

    /// <summary>Flush and finalize the MP4. Idempotent.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            try { _writer.Finalize(); } catch { }
        }
    }

    public void Dispose()
    {
        Finish();
        try { _writer.Dispose(); } catch { }
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
}
