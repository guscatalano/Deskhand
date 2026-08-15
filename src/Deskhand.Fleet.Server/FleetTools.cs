using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deskhand.Core;
using Deskhand.Core.Fleet;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Deskhand.Fleet.Server;

/// <summary>
/// Fleet-aware MCP tools: list connected PCs and drive any of them by <c>agentId</c>. Each call
/// routes through a <see cref="RemoteAgentBackend"/> to that machine's agent over the wire, so a
/// model pointed at the fleet server's /mcp can operate the whole fleet.
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

    private static IAutomationBackend A(AgentRegistry r, string agentId) =>
        new RemoteAgentBackend(r.Get(agentId) ?? throw new ArgumentException($"No agent '{agentId}' is connected. Use deskhand_list_agents."));

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

    [McpServerTool(Name = "deskhand_agent_info"), Description("Machine info (monitors, virtual screen, desktop state) for one fleet PC.")]
    public static string AgentInfo(AgentRegistry r, string agentId) => Json(A(r, agentId).GetMachineInfo());

    [McpServerTool(Name = "deskhand_agent_list_windows"), Description("Top-level windows on a fleet PC (the reliable way to target an app).")]
    public static string AgentWindows(AgentRegistry r, string agentId) => Json(A(r, agentId).GetTopLevelWindows());

    [McpServerTool(Name = "deskhand_agent_foreground"), Description("The foreground window on a fleet PC.")]
    public static string AgentForeground(AgentRegistry r, string agentId) => Json(A(r, agentId).GetForegroundWindow());

    [McpServerTool(Name = "deskhand_agent_get_tree"), Description("Walk the UIA tree on a fleet PC. Omit rootRef for the desktop.")]
    public static string AgentTree(AgentRegistry r, string agentId, string? rootRef = null, int depth = 2, int maxChildren = 40)
        => Json(A(r, agentId).GetTree(rootRef, depth, maxChildren));

    [McpServerTool(Name = "deskhand_agent_find"), Description("Find elements on a fleet PC by AND-combined conditions.")]
    public static string AgentFind(AgentRegistry r, string agentId, string? rootRef = null, string? name = null, string? automationId = null, string? controlType = null, string? className = null, string scope = "descendants", int max = 100)
        => Json(A(r, agentId).Find(rootRef, new FindQuery(name, automationId, controlType, className, scope, max)));

    [McpServerTool(Name = "deskhand_agent_wait_for_element"), Description("Poll until an element appears on a fleet PC (or timeout).")]
    public static string AgentWait(AgentRegistry r, string agentId, string? rootRef = null, string? name = null, string? automationId = null, string? controlType = null, string? className = null, int timeoutMs = 5000)
    {
        var f = A(r, agentId).WaitForElement(rootRef, new FindQuery(name, automationId, controlType, className, "descendants", 1), timeoutMs);
        return f is null ? "{\"error\":\"wait_timeout\"}" : Json(f);
    }

    [McpServerTool(Name = "deskhand_agent_get_properties"), Description("Every UIA property of an element on a fleet PC.")]
    public static string AgentProps(AgentRegistry r, string agentId, string reference) => Json(A(r, agentId).GetAllProperties(reference));

    [McpServerTool(Name = "deskhand_agent_capture_screen"), Description("Screenshot a fleet PC's monitor (or whole desktop). Returns an image.")]
    public static IEnumerable<ContentBlock> AgentCapture(AgentRegistry r, string agentId, int? monitor = null, string? format = null)
        => AsImage(agentId, A(r, agentId).CaptureScreen(monitor, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_agent_capture_element"), Description("Screenshot one element on a fleet PC. Returns an image.")]
    public static IEnumerable<ContentBlock> AgentCaptureElement(AgentRegistry r, string agentId, string reference, string? format = null)
        => AsImage(agentId, A(r, agentId).CaptureElement(reference, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_agent_invoke"), Description("Invoke an element on a fleet PC (press a button, etc.).")]
    public static string AgentInvoke(AgentRegistry r, string agentId, string reference) { A(r, agentId).Invoke(reference); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_set_value"), Description("Set an element's value on a fleet PC.")]
    public static string AgentSetValue(AgentRegistry r, string agentId, string reference, string text) { A(r, agentId).SetValue(reference, text); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_set_focus"), Description("Raise a window and focus an element on a fleet PC.")]
    public static string AgentSetFocus(AgentRegistry r, string agentId, string reference) { A(r, agentId).SetFocus(reference); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_click"), Description("Click at a point on a fleet PC. button: left|right|middle; count 2 = double.")]
    public static string AgentClick(AgentRegistry r, string agentId, int x, int y, string button = "left", int count = 1) { A(r, agentId).MouseClick(button, x, y, count); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_move"), Description("Move the mouse on a fleet PC.")]
    public static string AgentMove(AgentRegistry r, string agentId, int x, int y) { A(r, agentId).MouseMove(x, y); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_type"), Description("Type text on a fleet PC.")]
    public static string AgentType(AgentRegistry r, string agentId, string text) { A(r, agentId).TypeText(text); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_keys"), Description("Send a key chord on a fleet PC (e.g. \"ctrl+s\", \"enter\").")]
    public static string AgentKeys(AgentRegistry r, string agentId, string chord) { A(r, agentId).SendKeys(chord); return "ok"; }

    [McpServerTool(Name = "deskhand_agent_launch"), Description("Launch a program on a fleet PC; returns its window if it appears.")]
    public static string AgentLaunch(AgentRegistry r, string agentId, string path, string? args = null, string? workingDir = null, int waitForWindowMs = 4000)
        => Json(A(r, agentId).LaunchProcess(path, args, workingDir, waitForWindowMs));
}
