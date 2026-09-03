using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SdImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Deskhand.Core.Services;

public record MarkDto(int Id, string Source, string? Ref, string Label, string Type, int X, int Y,
    IReadOnlyList<string> Actions);

/// <summary>
/// Set-of-Mark: draw numbered boxes over every actionable target on a screenshot and return a legend, so a model
/// picks a NUMBER instead of guessing a pixel. This closes the gap where coordinate-free targets lose to the
/// image — now the targets ARE the image. Marks fuse UIA interactables (act by ref) and OCR text (click its
/// center). The mark set is remembered so a follow-up acts by id (see <see cref="MarkStore"/> / act_mark).
/// </summary>
public static class SetOfMarkService
{
    private static readonly string[] Interactable = { "Button", "MenuItem", "TabItem", "CheckBox", "RadioButton", "ComboBox", "Edit", "Hyperlink", "ListItem", "TreeItem", "Slider", "SplitButton", "Spinner" };

    /// <summary>Annotate the captured image with numbered marks. Returns the new PNG bytes, the marks (screen
    /// coordinates), and the TOTAL number of matching targets (so the caller knows when it's truncated). UIA
    /// controls are marked first (ranked, act-by-ref); OCR text fills in for UIA-blind UIs. On a dense UI, narrow
    /// with <paramref name="filter"/> (label substring) or <paramref name="only"/> ("uia"/"text"), or mark a
    /// smaller region — rather than drawing hundreds of unreadable boxes.</summary>
    public static (byte[] bytes, IReadOnlyList<MarkDto> marks, int total) Build(IAutomationBackend b, CaptureResultDto cap,
        bool includeText, bool includePopups, int max, string? filter = null, string? only = null)
    {
        max = Math.Clamp(max, 1, 300);
        string mode = (only ?? "all").Trim().ToLowerInvariant();
        string? needle = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        var cands = new List<(string src, string? refId, string label, string type, Rectangle box, List<string> acts)>();

        // UIA interactables (with real boxes) from the foreground + optional popups.
        try
        {
            string? fgRef = null; int? fgPid = null;
            try { var fg = b.GetForegroundWindow(); fgRef = fg?.Ref; fgPid = fg?.ProcessId; } catch { }
            var roots = new List<string?> { fgRef };
            if (includePopups)
                try { foreach (var w in b.GetTopLevelWindows()) if (w.Ref != fgRef && IsPopup(w, fgPid)) roots.Add(w.Ref); } catch { }

            foreach (var root in roots)
            {
                if (root is null) continue;
                try
                {
                    foreach (var e in b.Find(root, new FindQuery(Scope: "descendants", Max: 300)))
                    {
                        if (e.IsOffscreen || e.BoundingRect is not { Width: > 0, Height: > 0 } r) continue;
                        var acts = ActionsFor(e);
                        if (acts.Count == 0 && !Interactable.Contains(e.ControlType)) continue;
                        cands.Add(("uia", e.Ref, Clean(e.Name) ?? e.ControlType, e.ControlType,
                            new Rectangle(r.X, r.Y, r.Width, r.Height), acts.Count > 0 ? acts : new List<string> { "invoke" }));
                    }
                }
                catch { }
            }
        }
        catch { }

        // OCR text targets (screen boxes) — the fallback that makes UIA-blind UIs markable.
        if (includeText)
        {
            try
            {
                var ocr = OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y);
                foreach (var w in ocr.Words)
                    if (!string.IsNullOrWhiteSpace(w.Text))
                        cands.Add(("ocr", null, w.Text, "text", new Rectangle(w.X, w.Y, Math.Max(6, w.Width), Math.Max(6, w.Height)), new List<string> { "click" }));
            }
            catch { }
        }

