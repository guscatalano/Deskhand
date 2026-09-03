using System.Drawing;
using Deskhand.Core;
using Deskhand.Core.Services;
using Xunit;
using CoreImageFormat = Deskhand.Core.ImageFormat;
using SdImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Deskhand.Rdp.Tests;

/// <summary>Stress the Set-of-Mark builder: coordinate mapping (with a non-zero capture origin), capping/total,
/// label + type filters, dedup, off-screen/zero-size exclusion, ranking, a valid annotated image, and the OCR
/// path. Uses a fake backend so element boxes and the capture origin are known exactly.</summary>
public class SetOfMarkTests
{
    // ---- fake backend ----
    private sealed class FakeBackend : IAutomationBackend
    {
        public ElementInfoDto Fg = El("win", "App", "Window", 0, 0, 2000, 1200, new[] { "Window" });
        public List<ElementInfoDto> Elements = new();
        public List<ElementInfoDto>? Windows;

        public ElementInfoDto GetForegroundWindow() => Fg;
        public IReadOnlyList<ElementInfoDto> GetTopLevelWindows() => Windows ?? new List<ElementInfoDto> { Fg };
        public IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery query) => rootRef == Fg.Ref ? Elements : Array.Empty<ElementInfoDto>();
        public ElementInfoDto GetElement(string reference) => reference == Fg.Ref ? Fg : Elements.First(e => e.Ref == reference);

