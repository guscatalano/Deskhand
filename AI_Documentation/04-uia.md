# 04 — UI Automation (FlaUI / UIA3)

All UIA lives in `Deskhand.Core.Services.UiaService`. **Every method assumes it runs on the backend's
single STA thread** (`LocalAutomationBackend` guarantees this). The class is not thread-safe on its own.

## Setup

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;   // ControlType, TreeScope
using FlaUI.UIA3;

private readonly UIA3Automation _automation = new();
private readonly ElementRegistry _registry = new();
private AutomationElement Desktop => _automation.GetDesktop();
public void Dispose() => _automation.Dispose();
```

`UIA3Automation` is the FlaUI UIA3 provider. `GetDesktop()` returns the root element (the whole desktop);
its direct children are the top-level windows.

## Orientation

```csharp
public ElementInfoDto GetForegroundWindow()
{
    IntPtr hwnd = NativeMethods.GetForegroundWindow();
    if (hwnd == IntPtr.Zero) return Register(Desktop);
    return Register(_automation.FromHandle(hwnd));   // FromHandle: HWND -> AutomationElement
}

public ElementInfoDto GetFocusedElement() => Register(_automation.FocusedElement());
```

`GetTopLevelWindows()` walks `Desktop.FindAllChildren()`, keeps children that are `ControlType.Window`
**or** have a non-zero native window handle, and sorts named windows first (case-insensitive).

> **Gotcha — "get foreground window" is unreliable when a tool has focus.** The moment you click the
> browser dashboard or the MCP client is focused, *that* is the foreground window. So the reliable way to
> target a specific app is `GetTopLevelWindows()` (`/windows`, `deskhand_list_windows`) and pick from the
> list — not `/foreground`. The dashboard's "Foreground (3s)" button exists to give you time to click the
> real target before it grabs the foreground.

## The tree walk

```csharp
public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren)
{
    var root = rootRef is null ? Desktop : Resolve(rootRef);
    return BuildTree(root, Math.Max(0, depth), Math.Max(1, maxChildren));
}

