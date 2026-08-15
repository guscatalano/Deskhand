using System.Drawing;
using Deskhand.Core.Services;

namespace Deskhand.Core;

/// <summary>
/// The single-machine, in-session backend. All UIA/capture/input work is marshalled onto
/// one STA thread (<see cref="StaExecutor"/>) so COM apartment rules and UIA's lack of
/// thread-safety are handled in exactly one place. Covers the Default desktop; the secure
/// desktop is reported via <see cref="GetDesktopState"/> and is Phase 2 (SYSTEM helper).
/// </summary>
public sealed class LocalAutomationBackend : IAutomationBackend
{
    private readonly StaExecutor _sta = new();
    private readonly UiaService _uia;

    public LocalAutomationBackend()
    {
        _uia = _sta.Invoke(() => new UiaService());
    }

    /// <summary>Begin publishing UIA events (focus, window-open) into the hub. Host-level setup,
    /// not part of the per-call tool surface.</summary>
    public void StartEvents(Events.EventHub hub) => _sta.Invoke(() => _uia.StartEvents(hub));

    // ---- orientation (pure P/Invoke; no STA needed) ----
    public DesktopStateDto GetDesktopState() => DesktopInfo.GetDesktopState();
    public MachineInfoDto GetMachineInfo() => DesktopInfo.GetMachineInfo();

    // ---- orientation via UIA ----
    public ElementInfoDto GetForegroundWindow() => _sta.Invoke(_uia.GetForegroundWindow);
    public ElementInfoDto GetFocusedElement() => _sta.Invoke(_uia.GetFocusedElement);
    public IReadOnlyList<ElementInfoDto> GetTopLevelWindows() => _sta.Invoke(_uia.GetTopLevelWindows);

    // ---- uia read ----
    public TreeNodeDto GetTree(string? rootRef, int depth, int maxChildren)
        => _sta.Invoke(() => _uia.GetTree(rootRef, depth, maxChildren));

    public IReadOnlyList<ElementInfoDto> Find(string? rootRef, FindQuery query)
        => _sta.Invoke(() => _uia.Find(rootRef, query));

    // Poll off the STA thread — each probe hops onto it, but the thread is released between
    // probes so other calls aren't blocked for the whole timeout.
    public ElementInfoDto? WaitForElement(string? rootRef, FindQuery query, int timeoutMs)
    {
        var probe = query with { Max = 1 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var found = _sta.Invoke(() => _uia.Find(rootRef, probe));
            if (found.Count > 0) return found[0];
            if (sw.ElapsedMilliseconds >= Math.Max(0, timeoutMs)) return null;
            Thread.Sleep(150);
        }
    }

    public ElementInfoDto GetElement(string reference)
        => _sta.Invoke(() => _uia.GetElement(reference));

    public IReadOnlyDictionary<string, string?> GetAllProperties(string reference)
        => _sta.Invoke(() => _uia.GetAllProperties(reference));

    // ---- uia act ----
    public void Invoke(string reference) => _sta.Invoke(() => _uia.Invoke(reference));
    public void SetValue(string reference, string text) => _sta.Invoke(() => _uia.SetValue(reference, text));
    public void Toggle(string reference) => _sta.Invoke(() => _uia.Toggle(reference));
    public void ExpandCollapse(string reference, bool expand) => _sta.Invoke(() => _uia.ExpandCollapse(reference, expand));
    public void Select(string reference) => _sta.Invoke(() => _uia.Select(reference));
    public void SetFocus(string reference) => _sta.Invoke(() => _uia.SetFocus(reference));

    // ---- capture ----
    public CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int jpegQuality)
        => _sta.Invoke(() => ScreenCapture.CaptureScreen(monitor, format, jpegQuality));

    public CaptureResultDto CaptureRegion(int x, int y, int width, int height, ImageFormat format, int jpegQuality)
        => _sta.Invoke(() => ScreenCapture.CaptureRegion(x, y, width, height, format, jpegQuality));

    public CaptureResultDto CaptureWindow(long hwnd, ImageFormat format, int jpegQuality)
        => _sta.Invoke(() => ScreenCapture.CaptureWindow((IntPtr)hwnd, format, jpegQuality));

    public CaptureResultDto CaptureWindowByRef(string reference, ImageFormat format, int jpegQuality)
        => _sta.Invoke(() =>
        {
            var info = _uia.GetElement(reference);
            if (info.NativeWindowHandle != 0)
                return ScreenCapture.CaptureWindow((IntPtr)info.NativeWindowHandle, format, jpegQuality);
            return ScreenCapture.CaptureBounds(_uia.GetBounds(reference), format, jpegQuality);
        });

    public CaptureResultDto CaptureElement(string reference, ImageFormat format, int jpegQuality)
        => _sta.Invoke(() =>
        {
            Rectangle bounds = _uia.GetBounds(reference);
            return ScreenCapture.CaptureBounds(bounds, format, jpegQuality);
        });

    // Runs on its own throwaway thread (see SecureCapture) — must NOT use the UIA STA thread.
    public Services.SecureCapture.InputDesktopResult CaptureInputDesktop(ImageFormat format, int jpegQuality)
        => Services.SecureCapture.CaptureInputDesktop(format, jpegQuality);

    // ---- input ----
    public void MouseMove(int x, int y) => _sta.Invoke(() => InputInjector.MouseMove(x, y));
    public void MouseClick(string button, int? x, int? y, int count) => _sta.Invoke(() => InputInjector.MouseClick(button, x, y, count));
    public void MouseDown(string button, int? x, int? y) => _sta.Invoke(() => InputInjector.MouseDown(button, x, y));
    public void MouseUp(string button, int? x, int? y) => _sta.Invoke(() => InputInjector.MouseUp(button, x, y));
    public void MouseScroll(int dx, int dy) => _sta.Invoke(() => InputInjector.MouseScroll(dx, dy));
    public void TypeText(string text) => _sta.Invoke(() => InputInjector.TypeText(text));
    public void SendKeys(string chord) => _sta.Invoke(() => InputInjector.SendKeys(chord));

    public void Dispose()
    {
        try { _sta.Invoke(() => _uia.Dispose()); } catch { }
        _sta.Dispose();
    }
}
