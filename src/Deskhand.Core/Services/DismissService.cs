using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record DismissedDto(string Window, long Hwnd, string Via);
public record DismissResultDto(int Count, IReadOnlyList<DismissedDto> Dismissed, string Note);

/// <summary>
/// Find and close top-level dialogs / modal popups in one call — the routine tax of driving a real app. It
/// dismisses <b>non-committally</b> by default: it clicks a Cancel / Close / No / Don't-Save button before it
/// would ever click OK/Yes (so it doesn't accidentally confirm a destructive prompt), and falls back to sending
/// the window a close (WM_CLOSE) if there's no button it recognizes. Only windows that look like dialogs
/// (owned pop-ups, the classic <c>#32770</c> class, or "…Dialog" classes) are touched — never the app's main
/// window. Runs a few passes so a stack of dialogs clears at once.
/// </summary>
public static class DismissService
{
    // Priority order of button labels to click. Non-committal first; OK/Yes only if acceptOk/acceptYes.
    private static readonly string[] Safe = { "cancel", "close", "no", "don't save", "dont save", "dismiss", "later", "not now", "skip" };
    private static readonly string[] Ok = { "ok", "okay", "close" };
    private static readonly string[] Yes = { "yes" };

    public static DismissResultDto Dismiss(IAutomationBackend b, bool acceptOk = true, bool acceptYes = false, int maxPasses = 4,
        IReadOnlyList<string>? titleContains = null, bool includePopups = false)
    {
        maxPasses = Math.Clamp(maxPasses, 1, 10);
        var dismissed = new List<DismissedDto>();
        var seen = new HashSet<long>();

        for (int pass = 0; pass < maxPasses; pass++)
        {
            var dialogs = TopLevel(b)
                .Where(w => IsTarget(w, titleContains, includePopups))
                .Where(w => !seen.Contains(w.NativeWindowHandle)).ToList();
            if (dialogs.Count == 0) break;
            bool actedThisPass = false;

            foreach (var w in dialogs)
            {
                seen.Add(w.NativeWindowHandle);
                string? via = TryButtons(b, w, acceptOk, acceptYes);
                if (via is null)
                {
                    // No recognizable button — send the window a close.
                    var r = WindowService.Close(w.NativeWindowHandle);
                    via = r.Ok ? "close" : null;
                }
                if (via is not null)
                {
                    dismissed.Add(new DismissedDto(Clean(w.Name) ?? "(dialog)", w.NativeWindowHandle, via));
                    actedThisPass = true;
                    Thread.Sleep(150);   // let the next dialog (if any) surface
                }
            }
            if (!actedThisPass) break;
        }

        return new DismissResultDto(dismissed.Count, dismissed,
            dismissed.Count == 0 ? "No dialogs found to dismiss." : $"Dismissed {dismissed.Count} dialog(s).");
    }

    private static string? TryButtons(IAutomationBackend b, ElementInfoDto win, bool acceptOk, bool acceptYes)
    {
        List<ElementInfoDto> buttons;
        try { buttons = b.Find(win.Ref, new FindQuery(ControlType: "Button", Scope: "descendants", Max: 40)).ToList(); }
        catch { return null; }
        if (buttons.Count == 0) return null;

        foreach (var group in acceptYes ? new[] { Safe, Ok, Yes } : acceptOk ? new[] { Safe, Ok } : new[] { Safe })
            foreach (var label in group)
            {
                var btn = buttons.FirstOrDefault(bt => Norm(bt.Name) == label && bt.IsEnabled);
                if (btn is not null && btn.Patterns.Contains("Invoke"))
                {
                    try { b.Invoke(btn.Ref); return "button:" + label; } catch { }
                }
            }
        return null;
    }

    private static IReadOnlyList<ElementInfoDto> TopLevel(IAutomationBackend b)
    { try { return b.GetTopLevelWindows(); } catch { return Array.Empty<ElementInfoDto>(); } }

    private static bool IsTarget(ElementInfoDto w, IReadOnlyList<string>? titleContains, bool includePopups)
    {
        if (w.NativeWindowHandle == 0) return false;
        if (IsDialog(w)) return true;
        // Focus-stealers that appear over the app: close by explicit title match…
        if (titleContains is { Count: > 0 } && !string.IsNullOrWhiteSpace(w.Name))
            if (titleContains.Any(t => w.Name!.Contains(t, StringComparison.OrdinalIgnoreCase))) return true;
        // …or, opt-in, by popup/menu class (open menus, flyouts, dropdowns, notifications).
        if (includePopups && IsPopupClass(w)) return true;
        return false;
    }

    // A dialog: the classic Win32 dialog class, a "…Dialog" class, or an owned (secondary) top-level window.
    private static bool IsDialog(ElementInfoDto w)
    {
        if (w.NativeWindowHandle == 0) return false;
        var cls = w.ClassName ?? "";
        if (cls == "#32770" || cls.Contains("Dialog", StringComparison.OrdinalIgnoreCase)) return true;
        try { return GetWindow((IntPtr)w.NativeWindowHandle, GW_OWNER) != IntPtr.Zero; } catch { return false; }
    }

    private static bool IsPopupClass(ElementInfoDto w)
    {
        var cls = w.ClassName ?? "";
        return cls is "#32768" || cls.Contains("Popup", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("Menu", StringComparison.OrdinalIgnoreCase) || cls.Contains("Flyout", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("DropDown", StringComparison.OrdinalIgnoreCase) || cls.Contains("ToolTip", StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string? s) => (s ?? "").Replace("&", "").Trim().ToLowerInvariant();
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private const uint GW_OWNER = 4;
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
