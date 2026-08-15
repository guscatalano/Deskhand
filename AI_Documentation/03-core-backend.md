# 03 — Core Backend

`Deskhand.Core` is a plain library with no host. It defines the contract, the in-session implementation,
the thread discipline, the element-ref system, and all DTOs.

## `IAutomationBackend` — the full surface

`Deskhand.Core.IAutomationBackend : IDisposable`. Members grouped by family. UIA members are "agent-only";
capture and input are "transport-portable" (an RDP backend could implement them without in-session code).

```csharp
public interface IAutomationBackend : IDisposable
{
    // orientation
    DesktopStateDto GetDesktopState();
    MachineInfoDto  GetMachineInfo();
    ElementInfoDto  GetForegroundWindow();
    ElementInfoDto  GetFocusedElement();
    IReadOnlyList<ElementInfoDto> GetTopLevelWindows();

    // uia — read
    TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren);
    IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery query);
    ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs); // null on timeout
    ElementInfoDto GetElement(string reference);
    IReadOnlyDictionary<string, string?> GetAllProperties(string reference);

    // uia — act
    void Invoke(string reference);
    void SetValue(string reference, string text);
    void Toggle(string reference);
    void ExpandCollapse(string reference, bool expand);
    void Select(string reference);
    void SetFocus(string reference);

    // capture
    CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int jpegQuality);
    CaptureResultDto CaptureRegion(int x, int y, int width, int height, ImageFormat format, int jpegQuality);
    CaptureResultDto CaptureWindow(long hwnd, ImageFormat format, int jpegQuality);
    CaptureResultDto CaptureWindowByRef(string reference, ImageFormat format, int jpegQuality);
    CaptureResultDto CaptureElement(string reference, ImageFormat format, int jpegQuality);
    Services.SecureCapture.InputDesktopResult CaptureInputDesktop(ImageFormat format, int jpegQuality);

    // input
    void MouseMove(int x, int y);
    void MouseClick(string button, int? x, int? y, int count);
    void MouseDown(string button, int? x, int? y);
    void MouseUp(string button, int? x, int? y);
    void MouseScroll(int dx, int dy);
    void TypeText(string text);
    void SendKeys(string chord);
}
```

Two implementations exist in this build:
- `LocalAutomationBackend` — the real in-session backend.
- `GovernedBackend` — a decorator (see `10-governance-and-safety.md`) that wraps *any* `IAutomationBackend`.

## `LocalAutomationBackend`

Constructs a `StaExecutor` and, **on that STA thread**, a `UiaService`:

```csharp
private readonly StaExecutor _sta = new();
private readonly UiaService _uia;
public LocalAutomationBackend() { _uia = _sta.Invoke(() => new UiaService()); }
```

Every UIA / capture / input call is dispatched onto the STA thread, e.g.:

```csharp
public void Invoke(string reference) => _sta.Invoke(() => _uia.Invoke(reference));
public CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int q)
    => _sta.Invoke(() => ScreenCapture.CaptureScreen(monitor, format, q));
```

Two deliberate exceptions do **not** use the STA thread:

- **Orientation via P/Invoke** — `GetDesktopState()` and `GetMachineInfo()` call `DesktopInfo` directly
  (pure Win32, no COM), so they don't need the STA thread.
- **`CaptureInputDesktop`** — calls `Services.SecureCapture.CaptureInputDesktop` directly. That primitive
  spins up its **own throwaway MTA thread** because `SetThreadDesktop` fails on a thread that owns windows
  (see `11-secure-desktop.md`). It must never run on the UIA STA thread.

`WaitForElement` polls **off** the STA thread so the thread is released between probes:

```csharp
public ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs)
{
    var probe = query with { Max = 1 };
    var sw = Stopwatch.StartNew();
    while (true)
    {
        var found = _sta.Invoke(() => _uia.Find(rootRef, probe));   // each probe hops onto STA
        if (found.Count > 0) return found[0];
        if (sw.ElapsedMilliseconds >= Math.Max(0, timeoutMs)) return null;
        Thread.Sleep(150);
    }
}
```

`CaptureWindowByRef` prefers the element's native window handle, else falls back to its bounds:

```csharp
var info = _uia.GetElement(reference);
if (info.NativeWindowHandle != 0)
    return ScreenCapture.CaptureWindow((IntPtr)info.NativeWindowHandle, format, q);
return ScreenCapture.CaptureBounds(_uia.GetBounds(reference), format, q);
```

## `StaExecutor` — one dedicated STA thread

**Why:** UI Automation (UIA3) is a **COM** API and is **not safe for concurrent use**. All tree/pattern/
capture/input work is serialized onto one thread, which also gives the whole backend a stable
single-threaded apartment for the COM proxies.

Mechanism: a `BlockingCollection<Action>` queue drained by one background thread created with
`ApartmentState.STA`:

```csharp
_thread = new Thread(Run) { IsBackground = true, Name = "Deskhand-STA" };
_thread.SetApartmentState(ApartmentState.STA);
_thread.Start();
// Run(): foreach (var work in _queue.GetConsumingEnumerable()) { try { work(); } catch { } }
```

`Invoke<T>(Func<T>)` enqueues the work wrapped in a `TaskCompletionSource<T>` (created with
`RunContinuationsAsynchronously`) and **blocks the calling thread** on the result:

```csharp
var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
_queue.Add(() => { try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); } });
return tcs.Task.GetAwaiter().GetResult();   // block the request thread, not the STA thread
```

`GetAwaiter().GetResult()` unwraps the exception so callers see the original type (e.g.
`StaleElementException`), not an `AggregateException`. `Invoke(Action)` delegates to `Invoke<object?>`.
`Dispose()` calls `CompleteAdding()` and `Join`s for up to 2 seconds.

