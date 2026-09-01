using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

/// <summary>Where to capture from, for the wait/find/click compositions. target = screen | region | window.</summary>
public record CaptureSpec(string? Target = "screen", int? Monitor = null, int? X = null, int? Y = null,
    int? Width = null, int? Height = null, long? Hwnd = null, string? Reference = null);

public record ImageWaitDto(bool Found, long WaitedMs, ImageMatchResultDto Result);
public record TextWaitDto(bool Found, long WaitedMs, string? MatchText, int? CenterX, int? CenterY, int WordCount, string? FullText = null);
public record StableWaitDto(bool Ok, long WaitedMs, double LastDiff, string Mode);
public record VisionClickDto(bool Clicked, string What, int? X, int? Y, double? Score, string? Error = null);
public record PixelDto(bool Ok, int X, int Y, int R, int G, int B, string? Hex, string? Error = null);

/// <summary>
/// Higher-order vision compositions built on capture + OCR + template matching + input, all through a single
/// <see cref="IAutomationBackend"/> — so the local server and a fleet agent run the *same* loop (the agent runs
/// it locally from one RPC, not a screenshot per poll). Governance rides along: capture/input on the backend is
/// still gated by armed/capability, so e.g. click_* throws Disarmed when disarmed.
/// </summary>
public static class VisionOps
{
    private const int MinPoll = 50;

