using Deskhand.Core;

namespace Deskhand.Rdp;

/// <summary>
/// The zero-install RDP backend behind the shared <see cref="IAutomationBackend"/> seam. It drives a
/// remote host over the RDP wire with nothing installed on the target, so only the transport-portable
/// subset works: screen/region capture and mouse/keyboard input. Everything UIA (tree, elements,
/// patterns, windows, launch) throws — there is no accessibility tree over pure RDP.
/// </summary>
public sealed class RdpBackend(RdpHost host, string hostName) : IAutomationBackend
{
    private static T No<T>() => throw new NotSupportedException(
        "Not available over RDP: no agent runs on the target, so UIA/windows/launch aren't exposed — capture and input only.");

    private CaptureResultDto Shot(int x, int y, int w, int h)
    {
        var full = host.Capture();
        // region crop, if requested inside the remote desktop
        if (x == 0 && y == 0 && w == host.Width && h == host.Height)
            return new CaptureResultDto("rdp", new RectDto(0, 0, host.Width, host.Height), -1, 1.0, "png", full);

        using var src = System.Drawing.Image.FromStream(new MemoryStream(full));
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, w, h), new System.Drawing.Rectangle(x, y, w, h), System.Drawing.GraphicsUnit.Pixel);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return new CaptureResultDto("rdp", new RectDto(x, y, w, h), -1, 1.0, "png", ms.ToArray());
    }

    // ---- capture (supported) ----
    public CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int q) => Shot(0, 0, host.Width, host.Height);
    public CaptureResultDto CaptureRegion(int x, int y, int w, int h, ImageFormat format, int q) => Shot(x, y, w, h);

    // ---- input (supported) ----
    public void MouseMove(int x, int y) => host.MouseMove(x, y);
    public void MouseClick(string button, int? x, int? y, int count)
    {
        for (int i = 0; i < Math.Max(1, count); i++) host.MouseClick(button, x ?? 0, y ?? 0);
    }
    public void MouseDown(string button, int? x, int? y) => host.MouseClick(button, x ?? 0, y ?? 0);
    public void MouseUp(string button, int? x, int? y) { /* click covers down+up */ }
    public void Drag(int fromX, int fromY, int toX, int toY, string button, int steps, int holdMs)
        => throw new NotSupportedException("Drag isn't supported over the RDP backend (its input path can't press-and-hold).");
    public void MouseScroll(int dx, int dy) { /* wheel-over-RDP not wired in this build */ }
    public void TypeText(string text) => host.TypeText(text);
    public void SendKeys(string chord) => host.SendKey(ResolveVk(chord));

    private static ushort ResolveVk(string chord) => chord.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B, "space" => 0x20,
        "backspace" => 0x08, "delete" or "del" => 0x2E, "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
        _ => chord.Length == 1 ? (ushort)char.ToUpperInvariant(chord[0]) : (ushort)0,
    };

    // ---- orientation: minimal ----
    public DesktopStateDto GetDesktopState() => new("rdp", hostName, host.Connected, host.Connected ? "Connected over RDP." : $"Not connected ({host.LastReason}).");
    public MachineInfoDto GetMachineInfo() => new(hostName, "", "RDP", false,
        new[] { new MonitorDto(0, new RectDto(0, 0, host.Width, host.Height), true, 1.0) },
        new RectDto(0, 0, host.Width, host.Height), GetDesktopState());

    // ---- everything UIA / windows / launch: not available over RDP ----
    public ElementInfoDto GetForegroundWindow() => No<ElementInfoDto>();
    public ElementInfoDto GetFocusedElement() => No<ElementInfoDto>();
    public IReadOnlyList<ElementInfoDto> GetTopLevelWindows() => No<IReadOnlyList<ElementInfoDto>>();
    public IReadOnlyList<ProcessInfoDto> GetProcesses() => No<IReadOnlyList<ProcessInfoDto>>();
    public ProcessLaunchResultDto LaunchProcess(string p, string? a, string? w, int t) => No<ProcessLaunchResultDto>();
    public TreeNodeDto GetTree(string? r, int d, int m) => No<TreeNodeDto>();
    public IReadOnlyList<ElementInfoDto> Find(string? r, FindQuery q) => No<IReadOnlyList<ElementInfoDto>>();
    public ElementInfoDto? WaitForElement(string? r, FindQuery q, int t) => No<ElementInfoDto?>();
    public ElementInfoDto GetElement(string r) => No<ElementInfoDto>();
    public IReadOnlyDictionary<string, string?> GetAllProperties(string r) => No<IReadOnlyDictionary<string, string?>>();
    public ElementInfoDto GetElementFromPoint(int x, int y) => No<ElementInfoDto>();
    public void Invoke(string r) => No<object>();
    public void SetValue(string r, string t) => No<object>();
    public void Toggle(string r) => No<object>();
    public void ExpandCollapse(string r, bool e) => No<object>();
    public void Select(string r) => No<object>();
    public void SetFocus(string r) => No<object>();
    public CaptureResultDto CaptureWindow(long h, ImageFormat f, int q) => No<CaptureResultDto>();
    public CaptureResultDto CaptureWindowByRef(string r, ImageFormat f, int q) => No<CaptureResultDto>();
    public CaptureResultDto CaptureElement(string r, ImageFormat f, int q) => No<CaptureResultDto>();
    public Deskhand.Core.Services.SecureCapture.InputDesktopResult CaptureInputDesktop(ImageFormat f, int q) => No<Deskhand.Core.Services.SecureCapture.InputDesktopResult>();

    public void Dispose() => host.Dispose();
}
