namespace Deskhand.Core.Services;

public record UxTargetDto(
    string Source,                    // "uia" | "ocr"
    string? Ref,                      // element ref (uia only) — use with invoke/set_value/toggle/…
    string Label,                     // visible text / name
    string Type,                      // control type (uia) or "text" (ocr)
    int X, int Y,                     // click-ready SCREEN center
    bool Enabled,
    IReadOnlyList<string> Actions,    // what you can do: invoke, toggle, expand, setValue, select, click
    string? Value = null);

public record UxMapDto(
    string? Window, int? WindowHwnd, int UiaCount, int TextCount, int Returned,
    IReadOnlyList<UxTargetDto> Targets, string Note, string? Error = null);

/// <summary>
/// A compact, action-oriented map of the current screen for an agent to navigate by — instead of a verbose UIA
/// tree or a screenshot it can't see. It <b>fuses two sources</b>:
/// <list type="bullet">
///   <item><b>UIA interactables</b> — buttons, menu items, tabs, checkboxes, edits, list items … each with a
///   ref, a click-ready center, and the actions it supports (invoke / toggle / expand / setValue / select).</item>
///   <item><b>OCR text targets</b> — for apps with a thin or non-existent UIA tree (custom-drawn UIs, Chromium
///   canvases, audio plugins, games), every on-screen word becomes a click target at its center.</item>
/// </list>
/// So even a UIA-blind app is navigable: read the labels, click the coordinates. Results are ranked (enabled,
/// on-screen, reading order) and capped; pair with the tool-output budget so the map never overflows.
/// </summary>
public static class UxExplorer
{
    // Control types that are inherently interactable even if they expose no pattern name we recognize.
    private static readonly HashSet<string> Interactable = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button","MenuItem","TabItem","CheckBox","RadioButton","ComboBox","Edit","Hyperlink","ListItem",
        "TreeItem","Slider","SplitButton","Menu","Document","Spinner","Thumb","DataItem","Header","HeaderItem",
    };

    public static UxMapDto Explore(IAutomationBackend b, string? rootRef, bool includeUia = true, bool includeText = true,
        bool includeOffscreen = false, int max = 200)
    {
        max = Math.Clamp(max, 1, 1000);
        string? window = null; int? hwnd = null; RectDto? winRect = null;
        try
        {
            var root = rootRef is not null ? b.GetElement(rootRef) : b.GetForegroundWindow();
            window = root?.Name; hwnd = (int?)(root?.NativeWindowHandle); winRect = root?.BoundingRect;
            rootRef = root?.Ref ?? rootRef;
        }
        catch (Exception ex) { return new UxMapDto(null, null, 0, 0, 0, Array.Empty<UxTargetDto>(), "", "Could not resolve the target window: " + ex.Message); }

        var targets = new List<UxTargetDto>();
        int uiaCount = 0, textCount = 0;

        // ---- UIA interactables ----
        if (includeUia)
        {
            try
            {
                var found = b.Find(rootRef, new FindQuery(Scope: "descendants", Max: 800));
                foreach (var e in found)
                {
                    var actions = ActionsFor(e);
                    bool interactable = actions.Count > 0 || Interactable.Contains(e.ControlType);
                    if (!interactable) continue;
                    if (!includeOffscreen && e.IsOffscreen) continue;
                    if (e.BoundingRect is not { } r || r.Width <= 0 || r.Height <= 0) continue;
                    targets.Add(new UxTargetDto("uia", e.Ref, Clean(e.Name) ?? "", e.ControlType,
                        r.X + r.Width / 2, r.Y + r.Height / 2, e.IsEnabled,
                        actions.Count > 0 ? actions : new[] { "invoke" }));
                    uiaCount++;
                }
            }
            catch { /* thin/absent UIA tree — OCR carries the map */ }
        }

        // ---- OCR text targets (the fallback for UIA-blind UIs) ----
        if (includeText)
        {
            try
            {
                var cap = rootRef is not null && hwnd is > 0
                    ? b.CaptureWindowByRef(rootRef, ImageFormat.Png, 100)
                    : b.CaptureScreen(null, ImageFormat.Png, 100);
                var ocr = OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y);
                foreach (var w in ocr.Words)
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    targets.Add(new UxTargetDto("ocr", null, w.Text, "text",
                        w.X + w.Width / 2, w.Y + w.Height / 2, true, new[] { "click" }));
                    textCount++;
                }
            }
            catch { /* capture/OCR unavailable — UIA carries the map */ }
        }

        // Rank: enabled first, then top-to-bottom, left-to-right reading order.
        targets.Sort((p, q) =>
        {
            if (p.Enabled != q.Enabled) return q.Enabled.CompareTo(p.Enabled);
            int dy = (p.Y / 24).CompareTo(q.Y / 24);   // band by ~row so near-aligned items sort left-to-right
            return dy != 0 ? dy : p.X.CompareTo(q.X);
        });
        var page = targets.Count > max ? targets.GetRange(0, max) : targets;

        string note = uiaCount == 0
            ? "No UIA interactables — this UI is custom-drawn. Navigate by the OCR text targets: click their (x,y), or use deskhand_click_text / deskhand_find_image."
            : textCount == 0
                ? "UIA interactables only. Act on a target with deskhand_invoke(ref) / set_value(ref) / toggle(ref), or click its (x,y)."
                : "Fused UIA + OCR. Prefer a uia target (act by ref); for anything only in the OCR layer, click its (x,y) or use deskhand_click_text.";

        return new UxMapDto(Clean(window), hwnd, uiaCount, textCount, page.Count, page, note);
    }

    private static List<string> ActionsFor(ElementInfoDto e)
    {
        var a = new List<string>();
        foreach (var p in e.Patterns)
            switch (p)
            {
                case "Invoke": a.Add("invoke"); break;
                case "Toggle": a.Add("toggle"); break;
                case "ExpandCollapse": a.Add("expand"); break;
                case "Value": case "RangeValue": a.Add("setValue"); break;
                case "SelectionItem": a.Add("select"); break;
                case "ScrollItem": a.Add("scrollTo"); break;
            }
        if (a.Count == 0 && e.ControlType is "Edit" or "Document") a.Add("setValue");
        return a.Distinct().ToList();
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > 120 ? s[..120] + "…" : s;
    }
}
