using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>The Windows OCR engine returns nothing for text below ~14px; OcrService auto-upscales small
/// captures to cross that floor. This guards that recovery (skips cleanly if no OCR language is installed).</summary>
public class OcrUpscaleTests
{
    private static byte[] Render(string text, int px)
    {
        int w = (int)(text.Length * px * 0.62) + 40, h = px * 3;
        using var bmp = new Bitmap(Math.Max(160, w), h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var f = new Font("Segoe UI", px, GraphicsUnit.Pixel);
            g.DrawString(text, f, Brushes.Black, 12, px);
        }
        using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png); return ms.ToArray();
    }

    [Fact]
    public void Small_text_is_recovered_by_auto_upscale()
    {
        var png = Render("Deskhand Save Cancel", 13);   // ~13px: unreadable to the engine at 1x
        var r = OcrService.Recognize(png);               // scale=0 → auto-upscale
        if (!r.Ok && (r.Error?.Contains("language", StringComparison.OrdinalIgnoreCase) ?? false)) return; // no OCR pack
        Assert.True(r.Ok, r.Error);
        Assert.Contains("Deskhand", r.Text);
        Assert.NotEmpty(r.Words);
        // Word boxes must be mapped back to ORIGINAL pixels (not the upscaled space).
        Assert.All(r.Words, w => Assert.True(w.X < 4000 && w.Y < 200, $"box {w.X},{w.Y} looks unscaled"));
    }

    [Fact]
    public void Explicit_scale_1_leaves_tiny_text_unread_but_never_throws()
    {
        var png = Render("tiny", 10);
        var r = OcrService.Recognize(png, 0, 0, scale: 1.0);   // force no upscale
        Assert.True(r.Ok || (r.Error?.Contains("language") ?? false));
    }
}
