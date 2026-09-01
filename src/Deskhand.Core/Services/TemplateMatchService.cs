using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record ImageMatchDto(int X, int Y, int Width, int Height, int CenterX, int CenterY, double Score);
public record ImageMatchResultDto(
    bool Ok, int Count, IReadOnlyList<ImageMatchDto> Matches, double Threshold,
    int HaystackWidth = 0, int HaystackHeight = 0, string? Error = null)
{
    /// <summary>Convenience: the single best match (highest score), or null.</summary>
    public ImageMatchDto? Best => Matches.Count > 0 ? Matches[0] : null;
}

/// <summary>
/// Find a small template image (an icon, button, cursor — the "needle") inside a larger screenshot (the
/// "haystack") by grayscale normalized cross-correlation. This is the visual complement to OCR: locate things
/// that have no text and no UIA (custom-drawn buttons, tray icons, game elements), then click/drag to the
/// returned <b>screen-coordinate</b> center. Search is coarse-to-fine — a downscaled pass finds candidates
/// fast, then each is refined at full resolution — and overlapping hits are de-duplicated (non-max suppression).
/// NCC is robust to brightness/contrast shifts but not to scaling or rotation of the template.
/// </summary>
public static class TemplateMatchService
{
    /// <param name="offsetX">Haystack capture origin (screen X), added to every result so coordinates are click-ready.</param>
    public static ImageMatchResultDto Find(byte[] haystackImage, byte[] needleImage,
        double threshold = 0.85, int maxResults = 10, int offsetX = 0, int offsetY = 0)
    {
        if (haystackImage is null || haystackImage.Length == 0) return Err("No haystack image.");
        if (needleImage is null || needleImage.Length == 0) return Err("No template (needle) image.");
        threshold = Math.Clamp(threshold, 0.1, 1.0);
        maxResults = Math.Clamp(maxResults, 1, 100);

        Gray H, N;
        try { H = Gray.Load(haystackImage); } catch (Exception ex) { return Err("Haystack decode failed: " + ex.Message); }
        try { N = Gray.Load(needleImage); } catch (Exception ex) { return Err("Template decode failed: " + ex.Message); }

        if (N.W > H.W || N.H > H.H)
            return new ImageMatchResultDto(false, 0, Array.Empty<ImageMatchDto>(), threshold, H.W, H.H,
                $"Template ({N.W}x{N.H}) is larger than the haystack ({H.W}x{H.H}).");

        // Centered needle stats (reused at every position).
        var (nc, nNorm) = Center(N.Pix);
        if (nNorm < 1e-6)
            return new ImageMatchResultDto(false, 0, Array.Empty<ImageMatchDto>(), threshold, H.W, H.H,
                "Template is a flat (single-color) image — nothing distinctive to match.");

        // Coarse pass on downscaled images to find candidate locations cheaply. Pick the downscale factor from
        // the template size, but force at least 2x on a large haystack even for small templates — a full-res
        // exhaustive scan of a full screen is ~1e9 ops and must be avoided.
        int d = Math.Clamp(Math.Min(N.W, N.H) / 8, 1, 8);
        if (d == 1 && Math.Min(N.W, N.H) >= 4 && (H.W > 800 || H.H > 800)) d = 2;
        var candidates = d > 1 ? Coarse(H, N, d, threshold) : FullPositions(H, N);

        // Refine each candidate at full resolution in a small neighborhood; keep those clearing the threshold.
        var hits = new List<ImageMatchDto>();
        foreach (var (cx, cy) in candidates)
        {
            int x0 = Math.Max(0, cx - d), y0 = Math.Max(0, cy - d);
            int x1 = Math.Min(H.W - N.W, cx + d), y1 = Math.Min(H.H - N.H, cy + d);
            double best = -2; int bx = cx, by = cy;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double s = Ncc(H, x, y, N.W, N.H, nc, nNorm);
                    if (s > best) { best = s; bx = x; by = y; }
                }
            if (best >= threshold)
                hits.Add(new ImageMatchDto(bx + offsetX, by + offsetY, N.W, N.H,
                    bx + offsetX + N.W / 2, by + offsetY + N.H / 2, Math.Round(best, 4)));
        }

