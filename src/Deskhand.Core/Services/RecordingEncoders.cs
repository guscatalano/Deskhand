using System.Drawing;
using System.Text;
using DImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Deskhand.Core.Services;

/// <summary>Wraps a sequence of JPEG frames into a minimal MJPEG AVI (RIFF) — real video, full colour,
/// no external codec. Plays in Windows Media Player, VLC, browsers, etc.</summary>
internal static class AviMjpegWriter
{
    public static byte[] Write(int width, int height, int fps, byte[][] frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        void Cc(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        void Patch(long pos, int val) { long c = ms.Position; ms.Position = pos; w.Write(val); ms.Position = c; }

        Cc("RIFF"); long riffSz = ms.Position; w.Write(0); Cc("AVI ");

        Cc("LIST"); long hdrlSz = ms.Position; w.Write(0); Cc("hdrl"); long hdrlStart = ms.Position;
        Cc("avih"); w.Write(56);
        w.Write(1000000 / fps);      // dwMicroSecPerFrame
        w.Write(0); w.Write(0);      // maxBytesPerSec, paddingGranularity
        w.Write(0x10);               // dwFlags AVIF_HASINDEX
        w.Write(frames.Length);      // dwTotalFrames
        w.Write(0); w.Write(1); w.Write(0);   // initialFrames, streams, suggestedBufferSize
        w.Write(width); w.Write(height);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);   // reserved[4]

        Cc("LIST"); long strlSz = ms.Position; w.Write(0); Cc("strl"); long strlStart = ms.Position;
        Cc("strh"); w.Write(56);
        Cc("vids"); Cc("MJPG");
        w.Write(0); w.Write((short)0); w.Write((short)0);   // flags, priority, language
        w.Write(0);                  // initialFrames
        w.Write(1);                  // dwScale
        w.Write(fps);                // dwRate  (rate/scale = fps)
        w.Write(0);                  // dwStart
        w.Write(frames.Length);      // dwLength
        w.Write(0); w.Write(-1); w.Write(0);   // suggestedBuffer, quality, sampleSize
        w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height); // rcFrame
        Cc("strf"); w.Write(40);
        w.Write(40); w.Write(width); w.Write(height);
        w.Write((short)1); w.Write((short)24);
        Cc("MJPG");
        w.Write(width * height * 3);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Patch(strlSz, (int)(ms.Position - strlStart));
        Patch(hdrlSz, (int)(ms.Position - hdrlStart));

        Cc("LIST"); long moviSz = ms.Position; w.Write(0); Cc("movi"); long moviStart = ms.Position;
        var index = new List<(int off, int size)>();
        foreach (var f in frames)
        {
            long chunkPos = ms.Position;
            Cc("00dc"); w.Write(f.Length); w.Write(f);
            if ((f.Length & 1) == 1) w.Write((byte)0);
            index.Add(((int)(chunkPos - moviStart) + 4, f.Length));   // offset relative to 'movi' fourcc
        }
        Patch(moviSz, (int)(ms.Position - moviStart));

        Cc("idx1"); w.Write(index.Count * 16);
        foreach (var (off, size) in index) { Cc("00dc"); w.Write(0x10); w.Write(off); w.Write(size); }

        Patch(riffSz, (int)(ms.Position - (riffSz + 4)));
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>Assembles an animated GIF89a from JPEG frames. Each frame is handed to GDI+ to quantize to
/// ≤256 colours and LZW-compress (as a single-image GIF); we parse that out and re-wrap the frames into
/// one looping animation with proper per-frame delays — so no hand-rolled LZW/quantizer is needed.</summary>
internal static class GifWriter
{
    private record Frame(int W, int H, byte[] ColorTable, int SizeLog, byte[] ImageData);

    public static byte[] Write(int width, int height, int fps, byte[][] jpegFrames)
    {
        int delayCs = Math.Max(2, 100 / fps);
        using var o = new MemoryStream();
        o.Write(Encoding.ASCII.GetBytes("GIF89a"));
        WriteU16(o, width); WriteU16(o, height);
        o.WriteByte(0x00);   // packed: no global colour table
        o.WriteByte(0x00);   // background colour index
        o.WriteByte(0x00);   // pixel aspect ratio
        // NETSCAPE 2.0 application extension → loop forever
        o.Write(new byte[] { 0x21, 0xFF, 0x0B });
        o.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        o.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

        foreach (var jpeg in jpegFrames)
        {
            byte[] single;
            using (var msIn = new MemoryStream(jpeg))
            using (var bmp = new Bitmap(msIn))
            using (var g = new MemoryStream())
            {
                bmp.Save(g, DImageFormat.Gif);
                single = g.ToArray();
            }
            var f = Parse(single);
            // Graphic Control Extension with the frame delay
            o.Write(new byte[] { 0x21, 0xF9, 0x04, 0x00 });
            WriteU16(o, delayCs);
            o.WriteByte(0x00);   // transparent colour index (unused)
            o.WriteByte(0x00);   // block terminator
            // Image Descriptor with a Local Colour Table (this frame's palette)
            o.WriteByte(0x2C);
            WriteU16(o, 0); WriteU16(o, 0);
            WriteU16(o, f.W); WriteU16(o, f.H);
            o.WriteByte((byte)(0x80 | (f.SizeLog & 0x07)));   // LCT flag + size
            o.Write(f.ColorTable, 0, f.ColorTable.Length);
            o.Write(f.ImageData, 0, f.ImageData.Length);      // LZW min-code-size + sub-blocks (+terminator)
        }
        o.WriteByte(0x3B);   // trailer
        return o.ToArray();
    }

    private static Frame Parse(byte[] g)
    {
        int p = 6;                                   // skip "GIF87a"/"GIF89a"
        p += 4;                                      // logical screen w/h
        byte packed = g[p]; p += 3;                  // packed, bg, aspect
        byte[] gct = Array.Empty<byte>(); int gctSizeLog = 0;
        if ((packed & 0x80) != 0)
        {
            gctSizeLog = packed & 0x07;
            int len = 3 * (2 << gctSizeLog);
            gct = g[p..(p + len)]; p += len;
        }
        while (p < g.Length)
        {
            byte b = g[p++];
            if (b == 0x21)                           // extension: skip label + sub-blocks
            {
                p++;                                 // label
                while (p < g.Length && g[p] != 0) p += g[p] + 1;
                p++;                                 // terminator
            }
            else if (b == 0x2C)                      // image descriptor
            {
                p += 4;                              // left, top
                int w = g[p] | (g[p + 1] << 8); int h = g[p + 2] | (g[p + 3] << 8); p += 4;
                byte ip = g[p++];
                byte[] table = gct; int sizeLog = gctSizeLog;
                if ((ip & 0x80) != 0)                // local colour table
                {
                    sizeLog = ip & 0x07;
                    int len = 3 * (2 << sizeLog);
                    table = g[p..(p + len)]; p += len;
                }
                int dataStart = p;                   // LZW min code size byte
                p++;                                 // min code size
                while (p < g.Length && g[p] != 0) p += g[p] + 1;   // sub-blocks
                p++;                                 // terminating 0x00
                return new Frame(w, h, table, sizeLog, g[dataStart..p]);
            }
            else break;                              // trailer / unknown
        }
        throw new InvalidDataException("No image block in GDI+ GIF frame.");
    }

    private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)((v >> 8) & 0xFF)); }
}
