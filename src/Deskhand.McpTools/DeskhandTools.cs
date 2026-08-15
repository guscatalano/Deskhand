using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deskhand.Core;
using Deskhand.Core.Governance;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Deskhand.McpTools;

/// <summary>
/// The Deskhand MCP tool surface. Every tool delegates to the shared <see cref="IAutomationBackend"/>
/// (injected from DI), so this server and the HTTP server expose identical capabilities. UIA reads
/// return JSON text; captures return an MCP image plus a one-line text summary.
/// </summary>
[McpServerToolType]
public static class DeskhandTools
{
    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    private static string Json(object? o) => JsonSerializer.Serialize(o, J);

    private static ImageFormat Fmt(string? f) =>
        f?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;

    private static IEnumerable<ContentBlock> AsImage(CaptureResultDto c)
    {
        yield return new TextContentBlock
        {
            Text = $"desktop={c.Desktop} rect={c.Rect.Width}x{c.Rect.Height}@({c.Rect.X},{c.Rect.Y}) " +
                   $"monitor={c.Monitor} dpi={c.DpiScale} format={c.Format}",
        };
        yield return new ImageContentBlock
        {
            Data = c.Bytes,
            MimeType = c.Format == "jpeg" ? "image/jpeg" : "image/png",
        };
    }

    // ---------- orientation ----------

    [McpServerTool(Name = "deskhand_machine_info"), Description("Machine name, user, elevation, monitors, virtual-screen bounds, and current desktop state.")]
    public static string MachineInfo(IAutomationBackend b) => Json(b.GetMachineInfo());

    [McpServerTool(Name = "deskhand_desktop_state"), Description("Which desktop currently owns input: default, secure (UAC/lock/logon), or screensaver.")]
    public static string DesktopState(IAutomationBackend b) => Json(b.GetDesktopState());

    [McpServerTool(Name = "deskhand_list_windows"), Description("List all top-level windows. The reliable way to target a specific app (foreground is unreliable when a tool has focus).")]
    public static string ListWindows(IAutomationBackend b) => Json(b.GetTopLevelWindows());

    [McpServerTool(Name = "deskhand_list_processes"), Description("List every running process, each with the top-level windows it owns (windowed apps first; background processes have an empty windows list). Each window carries a live ref you can pass straight to deskhand_get_tree to expand its UIA tree — process → windows → elements.")]
    public static string ListProcesses(IAutomationBackend b) => Json(b.GetProcesses());

    [McpServerTool(Name = "deskhand_launch_process"), Description("Launch a program by path or shell name/URL (e.g. \"notepad\", \"C:\\\\app.exe\", \"https://...\"). Waits up to waitForWindowMs for its main window and returns it if it appears.")]
    public static string LaunchProcess(IAutomationBackend b, string path, string? args = null, string? workingDir = null, int waitForWindowMs = 4000)
        => Json(b.LaunchProcess(path, args, workingDir, waitForWindowMs));

    // ---------- governance ----------

    [McpServerTool(Name = "deskhand_control_status"), Description("Report the kill-switch/capability state: armed, inputEnabled, captureEnabled.")]
    public static string ControlStatus(ControlState s) => Json(new { armed = s.Armed, inputEnabled = s.InputEnabled, captureEnabled = s.CaptureEnabled });

    [McpServerTool(Name = "deskhand_disarm"), Description("Engage the kill switch: refuse all input and capture until re-armed.")]
    public static string Disarm(ControlState s) { s.Armed = false; return "disarmed"; }

    [McpServerTool(Name = "deskhand_arm"), Description("Release the kill switch: allow input and capture again.")]
    public static string Arm(ControlState s) { s.Armed = true; return "armed"; }

    // ---------- record & playback ----------

    [McpServerTool(Name = "deskhand_macro_start"), Description("Start recording actions (input + UIA acts) into a macro.")]
    public static string MacroStart(Deskhand.Core.Macros.MacroRecorder r) { r.Start(); return "recording"; }

    [McpServerTool(Name = "deskhand_macro_stop"), Description("Stop recording and return the recorded macro (also kept as the 'last' macro for playback).")]
    public static string MacroStop(Deskhand.Core.Macros.MacroRecorder r) => Json(r.Stop());

    [McpServerTool(Name = "deskhand_macro_status"), Description("Whether a recording is in progress, its step count, and whether a macro is available to play.")]
    public static string MacroStatus(Deskhand.Core.Macros.MacroRecorder r) =>
        Json(new { recording = r.IsRecording, count = r.CurrentCount, hasLast = r.LastMacro is not null, lastCount = r.LastMacro?.Steps.Count ?? 0 });

