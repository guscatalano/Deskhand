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
    /// <summary>OCR a PNG/BMP image. <paramref name="offsetX"/>/<paramref name="offsetY"/> are the capture's
    /// screen origin, added to every word box so the coordinates are click-ready.</summary>
    public static OcrResultDto Recognize(byte[] image, int offsetX = 0, int offsetY = 0)
    {
        if (image is null || image.Length == 0)
            return new OcrResultDto(false, "", Array.Empty<OcrWordDto>(), 0, 0, "No image data.");
        try { return RecognizeAsync(image, offsetX, offsetY).GetAwaiter().GetResult(); }
        catch (Exception ex) { return new OcrResultDto(false, "", Array.Empty<OcrWordDto>(), 0, 0, Describe(ex)); }
    }

    private static async Task<OcrResultDto> RecognizeAsync(byte[] image, int offsetX, int offsetY)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return new OcrResultDto(false, "", Array.Empty<OcrWordDto>(), 0, 0,
                "No OCR language is installed. Add one: Settings → Time & language → Language → add a language pack with the optional 'Basic typing / OCR' feature.");

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
                words.Add(new OcrWordDto(w.Text,
                    offsetX + (int)Math.Round(r.X), offsetY + (int)Math.Round(r.Y),
                    (int)Math.Round(r.Width), (int)Math.Round(r.Height)));
            }

        string text = string.Join("\n", result.Lines.Select(l => l.Text));
        return new OcrResultDto(true, text, words, words.Count, result.Lines.Count);
    }

    private static string Describe(Exception ex) =>
        ex is TypeLoadException or DllNotFoundException or NotSupportedException
            ? "Windows OCR is unavailable on this system: " + ex.Message
            : ex.GetType().Name + ": " + ex.Message;
}
