using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace Deskhand.Core.Services;

/// <summary>
/// Live H.264 encoder that produces a FRAGMENTED MP4 (ftyp+moov init segment, then moof+mdat fragments) via the
/// in-box Media Foundation fragmented-MP4 sink, written to a temp file. The HTTP live-view endpoint tails that
/// growing file to the browser, where a &lt;video&gt; + MediaSource Extensions plays it. Push 32bpp-ARGB frames as
/// they are captured; a short key-frame interval + low-latency mode keep fragments flowing for a responsive view.
/// This is the H.264 alternative to the MJPEG live stream — far less bandwidth, especially when idle.
/// </summary>
public sealed class Mp4LiveStreamer : IDisposable
{
    private static bool _started;
    private static readonly object _startGate = new();
    public static bool IsSupported { get { try { EnsureStarted(); return true; } catch { return false; } } }
    private static void EnsureStarted() { if (_started) return; lock (_startGate) { if (!_started) { MFStartup(false).CheckError(); _started = true; } } }

    private readonly int _w, _h;
    private readonly long _frameDuration;
    private readonly string _path;
    private readonly IMFByteStream _bs;
    private readonly IMFMediaSink _sink;
    private readonly IMFSinkWriter _writer;
    private readonly object _gate = new();
    private long _pts;
    private bool _finished;

    public string Path => _path;

    public Mp4LiveStreamer(int width, int height, int fps, int bitrate)
    {
        EnsureStarted();
        _w = width; _h = height; _frameDuration = 10_000_000L / fps;
        _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dhlive_" + Guid.NewGuid().ToString("N") + ".mp4");
        // AllowWriteSharing so the HTTP endpoint can open the same file read-only and tail it to the client as it grows.
        _bs = MFCreateFile(FileAccessMode.MfAccessModeReadwrite, FileOpenMode.MfOpenModeDeleteIfExist, FileFlags.FlagsAllowWriteSharing, _path);

        // Output: H.264. Short key-frame spacing → frequent fragments (quick start + low latency for a live view).
        var outType = MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        outType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)Math.Max(1, fps));
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)66);   // eAVEncH264VProfile_Base — no B-frames (low latency), stable MSE codec string avc1.42E0xx
        MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
        MFSetAttributeRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);

        MFCreateFMPEG4MediaSink(_bs, outType, null, out _sink);
        outType.Dispose();

        var attrs = MFCreateAttributes(1);
        attrs.Set(SinkWriterAttributeKeys.LowLatency, true);   // emit fragments promptly, don't buffer ahead
        _writer = MFCreateSinkWriterFromMediaSink(_sink, attrs);
        attrs.Dispose();

        // Input: the sink already owns stream 0 (H.264 out); tell the writer our frames are 32bpp ARGB, top-down.
        var inType = MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));
        MFSetAttributeSize(inType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MFSetAttributeRatio(inType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
        MFSetAttributeRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        _writer.SetInputMediaType(0, inType, null);
        inType.Dispose();

        _writer.BeginWriting();
    }

    /// <summary>Encode one 32bpp-ARGB frame at the recording's dimensions. Thread-safe; false once finished.</summary>
    public bool WriteFrame(Bitmap frame)
    {
        lock (_gate)
        {
            if (_finished) return false;
            int size = _w * 4 * _h;
            var buffer = MFCreateMemoryBuffer(size);
            buffer.Lock(out IntPtr dst, out _, out _);
            try
            {
                var bits = frame.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try { int row = _w * 4; for (int y = 0; y < _h; y++) CopyMemory(dst + y * row, bits.Scan0 + y * bits.Stride, (uint)row); }
                finally { frame.UnlockBits(bits); }
            }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = size;

            var sample = MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = _pts;
            sample.SampleDuration = _frameDuration;
            _pts += _frameDuration;
            _writer.WriteSample(0, sample);
            sample.Dispose();
            buffer.Dispose();
            return true;
        }
    }

    public void Finish() { lock (_gate) { if (_finished) return; _finished = true; try { _writer.Finalize(); } catch { } } }

    public void Dispose()
    {
        Finish();
        try { _writer.Dispose(); } catch { }
        try { _sink.Shutdown(); } catch { }
        try { _sink.Dispose(); } catch { }
        try { _bs.Close(); } catch { }
        try { _bs.Dispose(); } catch { }
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
}