**Gotcha:** the HTTP handlers are synchronous over this — they block the request thread while the STA
thread does the work. That's intentional; the STA thread is never itself blocked waiting.

## `ElementRegistry` — opaque refs + re-resolution recipe

**Why:** FlaUI `AutomationElement`s are live COM references that don't survive being handed to a client, and
a UIA `RuntimeId` is only stable while the element exists. So the backend never returns a raw element — it
returns an **opaque string ref** and keeps, server-side, both the cached element *and* a recipe to find it
again if it goes stale.

```csharp
public sealed class Entry(AutomationElement element)
{
    public AutomationElement Element { get; set; } = element;   // mutable: replaced on re-resolve
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public ControlType ControlType { get; init; }
    public IntPtr Hwnd { get; init; }                            // host window, the re-resolution scope
}
```

- Refs look like `el_` + the first 16 hex chars of a `Guid.NewGuid().ToString("N")`.
- Backed by a `ConcurrentDictionary<string, Entry>` plus a `ConcurrentQueue<string>` insertion order; the
  registry is capped at **20,000** entries (oldest evicted FIFO).

Re-resolution happens in `UiaService.Resolve`:

```csharp
private AutomationElement Resolve(string reference)
{
    var entry = _registry.Get(reference) ?? throw new UnknownElementException(reference);
    if (IsAlive(entry.Element)) return entry.Element;           // probe: read ProcessId, catch => dead
    var re = TryReResolve(entry);
    if (re is not null) { entry.Element = re; return re; }      // cache the fresh element
    throw new StaleElementException(reference);
}
```

`IsAlive` simply reads `entry.Element.Properties.ProcessId.Value` and treats any exception as "dead".
`TryReResolve` rebuilds a `ConditionBase` from the recipe — scoped to `FromHandle(entry.Hwnd)` if there is
a host HWND, else the desktop — preferring `AutomationId`, else `Name`, plus `ControlType` — and does a
`FindFirst(TreeScope.Descendants, cond)`.

**Recipe recap:** `AutomationId (or Name) [+ ControlType]`, scoped from the element's host window. If the
recipe can't be built or finds nothing, the ref is truly stale → `404 stale_element`, and the client is
told to re-query the tree.

## DTOs (`Models.cs`) — all `record`s

```csharp
public record RectDto(int X, int Y, int Width, int Height);   // physical pixels of the virtual desktop

public record DesktopStateDto(
    string Desktop,          // "default" | "secure" | "screensaver" | "unknown"
    string RawDesktopName,   // "Default", "Winlogon", or "" when inaccessible
    bool InputAvailable,
    string Note);

public record MonitorDto(int Index, RectDto Bounds, bool Primary, double DpiScale);

public record MachineInfoDto(
    string MachineName, string UserName, string OsVersion, bool IsElevated,
    IReadOnlyList<MonitorDto> Monitors, RectDto VirtualScreen, DesktopStateDto DesktopState);

public record ElementInfoDto(
    string Ref, string? Name, string ControlType, string? AutomationId, string? ClassName,
    string? RuntimeId, RectDto? BoundingRect, bool IsEnabled, bool IsOffscreen,
    long NativeWindowHandle, int? ProcessId, IReadOnlyList<string> Patterns);

public record TreeNodeDto(ElementInfoDto Element, IReadOnlyList<TreeNodeDto> Children);

public record FindQuery(                       // conditions AND-combined; null fields ignored
    string? Name = null, string? AutomationId = null, string? ControlType = null,
    string? ClassName = null, string Scope = "descendants", int Max = 100);  // scope: children|descendants|subtree

public enum ImageFormat { Png, Jpeg }

public record CaptureResultDto(
    string Desktop, RectDto Rect, int Monitor, double DpiScale, string Format, byte[] Bytes);
```

`ElementInfoDto.Patterns` is the subset of {Invoke, Value, Toggle, ExpandCollapse, SelectionItem,
Selection, Scroll, Window, Text, Grid} that the element supports. `RuntimeId` is the UIA runtime id joined
with `-` (informational only — never used as a durable key). `NativeWindowHandle` is `0` when the element
is not a window.

`SecureCapture.InputDesktopResult` is a nested record (see `11-secure-desktop.md`):

```csharp
public sealed record InputDesktopResult(
    bool Success, string DesktopName, string Kind, CaptureResultDto? Capture, string Note);
```

## Exceptions (`Exceptions.cs`)

| Type | Meaning | HTTP mapping (host) |
|---|---|---|
| `UnknownElementException(reference)` | ref never registered / evicted | 404 `stale_element` |
| `StaleElementException(reference)` | ref existed, gone, could not re-resolve | 404 `stale_element` |
| `PatternNotSupportedException(pattern, reference)` | element lacks the requested UIA pattern | 409 `pattern_not_supported` |
| `DesktopUnavailableException(message)` | action impossible on current desktop (e.g. SendInput blocked, no bounds) | 409 `desktop_unavailable` |
| `DisarmedException(action)` | kill switch engaged | 403 `disarmed` |
| `CapabilityDisabledException(capability)` | input or capture disabled by policy | 403 `capability_disabled` |

`ArgumentException` (bad monitor index, unknown control type, unknown key token, etc.) maps to 400
`bad_request`; anything else to 500 `internal`.

## `DpiHelper`

```csharp
public static bool EnablePerMonitorV2()
    => NativeMethods.SetProcessDpiAwarenessContext((IntPtr)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
```

**Must be called once at process startup, before any window/capture/coordinate work**, so captured pixels
and injected coordinates share one physical-pixel space across mixed-DPI monitors. Both hosts call it as
their very first line. See `06-input-and-dpi.md`.
