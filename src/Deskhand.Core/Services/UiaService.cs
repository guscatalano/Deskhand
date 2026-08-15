using System.Drawing;
using Deskhand.Core.Elements;
using Deskhand.Core.Events;
using Deskhand.Core.Interop;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.UIA3;

namespace Deskhand.Core.Services;

/// <summary>
/// FlaUI/UIA3 automation. Every method here assumes it is running on the backend's
/// single STA thread (the backend guarantees that). Not thread-safe by itself.
/// </summary>
public sealed class UiaService : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ElementRegistry _registry = new();
    private FocusChangedEventHandlerBase? _focusHandler;
    private AutomationEventHandlerBase? _windowOpenHandler;

    private AutomationElement Desktop => _automation.GetDesktop();

    public void Dispose()
    {
        try { _automation.UnregisterAllEvents(); } catch { }
        _automation.Dispose();
    }

    /// <summary>Register UIA events (focus changes, window opens) and feed them into the hub.
    /// Callbacks fire on UIA's own thread; the hub is thread-safe.</summary>
    public void StartEvents(EventHub hub)
    {
        _focusHandler = _automation.RegisterFocusChangedEvent(el =>
        {
            try { hub.Publish("focus", Safe(() => el.Properties.Name.ValueOrDefault), SafeControlType(el).ToString(), SafeNullable(() => el.Properties.ProcessId.ValueOrDefault)); }
            catch { }
        });
        try
        {
            _windowOpenHandler = Desktop.RegisterAutomationEvent(
                _automation.EventLibrary.Window.WindowOpenedEvent, TreeScope.Descendants, (el, _) =>
                {
                    try { hub.Publish("window_opened", Safe(() => el.Properties.Name.ValueOrDefault), "Window", SafeNullable(() => el.Properties.ProcessId.ValueOrDefault)); }
                    catch { }
                });
        }
        catch { /* WindowOpened may be unavailable in some environments */ }
    }

    // ---- entry points that return a fresh ref ----

    public ElementInfoDto GetForegroundWindow()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return Register(Desktop);
        return Register(_automation.FromHandle(hwnd));
    }

    public ElementInfoDto GetFocusedElement()
    {
        var el = _automation.FocusedElement();
        return Register(el);
    }

    /// <summary>Resolve and register an element from a native window handle (e.g. a launched app's window).</summary>
    public ElementInfoDto RegisterHandle(IntPtr hwnd) => Register(_automation.FromHandle(hwnd));

    /// <summary>Top-level windows (children of the desktop root) — the reliable entry points
    /// for exploring a specific app, since "foreground" is always the browser when clicked.</summary>
    public IReadOnlyList<ElementInfoDto> GetTopLevelWindows()
    {
        var list = new List<ElementInfoDto>();
        foreach (var k in Desktop.FindAllChildren())
        {
            var ct = SafeControlType(k);
            var hwnd = SafeStruct(() => k.Properties.NativeWindowHandle.ValueOrDefault, IntPtr.Zero);
            if (ct == ControlType.Window || hwnd != IntPtr.Zero)
                list.Add(Register(k));
        }
        return list
            .OrderByDescending(w => !string.IsNullOrEmpty(w.Name))
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every running process, each with the top-level UIA windows it owns (empty for
    /// background processes). Windowed apps are ordered first. The windows carry live refs, so a
    /// caller can expand any of them straight into the UIA tree.</summary>
    public IReadOnlyList<ProcessInfoDto> GetProcesses()
    {
        // Group the desktop's top-level windows by owning pid (one UIA pass, same filter as list_windows).
        var byPid = new Dictionary<int, List<ElementInfoDto>>();
        foreach (var k in Desktop.FindAllChildren())
        {
            var ct = SafeControlType(k);
            var hwnd = SafeStruct(() => k.Properties.NativeWindowHandle.ValueOrDefault, IntPtr.Zero);
            if (ct != ControlType.Window && hwnd == IntPtr.Zero) continue;
            var info = Register(k);
            int pid = info.ProcessId ?? 0;
            if (pid == 0) continue;
            if (!byPid.TryGetValue(pid, out var l)) byPid[pid] = l = new();
            l.Add(info);
        }

        var list = new List<ProcessInfoDto>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                byPid.TryGetValue(p.Id, out var wins);
                string? title = null;
                try { title = string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle; } catch { }
                long mem = 0; try { mem = p.WorkingSet64; } catch { }
                list.Add(new ProcessInfoDto(p.Id, p.ProcessName, title, mem,
                    wins ?? (IReadOnlyList<ElementInfoDto>)Array.Empty<ElementInfoDto>()));
            }
            catch { /* process may exit mid-enumeration */ }
            finally { p.Dispose(); }
        }

        return list
            .OrderByDescending(x => x.Windows.Count)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Total nodes any single get_tree may emit. Bushy trees (Chromium/Electron a11y) can be enormous;
    // without a cap the walk + JSON blow up. When hit, the tree comes back partial rather than failing.
    private const int TreeNodeBudget = 4000;

    public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren)
    {
        var root = rootRef is null ? Desktop : Resolve(rootRef);
        int budget = TreeNodeBudget;
        return BuildTree(root, Math.Max(0, depth), Math.Clamp(maxChildren, 1, 500), ref budget);
    }

    public IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery q)
    {
        var root = rootRef is null ? Desktop : Resolve(rootRef);
        var scope = q.Scope.ToLowerInvariant() switch
        {
            "children" => TreeScope.Children,
            "subtree" => TreeScope.Subtree,
            _ => TreeScope.Descendants,
        };
        var condition = BuildCondition(q);
        AutomationElement[] found = condition is not null
            ? root.FindAll(scope, condition)
            : scope == TreeScope.Children ? root.FindAllChildren() : root.FindAllDescendants();
        return found.Take(Math.Clamp(q.Max, 1, 1000)).Select(Register).ToList();
    }

    public ElementInfoDto GetElement(string reference) => BuildInfoOnly(reference, Resolve(reference));

    /// <summary>Deepest element at a screen point. Resolves fresh via UIA FromPoint — the escape
    /// hatch when a window's tree is thin or its refs go stale (Chromium/Electron).</summary>
    public ElementInfoDto GetElementFromPoint(int x, int y)
        => Register(_automation.FromPoint(new System.Drawing.Point(x, y)));

    /// <summary>Every UIA property the element supports, as name → string value (sorted).</summary>
    public IReadOnlyDictionary<string, string?> GetAllProperties(string reference)
    {
        var el = Resolve(reference);
        var dict = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in el.FrameworkAutomationElement.GetSupportedProperties())
        {
            try
            {
                var val = el.FrameworkAutomationElement.GetPropertyValue(pid);
                dict[pid.Name] = Stringify(val);
            }
            catch { /* individual property unreadable; skip */ }
        }
        return dict;
    }

    private static string? Stringify(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        System.Drawing.Rectangle r => $"{{x:{r.X}, y:{r.Y}, w:{r.Width}, h:{r.Height}}}",
        System.Drawing.Point pt => $"{{x:{pt.X}, y:{pt.Y}}}",
        int[] ia => string.Join("-", ia),
        double[] da => string.Join(", ", da),
        AutomationElement ae => Safe(() => ae.Properties.Name.ValueOrDefault) is { Length: > 0 } n ? $"<{n}>" : "<element>",
        AutomationElement[] aes => $"[{aes.Length} elements]",
        Array a => string.Join(", ", a.Cast<object?>().Select(x => x?.ToString())),
        _ => v.ToString(),
    };

    // ---- actions ----

    public void Invoke(string reference)
    {
        var el = Resolve(reference);
        var p = el.Patterns.Invoke.PatternOrDefault
                ?? throw new PatternNotSupportedException("Invoke", reference);
        p.Invoke();
    }

    public void SetValue(string reference, string text)
    {
        var el = Resolve(reference);
        var p = el.Patterns.Value.PatternOrDefault
                ?? throw new PatternNotSupportedException("Value", reference);
        p.SetValue(text);
    }

    public void Toggle(string reference)
    {
        var el = Resolve(reference);
        var p = el.Patterns.Toggle.PatternOrDefault
                ?? throw new PatternNotSupportedException("Toggle", reference);
        p.Toggle();
    }

    public void ExpandCollapse(string reference, bool expand)
    {
        var el = Resolve(reference);
        var p = el.Patterns.ExpandCollapse.PatternOrDefault
                ?? throw new PatternNotSupportedException("ExpandCollapse", reference);
        if (expand) p.Expand(); else p.Collapse();
    }

    public void Select(string reference)
    {
        var el = Resolve(reference);
        var p = el.Patterns.SelectionItem.PatternOrDefault
                ?? throw new PatternNotSupportedException("SelectionItem", reference);
        p.Select();
    }

    public void SetFocus(string reference)
    {
        var el = Resolve(reference);
        // Raise the host window past the foreground lock, then set keyboard focus on the element.
        var hwnd = HostHwnd(el);
        if (hwnd != IntPtr.Zero) WindowService.ForceForeground(hwnd);
        try { el.Focus(); } catch { /* focus is best-effort once the window is up */ }
    }

    /// <summary>The native handle of the element's nearest window ancestor (itself if it is one).</summary>
    private IntPtr HostHwnd(AutomationElement el)
    {
        var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
        var cur = el;
        for (int guard = 0; cur is not null && guard < 40; guard++)
        {
            var h = SafeStruct(() => cur!.Properties.NativeWindowHandle.ValueOrDefault, IntPtr.Zero);
            if (h != IntPtr.Zero) return h;
            try { cur = walker.GetParent(cur); } catch { break; }
        }
        return IntPtr.Zero;
    }

    /// <summary>Bounding rectangle in physical pixels, for capture_element.</summary>
    public Rectangle GetBounds(string reference)
    {
        var el = Resolve(reference);
        try
        {
            var r = el.BoundingRectangle;
            return new Rectangle(r.X, r.Y, r.Width, r.Height);
        }
        catch
        {
            throw new DesktopUnavailableException($"Element ref '{reference}' has no on-screen bounds.");
        }
    }

    // ---- resolution ----

    private AutomationElement Resolve(string reference)
    {
        var entry = _registry.Get(reference) ?? throw new UnknownElementException(reference);
        if (IsAlive(entry.Element)) return entry.Element;

        var re = TryReResolve(entry);
        if (re is not null) { entry.Element = re; return re; }
        throw new StaleElementException(reference);
    }

    private static bool IsAlive(AutomationElement el)
    {
        try { _ = el.Properties.ProcessId.Value; return true; }
        catch { return false; }
    }

    private AutomationElement? TryReResolve(ElementRegistry.Entry entry)
    {
        try
        {
            AutomationElement scope = entry.Hwnd != IntPtr.Zero
                ? _automation.FromHandle(entry.Hwnd)
                : Desktop;

            ConditionBase? cond = null;
            var cf = _automation.ConditionFactory;
            if (!string.IsNullOrEmpty(entry.AutomationId)) cond = And(cond, cf.ByAutomationId(entry.AutomationId));
            else if (!string.IsNullOrEmpty(entry.Name)) cond = And(cond, cf.ByName(entry.Name));
            if (entry.ControlType != default) cond = And(cond, cf.ByControlType(entry.ControlType));
            if (cond is null) return null;

            return scope.FindFirst(TreeScope.Descendants, cond);
        }
        catch { return null; }
    }

    // ---- helpers ----

    private ConditionBase? BuildCondition(FindQuery q)
    {
        var cf = _automation.ConditionFactory;
        ConditionBase? cond = null;
        if (!string.IsNullOrEmpty(q.Name)) cond = And(cond, cf.ByName(q.Name));
        if (!string.IsNullOrEmpty(q.AutomationId)) cond = And(cond, cf.ByAutomationId(q.AutomationId));
        if (!string.IsNullOrEmpty(q.ClassName)) cond = And(cond, cf.ByClassName(q.ClassName));
        if (!string.IsNullOrEmpty(q.ControlType))
        {
            if (!Enum.TryParse<ControlType>(q.ControlType, ignoreCase: true, out var ct))
                throw new ArgumentException($"Unknown control type '{q.ControlType}'.");
            cond = And(cond, cf.ByControlType(ct));
        }
        return cond;
    }

    private static ConditionBase And(ConditionBase? a, ConditionBase b) => a is null ? b : a.And(b);

    private TreeNodeDto BuildTree(AutomationElement el, int depth, int maxChildren, ref int budget)
    {
        var info = Register(el);
        budget--;
        if (depth == 0 || budget <= 0) return new TreeNodeDto(info, Array.Empty<TreeNodeDto>());

        var children = new List<TreeNodeDto>();
        try
        {
            foreach (var child in el.FindAllChildren().Take(maxChildren))
            {
                if (budget <= 0) break;
                children.Add(BuildTree(child, depth - 1, maxChildren, ref budget));
            }
        }
        catch { /* element may vanish mid-walk, or a provider (Chromium) may fault; return what we have */ }

        return new TreeNodeDto(info, children);
    }

    private ElementInfoDto Register(AutomationElement el)
    {
        string? name = Safe(() => el.Properties.Name.ValueOrDefault);
        string? autoId = Safe(() => el.Properties.AutomationId.ValueOrDefault);
        string? className = Safe(() => el.Properties.ClassName.ValueOrDefault);
        ControlType ct = SafeControlType(el);
        IntPtr hwnd = SafeStruct(() => el.Properties.NativeWindowHandle.ValueOrDefault, IntPtr.Zero);

        var entry = new ElementRegistry.Entry(el)
        {
            AutomationId = autoId,
            Name = name,
            ClassName = className,
            ControlType = ct,
            Hwnd = hwnd,
        };
        string refId = _registry.Add(entry);
        return BuildInfo(refId, el, name, autoId, className, ct, hwnd);
    }

    private ElementInfoDto BuildInfoOnly(string refId, AutomationElement el) => BuildInfo(
        refId, el,
        Safe(() => el.Properties.Name.ValueOrDefault),
        Safe(() => el.Properties.AutomationId.ValueOrDefault),
        Safe(() => el.Properties.ClassName.ValueOrDefault),
        SafeControlType(el),
        SafeStruct(() => el.Properties.NativeWindowHandle.ValueOrDefault, IntPtr.Zero));

    private static ElementInfoDto BuildInfo(string refId, AutomationElement el,
        string? name, string? autoId, string? className, ControlType ct, IntPtr hwnd)
    {
        RectDto? bounds = null;
        try
        {
            var r = el.BoundingRectangle;
            if (r is { Width: > 0, Height: > 0 }) bounds = new RectDto(r.X, r.Y, r.Width, r.Height);
        }
        catch { /* no bounds */ }

        string? runtimeId = null;
        try
        {
            var rid = el.Properties.RuntimeId.ValueOrDefault;
            if (rid is { Length: > 0 }) runtimeId = string.Join("-", rid);
        }
        catch { /* none */ }

        int? pid = SafeNullable(() => el.Properties.ProcessId.ValueOrDefault);

        return new ElementInfoDto(
            Ref: refId,
            Name: name,
            ControlType: ct == default ? "Unknown" : ct.ToString(),
            AutomationId: string.IsNullOrEmpty(autoId) ? null : autoId,
            ClassName: string.IsNullOrEmpty(className) ? null : className,
            RuntimeId: runtimeId,
            BoundingRect: bounds,
            IsEnabled: SafeStruct(() => el.Properties.IsEnabled.ValueOrDefault, true),
            IsOffscreen: SafeStruct(() => el.Properties.IsOffscreen.ValueOrDefault, false),
            NativeWindowHandle: hwnd.ToInt64(),
            ProcessId: pid,
            Patterns: SupportedPatterns(el));
    }

    private static IReadOnlyList<string> SupportedPatterns(AutomationElement el)
    {
        var list = new List<string>();
        void Check(string label, Func<bool> supported)
        {
            try { if (supported()) list.Add(label); } catch { }
        }
        Check("Invoke", () => el.Patterns.Invoke.IsSupported);
        Check("Value", () => el.Patterns.Value.IsSupported);
        Check("Toggle", () => el.Patterns.Toggle.IsSupported);
        Check("ExpandCollapse", () => el.Patterns.ExpandCollapse.IsSupported);
        Check("SelectionItem", () => el.Patterns.SelectionItem.IsSupported);
        Check("Selection", () => el.Patterns.Selection.IsSupported);
        Check("Scroll", () => el.Patterns.Scroll.IsSupported);
        Check("Window", () => el.Patterns.Window.IsSupported);
        Check("Text", () => el.Patterns.Text.IsSupported);
        Check("Grid", () => el.Patterns.Grid.IsSupported);
        return list;
    }

    private static string? Safe(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }

    private static T SafeStruct<T>(Func<T> f, T fallback) where T : struct
    {
        try { return f(); } catch { return fallback; }
    }

    private static int? SafeNullable(Func<int> f)
    {
        try { return f(); } catch { return null; }
    }

    private static ControlType SafeControlType(AutomationElement el)
    {
        try { return el.Properties.ControlType.ValueOrDefault; } catch { return default; }
    }
}
