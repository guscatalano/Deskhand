using Deskhand.Core.Interop;

namespace Deskhand.Core;

public static class DpiHelper
{
    /// <summary>
    /// Opt the process into Per-Monitor-v2 DPI awareness. Must be called once, before any
    /// window, capture, or coordinate work, so that captured pixels and injected coordinates
    /// share the same physical-pixel space across mixed-DPI monitors.
    /// </summary>
    public static bool EnablePerMonitorV2()
    {
        try { return NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { return false; }
    }
}
