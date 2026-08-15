using Deskhand.Core.Fleet;
using Deskhand.Core.Macros;

namespace Deskhand.Core.Governance;

/// <summary>
/// Wraps a real <see cref="IAutomationBackend"/> to enforce the kill switch / capability gates,
/// write an audit record for every call, notify on capture, and feed the macro recorder. Reads are
/// always allowed (and audited); input and capture are refused when disarmed or disabled. Both the
/// HTTP and MCP hosts use this, so governance lives in exactly one place at the backend seam.
/// </summary>
public sealed class GovernedBackend(IAutomationBackend inner, ControlState state, AuditLog audit, ICaptureNotifier? notifier = null, MacroRecorder? recorder = null) : IAutomationBackend
{
    // Snapshot the element's selector BEFORE the action runs, so an action that closes its own
    // element (e.g. a button that dismisses a dialog) is still recorded.
    private ElementInfoDto? RecSnapshot(string reference)
    {
        if (recorder?.IsRecording != true) return null;
        try { return inner.GetElement(reference); } catch { return null; }
    }

    private void NotifyCapture(string action, string desktop, int w, int h)
    {
        if (!state.NotifyOnCapture || notifier is null) return;
        try { notifier.Notify($"Deskhand took a screenshot  ·  {desktop}  ·  {w}×{h}"); audit.Record("screenshot_toast", action, "shown"); }
        catch { /* a notifier failure must never break capture */ }
    }

    private T Audited<T>(string action, string? detail, Func<T> op)
    {
        try { var r = op(); audit.Record(action, detail, "ok"); return r; }
        catch (Exception ex) { audit.Record(action, detail, "error:" + ex.GetType().Name); throw; }
    }

    private void RequireInput(string action)
    {
        if (!state.Armed) { audit.Record(action, null, "refused:disarmed"); throw new DisarmedException(action); }
        if (!state.InputEnabled) { audit.Record(action, null, "refused:input-disabled"); throw new CapabilityDisabledException("input"); }
    }

    private void RequireCapture(string action)
    {
        if (!state.Armed) { audit.Record(action, null, "refused:disarmed"); throw new DisarmedException(action); }
        if (!state.CaptureEnabled) { audit.Record(action, null, "refused:capture-disabled"); throw new CapabilityDisabledException("capture"); }
    }

    private CaptureResultDto Capture(string action, string? detail, Func<CaptureResultDto> op)
    {
        RequireCapture(action);
        try
        {
            var r = op();
            audit.Record(action, $"{detail} {r.Rect.Width}x{r.Rect.Height} sha={AuditLog.HashImage(r.Bytes)}", "ok");
            NotifyCapture(action, r.Desktop, r.Rect.Width, r.Rect.Height);
            return r;
        }
        catch (Exception ex) { audit.Record(action, detail, "error:" + ex.GetType().Name); throw; }
    }

    // ---- orientation (reads) ----
    public DesktopStateDto GetDesktopState() => Audited("desktop_state", null, inner.GetDesktopState);
    public MachineInfoDto GetMachineInfo() => Audited("machine_info", null, inner.GetMachineInfo);
    public ElementInfoDto GetForegroundWindow() => Audited("foreground_window", null, inner.GetForegroundWindow);
    public ElementInfoDto GetFocusedElement() => Audited("focused_element", null, inner.GetFocusedElement);
    public IReadOnlyList<ElementInfoDto> GetTopLevelWindows() => Audited("list_windows", null, inner.GetTopLevelWindows);
    public IReadOnlyList<ProcessInfoDto> GetProcesses() => Audited("list_processes", null, inner.GetProcesses);

    public ProcessLaunchResultDto LaunchProcess(string path, string? args, string? workingDir, int waitForWindowMs)
    {
        RequireInput("launch_process");
        var r = Audited("launch_process", $"{path} {args}", () => inner.LaunchProcess(path, args, workingDir, waitForWindowMs));
        recorder?.RecordInput(FleetMethods.Launch, new { path, args, workingDir, waitForWindowMs });
        return r;
    }