        // Dedup: overlapping foreground/popup scans can yield the same control twice. Key on ref (uia) or on
        // label+center (ocr), and drop near-identical boxes at the same spot.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cands = cands.Where(c =>
        {
            string key = c.refId is not null ? "r:" + c.refId
                : $"{c.src}:{c.label}:{(c.box.X + c.box.Width / 2) / 6}:{(c.box.Y + c.box.Height / 2) / 6}";
            return seen.Add(key);
        }).ToList();

        // Filter by type and by label substring, so a dense UI can be narrowed to what the model wants.
        if (mode is "uia" or "controls") cands = cands.Where(c => c.src == "uia").ToList();
        else if (mode is "text" or "ocr") cands = cands.Where(c => c.src == "ocr").ToList();
        if (needle is not null) cands = cands.Where(c => c.label.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();

        // Rank UIA before OCR, then reading order.
        cands = cands.OrderBy(c => c.src == "uia" ? 0 : 1).ThenBy(c => c.box.Y / 24).ThenBy(c => c.box.X).ToList();
        int total = cands.Count;
        if (cands.Count > max) cands = cands.GetRange(0, max);   // cap for a readable image

        var marks = new List<MarkDto>();
        for (int i = 0; i < cands.Count; i++)
        {
            var c = cands[i];
            marks.Add(new MarkDto(i + 1, c.src, c.refId, c.label, c.type,
                c.box.X + c.box.Width / 2, c.box.Y + c.box.Height / 2, c.acts));
        }

        byte[] annotated = Draw(cap, cands);
        return (annotated, marks, total);
    }

    private static byte[] Draw(CaptureResultDto cap, List<(string src, string? refId, string label, string type, Rectangle box, List<string> acts)> cands)
    {
        try
        {
            using var ms = new MemoryStream(cap.Bytes);
            using var src = Image.FromStream(ms);
            using var bmp = new Bitmap(src);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
            for (int i = 0; i < cands.Count; i++)
            {
                var c = cands[i];
                // screen → image pixels (capture origin at cap.Rect.X/Y).
                int x = c.box.X - cap.Rect.X, y = c.box.Y - cap.Rect.Y;
                var boxColor = c.src == "uia" ? Color.FromArgb(230, 0, 200, 255) : Color.FromArgb(210, 255, 210, 0);
                using var pen = new Pen(boxColor, 2);
                g.DrawRectangle(pen, x, y, c.box.Width, c.box.Height);

                string num = (i + 1).ToString();
                var sz = g.MeasureString(num, font);
                int bw = (int)sz.Width + 6, bh = (int)sz.Height + 2;
                int bx = Math.Max(0, x), by = Math.Max(0, y - bh);   // badge above the box top-left
                using var bg = new SolidBrush(Color.FromArgb(235, c.src == "uia" ? Color.FromArgb(0, 120, 200) : Color.FromArgb(160, 120, 0)));
                g.FillRectangle(bg, bx, by, bw, bh);
                g.DrawString(num, font, Brushes.White, bx + 3, by + 1);
            }
            using var outMs = new MemoryStream();
            bmp.Save(outMs, SdImageFormat.Png);
            return outMs.ToArray();
        }
        catch { return cap.Bytes; }   // if drawing fails, return the un-annotated capture
    }

    private static List<string> ActionsFor(ElementInfoDto e)
    {
        var a = new List<string>();
        foreach (var p in e.Patterns)
            switch (p) { case "Invoke": a.Add("invoke"); break; case "Toggle": a.Add("toggle"); break; case "ExpandCollapse": a.Add("expand"); break; case "Value": case "RangeValue": a.Add("setValue"); break; case "SelectionItem": a.Add("select"); break; }
        if (a.Count == 0 && e.ControlType is "Edit" or "Document") a.Add("setValue");
        return a.Distinct().ToList();
    }

    private static bool IsPopup(ElementInfoDto w, int? pid)
    {
        var cls = w.ClassName ?? "";
        if (cls is "#32768" or "#32770") return true;
        if (cls.Contains("Popup", StringComparison.OrdinalIgnoreCase) || cls.Contains("Menu", StringComparison.OrdinalIgnoreCase) || cls.Contains("Flyout", StringComparison.OrdinalIgnoreCase) || cls.Contains("DropDown", StringComparison.OrdinalIgnoreCase)) return true;
        return pid is int p && w.ProcessId == p;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > 60 ? s.Trim()[..60] + "…" : s.Trim());
}

/// <summary>Remembers Set-of-Mark sets so a follow-up can act by mark id. In-memory, bounded, per process.</summary>
public static class MarkStore
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<MarkDto>> Sets = new();
    private static readonly ConcurrentQueue<string> Order = new();
    private static volatile string _latest = "";

    public static string Save(IReadOnlyList<MarkDto> marks)
    {
        string id = "mk_" + Guid.NewGuid().ToString("N")[..8];
        Sets[id] = marks; _latest = id; Order.Enqueue(id);
        while (Order.Count > 20 && Order.TryDequeue(out var old)) Sets.TryRemove(old, out _);
        return id;
    }

    public static string Latest => _latest;
    public static IReadOnlyList<MarkDto>? GetSet(string? setId)
        => Sets.TryGetValue(string.IsNullOrEmpty(setId) ? _latest : setId!, out var m) ? m : null;
    public static MarkDto? Get(string? setId, int id) => GetSet(setId)?.FirstOrDefault(x => x.Id == id);
}
