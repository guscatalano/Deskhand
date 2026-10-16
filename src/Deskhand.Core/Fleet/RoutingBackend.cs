using System.Runtime.InteropServices;

namespace Deskhand.Core.Fleet;

/// <summary>
/// The hybrid front end's <see cref="IAutomationBackend"/>: every desktop call is forwarded to the
/// <see cref="RemoteAgentBackend"/> of a <b>per-session agent</b> instead of running in this (session-0) process.
/// The target session defaults to the <b>active console</b> (<see cref="NativeMethods"/> WTSGetActiveConsoleSessionId)
/// and can be overridden per async-flow via <see cref="TargetSession"/> — set it from a request's <c>session=</c>
/// before the call, which keeps concurrent callers independent (each HTTP request / tool call has its own flow).
/// When no agent is connected in the target session, calls throw a clear <see cref="RemoteAutomationException"/>.
/// </summary>
public sealed class RoutingBackend(AgentRegistry registry) : IAutomationBackend
{
    /// <summary>Per-async-flow target session override; null → the active console session.</summary>
    public static readonly AsyncLocal<int?> TargetSession = new();

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    private const uint INVALID_SESSION = 0xFFFFFFFF;

    /// <summary>Resolve the agent for the target session (override or active console), or throw.</summary>
    private RemoteAgentBackend Target()
    {
        int session = TargetSession.Value ?? ConsoleSession();
        var link = registry.GetBySession(session)
            ?? throw new RemoteAutomationException("no_agent",
                $"No Deskhand agent is connected in session {session} yet. " +
                "An agent is spawned into each interactive session; there is none at the logon screen until a user " +
                "signs in (or a SYSTEM agent is running). Non-desktop tools (files, registry, shell, events) still work.");
        return new RemoteAgentBackend(link);
    }

    private static int ConsoleSession()
    {
        uint s = WTSGetActiveConsoleSessionId();
        if (s == INVALID_SESSION) throw new RemoteAutomationException("no_console", "No active console session.");
        return (int)s;
    }

    // orientation
    public DesktopStateDto GetDesktopState() => Target().GetDesktopState();
    public MachineInfoDto GetMachineInfo() => Target().GetMachineInfo();
    public ElementInfoDto GetForegroundWindow() => Target().GetForegroundWindow();
    public ElementInfoDto GetFocusedElement() => Target().GetFocusedElement();
    public IReadOnlyList<ElementInfoDto> GetTopLevelWindows() => Target().GetTopLevelWindows();
    public IReadOnlyList<ProcessInfoDto> GetProcesses() => Target().GetProcesses();
    public ProcessLaunchResultDto LaunchProcess(string path, string? args, string? workingDir, int waitForWindowMs)
        => Target().LaunchProcess(path, args, workingDir, waitForWindowMs);

    // uia — read
    public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren) => Target().GetTree(rootRef, depth, maxChildren);
    public IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery query) => Target().Find(rootRef, query);
    public ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs) => Target().WaitForElement(rootRef, query, timeoutMs);
    public IReadOnlyList<ElementInfoDto> FindStartApps(string? query, int max) => Target().FindStartApps(query, max);
    public ElementInfoDto GetElement(string reference) => Target().GetElement(reference);
    public string? GetText(string reference) => Target().GetText(reference);
    public IReadOnlyDictionary<string, string?> GetAllProperties(string reference) => Target().GetAllProperties(reference);
    public ElementInfoDto GetElementFromPoint(int x, int y) => Target().GetElementFromPoint(x, y);

    // uia — act
    public void Invoke(string reference) => Target().Invoke(reference);
    public void SetValue(string reference, string text) => Target().SetValue(reference, text);
    public void Toggle(string reference) => Target().Toggle(reference);
    public void ExpandCollapse(string reference, bool expand) => Target().ExpandCollapse(reference, expand);
    public void Select(string reference) => Target().Select(reference);
    public void SetFocus(string reference) => Target().SetFocus(reference);

    // capture
    public CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int jpegQuality) => Target().CaptureScreen(monitor, format, jpegQuality);
    public CaptureResultDto CaptureRegion(int x, int y, int width, int height, ImageFormat format, int jpegQuality) => Target().CaptureRegion(x, y, width, height, format, jpegQuality);
    public CaptureResultDto CaptureWindow(long hwnd, ImageFormat format, int jpegQuality) => Target().CaptureWindow(hwnd, format, jpegQuality);
    public CaptureResultDto CaptureWindowByRef(string reference, ImageFormat format, int jpegQuality) => Target().CaptureWindowByRef(reference, format, jpegQuality);
    public CaptureResultDto CaptureElement(string reference, ImageFormat format, int jpegQuality) => Target().CaptureElement(reference, format, jpegQuality);
    public Services.SecureCapture.InputDesktopResult CaptureInputDesktop(ImageFormat format, int jpegQuality) => Target().CaptureInputDesktop(format, jpegQuality);

    // input
    public void MouseMove(int x, int y) => Target().MouseMove(x, y);
    public void MouseClick(string button, int? x, int? y, int count) => Target().MouseClick(button, x, y, count);
    public void MouseDown(string button, int? x, int? y) => Target().MouseDown(button, x, y);
    public void MouseUp(string button, int? x, int? y) => Target().MouseUp(button, x, y);
    public void MouseScroll(int dx, int dy) => Target().MouseScroll(dx, dy);
    public void Drag(int fromX, int fromY, int toX, int toY, string button, int steps, int holdMs) => Target().Drag(fromX, fromY, toX, toY, button, steps, holdMs);
    public void TypeText(string text) => Target().TypeText(text);
    public void SendKeys(string chord) => Target().SendKeys(chord);

    public void Dispose() { }   // links are owned by the registry
}
