using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>Clipboard round-trip, window-management guards, and OCR error handling. The clipboard is STA/
/// session-affine so the round-trip is skipped if the host can't open it; OCR's positive path needs a language
/// pack + real pixels and is covered by a live smoke, so here we only assert it fails cleanly on bad input.</summary>
public class PrimitivesTests
{
    [Fact]
    public void Clipboard_round_trips_unicode_text()
    {
        const string s = "deskhand ✓ 123 — round trip";
        var set = ClipboardService.SetText(s);
        if (!set.Ok) { Assert.Contains("clipboard", set.Error!, StringComparison.OrdinalIgnoreCase); return; } // headless host
        var get = ClipboardService.GetText();
        Assert.True(get.Ok);
        Assert.Equal(s, get.Text);
        Assert.True(get.HasText);
    }

    [Fact]
    public void Clipboard_clear_empties_text()
    {
        var c = ClipboardService.Clear();
        if (!c.Ok) { Assert.Contains("clipboard", c.Error!, StringComparison.OrdinalIgnoreCase); return; }
        var get = ClipboardService.GetText();
        Assert.True(get.Ok);
        Assert.False(get.HasText);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(123456789L)]   // almost certainly not a live HWND
    public void Window_actions_reject_an_invalid_handle(long hwnd)
    {
        var r = WindowService.Activate(hwnd);
        Assert.False(r.Ok);
        Assert.Equal("activate", r.Action);
        Assert.Contains("No window", r.Error);
        Assert.False(WindowService.Close(hwnd).Ok);
        Assert.False(WindowService.Minimize(hwnd).Ok);
        Assert.False(WindowService.SetBounds(hwnd, 0, 0, 100, 100).Ok);
    }

    [Fact]
    public void Ocr_empty_input_fails_cleanly()
    {
        var r = OcrService.Recognize(Array.Empty<byte>());
        Assert.False(r.Ok);
        Assert.Contains("No image", r.Error);
    }

    [Fact]
    public void Ocr_garbage_input_does_not_throw()
    {
        var r = OcrService.Recognize(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.False(r.Ok);                    // not a decodable image
        Assert.NotNull(r.Error);
        Assert.Empty(r.Words);
    }

    [Theory]
    [InlineData("0.2.3", "0.2.2", 1)]
    [InlineData("0.2.2", "0.2.3", -1)]
    [InlineData("0.2.2", "0.2.2", 0)]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.2.10", "0.2.9", 1)]     // numeric, not lexical
    [InlineData("0.3", "0.2.9", 1)]         // uneven segment counts
    [InlineData("0.2.3-beta", "0.2.3", 0)] // pre-release suffix ignored
    public void Version_compare_is_numeric(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(UpdateService.CompareVersions(a, b)));
}
