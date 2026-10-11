using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace Deskhand.Core.Services;

/// <summary>
/// Concatenate reboot-surviving recording segments (each a standalone H.264 MP4) into ONE continuous MP4, by
/// decoding each via a Media Foundation SourceReader (to RGB32 frames) and re-encoding them through
/// <see cref="Mp4SinkWriter"/> at a constant fps. In-box MF, no external tools. Used by record_stop(stitch:true).
/// </summary>
public static class Mp4Stitcher
{
    /// <summary>Stitch <paramref name="segmentPaths"/> into <paramref name="outputPath"/>. Returns the frame count
    /// written. Output dimensions are taken from the first segment; a segment of different dimensions is skipped.</summary>
    public static int Stitch(IReadOnlyList<string> segmentPaths, string outputPath, int fps, int bitrate)
    {
        Mp4SinkWriter? writer = null;
        int outW = 0, outH = 0, total = 0;
        try
        {
            foreach (var seg in segmentPaths)
            {
                if (!File.Exists(seg)) continue;
                // Enable the reader's video processor so it will convert the decoded YUV to RGB32 for us.
                var attrs = MFCreateAttributes(1);
                attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, (uint)1);
                IMFSourceReader reader = MFCreateSourceReaderFromURL(seg, attrs);
                attrs.Dispose();
                try
                {
                    reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

                    var rgb = MFCreateMediaType();
                    rgb.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    rgb.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                    reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, rgb);
                    rgb.Dispose();

                    var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                    ulong fsz = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    int w = (int)(fsz >> 32), h = (int)(fsz & 0xFFFFFFFF);
                    int stride; try { stride = unchecked((int)cur.GetUInt32(MediaTypeAttributeKeys.DefaultStride)); } catch { stride = w * 4; }
                    cur.Dispose();

                    if (writer is null) { outW = w; outH = h; writer = new Mp4SinkWriter(outputPath, outW, outH, fps, bitrate); }
                    if (w != outW || h != outH) continue;   // dimensions changed between segments — skip this one

                    while (true)
                    {
                        var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                            out _, out SourceReaderFlag flags, out _);
                        if ((flags & SourceReaderFlag.EndOfStream) != 0) { sample?.Dispose(); break; }
                        if (sample is null) continue;
                        try
                        {
                            using var buf = sample.ConvertToContiguousBuffer();
                            buf.Lock(out IntPtr src, out _, out _);
                            try
                            {
                                using var bmp = BufferToBitmap(src, w, h, stride);
                                writer!.WriteFrame(bmp);
                                total++;
                            }
                            finally { buf.Unlock(); }
                        }
                        finally { sample.Dispose(); }
                    }
                }
                finally { reader.Dispose(); }
            }
        }
        finally { writer?.Finish(); writer?.Dispose(); }
        return total;
    }

    /// <summary>Decode the first frame of an MP4 to PNG bytes (a poster frame) — also the orientation self-check.</summary>
    public static byte[]? FirstFramePng(string path)
    {
        if (!File.Exists(path)) return null;
        var attrs = MFCreateAttributes(1);
        attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, (uint)1);
        var reader = MFCreateSourceReaderFromURL(path, attrs);
        attrs.Dispose();
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            var rgb = MFCreateMediaType();
            rgb.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            rgb.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, rgb);
            rgb.Dispose();
            var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            ulong fsz = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            int w = (int)(fsz >> 32), h = (int)(fsz & 0xFFFFFFFF);
            int stride; try { stride = unchecked((int)cur.GetUInt32(MediaTypeAttributeKeys.DefaultStride)); } catch { stride = w * 4; }
            cur.Dispose();
            for (int tries = 0; tries < 10; tries++)
            {
                var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out SourceReaderFlag flags, out _);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) { sample?.Dispose(); return null; }
                if (sample is null) continue;
                try
                {
                    using var buf = sample.ConvertToContiguousBuffer();
                    buf.Lock(out IntPtr src, out _, out _);
                    try
                    {
                        using var bmp = BufferToBitmap(src, w, h, stride);
                        using var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return ms.ToArray();
                    }
                    finally { buf.Unlock(); }
                }
                finally { sample.Dispose(); }
            }
            return null;
        }
        finally { reader.Dispose(); }
    }

    // Copy an RGB32 MF buffer into a top-down 32bpp-ARGB bitmap, honouring the stride sign (negative = bottom-up).
    private static Bitmap BufferToBitmap(IntPtr src, int w, int h, int stride)
    {
        int absStride = Math.Abs(stride) == 0 ? w * 4 : Math.Abs(stride);
        // The reader's video processor delivers top-down RGB32 here (verified by a synthetic top-white/bottom-black
        // round-trip); only an explicit negative stride means bottom-up.
        bool bottomUp = stride < 0;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
            {
                int srcRow = bottomUp ? (h - 1 - y) : y;       // bottom-up buffers store the top image row last
                IntPtr s = src + srcRow * absStride;
                IntPtr d = bits.Scan0 + y * bits.Stride;
                CopyMemory(d, s, (uint)rowBytes);
            }
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
}