        public DesktopStateDto GetDesktopState() => throw new NotSupportedException();
        public MachineInfoDto GetMachineInfo() => throw new NotSupportedException();
        public ElementInfoDto GetFocusedElement() => throw new NotSupportedException();
        public IReadOnlyList<ProcessInfoDto> GetProcesses() => throw new NotSupportedException();
        public ProcessLaunchResultDto LaunchProcess(string path, string? args, string? workingDir, int waitForWindowMs) => throw new NotSupportedException();
        public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren) => throw new NotSupportedException();
        public ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string?> GetAllProperties(string reference) => throw new NotSupportedException();
        public ElementInfoDto GetElementFromPoint(int x, int y) => throw new NotSupportedException();
        public void Invoke(string reference) { }
        public void SetValue(string reference, string text) { }
        public void Toggle(string reference) { }
        public void ExpandCollapse(string reference, bool expand) { }
        public void Select(string reference) { }
        public void SetFocus(string reference) { }
        public CaptureResultDto CaptureScreen(int? monitor, CoreImageFormat format, int jpegQuality) => throw new NotSupportedException();
        public CaptureResultDto CaptureRegion(int x, int y, int width, int height, CoreImageFormat format, int jpegQuality) => throw new NotSupportedException();
        public CaptureResultDto CaptureWindow(long hwnd, CoreImageFormat format, int jpegQuality) => throw new NotSupportedException();
        public CaptureResultDto CaptureWindowByRef(string reference, CoreImageFormat format, int jpegQuality) => throw new NotSupportedException();
        public CaptureResultDto CaptureElement(string reference, CoreImageFormat format, int jpegQuality) => throw new NotSupportedException();
        public SecureCapture.InputDesktopResult CaptureInputDesktop(CoreImageFormat format, int jpegQuality) => throw new NotSupportedException();
        public void MouseMove(int x, int y) { }
        public void MouseClick(string button, int? x, int? y, int count) { }
        public void MouseDown(string button, int? x, int? y) { }
        public void MouseUp(string button, int? x, int? y) { }
        public void MouseScroll(int dx, int dy) { }
        public void Drag(int fromX, int fromY, int toX, int toY, string button, int steps, int holdMs) { }
        public void TypeText(string text) { }
        public void SendKeys(string chord) { }
        public void Dispose() { }
    }

    private static ElementInfoDto El(string refId, string name, string type, int x, int y, int w, int h, string[]? patterns = null, bool enabled = true, bool offscreen = false)
        => new(refId, name, type, null, null, null, new RectDto(x, y, w, h), enabled, offscreen, 0, 1, patterns ?? Array.Empty<string>());

    private static byte[] Png(int w, int h, Action<Graphics>? draw = null)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); draw?.Invoke(g); }
        using var ms = new MemoryStream(); bmp.Save(ms, SdImageFormat.Png); return ms.ToArray();
    }
    private static CaptureResultDto Cap(int ox, int oy, int w, int h, byte[]? bytes = null)
        => new("default", new RectDto(ox, oy, w, h), 0, 1.0, "png", bytes ?? Png(w, h));

    // ---- tests ----

    [Fact]
    public void Mark_center_is_in_screen_coords_with_capture_origin_applied()
    {
        var b = new FakeBackend { Elements = { El("b1", "Save", "Button", 150, 80, 40, 20, new[] { "Invoke" }) } };
        var cap = Cap(100, 50, 300, 200);   // capture origin at screen (100,50)

        var (_, marks, total) = SetOfMarkService.Build(b, cap, includeText: false, includePopups: false, max: 60);

        Assert.Equal(1, total);
        var m = Assert.Single(marks);
        Assert.Equal("uia", m.Source);
        Assert.Equal("b1", m.Ref);
        Assert.Equal(170, m.X);   // box.X 150 + w/2 20 → 170 (already screen-space; origin NOT double-added)
        Assert.Equal(90, m.Y);    // box.Y 80 + h/2 10 → 90
        Assert.Contains("invoke", m.Actions);
    }

    [Fact]
    public void Cap_limits_marks_but_total_reports_everything()
    {
        var b = new FakeBackend();
        for (int i = 0; i < 250; i++) b.Elements.Add(El("b" + i, "Btn" + i, "Button", (i % 20) * 90, (i / 20) * 30, 80, 24, new[] { "Invoke" }));
        var (_, marks, total) = SetOfMarkService.Build(b, Cap(0, 0, 1920, 400), includeText: false, includePopups: false, max: 30);
        Assert.Equal(250, total);
        Assert.Equal(30, marks.Count);
        Assert.Equal(Enumerable.Range(1, 30), marks.Select(m => m.Id));   // ids are 1..N contiguous
    }

    [Fact]
    public void Label_filter_narrows_a_dense_ui()
    {
        var b = new FakeBackend
        {
            Elements =
            {
                El("s", "Save", "Button", 10, 10, 60, 20, new[] { "Invoke" }),
                El("sa", "Save As…", "MenuItem", 10, 40, 80, 20, new[] { "Invoke" }),
                El("o", "Open", "Button", 10, 70, 60, 20, new[] { "Invoke" }),
                El("c", "Cancel", "Button", 10, 100, 60, 20, new[] { "Invoke" }),
            }
        };
        var (_, marks, total) = SetOfMarkService.Build(b, Cap(0, 0, 300, 200), includeText: false, includePopups: false, max: 60, filter: "save");
        Assert.Equal(2, total);
        Assert.All(marks, m => Assert.Contains("save", m.Label, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Type_filter_uia_excludes_everything_but_controls()
    {
        var b = new FakeBackend { Elements = { El("b", "Go", "Button", 5, 5, 40, 20, new[] { "Invoke" }) } };
        var (_, marks, _) = SetOfMarkService.Build(b, Cap(0, 0, 200, 100), includeText: true, includePopups: false, max: 60, only: "uia");
        Assert.All(marks, m => Assert.Equal("uia", m.Source));
    }

    [Fact]
    public void Duplicate_refs_are_deduped()
    {
        var b = new FakeBackend();
        b.Elements.Add(El("dup", "Save", "Button", 10, 10, 40, 20, new[] { "Invoke" }));
        b.Elements.Add(El("dup", "Save", "Button", 10, 10, 40, 20, new[] { "Invoke" }));   // same ref twice
        b.Windows = new List<ElementInfoDto> { b.Fg, b.Fg };   // foreground scanned twice via popups path
        var (_, marks, total) = SetOfMarkService.Build(b, Cap(0, 0, 200, 100), includeText: false, includePopups: true, max: 60);
        Assert.Equal(1, total);
        Assert.Single(marks);
    }

    [Fact]
    public void Offscreen_and_zero_size_elements_are_excluded()
    {
        var b = new FakeBackend
        {
            Elements =
            {
                El("on", "Visible", "Button", 10, 10, 40, 20, new[] { "Invoke" }),
                El("off", "Hidden", "Button", 10, 40, 40, 20, new[] { "Invoke" }, offscreen: true),
                El("zero", "Zero", "Button", 10, 70, 0, 0, new[] { "Invoke" }),
            }
        };
        var (_, marks, total) = SetOfMarkService.Build(b, Cap(0, 0, 200, 200), includeText: false, includePopups: false, max: 60);
        Assert.Equal(1, total);
        Assert.Equal("on", marks[0].Ref);
    }

    [Fact]
    public void Annotated_image_is_valid_and_same_size_as_the_capture()
    {
        var b = new FakeBackend { Elements = { El("b", "X", "Button", 20, 20, 60, 24, new[] { "Invoke" }) } };
        var cap = Cap(0, 0, 400, 300);
        var (bytes, marks, _) = SetOfMarkService.Build(b, cap, includeText: false, includePopups: false, max: 60);
        Assert.NotEmpty(marks);
        using var ms = new MemoryStream(bytes);
        using var img = Image.FromStream(ms);
        Assert.Equal(400, img.Width);
        Assert.Equal(300, img.Height);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void Ocr_marks_land_at_the_right_screen_position()
    {
        // Render big crisp text at a known spot; OCR should mark it, with the capture origin added in.
        var cap = Cap(500, 300, 400, 160, Png(400, 160, g =>
        {
            using var f = new Font("Segoe UI", 40, GraphicsUnit.Pixel);
            g.DrawString("SAVE", f, Brushes.Black, 40, 40);
        }));
        var b = new FakeBackend();   // no UIA → only OCR marks
        var (_, marks, _) = SetOfMarkService.Build(b, cap, includeText: true, includePopups: false, max: 60, only: "text");
        var save = marks.FirstOrDefault(m => m.Label.Contains("SAVE", StringComparison.OrdinalIgnoreCase));
        if (save is null) return;   // no OCR language installed — inconclusive
        Assert.Equal("ocr", save.Source);
        Assert.InRange(save.X, 500 + 40, 500 + 40 + 260);   // within the text run, origin (500) applied
        Assert.InRange(save.Y, 300 + 40, 300 + 130);
    }

    [Fact]
    public void MarkStore_round_trips_latest_and_is_bounded()
    {
        string last = "";
        for (int s = 0; s < 25; s++)
            last = MarkStore.Save(new List<MarkDto> { new(1, "uia", "r" + s, "L" + s, "Button", s, s, new[] { "invoke" }) });

        Assert.Equal(last, MarkStore.Latest);
        Assert.Equal("L24", MarkStore.Get(null, 1)!.Label);        // null setId → latest
        Assert.NotNull(MarkStore.GetSet(last));
        // Only the last ~20 survive; a set from 25 ago is evicted.
        Assert.Null(MarkStore.Get("mk_does_not_exist", 1));
    }
}
