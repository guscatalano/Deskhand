using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SdImageFormat = System.Drawing.Imaging.ImageFormat;   // disambiguate from Deskhand.Core.ImageFormat (the capture enum)

namespace Deskhand.Core.Services;

/// <summary>The result of fitting a capture to a size/resolution budget: the (possibly re-encoded) bytes, the
/// image's actual pixel dimensions, its format, and the scale relative to the original screen pixels. A client
/// maps an image pixel (px,py) back to a screen coordinate as (rect.X + px/scale, rect.Y + py/scale).</summary>
public record ScaledImage(byte[] Bytes, int Width, int Height, string Format, double Scale);

/// <summary>
/// Shrink a screenshot to fit a caller-supplied budget so it survives a size-capped tool channel:
/// <c>maxWidth</c> caps the resolution, <c>maxBytes</c> caps the encoded payload. When a byte budget can't be
/// met by resolution alone, a PNG is re-encoded as JPEG (far smaller for photos/UI) and then progressively
/// downscaled. The original screen rectangle is unchanged — only the returned image gets smaller — and the
/// <see cref="ScaledImage.Scale"/> lets the caller convert image pixels back to screen coordinates.
/// </summary>
public static class ImageScaler
{
    private const int MinWidth = 96;   // floor so we never scale to an unusable sliver

    public static ScaledImage Fit(byte[] bytes, string sourceFormat, int? maxWidth, int? maxBytes, int jpegQuality = 80)
    {
        // Nothing requested → return the bytes untouched.
        if ((maxWidth is null or <= 0) && (maxBytes is null or <= 0))
            return new ScaledImage(bytes, 0, 0, sourceFormat, 1.0);

        try
        {
            using var src = LoadBitmap(bytes);
            int ow = src.Width, oh = src.Height;
            double scale = 1.0;
            if (maxWidth is int mw && mw > 0 && ow > mw) scale = Math.Max((double)MinWidth / ow, (double)mw / ow);

            string fmt = sourceFormat;
            var (outBytes, w, h) = Encode(src, scale, fmt, jpegQuality);

            if (maxBytes is int mb && mb > 0 && outBytes.Length > mb)
            {
                // Step 1: PNG → JPEG usually wins big for screenshots.
                if (fmt != "jpeg") { fmt = "jpeg"; (outBytes, w, h) = Encode(src, scale, fmt, jpegQuality); }
                // Step 2: shrink until it fits or we hit the width floor.
                int guard = 0;
                while (outBytes.Length > mb && w > MinWidth && guard++ < 12)
                {
                    scale *= 0.8;
                    (outBytes, w, h) = Encode(src, scale, fmt, jpegQuality);
                }
            }

            return new ScaledImage(outBytes, w, h, fmt, Math.Round((double)w / ow, 4));
        }
        catch
        {
            // If anything goes wrong decoding/resizing, fall back to the original bytes rather than failing capture.
            return new ScaledImage(bytes, 0, 0, sourceFormat, 1.0);
        }
    }

    private static (byte[] bytes, int w, int h) Encode(Bitmap src, double scale, string format, int jpegQuality)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var ms = new MemoryStream();
        if (Math.Abs(scale - 1.0) < 1e-6)
        {
            Save(src, ms, format, jpegQuality);
        }
        else
        {
            using var dst = new Bitmap(w, h);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            Save(dst, ms, format, jpegQuality);
        }
        return (ms.ToArray(), w, h);
    }

    private static void Save(Bitmap bmp, Stream s, string format, int jpegQuality)
    {
        if (format == "jpeg")
        {
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == SdImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
            bmp.Save(s, enc, ep);
        }
        else bmp.Save(s, SdImageFormat.Png);
    }

    // Copy into an independent Bitmap so we can close the stream (GDI+ otherwise keeps the stream locked).
    private static Bitmap LoadBitmap(byte[] bytes) { using var ms = new MemoryStream(bytes); using var img = Image.FromStream(ms); return new Bitmap(img); }
}
