using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deskhand.Core;
using Deskhand.Core.Fleet;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Deskhand.Fleet.Server;

/// <summary>
/// Fleet-aware MCP tools: list connected PCs and drive any of them by <c>agentId</c>. Every call is
/// written to the <see cref="FleetAudit"/> (the same durable log as the web dashboard's API), so
/// MCP-driven fleet activity is recorded too.
/// </summary>
[McpServerToolType]
public static class FleetTools
{
    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };
    private static string Json(object? o) => JsonSerializer.Serialize(o, J);
    private static string Raw(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "null" : e.GetRawText();

    // Resolve the remote backend for an agent AND record the action in the audit.
    private static IAutomationBackend A(AgentRegistry r, FleetAudit audit, string agentId, string action)
    {
        audit.Record("action", "mcp", agentId, action);
        return new RemoteAgentBackend(r.Get(agentId) ?? throw new ArgumentException($"No agent '{agentId}' is connected. Use deskhand_list_agents."));
    }

    // Resolve the remote OBSERVER (events/hooks/recording/input) for an agent AND audit the action.
    private static RemoteAgentObserver O(AgentRegistry r, FleetAudit audit, string agentId, string action)
    {
        audit.Record("action", "mcp", agentId, action);
        return new RemoteAgentObserver(r.Get(agentId) ?? throw new ArgumentException($"No agent '{agentId}' is connected. Use deskhand_list_agents."));
    }

    private static ImageFormat Fmt(string? f) => f?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;

    private static IEnumerable<ContentBlock> AsImage(string agentId, CaptureResultDto c)
    {
        yield return new TextContentBlock { Text = $"agent={agentId} {c.Rect.Width}x{c.Rect.Height} desktop={c.Desktop}" };
        yield return new ImageContentBlock { Data = c.Bytes, MimeType = c.Format == "jpeg" ? "image/jpeg" : "image/png" };
    }

    [McpServerTool(Name = "deskhand_list_agents"), Description("List the PCs (agents) currently connected to the fleet, with machine name, desktop state, monitors, and elevation.")]
    public static string ListAgents(AgentRegistry r) => Json(r.All.Select(a => new
    {
        agentId = a.AgentId, machine = a.MachineName,
        monitors = a.Info?.Monitors.Count ?? 0, desktop = a.Info?.DesktopState.Desktop, elevated = a.Info?.IsElevated ?? false,
    }));

    [McpServerTool(Name = "deskhand_fleet_audit"), Description("Recent fleet audit entries newer than sinceId: agent connect/disconnect and every action (from dashboard, HTTP, or MCP), with the caller's address. The full record is a durable JSONL file on the server.")]
    public static string Audit(FleetAudit audit, long sinceId = 0) => Json(new { lastId = audit.LastId, dir = audit.Directory, entries = audit.Since(sinceId) });

    [McpServerTool(Name = "deskhand_agent_info"), Description("Machine info (monitors, virtual screen, desktop state) for one fleet PC.")]
    public static string AgentInfo(AgentRegistry r, FleetAudit audit, string agentId) => Json(A(r, audit, agentId, "info").GetMachineInfo());

    [McpServerTool(Name = "deskhand_agent_list_windows"), Description("Top-level windows on a fleet PC (the reliable way to target an app).")]
    public static string AgentWindows(AgentRegistry r, FleetAudit audit, string agentId) => Json(A(r, audit, agentId, "list_windows").GetTopLevelWindows());

    [McpServerTool(Name = "deskhand_agent_list_processes"), Description("Every running process on a fleet PC with the top-level windows it owns (windowed apps first). Each window ref expands into the UIA tree via deskhand_agent_get_tree.")]
    public static string AgentProcesses(AgentRegistry r, FleetAudit audit, string agentId) => Json(A(r, audit, agentId, "list_processes").GetProcesses());

    [McpServerTool(Name = "deskhand_agent_foreground"), Description("The foreground window on a fleet PC.")]
    public static string AgentForeground(AgentRegistry r, FleetAudit audit, string agentId) => Json(A(r, audit, agentId, "foreground").GetForegroundWindow());

    [McpServerTool(Name = "deskhand_agent_get_tree"), Description("Walk the UIA tree on a fleet PC. Omit rootRef for the desktop.")]
    public static string AgentTree(AgentRegistry r, FleetAudit audit, string agentId, string? rootRef = null, int depth = 2, int maxChildren = 40)
        => Json(A(r, audit, agentId, "get_tree").GetTree(rootRef, depth, maxChildren));

    [McpServerTool(Name = "deskhand_agent_find"), Description("Find elements on a fleet PC by AND-combined conditions.")]
    public static string AgentFind(AgentRegistry r, FleetAudit audit, string agentId, string? rootRef = null, string? name = null, string? automationId = null, string? controlType = null, string? className = null, string scope = "descendants", int max = 100)
        => Json(A(r, audit, agentId, "find").Find(rootRef, new FindQuery(name, automationId, controlType, className, scope, max)));

    [McpServerTool(Name = "deskhand_agent_wait_for_element"), Description("Poll until an element appears on a fleet PC (or timeout).")]
    public static string AgentWait(AgentRegistry r, FleetAudit audit, string agentId, string? rootRef = null, string? name = null, string? automationId = null, string? controlType = null, string? className = null, int timeoutMs = 5000)
    {
        var f = A(r, audit, agentId, "wait_for_element").WaitForElement(rootRef, new FindQuery(name, automationId, controlType, className, "descendants", 1), timeoutMs);
        return f is null ? "{\"error\":\"wait_timeout\"}" : Json(f);
    }

    [McpServerTool(Name = "deskhand_agent_get_properties"), Description("Every UIA property of an element on a fleet PC.")]
    public static string AgentProps(AgentRegistry r, FleetAudit audit, string agentId, string reference) => Json(A(r, audit, agentId, "get_properties").GetAllProperties(reference));

    [McpServerTool(Name = "deskhand_agent_element_from_point"), Description("Return the UIA element at a screen coordinate on a fleet PC (virtual-desktop pixels). The reliable 'find element' when a window's tree is thin or its refs go stale (Chromium/Electron): capture the PC, pick a pixel, get the element + fresh ref.")]
    public static string AgentElementFromPoint(AgentRegistry r, FleetAudit audit, string agentId, int x, int y)
        => Json(A(r, audit, agentId, $"element_from_point {x},{y}").GetElementFromPoint(x, y));

    [McpServerTool(Name = "deskhand_agent_capture_screen"), Description("Screenshot a fleet PC's monitor (or whole desktop). Returns an image.")]
    public static IEnumerable<ContentBlock> AgentCapture(AgentRegistry r, FleetAudit audit, string agentId, int? monitor = null, string? format = null)
        => AsImage(agentId, A(r, audit, agentId, "capture_screen").CaptureScreen(monitor, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_agent_capture_element"), Description("Screenshot one element on a fleet PC. Returns an image.")]
    public static IEnumerable<ContentBlock> AgentCaptureElement(AgentRegistry r, FleetAudit audit, string agentId, string reference, string? format = null)
        => AsImage(agentId, A(r, audit, agentId, "capture_element").CaptureElement(reference, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_agent_invoke"), Description("Invoke an element on a fleet PC (press a button, etc.).")]
    public static string AgentInvoke(AgentRegistry r, FleetAudit audit, string agentId, string reference) { A(r, audit, agentId, "invoke").Invoke(reference); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_set_value"), Description("Set an element's value on a fleet PC.")]
    public static string AgentSetValue(AgentRegistry r, FleetAudit audit, string agentId, string reference, string text) { A(r, audit, agentId, "set_value").SetValue(reference, text); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_set_focus"), Description("Raise a window and focus an element on a fleet PC.")]
    public static string AgentSetFocus(AgentRegistry r, FleetAudit audit, string agentId, string reference) { A(r, audit, agentId, "set_focus").SetFocus(reference); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_click"), Description("Click at a point on a fleet PC. button: left|right|middle; count 2 = double.")]
    public static string AgentClick(AgentRegistry r, FleetAudit audit, string agentId, int x, int y, string button = "left", int count = 1) { A(r, audit, agentId, $"click {x},{y}").MouseClick(button, x, y, count); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_move"), Description("Move the mouse on a fleet PC.")]
    public static string AgentMove(AgentRegistry r, FleetAudit audit, string agentId, int x, int y) { A(r, audit, agentId, $"move {x},{y}").MouseMove(x, y); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_type"), Description("Type text on a fleet PC.")]
    public static string AgentType(AgentRegistry r, FleetAudit audit, string agentId, string text) { A(r, audit, agentId, "type").TypeText(text); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_keys"), Description("Send a key chord on a fleet PC (e.g. \"ctrl+s\", \"enter\").")]
    public static string AgentKeys(AgentRegistry r, FleetAudit audit, string agentId, string chord) { A(r, audit, agentId, $"keys {chord}").SendKeys(chord); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_launch"), Description("Launch a program on a fleet PC; returns its window if it appears.")]
    public static string AgentLaunch(AgentRegistry r, FleetAudit audit, string agentId, string path, string? args = null, string? workingDir = null, int waitForWindowMs = 4000)
        => Json(A(r, audit, agentId, $"launch {path}").LaunchProcess(path, args, workingDir, waitForWindowMs));

    // ---------- fleet observation: events, hooks, recording, user-input ----------

    [McpServerTool(Name = "deskhand_agent_get_events"), Description("Poll a fleet PC's event feed newer than sinceId: focus_changed, window_opened, process_started, process_exited. Returns lastId to pass next time.")]
    public static string AgentGetEvents(AgentRegistry r, FleetAudit audit, string agentId, long sinceId = 0)
        => Raw(O(r, audit, agentId, "get_events").GetEvents(sinceId));

    [McpServerTool(Name = "deskhand_agent_wait_for_process"), Description("Block until a process starts or exits on a fleet PC. event=\"start\"|\"exit\", matched by name substring and/or pid.")]
    public static string AgentWaitForProcess(AgentRegistry r, FleetAudit audit, string agentId, string @event = "start", string? name = null, int? pid = null, int timeoutMs = 30000)
        => Raw(O(r, audit, agentId, $"wait_for_process {@event}").WaitForProcess(@event, name, pid, timeoutMs));

    [McpServerTool(Name = "deskhand_agent_record_start"), Description("Start recording a fleet PC's screen to GIF or MJPEG-AVI. monitor: index or omit for all monitors. Hard maxDurationMs auto-stops. Returns a recording id.")]
    public static string AgentRecordStart(AgentRegistry r, FleetAudit audit, string agentId, int? monitor = null, string format = "gif", int fps = 10, int scale = 100, int quality = 75, int maxDurationMs = 30000)
        => Raw(O(r, audit, agentId, "record_start").RecordStart(monitor, format, fps, scale, quality, maxDurationMs));

    [McpServerTool(Name = "deskhand_agent_record_stop"), Description("Stop and finalize a fleet PC's screen recording. The file is saved on that PC; download it at /agents/{id}/recordings/{recId}.")]
    public static string AgentRecordStop(AgentRegistry r, FleetAudit audit, string agentId, string recId)
        => Raw(O(r, audit, agentId, "record_stop").RecordStop(recId));

    [McpServerTool(Name = "deskhand_agent_record_status"), Description("Status of one recording (recId) or all recordings on a fleet PC (omit recId).")]
    public static string AgentRecordStatus(AgentRegistry r, FleetAudit audit, string agentId, string? recId = null)
        => Raw(O(r, audit, agentId, "record_status").RecordStatus(recId));

    [McpServerTool(Name = "deskhand_agent_user_input_start"), Description("Start recording the USER's mouse/keyboard on a fleet PC; each click is annotated with the element it hit. The watched PC shows a persistent on-screen banner + toast. PRIVACY: captures real keystrokes; captureText=false for mouse-only.")]
    public static string AgentInputStart(AgentRegistry r, FleetAudit audit, string agentId, bool captureText = true)
        => Raw(O(r, audit, agentId, "input_record_start").InputStart(captureText));

    [McpServerTool(Name = "deskhand_agent_user_input_stop"), Description("Stop user-input recording on a fleet PC and return the full event sequence (clicks+elements, scrolls, text, keys).")]
    public static string AgentInputStop(AgentRegistry r, FleetAudit audit, string agentId)
        => Raw(O(r, audit, agentId, "input_record_stop").InputStop());

    [McpServerTool(Name = "deskhand_agent_user_input_get"), Description("Get user-input events newer than sinceId from a fleet PC while recording is in progress.")]
    public static string AgentInputGet(AgentRegistry r, FleetAudit audit, string agentId, long sinceId = 0)
        => Raw(O(r, audit, agentId, "input_record_get").InputGet(sinceId));

    [McpServerTool(Name = "deskhand_agent_registry_browse"), Description("Browse a fleet PC's registry (read-only): subkeys + values of a key. path empty = hive roots, or e.g. \"HKLM\\SOFTWARE\\Microsoft\". Not available on RDP agents.")]
    public static string AgentRegistryBrowse(AgentRegistry r, FleetAudit audit, string agentId, string? path = null)
        => Raw(O(r, audit, agentId, "registry_browse").RegistryBrowse(path));

    [McpServerTool(Name = "deskhand_agent_dump_process"), Description("Write a full-memory .dmp of a process (by pid) on a fleet PC. The dump is saved ON THAT PC (large + sensitive); returns its path/size. Not available on RDP agents.")]
    public static string AgentDumpProcess(AgentRegistry r, FleetAudit audit, string agentId, int pid)
        => Raw(O(r, audit, agentId, $"dump_process {pid}").DumpProcess(pid));

    [McpServerTool(Name = "deskhand_agent_list_apps"), Description("List Start Menu apps on a fleet PC (launch one via deskhand_agent_launch with its path). Not available on RDP agents.")]
    public static string AgentListApps(AgentRegistry r, FleetAudit audit, string agentId)
        => Raw(O(r, audit, agentId, "list_apps").ListApps());

    [McpServerTool(Name = "deskhand_agent_list_desktops"), Description("Virtual desktops on a fleet PC: windows grouped by desktop (current flagged). Not available on RDP agents.")]
    public static string AgentListDesktops(AgentRegistry r, FleetAudit audit, string agentId)
        => Raw(O(r, audit, agentId, "list_desktops").ListDesktops());

    [McpServerTool(Name = "deskhand_agent_move_window_to_desktop"), Description("Move a window (hwnd) to a virtual desktop on a fleet PC. Omit desktopId for the current desktop, or pass a GUID from deskhand_agent_list_desktops.")]
    public static string AgentMoveWindow(AgentRegistry r, FleetAudit audit, string agentId, long hwnd, string? desktopId = null)
        => Raw(O(r, audit, agentId, "move_window_to_desktop").MoveWindowToDesktop(hwnd, desktopId));

    // ---- files + shell on a fleet PC (native agents only; RDP agents return a clean error) ----

    [McpServerTool(Name = "deskhand_agent_browse_files"), Description("Browse a fleet PC's file system (read-only): folders + files in a directory. path empty = drive roots, or a folder like \"C:\\\\Users\". Not available on RDP agents.")]
    public static string AgentBrowseFiles(AgentRegistry r, FleetAudit audit, string agentId, string? path = null)
        => Raw(O(r, audit, agentId, "browse_files").BrowseFiles(path));

    [McpServerTool(Name = "deskhand_agent_read_file"), Description("Download a file from a fleet PC as base64: { path, size, base64, error? }. Refused over ~25 MB. Not available on RDP agents.")]
    public static string AgentReadFile(AgentRegistry r, FleetAudit audit, string agentId, string path)
        => Raw(O(r, audit, agentId, "read_file").ReadFile(path));

    [McpServerTool(Name = "deskhand_agent_write_file"), Description("Upload/write a file to a fleet PC from base64. overwrite=false (default) fails if it exists. Not available on RDP agents.")]
    public static string AgentWriteFile(AgentRegistry r, FleetAudit audit, string agentId, string path, string contentBase64, bool overwrite = false)
        => Raw(O(r, audit, agentId, "write_file").WriteFile(path, contentBase64, overwrite));

    [McpServerTool(Name = "deskhand_agent_delete_path"), Description("Delete a file/folder on a fleet PC (→ Recycle Bin unless permanent=true). Not available on RDP agents.")]
    public static string AgentDeletePath(AgentRegistry r, FleetAudit audit, string agentId, string path, bool permanent = false)
        => Raw(O(r, audit, agentId, "delete_path").DeletePath(path, permanent));

    [McpServerTool(Name = "deskhand_agent_rename_path"), Description("Rename a file/folder in place on a fleet PC (newName is a bare name). Not available on RDP agents.")]
    public static string AgentRenamePath(AgentRegistry r, FleetAudit audit, string agentId, string path, string newName)
        => Raw(O(r, audit, agentId, "rename_path").RenamePath(path, newName));

    [McpServerTool(Name = "deskhand_agent_move_path"), Description("Move a file/folder on a fleet PC. dest may be an existing folder or a full path. Not available on RDP agents.")]
    public static string AgentMovePath(AgentRegistry r, FleetAudit audit, string agentId, string source, string dest, bool overwrite = false)
        => Raw(O(r, audit, agentId, "move_path").MovePath(source, dest, overwrite));

    [McpServerTool(Name = "deskhand_agent_copy_path"), Description("Copy a file, or a folder recursively, on a fleet PC. dest may be an existing folder or a full path. Not available on RDP agents.")]
    public static string AgentCopyPath(AgentRegistry r, FleetAudit audit, string agentId, string source, string dest, bool overwrite = false)
        => Raw(O(r, audit, agentId, "copy_path").CopyPath(source, dest, overwrite));

    [McpServerTool(Name = "deskhand_agent_zip"), Description("Create a .zip on a fleet PC from files/folders (recursive). Not available on RDP agents.")]
    public static string AgentZip(AgentRegistry r, FleetAudit audit, string agentId, string[] sources, string dest, bool overwrite = false)
        => Raw(O(r, audit, agentId, "zip").Zip(sources, dest, overwrite));

    [McpServerTool(Name = "deskhand_agent_unzip"), Description("Extract a .zip on a fleet PC into a folder (defaults to a folder named after the zip). Not available on RDP agents.")]
    public static string AgentUnzip(AgentRegistry r, FleetAudit audit, string agentId, string zipPath, string? dest = null, bool overwrite = false)
        => Raw(O(r, audit, agentId, "unzip").Unzip(zipPath, dest, overwrite));

    [McpServerTool(Name = "deskhand_agent_run_command"), Description("Run a one-shot shell command on a fleet PC (default PowerShell; shell=\"cmd\"/\"pwsh\") and return output. STATELESS. The agent must have been started with DESKHAND_ENABLE_SHELL or it returns a shell_disabled error. Not available on RDP agents.")]
    public static string AgentRunCommand(AgentRegistry r, FleetAudit audit, string agentId, string command, string? shell = null, string? cwd = null, int? timeoutMs = null)
        => Raw(O(r, audit, agentId, "run_command").RunCommand(shell, command, cwd, timeoutMs));
}
