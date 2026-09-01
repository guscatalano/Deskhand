using System.Drawing;
using System.Drawing.Imaging;
using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>Template (image) matching: locate a needle inside a haystack and hand back click-ready coordinates.</summary>
public class VisionTests
{
    private static byte[] Png(Bitmap b) { using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray(); }

    [Fact]
    public void Find_locates_a_template_placed_in_the_haystack()
    {
        using var hay = new Bitmap(200, 150);
        using (var g = Graphics.FromImage(hay))
        {
            g.Clear(Color.FromArgb(28, 28, 38));
            using var pen = new Pen(Color.FromArgb(60, 60, 84));      // texture so the background isn't flat
            for (int i = 0; i < 200; i += 12) g.DrawLine(pen, i, 0, i, 150);
            g.FillRectangle(Brushes.OrangeRed, 120, 80, 24, 24);      // distinctive marker
            g.FillEllipse(Brushes.White, 126, 86, 12, 12);
        }
        using var needle = hay.Clone(new Rectangle(120, 80, 24, 24), hay.PixelFormat);

        var res = TemplateMatchService.Find(Png(hay), Png(needle), threshold: 0.8, maxResults: 5);

        Assert.True(res.Ok, res.Error);
        Assert.NotNull(res.Best);
        Assert.True(res.Best!.Score > 0.9, $"score was {res.Best.Score}");
        Assert.InRange(res.Best.CenterX, 132 - 2, 132 + 2);          // 120 + 24/2
        Assert.InRange(res.Best.CenterY, 92 - 2, 92 + 2);            // 80 + 24/2
    }

    [Fact]
    public void Find_applies_the_screen_offset_to_results()
    {
        using var hay = new Bitmap(120, 120);
        using (var g = Graphics.FromImage(hay)) { g.Clear(Color.Navy); g.FillRectangle(Brushes.Yellow, 40, 30, 16, 16); g.FillEllipse(Brushes.Black, 44, 34, 8, 8); }
        using var needle = hay.Clone(new Rectangle(40, 30, 16, 16), hay.PixelFormat);

        var res = TemplateMatchService.Find(Png(hay), Png(needle), 0.8, 5, offsetX: 1000, offsetY: 500);

        Assert.True(res.Ok, res.Error);
        Assert.InRange(res.Best!.CenterX, 1000 + 48 - 2, 1000 + 48 + 2);
        Assert.InRange(res.Best.CenterY, 500 + 38 - 2, 500 + 38 + 2);
    }

    [Fact]
    public void Flat_template_is_rejected_with_a_clear_message()
    {
        using var hay = new Bitmap(80, 80);
        using var needle = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(needle)) g.Clear(Color.Gray);   // single color → nothing to match
        var res = TemplateMatchService.Find(Png(hay), Png(needle), 0.8, 5);
        Assert.False(res.Ok);
        Assert.Contains("flat", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Template_larger_than_haystack_is_rejected()
    {
        using var hay = new Bitmap(40, 40);
        using var needle = new Bitmap(80, 80);
        using (var g = Graphics.FromImage(needle)) { g.Clear(Color.Black); g.FillRectangle(Brushes.Red, 5, 5, 40, 40); }
        var res = TemplateMatchService.Find(Png(hay), Png(needle), 0.8, 5);
        Assert.False(res.Ok);
        Assert.Contains("larger", res.Error!);
    }
}
