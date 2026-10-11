using Deskhand.Core.Fleet;

namespace Deskhand.Core.Macros;

/// <summary>
/// Replays a <see cref="Macro"/> — synchronized, not blind. UIA action steps re-resolve (i.e. WAIT
/// for) their target element before acting, and explicit "wait" steps block until an expected element
/// appears. Only raw coordinate/keyboard input honors the recorded timing (scaled by <c>speed</c>,
/// each gap capped). So a macro is "do X, wait for Y, do Z" rather than fire-and-hope.
/// </summary>
public sealed record MacroReplayStep(int Index, long TMs, string Kind, string Method, bool Ok, string? Detail);
public sealed record MacroReplayReport(bool Passed, int Steps, int Failures, IReadOnlyList<MacroReplayStep> Results);

public static class MacroPlayer
{
    private const int TargetWaitMs = 8000;

    /// <summary>Replay a macro as a REGRESSION TEST: run every step, check each assertion, and return a per-step
    /// pass/fail report instead of throwing. Assertion ("assert") steps are checked via WaitService
    /// (window/process/file/registry/text); an element "wait"/"uia" step that can't re-resolve is recorded as a
    /// failure (loud), not a silent abort. With <paramref name="stopOnFailure"/> the replay halts at the first
    /// failure (default) — the usual "did my change break the flow?" behaviour.</summary>
    public static MacroReplayReport PlayReport(Macro macro, IAutomationBackend backend, double speed = 1.0,
        int maxStepDelayMs = 3000, bool stopOnFailure = true, Action<string>? log = null)
    {
        if (speed <= 0) speed = 1.0;
        long prev = 0;
        var results = new List<MacroReplayStep>();
        int failures = 0, i = 0;
        foreach (var s in macro.Steps)
        {
            long gap = (long)((s.TMs - prev) / speed);
            prev = s.TMs;
            if (s.Kind == "input" && gap > 0) Thread.Sleep((int)Math.Min(gap, maxStepDelayMs));
            bool ok; string? detail;
            try
            {
                if (s.Kind == "assert") (ok, detail) = RunAssert(s, backend);
                else if (s.Kind == "assert-text") (ok, detail) = RunAssertText(s, backend);
                else { Execute(s, backend, log); ok = true; detail = null; }
            }
            catch (Exception ex) { ok = false; detail = ex.Message; }
            results.Add(new MacroReplayStep(i, s.TMs, s.Kind, s.Method, ok, detail));
            if (!ok) { failures++; log?.Invoke($"FAIL step {i} ({s.Kind} {s.Method}): {detail}"); if (stopOnFailure) break; }
            i++;
        }
        return new MacroReplayReport(failures == 0, results.Count, failures, results);
    }

    private static (bool ok, string? detail) RunAssert(MacroStep s, IAutomationBackend b)
    {
        var a = s.Args;
        string match = a.Str("match") ?? "";
        string expect = a.Str("expect") ?? "present";
        int timeout = a.Int("timeoutMs", 5000);
        var r = Deskhand.Core.Services.WaitService.WaitAsync(s.Method, match, expect, timeout, 500).GetAwaiter().GetResult();
        return r.Met
            ? (true, r.Detail)
            : (false, $"assert {s.Method} '{match}' ({expect}) not met within {timeout}ms{(r.Detail is null ? "" : " — " + r.Detail)}");
    }

    private static (bool ok, string? detail) RunAssertText(MacroStep s, IAutomationBackend b)
    {
        var sel = s.Selector;
        if (sel is null) return (false, "(assert-text step missing selector)");
        var a = s.Args;
        string expected = a.Str("expected") ?? "";
        bool contains = a.Bool("contains");
        int timeout = a.Int("timeoutMs", 5000);
        var el = b.WaitForElement(null, Query(sel), timeout);
        if (el is null) return (false, $"element {sel.ControlType} '{sel.Name}' not found within {timeout}ms");
        string text = (b.GetText(el.Ref) ?? "").Trim();
        bool match = contains
            ? text.Contains(expected, StringComparison.OrdinalIgnoreCase)
            : string.Equals(text, expected.Trim(), StringComparison.Ordinal);
        string shown = text.Length > 60 ? text[..60] + "…" : text;
        return match ? (true, $"text {(contains ? "contains" : "==")} expected ('{shown}')")
                     : (false, $"element text {(contains ? "does not contain" : "!=")} expected — got '{shown}'");
    }

