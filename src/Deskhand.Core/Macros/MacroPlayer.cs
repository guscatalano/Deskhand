using Deskhand.Core.Fleet;

namespace Deskhand.Core.Macros;

/// <summary>
/// Replays a <see cref="Macro"/> — synchronized, not blind. UIA action steps re-resolve (i.e. WAIT
/// for) their target element before acting, and explicit "wait" steps block until an expected element
/// appears. Only raw coordinate/keyboard input honors the recorded timing (scaled by <c>speed</c>,
/// each gap capped). So a macro is "do X, wait for Y, do Z" rather than fire-and-hope.
/// </summary>
public static class MacroPlayer
{
    private const int TargetWaitMs = 8000;

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
            case FleetMethods.TypeText: b.TypeText(a.Str("text") ?? ""); break;
            case FleetMethods.SendKeys: b.SendKeys(a.Str("chord") ?? ""); break;
        }
    }

    private static FindQuery Query(ElementSelectorDto s) =>
        new(s.Name, s.AutomationId, s.ControlType, s.ClassName, "descendants", 1);
}
