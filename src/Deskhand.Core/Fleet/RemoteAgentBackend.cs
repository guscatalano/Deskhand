namespace Deskhand.Core.Fleet;

/// <summary>An error that happened on a remote agent, carrying the agent's error type so the
/// fleet server can map it to the same HTTP status the local server would.</summary>
public sealed class RemoteAutomationException(string errorType, string message) : Exception(message)
{
    public string ErrorType { get; } = errorType;
}

/// <summary>The link to one connected agent: send a command, await its correlated result.</summary>
public interface IAgentLink
{
    string AgentId { get; }
    MachineInfoDto? Info { get; }
    Task<FleetResult> SendAsync(string method, object? args, CancellationToken ct = default);
}

/// <summary>
/// Server-side <see cref="IAutomationBackend"/> that forwards every call to a remote agent over its
/// link. This is the seam that makes the fleet work: the fleet server hands this to the exact same
/// tool surface, and calls land on the selected machine.
/// </summary>
public sealed class RemoteAgentBackend(IAgentLink link) : IAutomationBackend
{
    private T Call<T>(string method, object? args)
    {
        var res = link.SendAsync(method, args).GetAwaiter().GetResult();
        if (!res.Ok) throw new RemoteAutomationException(res.ErrorType ?? "internal", res.Error ?? "remote error");
        return FleetJson.Deserialize<T>(res.Result.GetRawText())!;
    }

    private void Send(string method, object? args)
    {
        var res = link.SendAsync(method, args).GetAwaiter().GetResult();
        if (!res.Ok) throw new RemoteAutomationException(res.ErrorType ?? "internal", res.Error ?? "remote error");
    }

    public DesktopStateDto GetDesktopState() => Call<DesktopStateDto>(FleetMethods.DesktopState, null);
    public MachineInfoDto GetMachineInfo() => Call<MachineInfoDto>(FleetMethods.MachineInfo, null);
    public ElementInfoDto GetForegroundWindow() => Call<ElementInfoDto>(FleetMethods.ForegroundWindow, null);
    public ElementInfoDto GetFocusedElement() => Call<ElementInfoDto>(FleetMethods.FocusedElement, null);
    public IReadOnlyList<ElementInfoDto> GetTopLevelWindows() => Call<List<ElementInfoDto>>(FleetMethods.ListWindows, null);
    public IReadOnlyList<ProcessInfoDto> GetProcesses() => Call<List<ProcessInfoDto>>(FleetMethods.ListProcesses, null);
    public ProcessLaunchResultDto LaunchProcess(string path, string? args, string? workingDir, int waitForWindowMs) => Call<ProcessLaunchResultDto>(FleetMethods.Launch, new { path, args, workingDir, waitForWindowMs });

    public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren) => Call<TreeNodeDto>(FleetMethods.GetTree, new { rootRef, depth, maxChildren });
    public IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery query) => Call<List<ElementInfoDto>>(FleetMethods.Find, new { rootRef, query });
    public ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs) => Call<ElementInfoDto?>(FleetMethods.WaitForElement, new { rootRef, query, timeoutMs });
    public ElementInfoDto GetElement(string reference) => Call<ElementInfoDto>(FleetMethods.GetElement, new { reference });
    public ElementInfoDto GetElementFromPoint(int x, int y) => Call<ElementInfoDto>(FleetMethods.ElementFromPoint, new { x, y });
    public IReadOnlyDictionary<string, string?> GetAllProperties(string reference) => Call<Dictionary<string, string?>>(FleetMethods.GetAllProperties, new { reference });

    public void Invoke(string reference) => Send(FleetMethods.Invoke, new { reference });
    public void SetValue(string reference, string text) => Send(FleetMethods.SetValue, new { reference, text });
    public void Toggle(string reference) => Send(FleetMethods.Toggle, new { reference });
    public void ExpandCollapse(string reference, bool expand) => Send(FleetMethods.ExpandCollapse, new { reference, expand });
    public void Select(string reference) => Send(FleetMethods.Select, new { reference });
    public void SetFocus(string reference) => Send(FleetMethods.SetFocus, new { reference });

    public CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int q) => Call<CaptureResultDto>(FleetMethods.CaptureScreen, new { monitor, format = format.ToString(), quality = q });
    public CaptureResultDto CaptureRegion(int x, int y, int w, int h, ImageFormat format, int q) => Call<CaptureResultDto>(FleetMethods.CaptureRegion, new { x, y, width = w, height = h, format = format.ToString(), quality = q });
    public CaptureResultDto CaptureWindow(long hwnd, ImageFormat format, int q) => Call<CaptureResultDto>(FleetMethods.CaptureWindow, new { hwnd, format = format.ToString(), quality = q });
    public CaptureResultDto CaptureWindowByRef(string reference, ImageFormat format, int q) => Call<CaptureResultDto>(FleetMethods.CaptureWindowByRef, new { reference, format = format.ToString(), quality = q });
    public CaptureResultDto CaptureElement(string reference, ImageFormat format, int q) => Call<CaptureResultDto>(FleetMethods.CaptureElement, new { reference, format = format.ToString(), quality = q });
    public Services.SecureCapture.InputDesktopResult CaptureInputDesktop(ImageFormat format, int q) => Call<Services.SecureCapture.InputDesktopResult>(FleetMethods.CaptureInputDesktop, new { format = format.ToString(), quality = q });

    public void MouseMove(int x, int y) => Send(FleetMethods.MouseMove, new { x, y });
    public void MouseClick(string button, int? x, int? y, int count) => Send(FleetMethods.MouseClick, new { button, x, y, count });
    public void MouseDown(string button, int? x, int? y) => Send(FleetMethods.MouseDown, new { button, x, y });
    public void MouseUp(string button, int? x, int? y) => Send(FleetMethods.MouseUp, new { button, x, y });
    public void Drag(int fromX, int fromY, int toX, int toY, string button, int steps, int holdMs) => Send(FleetMethods.Drag, new { fromX, fromY, toX, toY, button, steps, holdMs });
    public void MouseScroll(int dx, int dy) => Send(FleetMethods.MouseScroll, new { dx, dy });
    public void TypeText(string text) => Send(FleetMethods.TypeText, new { text });
    public void SendKeys(string chord) => Send(FleetMethods.SendKeys, new { chord });

    public void Dispose() { /* the link is owned by the registry */ }
}
