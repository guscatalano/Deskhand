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

    // Resolve the remote backend for an agent AND record the action in the audit.
    private static IAutomationBackend A(AgentRegistry r, FleetAudit audit, string agentId, string action)
    {
        audit.Record("action", "mcp", agentId, action);
        return new RemoteAgentBackend(r.Get(agentId) ?? throw new ArgumentException($"No agent '{agentId}' is connected. Use deskhand_list_agents."));
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
}