        // Non-max suppression: drop lower-scoring hits that overlap a better one.
        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<ImageMatchDto>();
        foreach (var m in hits)
        {
            bool overlaps = kept.Any(k => Math.Abs(k.X - m.X) < N.W / 2 && Math.Abs(k.Y - m.Y) < N.H / 2);
            if (!overlaps) kept.Add(m);
            if (kept.Count >= maxResults) break;
        }
        return new ImageMatchResultDto(true, kept.Count, kept, threshold, H.W, H.H);
    }

    // Coarse search over downscaled images → candidate top-left positions (mapped back to full-res coords).
    private static List<(int x, int y)> Coarse(Gray H, Gray N, int d, double threshold)
    {
        var hs = H.Downscale(d); var ns = N.Downscale(d);
        var cand = new List<(int x, int y)>();
        if (ns.W == 0 || ns.H == 0 || ns.W > hs.W || ns.H > hs.H) return FullPositionsStep(H, N, d);
        var (nc, nNorm) = Center(ns.Pix);
        if (nNorm < 1e-6) return FullPositionsStep(H, N, d);

        double coarseThresh = Math.Max(0.1, threshold - 0.15);   // looser at low res, refined later
        var scored = new List<(double s, int x, int y)>();
        for (int y = 0; y <= hs.H - ns.H; y++)
            for (int x = 0; x <= hs.W - ns.W; x++)
            {
                double s = Ncc(hs, x, y, ns.W, ns.H, nc, nNorm);
                if (s >= coarseThresh) scored.Add((s, x, y));
            }
        scored.Sort((a, b) => b.s.CompareTo(a.s));
        foreach (var c in scored.Take(200))                       // map coarse → full-res top-left
            cand.Add((Math.Min(H.W - N.W, c.x * d), Math.Min(H.H - N.H, c.y * d)));
        return cand;
    }

    private static List<(int, int)> FullPositions(Gray H, Gray N)
    {
        var list = new List<(int, int)>();
        for (int y = 0; y <= H.H - N.H; y++)
            for (int x = 0; x <= H.W - N.W; x++) list.Add((x, y));
        return list;
    }
    private static List<(int, int)> FullPositionsStep(Gray H, Gray N, int step)
    {
        var list = new List<(int, int)>();
        for (int y = 0; y <= H.H - N.H; y += step)
            for (int x = 0; x <= H.W - N.W; x += step) list.Add((x, y));
        return list;
    }

    // Normalized cross-correlation of the needle against the haystack window at (ox,oy). Range [-1,1].
    private static double Ncc(Gray H, int ox, int oy, int nw, int nh, float[] needleCentered, double needleNorm)
    {
        double sum = 0; int count = nw * nh;
        for (int y = 0; y < nh; y++) { int hr = (oy + y) * H.W + ox; for (int x = 0; x < nw; x++) sum += H.Pix[hr + x]; }
        double mean = sum / count;
        double dot = 0, winSq = 0;
        for (int y = 0; y < nh; y++)
        {
            int hr = (oy + y) * H.W + ox; int nr = y * nw;
            for (int x = 0; x < nw; x++)
            {
                double hv = H.Pix[hr + x] - mean;
                dot += hv * needleCentered[nr + x];
                winSq += hv * hv;
            }
        }
        double denom = Math.Sqrt(winSq) * needleNorm;
        return denom < 1e-6 ? 0 : dot / denom;
    }

    private static (float[] centered, double norm) Center(float[] pix)
    {
        double mean = 0; foreach (var v in pix) mean += v; mean /= pix.Length;
        var c = new float[pix.Length]; double sq = 0;
        for (int i = 0; i < pix.Length; i++) { c[i] = (float)(pix[i] - mean); sq += c[i] * c[i]; }
        return (c, Math.Sqrt(sq));
    }

    private static ImageMatchResultDto Err(string msg) =>
        new(false, 0, Array.Empty<ImageMatchDto>(), 0, 0, 0, msg);

    // Grayscale image buffer decoded from PNG/BMP/JPEG bytes.
    private sealed class Gray
    {
        public required float[] Pix;
        public required int W;
        public required int H;

        public static Gray Load(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var bmp = new Bitmap(ms);
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                var px = new float[w * h];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;                         // BGRA
                        px[y * w + x] = 0.114f * buf[i] + 0.587f * buf[i + 1] + 0.299f * buf[i + 2];
                    }
                }
                return new Gray { Pix = px, W = w, H = h };
            }
            finally { bmp.UnlockBits(data); }
        }

        // Box-average downscale by an integer factor.
        public Gray Downscale(int d)
        {
            int nw = W / d, nh = H / d;
            var o = new float[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    double s = 0;
                    for (int yy = 0; yy < d; yy++) { int row = (y * d + yy) * W + x * d; for (int xx = 0; xx < d; xx++) s += Pix[row + xx]; }
                    o[y * nw + x] = (float)(s / (d * d));
                }
            return new Gray { Pix = o, W = nw, H = nh };
        }
    }
}