    // ---- uia read ----
    public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren)
        => Audited("get_tree", $"root={rootRef ?? "desktop"} depth={depth}", () => inner.GetTree(rootRef, depth, maxChildren));
    public IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery query)
        => Audited("find", $"root={rootRef ?? "desktop"}", () => inner.Find(rootRef, query));
    public ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs)
        => Audited("wait_for_element", $"timeout={timeoutMs}", () => inner.WaitForElement(rootRef, query, timeoutMs));
    public ElementInfoDto GetElement(string reference) => Audited("get_element", reference, () => inner.GetElement(reference));
    public ElementInfoDto GetElementFromPoint(int x, int y) => Audited("element_from_point", $"{x},{y}", () => inner.GetElementFromPoint(x, y));
    public IReadOnlyDictionary<string, string?> GetAllProperties(string reference)
        => Audited("get_all_properties", reference, () => inner.GetAllProperties(reference));

    // ---- uia act (input-class: gated) ----
    public void Invoke(string reference) { RequireInput("invoke"); var s = RecSnapshot(reference); Audited<object?>("invoke", reference, () => { inner.Invoke(reference); return null; }); if (s != null) recorder!.RecordUia(FleetMethods.Invoke, s, null); }
    public void SetValue(string reference, string text) { RequireInput("set_value"); var s = RecSnapshot(reference); Audited<object?>("set_value", reference, () => { inner.SetValue(reference, text); return null; }); if (s != null) recorder!.RecordUia(FleetMethods.SetValue, s, new { text }); }
    public void Toggle(string reference) { RequireInput("toggle"); var s = RecSnapshot(reference); Audited<object?>("toggle", reference, () => { inner.Toggle(reference); return null; }); if (s != null) recorder!.RecordUia(FleetMethods.Toggle, s, null); }
    public void ExpandCollapse(string reference, bool expand) { RequireInput("expand_collapse"); var s = RecSnapshot(reference); Audited<object?>("expand_collapse", $"{reference} expand={expand}", () => { inner.ExpandCollapse(reference, expand); return null; }); if (s != null) recorder!.RecordUia(FleetMethods.ExpandCollapse, s, new { expand }); }
    public void Select(string reference) { RequireInput("select"); var s = RecSnapshot(reference); Audited<object?>("select", reference, () => { inner.Select(reference); return null; }); if (s != null) recorder!.RecordUia(FleetMethods.Select, s, null); }
    public void SetFocus(string reference) { RequireInput("set_focus"); var s = RecSnapshot(reference); Audited<object?>("set_focus", reference, () => { inner.SetFocus(reference); return null; }); if (s != null) recorder!.RecordUia(FleetMethods.SetFocus, s, null); }

    // ---- capture (gated) ----
    public CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int q) => Capture("capture_screen", $"monitor={monitor}", () => inner.CaptureScreen(monitor, format, q));
    public CaptureResultDto CaptureRegion(int x, int y, int w, int h, ImageFormat format, int q) => Capture("capture_region", $"{x},{y},{w},{h}", () => inner.CaptureRegion(x, y, w, h, format, q));
    public CaptureResultDto CaptureWindow(long hwnd, ImageFormat format, int q) => Capture("capture_window", $"hwnd={hwnd}", () => inner.CaptureWindow(hwnd, format, q));
    public CaptureResultDto CaptureWindowByRef(string reference, ImageFormat format, int q) => Capture("capture_window", reference, () => inner.CaptureWindowByRef(reference, format, q));
    public CaptureResultDto CaptureElement(string reference, ImageFormat format, int q) => Capture("capture_element", reference, () => inner.CaptureElement(reference, format, q));

    public Services.SecureCapture.InputDesktopResult CaptureInputDesktop(ImageFormat format, int q)
    {
        RequireCapture("capture_input_desktop");
        try
        {
            var r = inner.CaptureInputDesktop(format, q);
            audit.Record("capture_input_desktop", $"desktop={r.DesktopName}", r.Success ? "ok" : "empty");
            if (r.Success && r.Capture is not null) NotifyCapture("capture_input_desktop", r.Capture.Desktop, r.Capture.Rect.Width, r.Capture.Rect.Height);
            return r;
        }
        catch (Exception ex) { audit.Record("capture_input_desktop", null, "error:" + ex.GetType().Name); throw; }
    }

    // ---- input (gated) ----
    public void MouseMove(int x, int y) { RequireInput("mouse_move"); Audited<object?>("mouse_move", $"{x},{y}", () => { inner.MouseMove(x, y); return null; }); recorder?.RecordInput(FleetMethods.MouseMove, new { x, y }); }
    public void MouseClick(string button, int? x, int? y, int count) { RequireInput("mouse_click"); Audited<object?>("mouse_click", $"{button} {x},{y} x{count}", () => { inner.MouseClick(button, x, y, count); return null; }); recorder?.RecordInput(FleetMethods.MouseClick, new { button, x, y, count }); }
    public void MouseDown(string button, int? x, int? y) { RequireInput("mouse_down"); Audited<object?>("mouse_down", button, () => { inner.MouseDown(button, x, y); return null; }); recorder?.RecordInput(FleetMethods.MouseDown, new { button, x, y }); }
    public void MouseUp(string button, int? x, int? y) { RequireInput("mouse_up"); Audited<object?>("mouse_up", button, () => { inner.MouseUp(button, x, y); return null; }); recorder?.RecordInput(FleetMethods.MouseUp, new { button, x, y }); }
    public void MouseScroll(int dx, int dy) { RequireInput("mouse_scroll"); Audited<object?>("mouse_scroll", $"{dx},{dy}", () => { inner.MouseScroll(dx, dy); return null; }); recorder?.RecordInput(FleetMethods.MouseScroll, new { dx, dy }); }
    public void TypeText(string text) { RequireInput("type_text"); Audited<object?>("type_text", $"len={text?.Length ?? 0}", () => { inner.TypeText(text!); return null; }); recorder?.RecordInput(FleetMethods.TypeText, new { text }); }
    public void SendKeys(string chord) { RequireInput("send_keys"); Audited<object?>("send_keys", chord, () => { inner.SendKeys(chord); return null; }); recorder?.RecordInput(FleetMethods.SendKeys, new { chord }); }

    public void Dispose() => inner.Dispose();
}