    public static int Play(Macro macro, IAutomationBackend backend, double speed = 1.0, int maxStepDelayMs = 3000, Action<string>? log = null)
    {
        if (speed <= 0) speed = 1.0;
        long prev = 0;
        int done = 0;
        foreach (var s in macro.Steps)
        {
            long gap = (long)((s.TMs - prev) / speed);
            prev = s.TMs;
            // Only raw input waits on the clock; UIA and wait steps synchronize on the element itself.
            if (s.Kind == "input" && gap > 0) Thread.Sleep((int)Math.Min(gap, maxStepDelayMs));
            Execute(s, backend, log);
            done++;
        }
        return done;
    }

    private static void Execute(MacroStep s, IAutomationBackend b, Action<string>? log)
    {
        var a = s.Args;

        if (s.Kind == "wait")
        {
            var expect = s.Selector ?? throw new ArgumentException("(macro) wait step missing selector");
            int timeout = a.Int("timeoutMs", 5000);
            var hit = b.WaitForElement(null, Query(expect), timeout)
                ?? throw new DesktopUnavailableException($"(macro) expected {expect.ControlType} '{expect.Name}' did not appear within {timeout}ms");
            log?.Invoke($"expect ok: {expect.ControlType} '{expect.Name}'");
            return;
        }

        if (s.Kind == "uia")
        {
            var sel = s.Selector ?? throw new StaleElementException("(macro step missing selector)");
            var el = b.WaitForElement(null, Query(sel), TargetWaitMs)
                ?? throw new StaleElementException($"(macro) could not re-resolve {sel.ControlType} '{sel.Name}'");
            log?.Invoke($"{s.Method} -> {sel.ControlType} '{sel.Name}'");
            switch (s.Method)
            {
                case FleetMethods.Invoke: b.Invoke(el.Ref); break;
                case FleetMethods.SetValue: b.SetValue(el.Ref, a.Str("text") ?? ""); break;
                case FleetMethods.Toggle: b.Toggle(el.Ref); break;
                case FleetMethods.ExpandCollapse: b.ExpandCollapse(el.Ref, a.Bool("expand")); break;
                case FleetMethods.Select: b.Select(el.Ref); break;
                case FleetMethods.SetFocus: b.SetFocus(el.Ref); break;
            }
            return;
        }

        // input — replayed verbatim
        log?.Invoke(s.Method);
        switch (s.Method)
        {
            case FleetMethods.MouseMove: b.MouseMove(a.Int("x"), a.Int("y")); break;
            case FleetMethods.MouseClick: b.MouseClick(a.Str("button") ?? "left", a.IntN("x"), a.IntN("y"), a.Int("count", 1)); break;
            case FleetMethods.MouseDown: b.MouseDown(a.Str("button") ?? "left", a.IntN("x"), a.IntN("y")); break;
            case FleetMethods.MouseUp: b.MouseUp(a.Str("button") ?? "left", a.IntN("x"), a.IntN("y")); break;
            case FleetMethods.MouseScroll: b.MouseScroll(a.Int("dx"), a.Int("dy")); break;
            case FleetMethods.Drag: b.Drag(a.Int("fromX"), a.Int("fromY"), a.Int("toX"), a.Int("toY"), a.Str("button") ?? "left", a.Int("steps", 20), a.Int("holdMs", 60)); break;
            case FleetMethods.TypeText: b.TypeText(a.Str("text") ?? ""); break;
            case FleetMethods.SendKeys: b.SendKeys(a.Str("chord") ?? ""); break;
            case FleetMethods.Launch: b.LaunchProcess(a.Str("path") ?? "", a.Str("args"), a.Str("workingDir"), a.Int("waitForWindowMs", 4000)); break;
        }
    }

    private static FindQuery Query(ElementSelectorDto s) =>
        new(s.Name, s.AutomationId, s.ControlType, s.ClassName, "descendants", 1);
}