    [McpServerTool(Name = "deskhand_macro_expect"), Description("While recording, insert an expectation: playback will WAIT for an element matching these conditions to appear before continuing (do X, expect Y, do Z).")]
    public static string MacroExpect(Deskhand.Core.Macros.MacroRecorder r,
        string? name = null, string? automationId = null, string? controlType = null, string? className = null, int timeoutMs = 5000)
    {
        if (!r.IsRecording) return "not recording";
        r.RecordWait(new Deskhand.Core.Macros.ElementSelectorDto(name, automationId, controlType, className), timeoutMs);
        return $"expectation added ({r.CurrentCount} steps)";
    }

    [McpServerTool(Name = "deskhand_macro_play"), Description("Replay the last recorded macro. UIA steps wait for their target element; explicit expectations block until met. speed>1 plays faster.")]
    public static string MacroPlay(IAutomationBackend b, Deskhand.Core.Macros.MacroRecorder r, double speed = 1.0)
    {
        var macro = r.LastMacro ?? throw new ArgumentException("No macro recorded yet.");
        int played = Deskhand.Core.Macros.MacroPlayer.Play(macro, b, speed);
        return $"played {played} steps";
    }

    [McpServerTool(Name = "deskhand_foreground_window"), Description("The current foreground window element.")]
    public static string ForegroundWindow(IAutomationBackend b) => Json(b.GetForegroundWindow());

    [McpServerTool(Name = "deskhand_get_events"), Description("Poll buffered UIA events (focus changes, windows opening) newer than sinceId. Returns lastId to pass next time.")]
    public static string GetEvents(Deskhand.Core.Events.EventHub hub, long sinceId = 0) =>
        Json(new { lastId = hub.LastId, events = hub.Since(sinceId) });

    [McpServerTool(Name = "deskhand_focused_element"), Description("The element that currently has keyboard focus.")]
    public static string FocusedElement(IAutomationBackend b) => Json(b.GetFocusedElement());

    // ---------- uia read ----------

    [McpServerTool(Name = "deskhand_get_tree"), Description("Walk the UI Automation tree. Omit rootRef to start at the desktop. Returns nested elements with opaque refs.")]
    public static string GetTree(IAutomationBackend b,
        [Description("Element ref to start from; omit for the desktop root.")] string? rootRef = null,
        [Description("How many levels deep to expand (default 2).")] int depth = 2,
        [Description("Max children per node (default 40).")] int maxChildren = 40)
        => Json(b.GetTree(rootRef, depth, maxChildren));

    [McpServerTool(Name = "deskhand_find_elements"), Description("Find elements under a root by AND-combined conditions (name, automationId, controlType, className).")]
    public static string FindElements(IAutomationBackend b,
        [Description("Root element ref; omit for desktop.")] string? rootRef = null,
        string? name = null, string? automationId = null, string? controlType = null, string? className = null,
        [Description("children | descendants | subtree (default descendants).")] string scope = "descendants",
        int max = 100)
        => Json(b.Find(rootRef, new FindQuery(name, automationId, controlType, className, scope, max)));

    [McpServerTool(Name = "deskhand_wait_for_element"), Description("Poll until an element matching the conditions appears (or the timeout elapses). Returns the element, or a not-found message on timeout.")]
    public static string WaitForElement(IAutomationBackend b,
        string? rootRef = null, string? name = null, string? automationId = null, string? controlType = null, string? className = null,
        [Description("Scope: children | descendants | subtree (default descendants).")] string scope = "descendants",
        [Description("Timeout in milliseconds (default 5000).")] int timeoutMs = 5000)
    {
        var found = b.WaitForElement(rootRef, new FindQuery(name, automationId, controlType, className, scope, 1), timeoutMs);
        return found is null ? "{\"error\":\"wait_timeout\"}" : Json(found);
    }

    [McpServerTool(Name = "deskhand_get_element"), Description("Re-read a single element by ref (summary properties + supported patterns).")]
    public static string GetElement(IAutomationBackend b, string reference) => Json(b.GetElement(reference));

    [McpServerTool(Name = "deskhand_get_all_properties"), Description("Every UIA property the element supports, as a name→value map.")]
    public static string GetAllProperties(IAutomationBackend b, string reference) => Json(b.GetAllProperties(reference));

    [McpServerTool(Name = "deskhand_element_from_point"), Description("Return the UIA element at a screen coordinate (virtual-desktop pixels). The reliable 'find element' when a window's tree is thin or its refs go stale (Chromium/Electron apps): screenshot the app, pick a pixel on the target, and get the element + a fresh ref to act on.")]
    public static string ElementFromPoint(IAutomationBackend b,
        [Description("X in virtual-desktop pixels.")] int x,
        [Description("Y in virtual-desktop pixels.")] int y)
        => Json(b.GetElementFromPoint(x, y));

    // ---------- uia act ----------

