using System.Drawing;
using System.Drawing.Imaging;
using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>Fitting a screenshot to a resolution cap (maxWidth) and an output-size budget (maxBytes),
/// including the PNG→JPEG switch when bytes alone can't be met by resolution.</summary>
public class ImageScalerTests
{
    // A noisy image so PNG doesn't trivially compress to nothing.
    private static byte[] NoisyPng(int w, int h)
    {
        using var bmp = new Bitmap(w, h);
        var rnd = new Random(1);
        using (var g = Graphics.FromImage(bmp))
            for (int i = 0; i < 4000; i++)
                using (var br = new SolidBrush(Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256))))
                    g.FillRectangle(br, rnd.Next(w), rnd.Next(h), rnd.Next(30), rnd.Next(30));
        using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png); return ms.ToArray();
    }

    [Fact]
    public void No_budget_returns_input_unchanged()
    {
        var src = NoisyPng(300, 200);
        var r = ImageScaler.Fit(src, "png", null, null);
        Assert.Equal(1.0, r.Scale);
        Assert.Same(src, r.Bytes);
    }

    [Fact]
    public void MaxWidth_caps_the_resolution()
    {
        var src = NoisyPng(1000, 800);
        var r = ImageScaler.Fit(src, "png", maxWidth: 200, maxBytes: null);
        Assert.Equal(200, r.Width);
        Assert.Equal(160, r.Height);              // aspect preserved
        Assert.Equal(0.2, r.Scale, 3);
        Assert.Equal("png", r.Format);
    }

    [Fact]
    public void MaxBytes_switches_to_jpeg_and_fits_the_budget()
    {
        var src = NoisyPng(1200, 900);
        var r = ImageScaler.Fit(src, "png", maxWidth: null, maxBytes: 40_000);
        Assert.Equal("jpeg", r.Format);           // PNG couldn't fit → re-encoded as JPEG
        Assert.True(r.Bytes.Length <= 40_000, $"was {r.Bytes.Length}");
        Assert.True(r.Scale <= 1.0);
    }

    [Fact]
    public void Fit_falls_back_to_original_on_garbage_input()
    {
        var junk = new byte[] { 1, 2, 3, 4 };
        var r = ImageScaler.Fit(junk, "png", 100, 100);
        Assert.Same(junk, r.Bytes);               // couldn't decode → original returned, capture never fails
        Assert.Equal(1.0, r.Scale);
    }
}
