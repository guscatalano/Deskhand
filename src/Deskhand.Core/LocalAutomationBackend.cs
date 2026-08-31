using System.Drawing;
using Deskhand.Core.Services;

namespace Deskhand.Core;

/// <summary>
/// The single-machine, in-session backend. <b>UIA</b> (COM, not thread-safe) is marshalled onto one STA
/// thread (<see cref="StaExecutor"/>) so apartment rules live in one place. <b>Capture</b> (GDI) and
/// <b>input</b> (SendInput) are thread-agnostic, so they run OFF the STA thread — this lets a screenshot,
/// an input action, and a UIA query all proceed concurrently instead of queuing behind one another
/// (ref-based captures still resolve the element on the STA first, then capture off it). Covers the Default
/// desktop; the secure desktop is reported via <see cref="GetDesktopState"/> and is Phase 2 (SYSTEM helper).
/// </summary>
public sealed class LocalAutomationBackend : IAutomationBackend
{
    private readonly StaExecutor _sta;
    private UiaService _uia = null!;              // (re)created by the STA worker's onStart, incl. after a restart
    private Events.EventHub? _hub;                // remembered so events re-attach on a worker restart
    // Input is off the STA thread (SendInput is thread-safe), but each action is serialized so two
    // concurrent Type/click calls can't interleave their SendInput streams into a scrambled sequence.
    private readonly object _inputGate = new();

    public LocalAutomationBackend()
    {
        // The STA worker runs this on every (re)start, so if it self-heals after a hung UIA call it comes back
        // with a fresh UIA object + re-attached events — no restart of the process needed.
        _sta = new StaExecutor(onStart: () =>
        {
            _uia = new UiaService();
            var h = _hub;
            if (h is not null) { try { _uia.StartEvents(h); } catch { } }
        });
    }

    /// <summary>Begin publishing UIA events (focus, window-open) into the hub. Host-level setup,
    /// not part of the per-call tool surface.</summary>
    public void StartEvents(Events.EventHub hub)
    {
        _hub = hub;
        _sta.Invoke(() => _uia.StartEvents(hub));
    }

    // ---- orientation (pure P/Invoke; no STA needed) ----
    public DesktopStateDto GetDesktopState() => DesktopInfo.GetDesktopState();
    public MachineInfoDto GetMachineInfo() => DesktopInfo.GetMachineInfo();

    // Chromium-family executables that expose a usable UIA tree only when accessibility is forced on.
    // Electron apps have arbitrary exe names, so they're covered via DESKHAND_FORCE_A11Y=always.
    private static readonly string[] ChromiumExes =
        { "chrome", "msedge", "brave", "opera", "vivaldi", "chromium", "thorium", "chrome_proxy" };

    /// <summary>Dynamically append <c>--force-renderer-accessibility</c> when launching a Chromium/Electron
    /// app, so its web contents show up in the UIA tree. Auto-fires for known browsers; force on for any
    /// exe with <c>DESKHAND_FORCE_A11Y=always</c>; disable entirely with <c>DESKHAND_FORCE_A11Y=off</c>.
    /// (No effect if the browser is already running with the same profile — Chromium hands off to the
    /// existing instance. Launch with a fresh --user-data-dir, or the app not yet running, to take hold.)</summary>
    internal static string? InjectAccessibilityFlag(string path, string? args)
    {
        const string flag = "--force-renderer-accessibility";
        var mode = Environment.GetEnvironmentVariable("DESKHAND_FORCE_A11Y")?.Trim().ToLowerInvariant();
        if (mode is "0" or "off" or "false" or "no") return args;
        if (args?.Contains(flag, StringComparison.OrdinalIgnoreCase) == true) return args;

        string exe = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        bool force = mode is "1" or "on" or "true" or "always";
        if (!force && !ChromiumExes.Contains(exe)) return args;

        return string.IsNullOrEmpty(args) ? flag : $"{args} {flag}";
    }

    public ProcessLaunchResultDto LaunchProcess(string path, string? args, string? workingDir, int waitForWindowMs)
    {
        args = InjectAccessibilityFlag(path, args);
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;

        // Snapshot existing top-level windows so we can spot a *new* one — needed because packaged / Store
        // apps (Win11 Notepad, Terminal, Calculator…) hand the window to a DIFFERENT process than the one we
        // started, so watching only the launched process reports "no window" when a window did appear.
        var before = new HashSet<long>();
        string exeBase = System.IO.Path.GetFileNameWithoutExtension(path) ?? "";
        if (waitForWindowMs > 0)
            try { foreach (var w in _sta.Invoke(() => _uia.GetTopLevelWindows())) before.Add(w.NativeWindowHandle); } catch { }

        // Bad paths throw here (Win32Exception "file not found" etc.) — a real error, surfaced as-is. A NULL
        // return is NOT an error: the shell reused an existing process (a URL opened in an already-running
        // browser, a document opened in a running app). We keep going and still try to catch a new window.
        var proc = System.Diagnostics.Process.Start(psi);

        int pid = -1; string name = "";
        if (proc is not null) { try { pid = proc.Id; name = proc.ProcessName; } catch { } }

        ElementInfoDto? window = null;
        IntPtr hwnd = IntPtr.Zero;
        if (waitForWindowMs > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitForWindowMs)
            {
                // 1) the launched process's own main window
                try { if (proc is not null) { proc.Refresh(); if (!proc.HasExited) { hwnd = proc.MainWindowHandle; if (hwnd != IntPtr.Zero) break; } } } catch { }
                // 2) a NEW top-level window that appeared since launch, owned by the launched pid OR by a
                //    process whose name relates to the launched exe (the Store-app handoff).
                try
                {
                    var cand = _sta.Invoke(() => _uia.GetTopLevelWindows())
                        .FirstOrDefault(w => !before.Contains(w.NativeWindowHandle) && OwnedByLaunch(w.ProcessId ?? -1, pid, exeBase));
                    if (cand is not null) { window = cand; break; }
                }
                catch { }
                Thread.Sleep(100);
            }
        }

