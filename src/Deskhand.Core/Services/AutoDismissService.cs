using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

/// <summary>An allowlist rule: a window is acted on only if it matches (a title substring and/or a class). At
/// least one of TitleContains/ClassName must be set — a rule that matches everything is rejected. Action is
/// "hide" (default, SW_HIDE — removes the obstacle WITHOUT running the app's close handler) or "close" (WM_CLOSE).</summary>
public record AutoRule(string? TitleContains, string? ClassName, string? Action);
public record AutoDismissEntry(string Ts, long Hwnd, string? Title, string? Class, string Action, string Rule);
public record AutoDismissStatusDto(bool Enabled, int RuleCount, IReadOnlyList<AutoRule> Rules, int Acted, string Note);

/// <summary>
/// Continuously-present auto-dismisser for nag windows that appear between an agent's discrete turns (when it
/// can't act). Deliberately conservative, per the design:
/// <list type="bullet">
///   <item><b>Opt-in + allowlisted</b> — acts only on windows matching explicit caller rules, never "anything unexpected".</item>
///   <item><b>Hide-preferred</b> — default SW_HIDE removes the window without invoking its close handler (can't cascade); "close" (WM_CLOSE) is opt-in per rule.</item>
///   <item><b>Audited + surfaced</b> — every action is logged so the agent can ask "what did you close while I was thinking?" (auto-dismiss_log).</item>
///   <item><b>Kill-switch bound</b> — <see cref="Tick"/> does nothing while disarmed.</item>
/// </list>
/// A host background loop calls <see cref="Tick"/> a few times a second.
/// </summary>
public static class AutoDismissService
{
    private static volatile bool _enabled;
    private static volatile IReadOnlyList<AutoRule> _rules = Array.Empty<AutoRule>();
    private static readonly ConcurrentQueue<AutoDismissEntry> _log = new();
    private static int _acted;

    public static AutoDismissStatusDto Configure(IReadOnlyList<AutoRule>? rules, bool? enabled)
    {
        if (rules is not null)
            // Reject match-all rules — a rule with neither a title nor a class would close the save dialog too.
            _rules = rules.Where(r => !string.IsNullOrWhiteSpace(r.TitleContains) || !string.IsNullOrWhiteSpace(r.ClassName)).ToList();
        if (enabled.HasValue) _enabled = enabled.Value;
        return Status();
    }

    public static AutoDismissStatusDto Status() => new(_enabled, _rules.Count, _rules, _acted,
        _rules.Count == 0 ? "No rules set — add allowlist rules (titleContains and/or className)."
        : _enabled ? "Active while armed." : "Configured but disabled.");

    public static IReadOnlyList<AutoDismissEntry> Log(int limit = 100)
        => _log.Reverse().Take(Math.Clamp(limit, 1, 1000)).ToList();

    /// <summary>Called by the host loop. Does nothing unless enabled AND armed (kill-switch bound).</summary>
    public static void Tick(bool armed)
    {
        if (!_enabled || !armed) return;
        var rules = _rules;
        if (rules.Count == 0) return;

        foreach (var w in Win32Windows.List())
        {
            var rule = rules.FirstOrDefault(r => Matches(r, w));
            if (rule is null) continue;
            string action = string.Equals(rule.Action, "close", StringComparison.OrdinalIgnoreCase) ? "close" : "hide";
            bool ok = action == "close"
                ? PostMessage((IntPtr)w.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)
                : ShowWindow((IntPtr)w.Hwnd, SW_HIDE);
            if (ok)
            {
                Interlocked.Increment(ref _acted);
                Enqueue(new AutoDismissEntry(DateTime.Now.ToString("s"), w.Hwnd, w.Title, w.Class, action, Describe(rule)));
            }
        }
    }

    private static bool Matches(AutoRule r, Win32Window w)
    {
        if (!string.IsNullOrWhiteSpace(r.TitleContains) && !(w.Title ?? "").Contains(r.TitleContains!, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(r.ClassName) && !(w.Class ?? "").Contains(r.ClassName!, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string Describe(AutoRule r) => $"title~'{r.TitleContains}' class~'{r.ClassName}' → {(string.Equals(r.Action, "close", StringComparison.OrdinalIgnoreCase) ? "close" : "hide")}";
    private static void Enqueue(AutoDismissEntry e) { _log.Enqueue(e); while (_log.Count > 500 && _log.TryDequeue(out _)) { } }

    private const int SW_HIDE = 0;
    private const uint WM_CLOSE = 0x0010;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