private TreeNodeDto BuildTree(AutomationElement el, int depth, int maxChildren)
{
    var info = Register(el);                                 // every visited element gets a ref
    if (depth == 0) return new TreeNodeDto(info, Array.Empty<TreeNodeDto>());
    var children = new List<TreeNodeDto>();
    try { foreach (var child in el.FindAllChildren().Take(maxChildren))
              children.Add(BuildTree(child, depth - 1, maxChildren)); }
    catch { /* element may vanish mid-walk; return what we have */ }
    return new TreeNodeDto(info, children);
}
```

Note `FindAllChildren()` (no argument) — see the TrueCondition gotcha below.

## Find with conditions

```csharp
public IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery q)
{
    var root = rootRef is null ? Desktop : Resolve(rootRef);
    var scope = q.Scope.ToLowerInvariant() switch
    {
        "children" => TreeScope.Children,
        "subtree"  => TreeScope.Subtree,
        _          => TreeScope.Descendants,
    };
    var condition = BuildCondition(q);
    AutomationElement[] found = condition is not null
        ? root.FindAll(scope, condition)
        : scope == TreeScope.Children ? root.FindAllChildren() : root.FindAllDescendants();
    return found.Take(Math.Clamp(q.Max, 1, 1000)).Select(Register).ToList();
}
```

`BuildCondition` uses `_automation.ConditionFactory` and AND-combines only the non-null fields:

```csharp
var cf = _automation.ConditionFactory;
ConditionBase? cond = null;
if (!string.IsNullOrEmpty(q.Name))        cond = And(cond, cf.ByName(q.Name));
if (!string.IsNullOrEmpty(q.AutomationId)) cond = And(cond, cf.ByAutomationId(q.AutomationId));
if (!string.IsNullOrEmpty(q.ClassName))   cond = And(cond, cf.ByClassName(q.ClassName));
if (!string.IsNullOrEmpty(q.ControlType)) {
    if (!Enum.TryParse<ControlType>(q.ControlType, ignoreCase: true, out var ct))
        throw new ArgumentException($"Unknown control type '{q.ControlType}'.");
    cond = And(cond, cf.ByControlType(ct));
}
// And(a,b) => a is null ? b : a.And(b);
```

> **Gotcha — `TrueCondition` has no parameterless constructor in FlaUI 4.0.0.** You cannot write
> `root.FindAll(scope, new TrueCondition())` to mean "match everything". The code handles the
> no-filter case by calling the **argument-less** `FindAllChildren()` / `FindAllDescendants()` instead,
> which return every child/descendant. Only build a `ConditionBase` when at least one filter field is set.

## Control patterns (act)

Each action resolves the ref, fetches the pattern via `PatternOrDefault`, and throws
`PatternNotSupportedException` if null:

```csharp
public void Invoke(string reference) {
    var p = Resolve(reference).Patterns.Invoke.PatternOrDefault
            ?? throw new PatternNotSupportedException("Invoke", reference);
    p.Invoke();
}
public void SetValue(string reference, string text) {
    var p = Resolve(reference).Patterns.Value.PatternOrDefault
            ?? throw new PatternNotSupportedException("Value", reference);
    p.SetValue(text);
}
public void Toggle(string reference)               => ...Patterns.Toggle...        .Toggle();
public void ExpandCollapse(string r, bool expand)  => expand ? p.Expand() : p.Collapse();  // Patterns.ExpandCollapse
public void Select(string reference)               => ...Patterns.SelectionItem... .Select();
```

| Method | Pattern | Use |
|---|---|---|
| `Invoke` | `InvokePattern` | press a button, activate a menu item |
| `SetValue` | `ValuePattern` | set text-box contents |
| `Toggle` | `TogglePattern` | flip a checkbox / switch |
| `ExpandCollapse` | `ExpandCollapsePattern` | open/close a tree item, combo box |
| `Select` | `SelectionItemPattern` | select a list item / tab |

`SetFocus` is special — it raises the host window past the foreground lock first, then focuses:

```csharp
public void SetFocus(string reference) {
    var el = Resolve(reference);
    var hwnd = HostHwnd(el);                        // walk ControlViewWalker up to nearest window
    if (hwnd != IntPtr.Zero) WindowService.ForceForeground(hwnd);   // see 06-input-and-dpi.md
    try { el.Focus(); } catch { /* best-effort once the window is up */ }
}
```

`HostHwnd` walks up via `_automation.TreeWalkerFactory.GetControlViewWalker().GetParent(...)` (guarded to
40 iterations) looking for the first ancestor with a non-zero `NativeWindowHandle`.

## Supported-patterns probe (for `ElementInfoDto.Patterns`)

Each pattern's `IsSupported` is checked defensively (any throw = "not supported"):

```csharp
Check("Invoke",         () => el.Patterns.Invoke.IsSupported);
Check("Value",          () => el.Patterns.Value.IsSupported);
Check("Toggle",         () => el.Patterns.Toggle.IsSupported);
Check("ExpandCollapse", () => el.Patterns.ExpandCollapse.IsSupported);
Check("SelectionItem",  () => el.Patterns.SelectionItem.IsSupported);
Check("Selection",      () => el.Patterns.Selection.IsSupported);
Check("Scroll",         () => el.Patterns.Scroll.IsSupported);
Check("Window",         () => el.Patterns.Window.IsSupported);
Check("Text",           () => el.Patterns.Text.IsSupported);
Check("Grid",           () => el.Patterns.Grid.IsSupported);
```

## Reading **every** property

```csharp
public IReadOnlyDictionary<string, string?> GetAllProperties(string reference)
{
    var el = Resolve(reference);
    var dict = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var pid in el.FrameworkAutomationElement.GetSupportedProperties())  // the key API
    {
        try { dict[pid.Name] = Stringify(el.FrameworkAutomationElement.GetPropertyValue(pid)); }
        catch { /* individual property unreadable; skip */ }
    }
    return dict;
}
```

`FrameworkAutomationElement.GetSupportedProperties()` enumerates the `PropertyId`s the element actually
supports; `GetPropertyValue(pid)` reads each. `Stringify` renders values sensibly: `bool`→`"true"/"false"`,
`Rectangle`→`{x:..,y:..,w:..,h:..}`, `Point`→`{x:..,y:..}`, `int[]`→`-`-joined, `double[]`→`, `-joined,
nested `AutomationElement`→`<name>`, `AutomationElement[]`→`[N elements]`, other arrays→comma-joined,
else `ToString()`.

## Bounds (for capture_element)

```csharp
public Rectangle GetBounds(string reference)
{
    var r = Resolve(reference).BoundingRectangle;   // System.Drawing.Rectangle, physical px
    return new Rectangle(r.X, r.Y, r.Width, r.Height);
    // throws DesktopUnavailableException if there are no on-screen bounds
}
```

## `wait_for_element` polling

The interface method `WaitForElement` is implemented in `LocalAutomationBackend` (not `UiaService`) so the
poll loop can run *off* the STA thread and only hop on for each probe (each with `Max = 1`), sleeping 150ms
between probes and returning `null` on timeout. See `03-core-backend.md`.

## Robustness helpers

All property reads inside `Register`/`BuildInfo` go through `Safe`/`SafeStruct`/`SafeNullable`/
`SafeControlType` wrappers that swallow exceptions and return a fallback — UIA elements routinely throw
`COMException` mid-read when the target UI changes. `BuildInfo` also guards `BoundingRectangle`,
`RuntimeId`, `ProcessId`, `IsEnabled`, `IsOffscreen`, and `NativeWindowHandle` individually.

## Chromium/Electron caveat

Chromium/Electron apps may expose thin UIA trees. Launch them with `--force-renderer-accessibility`, or fall
back to capture + coordinate input.