        if (window is null && hwnd != IntPtr.Zero) { var h = hwnd; window = _sta.Invoke(() => _uia.RegisterHandle(h)); }
        // Final fallback: any top-level window owned by the launched process.
        if (window is null && waitForWindowMs > 0 && pid > 0)
            try { window = _sta.Invoke(() => _uia.GetTopLevelWindows()).FirstOrDefault(w => w.ProcessId == pid); } catch { }

        return new ProcessLaunchResultDto(pid, name, window is not null, window);
    }

    // Does a top-level window's owning process belong to what we just launched? True if it's the launched
    // process, or a different process whose name relates to the launched exe (packaged-app handoff, e.g.
    // launched "notepad" → the window is owned by the "Notepad" store-app process).
    private static bool OwnedByLaunch(int windowPid, int launchedPid, string exeBase)
    {
        if (windowPid == launchedPid) return true;
        if (string.IsNullOrEmpty(exeBase)) return false;
        try
        {
            var pn = System.Diagnostics.Process.GetProcessById(windowPid).ProcessName;
            return pn.Contains(exeBase, StringComparison.OrdinalIgnoreCase)
                || exeBase.Contains(pn, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---- orientation via UIA ----
    public ElementInfoDto GetForegroundWindow() => _sta.Invoke(_uia.GetForegroundWindow);
    public ElementInfoDto GetFocusedElement() => _sta.Invoke(_uia.GetFocusedElement);
    public IReadOnlyList<ElementInfoDto> GetTopLevelWindows() => _sta.Invoke(_uia.GetTopLevelWindows);
    public IReadOnlyList<ProcessInfoDto> GetProcesses() => _sta.Invoke(_uia.GetProcesses);

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

    public ElementInfoDto GetElementFromPoint(int x, int y)
        => _sta.Invoke(() => _uia.GetElementFromPoint(x, y));

    // ---- uia act ----
    public void Invoke(string reference) => _sta.Invoke(() => _uia.Invoke(reference));
    public void SetValue(string reference, string text) => _sta.Invoke(() => _uia.SetValue(reference, text));
    public void Toggle(string reference) => _sta.Invoke(() => _uia.Toggle(reference));
    public void ExpandCollapse(string reference, bool expand) => _sta.Invoke(() => _uia.ExpandCollapse(reference, expand));
    public void Select(string reference) => _sta.Invoke(() => _uia.Select(reference));
    public void SetFocus(string reference) => _sta.Invoke(() => _uia.SetFocus(reference));

    // ---- capture (GDI: thread-agnostic → runs OFF the STA thread, concurrent with UIA) ----
    public CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int jpegQuality)
        => ScreenCapture.CaptureScreen(monitor, format, jpegQuality);

    public CaptureResultDto CaptureRegion(int x, int y, int width, int height, ImageFormat format, int jpegQuality)
        => ScreenCapture.CaptureRegion(x, y, width, height, format, jpegQuality);

    public CaptureResultDto CaptureWindow(long hwnd, ImageFormat format, int jpegQuality)
        => ScreenCapture.CaptureWindow((IntPtr)hwnd, format, jpegQuality);

    public CaptureResultDto CaptureWindowByRef(string reference, ImageFormat format, int jpegQuality)
    {
        // Resolve the element (UIA) on the STA thread, but do the GDI capture + encode off it.
        var (hwnd, bounds) = _sta.Invoke(() =>
        {
            var info = _uia.GetElement(reference);
            long h = info.NativeWindowHandle;
            return (h, h != 0 ? default : _uia.GetBounds(reference));
        });
        return hwnd != 0
            ? ScreenCapture.CaptureWindow((IntPtr)hwnd, format, jpegQuality)
            : ScreenCapture.CaptureBounds(bounds, format, jpegQuality);
    }

    public CaptureResultDto CaptureElement(string reference, ImageFormat format, int jpegQuality)
    {
        Rectangle bounds = _sta.Invoke(() => _uia.GetBounds(reference));   // UIA resolve on STA
        return ScreenCapture.CaptureBounds(bounds, format, jpegQuality);   // capture off it
    }

    // Runs on its own throwaway thread (see SecureCapture) — must NOT use the UIA STA thread.
    public Services.SecureCapture.InputDesktopResult CaptureInputDesktop(ImageFormat format, int jpegQuality)
        => Services.SecureCapture.CaptureInputDesktop(format, jpegQuality);

    // ---- input (SendInput: thread-agnostic → OFF the STA thread, serialized on _inputGate so
    //      concurrent actions stay atomic instead of interleaving keystrokes/clicks) ----
    public void MouseMove(int x, int y) { lock (_inputGate) InputInjector.MouseMove(x, y); }
    public void MouseClick(string button, int? x, int? y, int count) { lock (_inputGate) InputInjector.MouseClick(button, x, y, count); }
    public void MouseDown(string button, int? x, int? y) { lock (_inputGate) InputInjector.MouseDown(button, x, y); }
    public void MouseUp(string button, int? x, int? y) { lock (_inputGate) InputInjector.MouseUp(button, x, y); }
    public void MouseScroll(int dx, int dy) { lock (_inputGate) InputInjector.MouseScroll(dx, dy); }
    public void TypeText(string text) { lock (_inputGate) InputInjector.TypeText(text); }
    public void SendKeys(string chord) { lock (_inputGate) InputInjector.SendKeys(chord); }

    public void Dispose()
    {
        try { _sta.Invoke(() => _uia.Dispose()); } catch { }
        _sta.Dispose();
    }
}
