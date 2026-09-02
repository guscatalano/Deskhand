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

    [McpServerTool(Name = "deskhand_agent_drag"), Description("Drag-and-drop on a fleet PC: press at (fromX,fromY), move to (toX,toY), release. button left|right|middle; steps=smoothness (default 20); holdMs=dwell (default 60). Not available on RDP agents.")]
    public static string AgentDrag(AgentRegistry r, FleetAudit audit, string agentId, int fromX, int fromY, int toX, int toY, string button = "left", int steps = 20, int holdMs = 60)
    { A(r, audit, agentId, $"drag {fromX},{fromY}->{toX},{toY}").Drag(fromX, fromY, toX, toY, button, steps, holdMs); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_type"), Description("Type text on a fleet PC.")]
    public static string AgentType(AgentRegistry r, FleetAudit audit, string agentId, string text) { A(r, audit, agentId, "type").TypeText(text); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_keys"), Description("Send a key chord on a fleet PC (e.g. \"ctrl+s\", \"enter\").")]
    public static string AgentKeys(AgentRegistry r, FleetAudit audit, string agentId, string chord) { A(r, audit, agentId, $"keys {chord}").SendKeys(chord); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_launch"), Description("Launch a program on a fleet PC; returns its window if it appears.")]
    public static string AgentLaunch(AgentRegistry r, FleetAudit audit, string agentId, string path, string? args = null, string? workingDir = null, int waitForWindowMs = 10000)
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

    [McpServerTool(Name = "deskhand_agent_dump_process"), Description("Write a full-memory .dmp of a process (by pid) on a fleet PC. The dump is saved ON THAT PC (large + sensitive); returns its path/size. Download it with the fleet HTTP route /agents/{id}/dumps/{name}. Not available on RDP agents.")]
    public static string AgentDumpProcess(AgentRegistry r, FleetAudit audit, string agentId, int pid)
        => Raw(O(r, audit, agentId, $"dump_process {pid}").DumpProcess(pid));

    [McpServerTool(Name = "deskhand_agent_dumps"), Description("List the process dumps saved on a fleet PC: { name, sizeBytes, ts }. Download one via the fleet HTTP route GET /agents/{id}/dumps/{name} (streamed off the agent; refused over ~1.5 GB — pull those directly from the agent).")]
    public static string AgentDumps(AgentRegistry r, FleetAudit audit, string agentId)
        => Raw(O(r, audit, agentId, "dump_list").DumpList());

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

    [McpServerTool(Name = "deskhand_agent_launch_process_as"), Description("Launch a program on a fleet PC into a SPECIFIC session, on a SPECIFIC window-station\\desktop, as a SPECIFIC user (CreateProcessAsUser). as=\"session\" (default: run as whoever is logged into that session — no password needed), \"credentials\" (user/domain/password), or \"system\". sessionId defaults to the active console session; desktop defaults to \"winsta0\\default\". Returns { ok, processId, sessionId, desktop, as, user, error?, win32?, hint? }. The agent must have been started with DESKHAND_ENABLE_SESSION_LAUNCH. Crossing a session/user boundary needs the agent running as LocalSystem (the Deskhand Fleet Launcher service does) — otherwise a clear ERROR_PRIVILEGE_NOT_HELD + hint. Not available on RDP agents.")]
    public static string AgentLaunchProcessAs(AgentRegistry r, FleetAudit audit, string agentId, string path,
        string? args = null, string? workingDir = null, int? sessionId = null, string? desktop = null,
        string? @as = null, string? user = null, string? domain = null, string? password = null, bool noWindow = false)
        => Raw(O(r, audit, agentId, "launch_as").LaunchProcessAs(path, args, workingDir, sessionId, desktop, @as, user, domain, password, noWindow));

    [McpServerTool(Name = "deskhand_agent_system_info"), Description("About a fleet PC (read-only): Windows version + BuildLab, uptime, CPU, memory, disks, network, firewall. Not available on RDP agents.")]
    public static string AgentSystemInfo(AgentRegistry r, FleetAudit audit, string agentId)
        => Raw(O(r, audit, agentId, "system_info").SystemInfo());

    [McpServerTool(Name = "deskhand_agent_firewall_rules"), Description("List a fleet PC's Windows Firewall rules (read-only). Filters: direction in/out, port, enabledOnly, contains, managedOnly (rules Deskhand opened), max. Not available on RDP agents.")]
    public static string AgentFirewallRules(AgentRegistry r, FleetAudit audit, string agentId,
        string? direction = null, int? port = null, bool? enabledOnly = null, string? contains = null, bool managedOnly = false, int max = 200)
        => Raw(O(r, audit, agentId, "firewall_rules").FirewallRules(direction, port, enabledOnly, contains, managedOnly, max));

    [McpServerTool(Name = "deskhand_agent_firewall_open_port"), Description("Open a port on a fleet PC: add an inbound/outbound ALLOW rule for a TCP/UDP port, tagged Deskhand-managed for clean removal. The agent must run as Administrator and be started with DESKHAND_ENABLE_FIREWALL_ADMIN. Not available on RDP agents.")]
    public static string AgentFirewallOpenPort(AgentRegistry r, FleetAudit audit, string agentId,
        int port, string? protocol = "tcp", string? direction = "in", string? remoteAddresses = null, string? name = null)
        => Raw(O(r, audit, agentId, "firewall_open").FirewallOpen(port, protocol, direction, remoteAddresses, name));

    [McpServerTool(Name = "deskhand_agent_firewall_close_port"), Description("Close a port DESKHAND opened on a fleet PC (all=true removes every Deskhand-managed rule). Only ever removes rules Deskhand created — never pre-existing ones. Requires Administrator + DESKHAND_ENABLE_FIREWALL_ADMIN. Not available on RDP agents.")]
    public static string AgentFirewallClosePort(AgentRegistry r, FleetAudit audit, string agentId,
        int port = 0, string? protocol = "tcp", string? direction = "in", bool all = false)
        => Raw(O(r, audit, agentId, "firewall_close").FirewallClose(port, protocol, direction, all));

    [McpServerTool(Name = "deskhand_agent_clipboard_get"), Description("Read a fleet PC's clipboard text. Not available on RDP agents.")]
    public static string AgentClipboardGet(AgentRegistry r, FleetAudit audit, string agentId)
        => Raw(O(r, audit, agentId, "clipboard_get").ClipboardGet());

    [McpServerTool(Name = "deskhand_agent_clipboard_set"), Description("Set a fleet PC's clipboard text. Not available on RDP agents.")]
    public static string AgentClipboardSet(AgentRegistry r, FleetAudit audit, string agentId, string text)
        => Raw(O(r, audit, agentId, "clipboard_set").ClipboardSet(text));

    [McpServerTool(Name = "deskhand_agent_window"), Description("Manage a window on a fleet PC by nativeWindowHandle: action = activate|minimize|maximize|restore|close|move|resize|bounds (move needs x,y; resize needs width,height; bounds all four). Not available on RDP agents.")]
    public static string AgentWindow(AgentRegistry r, FleetAudit audit, string agentId, long hwnd, string action,
        int? x = null, int? y = null, int? width = null, int? height = null)
        => Raw(O(r, audit, agentId, "window").WindowAction(hwnd, action, x, y, width, height));

    [McpServerTool(Name = "deskhand_agent_ocr_screen"), Description("OCR a fleet PC's screen: read on-screen text (built-in Windows OCR) for apps UIA can't see. Returns text + word boxes in screen coordinates. Works on native and RDP agents.")]
    public static string AgentOcrScreen(AgentRegistry r, FleetAudit audit, string agentId, int? monitor = null)
        => Raw(O(r, audit, agentId, "ocr_screen").OcrScreen(monitor));

    [McpServerTool(Name = "deskhand_agent_ocr_region"), Description("OCR a screen rectangle on a fleet PC. Returns text + word boxes in screen coordinates.")]
    public static string AgentOcrRegion(AgentRegistry r, FleetAudit audit, string agentId, int x, int y, int width, int height)
        => Raw(O(r, audit, agentId, "ocr_region").OcrRegion(x, y, width, height));

    [McpServerTool(Name = "deskhand_agent_ocr_window"), Description("OCR a window on a fleet PC by nativeWindowHandle or element reference. Returns text + word boxes in screen coordinates.")]
    public static string AgentOcrWindow(AgentRegistry r, FleetAudit audit, string agentId, long? hwnd = null, string? reference = null)
        => Raw(O(r, audit, agentId, "ocr_window").OcrWindow(hwnd, reference));

    [McpServerTool(Name = "deskhand_agent_find_image"), Description("Find a template image (base64 PNG) on a fleet PC's screen by normalized cross-correlation. target: screen (default) / region (x,y,width,height) / window (hwnd or reference). Returns matches with SCREEN-coordinate centers, sorted best-first. threshold 0.1-1.0 (default 0.85).")]
    public static string AgentFindImage(AgentRegistry r, FleetAudit audit, string agentId, string templateBase64,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, double threshold = 0.85, int maxResults = 10)
        => Raw(O(r, audit, agentId, "find_image").FindImage(templateBase64, target, monitor, x, y, width, height, hwnd, reference, threshold, maxResults));

    [McpServerTool(Name = "deskhand_agent_wait_for_image"), Description("On a fleet PC, poll until a template image (base64 PNG) appears (absent=true → disappears) or timeoutMs elapses. target screen|region|window. Returns { found, waitedMs, result }.")]
    public static string AgentWaitForImage(AgentRegistry r, FleetAudit audit, string agentId, string templateBase64,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, double threshold = 0.85, int timeoutMs = 5000, bool absent = false, int pollMs = 250)
        => Raw(O(r, audit, agentId, "wait_for_image").WaitForImage(templateBase64, target, monitor, x, y, width, height, hwnd, reference, threshold, timeoutMs, absent, pollMs));

    [McpServerTool(Name = "deskhand_agent_wait_for_text"), Description("On a fleet PC, poll with OCR until text appears (absent=true → disappears) or timeoutMs. Returns { found, waitedMs, matchText, centerX, centerY }.")]
    public static string AgentWaitForText(AgentRegistry r, FleetAudit audit, string agentId, string text,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, int timeoutMs = 5000, bool absent = false, int pollMs = 250)
        => Raw(O(r, audit, agentId, "wait_for_text").WaitForText(text, target, monitor, x, y, width, height, hwnd, reference, timeoutMs, absent, pollMs));

    [McpServerTool(Name = "deskhand_agent_wait_stable"), Description("On a fleet PC, block until a screen area settles (waitForChange=true → until it starts changing) or timeoutMs. Returns { ok, waitedMs, lastDiff, mode }.")]
    public static string AgentWaitStable(AgentRegistry r, FleetAudit audit, string agentId,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, int settleMs = 700, int timeoutMs = 8000, int pollMs = 250, double epsilon = 0.01, bool waitForChange = false)
        => Raw(O(r, audit, agentId, "wait_stable").WaitStable(target, monitor, x, y, width, height, hwnd, reference, settleMs, timeoutMs, pollMs, epsilon, waitForChange));

    [McpServerTool(Name = "deskhand_agent_click_image"), Description("On a fleet PC, find a template image and click its best match (optionally wait timeoutMs). button left|right|middle; count 2=double. Returns { clicked, x, y, score }.")]
    public static string AgentClickImage(AgentRegistry r, FleetAudit audit, string agentId, string templateBase64,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, double threshold = 0.85, string button = "left", int count = 1, int timeoutMs = 0)
        => Raw(O(r, audit, agentId, "click_image").ClickImage(templateBase64, target, monitor, x, y, width, height, hwnd, reference, threshold, button, count, timeoutMs));

    [McpServerTool(Name = "deskhand_agent_click_text"), Description("On a fleet PC, find on-screen text with OCR and click it (optionally wait timeoutMs). Returns { clicked, x, y }.")]
    public static string AgentClickText(AgentRegistry r, FleetAudit audit, string agentId, string text,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, string button = "left", int count = 1, int timeoutMs = 0)
        => Raw(O(r, audit, agentId, "click_text").ClickText(text, target, monitor, x, y, width, height, hwnd, reference, button, count, timeoutMs));

    [McpServerTool(Name = "deskhand_agent_get_pixel"), Description("Read the RGB color of a pixel on a fleet PC's screen. Returns { ok, x, y, r, g, b, hex }.")]
    public static string AgentGetPixel(AgentRegistry r, FleetAudit audit, string agentId, int x, int y)
        => Raw(O(r, audit, agentId, "get_pixel").GetPixel(x, y));

    [McpServerTool(Name = "deskhand_agent_explore_ux"), Description("Compact UX map of a fleet PC's foreground window (or element ref): fused UIA interactables + OCR text targets, each with a click-ready screen center. The way to navigate a remote UI — including custom-drawn/canvas apps with no UIA tree.")]
    public static string AgentExploreUx(AgentRegistry r, FleetAudit audit, string agentId, string? reference = null, bool uia = true, bool text = true, bool includeOffscreen = false, int max = 200)
        => Raw(O(r, audit, agentId, "explore_ux").ExploreUx(reference, uia, text, includeOffscreen, max));

    [McpServerTool(Name = "deskhand_agent_dismiss_modals"), Description("Find and close dialogs/modals on a fleet PC non-committally (Cancel/Close/No before OK; Yes only if acceptYes). Returns { count, dismissed[] }.")]
    public static string AgentDismissModals(AgentRegistry r, FleetAudit audit, string agentId, bool acceptOk = true, bool acceptYes = false, int maxPasses = 4)
        => Raw(O(r, audit, agentId, "dismiss_modals").DismissModals(acceptOk, acceptYes, maxPasses));

    [McpServerTool(Name = "deskhand_agent_crawl_ux"), Description("Safely crawl a fleet PC's window UX to a depth (expands structure, never invokes commands) and cache the deep map on that agent. useCache=true returns the agent's saved map. depth 1–8 (default 3).")]
    public static string AgentCrawlUx(AgentRegistry r, FleetAudit audit, string agentId, string? reference = null, int depth = 3, int maxNodes = 1500, bool selectTabs = false, bool useCache = false)
        => Raw(O(r, audit, agentId, "crawl_ux").CrawlUx(reference, depth, maxNodes, selectTabs, useCache));

    [McpServerTool(Name = "deskhand_agent_paste_text"), Description("Paste text on a fleet PC (clipboard + Ctrl+V). Not available on RDP agents.")]
    public static string AgentPasteText(AgentRegistry r, FleetAudit audit, string agentId, string text)
        => Raw(O(r, audit, agentId, "paste").Paste(text));

    [McpServerTool(Name = "deskhand_agent_process_control"), Description("Control a process on a fleet PC: action = kill|suspend|resume|priority (level idle..realtime). DESTRUCTIVE (kill, suspend) require confirm=true. The agent refuses to kill its OWN Deskhand process; OS-critical processes need force=true. Not available on RDP agents.")]
    public static string AgentProcessControl(AgentRegistry r, FleetAudit audit, string agentId, int pid, string action, bool tree = true, string? level = null, bool force = false, bool confirm = false)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();
        if (act is "kill" or "terminate" or "suspend" && !confirm)
            return $"{{\"ok\":false,\"confirmationRequired\":true,\"action\":\"{act}\",\"pid\":{pid},\"message\":\"destructive — resend with confirm=true\"}}";
        return Raw(O(r, audit, agentId, "process_control").ProcessControl(pid, action, tree, level, force));
    }

    [McpServerTool(Name = "deskhand_agent_service_control"), Description("Start/stop/restart a Windows service on a fleet PC. DESTRUCTIVE (stop, restart) require confirm=true. The agent refuses to stop the service hosting itself. Not available on RDP agents.")]
    public static string AgentServiceControl(AgentRegistry r, FleetAudit audit, string agentId, string name, string action, bool confirm = false)
    {
        var act = (action ?? "").Trim().ToLowerInvariant();
        if (act is "stop" or "restart" && !confirm)
            return $"{{\"ok\":false,\"confirmationRequired\":true,\"action\":\"{act}\",\"name\":\"{name}\",\"message\":\"destructive — resend with confirm=true\"}}";
        return Raw(O(r, audit, agentId, "service_control").ServiceControl(name, action));
    }

    [McpServerTool(Name = "deskhand_agent_env_get"), Description("Read an environment variable on a fleet PC (scope process|user|machine).")]
    public static string AgentEnvGet(AgentRegistry r, FleetAudit audit, string agentId, string name, string? scope = null)
        => Raw(O(r, audit, agentId, "env_get").EnvGet(name, scope));

    [McpServerTool(Name = "deskhand_agent_env_set"), Description("Set (or delete, value omitted) an environment variable on a fleet PC (scope process|user|machine; machine needs elevation).")]
    public static string AgentEnvSet(AgentRegistry r, FleetAudit audit, string agentId, string name, string? value = null, string? scope = null)
        => Raw(O(r, audit, agentId, "env_set").EnvSet(name, value, scope));

    [McpServerTool(Name = "deskhand_agent_task_action"), Description("Run/end/enable/disable a Scheduled Task on a fleet PC. Not available on RDP agents.")]
    public static string AgentTaskAction(AgentRegistry r, FleetAudit audit, string agentId, string task, string action)
        => Raw(O(r, audit, agentId, "task_action").TaskAction(task, action));

    [McpServerTool(Name = "deskhand_agent_uac_status"), Description("Read UAC configuration on a fleet PC.")]
    public static string AgentUacStatus(AgentRegistry r, FleetAudit audit, string agentId)
        => Raw(O(r, audit, agentId, "uac_status").UacStatus());

    [McpServerTool(Name = "deskhand_agent_uac_config"), Description("Configure UAC on a fleet PC (needs elevation): one of enabled, promptOnSecureDesktop, autoApprove (silent elevation), or adminBehavior 0..5.")]
    public static string AgentUacConfig(AgentRegistry r, FleetAudit audit, string agentId, bool? enabled = null, bool? promptOnSecureDesktop = null, bool? autoApprove = null, int? adminBehavior = null)
        => Raw(O(r, audit, agentId, "uac_config").UacConfig(enabled, promptOnSecureDesktop, autoApprove, adminBehavior));

    [McpServerTool(Name = "deskhand_agent_uac_respond"), Description("Best-effort answer a UAC prompt on a fleet PC (accept=Yes/false=No). Only reaches prompts on the normal desktop with an elevated agent.")]
    public static string AgentUacRespond(AgentRegistry r, FleetAudit audit, string agentId, bool accept = true, int timeoutMs = 5000)
        => Raw(O(r, audit, agentId, "uac_respond").UacRespond(accept, timeoutMs));

    [McpServerTool(Name = "deskhand_agent_fetch_url"), Description("Download a URL to a file on a fleet PC (path or folder; omit for temp). Size-capped. Not available on RDP agents.")]
    public static string AgentFetchUrl(AgentRegistry r, FleetAudit audit, string agentId, string url, string? path = null, long? maxBytes = null)
        => Raw(O(r, audit, agentId, "fetch").Fetch(url, path, maxBytes));
}