    [McpServerTool(Name = "deskhand_invoke"), Description("Invoke an element (press a button, activate a menu item) via its UIA Invoke pattern.")]
    public static string Invoke(IAutomationBackend b, string reference) { b.Invoke(reference); return "ok"; }

    [McpServerTool(Name = "deskhand_set_value"), Description("Set an element's value (e.g. type into a text box) via the UIA Value pattern.")]
    public static string SetValue(IAutomationBackend b, string reference, string text) { b.SetValue(reference, text); return "ok"; }

    [McpServerTool(Name = "deskhand_toggle"), Description("Toggle a checkbox or switch via the UIA Toggle pattern.")]
    public static string Toggle(IAutomationBackend b, string reference) { b.Toggle(reference); return "ok"; }

    [McpServerTool(Name = "deskhand_expand_collapse"), Description("Expand or collapse a tree item / combo box via the UIA ExpandCollapse pattern.")]
    public static string ExpandCollapse(IAutomationBackend b, string reference, bool expand) { b.ExpandCollapse(reference, expand); return "ok"; }

    [McpServerTool(Name = "deskhand_select"), Description("Select a list item / tab via the UIA SelectionItem pattern.")]
    public static string Select(IAutomationBackend b, string reference) { b.Select(reference); return "ok"; }

    [McpServerTool(Name = "deskhand_set_focus"), Description("Raise the element's window to the foreground (defeating the foreground lock) and give it keyboard focus.")]
    public static string SetFocus(IAutomationBackend b, string reference) { b.SetFocus(reference); return "ok"; }

    // ---------- capture (returns MCP image content) ----------

    [McpServerTool(Name = "deskhand_capture_screen"), Description("Screenshot a monitor (by index) or the whole virtual desktop (omit monitor). Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureScreen(IAutomationBackend b, int? monitor = null, string? format = null)
        => AsImage(b.CaptureScreen(monitor, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_capture_region"), Description("Screenshot an arbitrary rectangle in virtual-desktop pixels. Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureRegion(IAutomationBackend b, int x, int y, int width, int height, string? format = null)
        => AsImage(b.CaptureRegion(x, y, width, height, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_capture_window"), Description("Screenshot one window by element ref (its host window). Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureWindow(IAutomationBackend b, string reference, string? format = null)
        => AsImage(b.CaptureWindowByRef(reference, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_capture_element"), Description("Screenshot a single element's bounding rectangle. Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureElement(IAutomationBackend b, string reference, string? format = null)
        => AsImage(b.CaptureElement(reference, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_capture_input_desktop"), Description("Phase 2: screenshot whichever desktop currently owns input (the secure desktop when running as SYSTEM). Returns an image plus a status line.")]
    public static IEnumerable<ContentBlock> CaptureInputDesktop(IAutomationBackend b, string? format = null)
    {
        var r = b.CaptureInputDesktop(Fmt(format), 80);
        yield return new TextContentBlock { Text = $"success={r.Success} desktop={r.DesktopName} kind={r.Kind} note={r.Note}" };
        if (r.Success && r.Capture is not null)
            yield return new ImageContentBlock
            {
                Data = r.Capture.Bytes,
                MimeType = r.Capture.Format == "jpeg" ? "image/jpeg" : "image/png",
            };
    }

    // ---------- input ----------

    [McpServerTool(Name = "deskhand_mouse_move"), Description("Move the mouse to a virtual-desktop pixel coordinate.")]
    public static string MouseMove(IAutomationBackend b, int x, int y) { b.MouseMove(x, y); return "ok"; }

    [McpServerTool(Name = "deskhand_mouse_click"), Description("Click at a point (or at the current cursor if x/y omitted). button: left|right|middle. count: 2 for double-click.")]
    public static string MouseClick(IAutomationBackend b, string button = "left", int? x = null, int? y = null, int count = 1)
    { b.MouseClick(button, x, y, count); return "ok"; }

    [McpServerTool(Name = "deskhand_mouse_scroll"), Description("Scroll the wheel. dy positive scrolls up, dx positive scrolls right (in notches).")]
    public static string MouseScroll(IAutomationBackend b, int dx, int dy) { b.MouseScroll(dx, dy); return "ok"; }

    [McpServerTool(Name = "deskhand_type_text"), Description("Type a literal Unicode string via synthetic keyboard input.")]
    public static string TypeText(IAutomationBackend b, string text) { b.TypeText(text); return "ok"; }

    [McpServerTool(Name = "deskhand_send_keys"), Description("Send a key chord, e.g. \"ctrl+shift+s\", \"alt+F4\", \"enter\", \"tab\".")]
    public static string SendKeys(IAutomationBackend b, string chord) { b.SendKeys(chord); return "ok"; }
}