    // ---- wait for a template image to appear (or disappear) ----
    public static ImageWaitDto WaitForImage(IAutomationBackend b, byte[] needle, CaptureSpec spec,
        double threshold, int timeoutMs, bool appear, int pollMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 0, 600_000);
        pollMs = Math.Clamp(pollMs, MinPoll, 10_000);
        var sw = Stopwatch.StartNew();
        ImageMatchResultDto last;
        while (true)
        {
            var cap = Capture(b, spec);
            last = TemplateMatchService.Find(cap.Bytes, needle, threshold, 10, cap.Rect.X, cap.Rect.Y);
            bool present = last.Ok && last.Count > 0;
            if (present == appear) return new ImageWaitDto(true, sw.ElapsedMilliseconds, last);
            if (sw.ElapsedMilliseconds >= timeoutMs) return new ImageWaitDto(false, sw.ElapsedMilliseconds, last);
            Thread.Sleep(pollMs);
        }
    }

    // ---- wait for OCR text to appear (or disappear) ----
    public static TextWaitDto WaitForText(IAutomationBackend b, string query, CaptureSpec spec,
        int timeoutMs, bool appear, int pollMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 0, 600_000);
        pollMs = Math.Clamp(pollMs, MinPoll, 10_000);
        query = (query ?? "").Trim();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var cap = Capture(b, spec);
            var ocr = OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y);
            OcrWordDto? hit = FindWord(ocr, query);
            bool present = hit is not null;
            if (present == appear)
                return new TextWaitDto(true, sw.ElapsedMilliseconds, hit?.Text,
                    hit is null ? null : hit.X + hit.Width / 2, hit is null ? null : hit.Y + hit.Height / 2,
                    ocr.WordCount, ocr.Text);
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return new TextWaitDto(false, sw.ElapsedMilliseconds, null, null, null, ocr.WordCount, ocr.Text);
            Thread.Sleep(pollMs);
        }
    }

    // ---- wait until a region stops changing (settle) or starts changing ----
    public static StableWaitDto WaitStable(IAutomationBackend b, CaptureSpec spec, int settleMs, int timeoutMs, int pollMs, double epsilon, bool waitForChange)
    {
        timeoutMs = Math.Clamp(timeoutMs, 0, 600_000);
        pollMs = Math.Clamp(pollMs, MinPoll, 10_000);
        settleMs = Math.Clamp(settleMs, 0, timeoutMs);
        epsilon = Math.Clamp(epsilon, 0.0, 1.0);
        var sw = Stopwatch.StartNew();
        var (prev, pw, ph) = LoadGray(Capture(b, spec).Bytes);
        long stableSince = sw.ElapsedMilliseconds;
        double diff = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollMs);
            var (cur, cw, ch) = LoadGray(Capture(b, spec).Bytes);
            diff = (cw == pw && ch == ph) ? MeanAbsDiff(prev, cur) : 1.0;   // size change = fully changed
            prev = cur; pw = cw; ph = ch;
            if (waitForChange)
            {
                if (diff > epsilon) return new StableWaitDto(true, sw.ElapsedMilliseconds, diff, "change");
            }
            else
            {
                if (diff > epsilon) stableSince = sw.ElapsedMilliseconds;   // reset the settle clock
                else if (sw.ElapsedMilliseconds - stableSince >= settleMs) return new StableWaitDto(true, sw.ElapsedMilliseconds, diff, "stable");
            }
        }
        return new StableWaitDto(false, sw.ElapsedMilliseconds, diff, waitForChange ? "change" : "stable");
    }

    // ---- find an image, then click/double-click its best match ----
    public static VisionClickDto ClickImage(IAutomationBackend b, byte[] needle, CaptureSpec spec,
        double threshold, string button, int count, int timeoutMs)
    {
        var w = WaitForImage(b, needle, spec, threshold, timeoutMs, appear: true, pollMs: 250);
        var m = w.Result.Best;
        if (m is null) return new VisionClickDto(false, "image", null, null, null, $"Template not found (best-effort over {w.WaitedMs} ms).");
        b.MouseClick(button, m.CenterX, m.CenterY, count);
        return new VisionClickDto(true, "image", m.CenterX, m.CenterY, m.Score);
    }

    // ---- find OCR text, then click/double-click it ----
    public static VisionClickDto ClickText(IAutomationBackend b, string query, CaptureSpec spec,
        string button, int count, int timeoutMs)
    {
        var w = WaitForText(b, query, spec, timeoutMs, appear: true, pollMs: 250);
        if (!w.Found || w.CenterX is null) return new VisionClickDto(false, "text", null, null, null, $"Text '{query}' not found (over {w.WaitedMs} ms).");
        b.MouseClick(button, w.CenterX.Value, w.CenterY!.Value, count);
        return new VisionClickDto(true, "text", w.CenterX, w.CenterY, null);
    }

    // ---- read the color of a single screen pixel ----
    public static PixelDto GetPixel(IAutomationBackend b, int x, int y)
    {
        try
        {
            var cap = b.CaptureRegion(x, y, 1, 1, ImageFormat.Png, 100);
            using var ms = new MemoryStream(cap.Bytes);
            using var bmp = new Bitmap(ms);
            var c = bmp.GetPixel(0, 0);
            return new PixelDto(true, x, y, c.R, c.G, c.B, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
        }
        catch (Exception ex) { return new PixelDto(false, x, y, 0, 0, 0, null, ex.Message); }
    }

    // ---- helpers ----

    private static CaptureResultDto Capture(IAutomationBackend b, CaptureSpec s) =>
        (s.Target ?? "screen").ToLowerInvariant() switch
        {
            "region" => b.CaptureRegion(s.X ?? 0, s.Y ?? 0, s.Width ?? 0, s.Height ?? 0, ImageFormat.Png, 100),
            "window" => s.Reference is not null ? b.CaptureWindowByRef(s.Reference, ImageFormat.Png, 100)
                                                : b.CaptureWindow(s.Hwnd ?? 0, ImageFormat.Png, 100),
            _ => b.CaptureScreen(s.Monitor, ImageFormat.Png, 100),
        };

    private static OcrWordDto? FindWord(OcrResultDto ocr, string query)
    {
        if (!ocr.Ok || query.Length == 0) return null;
        // exact-ish: a word containing the query; falls back to matching the first token of a phrase.
        var token = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? query;
        return ocr.Words.FirstOrDefault(w => w.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? ocr.Words.FirstOrDefault(w => w.Text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static (float[] pix, int w, int h) LoadGray(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            var px = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++) { int i = row + x * 4; px[y * w + x] = 0.114f * buf[i] + 0.587f * buf[i + 1] + 0.299f * buf[i + 2]; }
            }
            return (px, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static double MeanAbsDiff(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 1.0;
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length / 255.0;   // normalize to 0..1
    }
}
