namespace Deskhand.Core;

/// <summary>
/// The single seam every command routes through. The local (in-session) backend is the
/// first implementation; a future gRPC fleet-agent backend and an RDP-protocol backend
/// implement this same contract so the HTTP/MCP surface never changes. UIA members are
/// "agent-only"; capture and input are transport-portable.
/// </summary>
public interface IAutomationBackend : IDisposable
{
    // orientation
    DesktopStateDto GetDesktopState();
    MachineInfoDto GetMachineInfo();
    ElementInfoDto GetForegroundWindow();
    ElementInfoDto GetFocusedElement();
    IReadOnlyList<ElementInfoDto> GetTopLevelWindows();

    /// <summary>Launch a program (by path or shell name/URL/document), optionally waiting for its
    /// main window. Governed as an input-class action.</summary>
    ProcessLaunchResultDto LaunchProcess(string path, string? args, string? workingDir, int waitForWindowMs);

    // uia — read
    TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren);
    IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery query);

    /// <summary>Poll for an element matching the query until it appears or the timeout elapses.
    /// Returns the first match, or null on timeout.</summary>
    ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs);

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

    /// <summary>Phase 2: capture whichever desktop currently owns input (covers the secure
    /// desktop when this process runs as SYSTEM in the console session).</summary>
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
