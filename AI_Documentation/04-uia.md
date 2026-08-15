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
private const int TreeNodeBudget = 4000;                    // total nodes any one call may emit

public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren)
{
    var root = rootRef is null ? Desktop : Resolve(rootRef);
    int budget = TreeNodeBudget;
    return BuildTree(root, Math.Max(0, depth), Math.Clamp(maxChildren, 1, 500), ref budget);
}

private TreeNodeDto BuildTree(AutomationElement el, int depth, int maxChildren, ref int budget)
{
    var info = Register(el);                                 // every visited element gets a ref
    budget--;
    if (depth == 0 || budget <= 0) return new TreeNodeDto(info, Array.Empty<TreeNodeDto>());
    var children = new List<TreeNodeDto>();
    try { foreach (var child in el.FindAllChildren().Take(maxChildren)) {
              if (budget <= 0) break;
              children.Add(BuildTree(child, depth - 1, maxChildren, ref budget)); } }
    catch { /* element may vanish mid-walk, or a provider (Chromium) may fault; return what we have */ }
    return new TreeNodeDto(info, children);
}
```

Note `FindAllChildren()` (no argument) — see the TrueCondition gotcha below.

**Why the node budget.** A Chromium/Electron window with accessibility forced on exposes an
enormous, bushy a11y tree; an unbounded walk (plus the recursive JSON) can balloon. The `TreeNodeBudget`
caps a single `get_tree` at 4000 nodes — past that the tree comes back **partial** rather than
exhausting the walk. `maxChildren` is also clamped to ≤500 per node. Normal app windows never hit this.

## Element from a point

The escape hatch for apps whose tree is thin or whose refs go stale (Chromium/Electron — see the
caveat below): resolve the element **under a pixel**, fresh, with no walk and no stored ref.

```csharp
public ElementInfoDto GetElementFromPoint(int x, int y)
    => Register(_automation.FromPoint(new System.Drawing.Point(x, y)));   // x,y = virtual-desktop pixels
```

Exposed as `deskhand_element_from_point(x, y)` (MCP), `POST /uia/element-from-point` (HTTP), and the
fleet mirrors (`deskhand_agent_element_from_point`, `POST /agents/{id}/uia/element-from-point`). Both
dashboards wire it to an on-click **🔍 pick element** mode on the screenshot: the pixel is mapped to
virtual-desktop coordinates (from the capture rect) and posted to the endpoint. Because it resolves
fresh each call, it sidesteps the stale-ref problem entirely.

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

Chromium/Electron apps are the hard case for UIA, and it shows up two ways:

1. **Thin tree without the flag.** By default the renderer's web contents are *not* in the UIA tree —
   you see the browser chrome (tabs, toolbar) and nothing of the page. Chromium only builds the web
   a11y tree when it detects an assistive client or is told to via `--force-renderer-accessibility`.
2. **Unstable refs even with the flag.** A Chromium top-level window's UIA element goes **stale within
   milliseconds** of being enumerated by `GetTopLevelWindows()` — `IsAlive` (a `ProcessId` read) throws
   and the selector re-resolve can't find it, so a follow-up `get_tree`/`get_element` returns
   `404 stale_element`. This is not a Deskhand bug; it's how Chromium virtualizes its windows.

Because of (2), ref-then-walk is unreliable on these apps. Deskhand's built-in mitigations:

- **Auto-flag on launch.** `LocalAutomationBackend.InjectAccessibilityFlag` appends
  `--force-renderer-accessibility` when the launched exe is a known Chromium browser (chrome, msedge,
  brave, opera, vivaldi, chromium, thorium). `DESKHAND_FORCE_A11Y=always` forces it for any exe
  (Electron apps, whose exe names vary); `=off` disables. Caveat: Chromium is **single-instance per
  profile**, so the flag only takes effect on a *fresh* instance — a new `--user-data-dir`, or the
  browser not already running. Launching into an already-open profile hands off and ignores the flag.
- **Point-based access.** `GetElementFromPoint` (above) resolves the element under a pixel with no
  stale-ref window. This is the recommended "find element" for Chromium/Electron.
- **Last resort:** capture + coordinate input — screenshot the app, the model reads it, click by pixel.

### How to test the thin-tree behavior yourself

Launch the app twice — once plain, once with `--force-renderer-accessibility` (fresh `--user-data-dir`
each time so the flag actually applies) — and compare whether known page content appears:

```powershell
# fresh instance so the flag isn't swallowed by an existing one
Start-Process msedge --% --user-data-dir=%TEMP%\a --force-renderer-accessibility --new-window https://example.com
# then, via the running deskhand-http:
#   POST /windows           -> find the "Example Domain ..." window (controlType=Window), take ONE ref
#   POST /uia/find {rootRef, controlType:"Hyperlink", scope:"descendants"}   -> page links present?
# Repeat WITHOUT the flag: the page links/headings are missing (thin tree).
```

Gotcha that will bite you: `/windows` returns *many* windows named after the page (Edge spawns several
top-level windows). Filter to `controlType == "Window"` and take a **single scalar** `ref` — passing an
array of refs as `rootRef` yields an empty-body `400` (JSON binding failure), which looks like a tree
bug but isn't.
