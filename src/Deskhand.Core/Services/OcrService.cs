using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Deskhand.Core.Services;

public record OcrWordDto(string Text, int X, int Y, int Width, int Height);
public record OcrResultDto(bool Ok, string Text, IReadOnlyList<OcrWordDto> Words, int WordCount, int LineCount, string? Error = null);

/// <summary>
/// Read text off the screen with the Windows OCR engine (Windows.Media.Ocr — built into Windows, no external
/// dependency or network). This is the answer for apps UI Automation can't see into (custom-drawn, Chromium
/// canvases, games, remote-desktop pixels): capture pixels, get back the text plus each word's on-screen
/// bounding box so you can click it. Word boxes are returned in <b>screen</b> coordinates (the capture's
/// origin is added in), ready to hand to a mouse-move/click.
/// </summary>
public static class OcrService
{
    /// <summary>
    /// OCR a PNG/BMP image. <paramref name="offsetX"/>/<paramref name="offsetY"/> are the capture's screen
    /// origin, added to every word box so the coordinates are click-ready. <paramref name="scale"/> upscales the
    /// image before OCR (word boxes are mapped back to original pixels): the Windows OCR engine returns nothing
    /// for text below ~14px, so small UI text needs this — 0 (default) auto-picks a factor for small captures.
    /// </summary>
    public static OcrResultDto Recognize(byte[] image, int offsetX = 0, int offsetY = 0, double scale = 0)
    {
        if (image is null || image.Length == 0)
            return new OcrResultDto(false, "", Array.Empty<OcrWordDto>(), 0, 0, "No image data.");
        try
        {
            double factor = scale >= 1 ? Math.Min(scale, 4) : AutoFactor(image);
            byte[] ocrBytes = factor > 1.01 ? Upscale(image, factor) : image;
            var raw = RecognizeRawAsync(ocrBytes).GetAwaiter().GetResult();
            if (!raw.Ok) return new OcrResultDto(false, "", Array.Empty<OcrWordDto>(), 0, 0, raw.Error);

            // Map word boxes from upscaled px back to original image px, then add the screen offset.
            var words = raw.Words.Select(w => new OcrWordDto(w.Text,
                offsetX + (int)Math.Round(w.X / factor), offsetY + (int)Math.Round(w.Y / factor),
                (int)Math.Round(w.Width / factor), (int)Math.Round(w.Height / factor))).ToList();
            return new OcrResultDto(true, raw.Text, words, words.Count, raw.Lines);
        }
        catch (Exception ex) { return new OcrResultDto(false, "", Array.Empty<OcrWordDto>(), 0, 0, Describe(ex)); }
    }

    private record RawOcr(bool Ok, string? Error, string Text, IReadOnlyList<OcrWordDto> Words, int Lines);

    private static async Task<RawOcr> RecognizeRawAsync(byte[] image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return new RawOcr(false,
                "No OCR language is installed. Add one: Settings → Time & language → Language → add a language pack with the optional 'Basic typing / OCR' feature.",
                "", Array.Empty<OcrWordDto>(), 0);

        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(image);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bmp = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bmp);

        var words = new List<OcrWordDto>();
        foreach (var line in result.Lines)
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWordDto(w.Text, (int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height)));
            }
        string text = string.Join("\n", result.Lines.Select(l => l.Text));
        return new RawOcr(true, null, text, words, result.Lines.Count);
    }

    // Small captures need upscaling to cross the engine's ~14px floor; big ones (a full screen) are left alone
    // so we don't quadruple pixels/time. Capped so the upscaled image stays under ~40 MP.
    private static double AutoFactor(byte[] image)
    {
        try
        {
            using var ms = new MemoryStream(image);
            using var img = System.Drawing.Image.FromStream(ms);
            int min = Math.Min(img.Width, img.Height);
            double f = Math.Clamp(Math.Round(1500.0 / Math.Max(1, min)), 1, 3);
            while (f > 1 && (long)(img.Width * f) * (long)(img.Height * f) > 40_000_000) f--;
            return f;
        }
        catch { return 1; }
    }

    private static byte[] Upscale(byte[] image, double factor)
    {
        using var ms = new MemoryStream(image);
        using var src = System.Drawing.Image.FromStream(ms);
        int w = (int)Math.Round(src.Width * factor), h = (int)Math.Round(src.Height * factor);
        using var big = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        using var outMs = new MemoryStream();
        big.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
        return outMs.ToArray();
    }

    private static string Describe(Exception ex) =>
        ex is TypeLoadException or DllNotFoundException or NotSupportedException
            ? "Windows OCR is unavailable on this system: " + ex.Message
            : ex.GetType().Name + ": " + ex.Message;
}
