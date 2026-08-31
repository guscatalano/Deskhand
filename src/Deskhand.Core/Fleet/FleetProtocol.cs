using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deskhand.Core.Fleet;

/// <summary>
/// Fleet wire protocol. The agent dials OUT to the server and holds one WebSocket open; the server
/// pushes <see cref="FleetCommand"/>s down it and the agent replies with <see cref="FleetResult"/>s
/// (reverse RPC). This keeps user machines free of inbound ports. Messages are JSON, reusing the
/// same DTOs the HTTP/MCP surface already returns.
/// </summary>
public static class FleetJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>First message the agent sends after connecting: who it is and what it can do.</summary>
public sealed record AgentHello(string AgentId, string MachineName, MachineInfoDto Info);

/// <summary>Envelope pushed from server → agent.</summary>
public sealed record FleetCommand(string Id, string Method, JsonElement Args);

/// <summary>Envelope returned from agent → server, correlated by <see cref="Id"/>.</summary>
public sealed record FleetResult(string Id, bool Ok, JsonElement Result, string? Error, string? ErrorType);

/// <summary>Canonical method names shared by the server proxy and the agent dispatcher.</summary>
public static class FleetMethods
{
    public const string DesktopState = "desktop_state";
    public const string MachineInfo = "machine_info";
    public const string ForegroundWindow = "foreground_window";
    public const string FocusedElement = "focused_element";
    public const string ListWindows = "list_windows";
    public const string ListProcesses = "list_processes";
    public const string Launch = "launch";
    public const string LaunchAs = "launch_as";
    public const string GetTree = "get_tree";
    public const string Find = "find";
    public const string WaitForElement = "wait_for_element";
    public const string GetElement = "get_element";
    public const string GetAllProperties = "get_all_properties";
    public const string ElementFromPoint = "element_from_point";
    public const string Invoke = "invoke";
    public const string SetValue = "set_value";
    public const string Toggle = "toggle";
    public const string ExpandCollapse = "expand_collapse";
    public const string Select = "select";
    public const string SetFocus = "set_focus";
    public const string CaptureScreen = "capture_screen";
    public const string CaptureRegion = "capture_region";
    public const string CaptureWindow = "capture_window";
    public const string CaptureWindowByRef = "capture_window_ref";
    public const string CaptureElement = "capture_element";
    public const string CaptureInputDesktop = "capture_input_desktop";
    public const string MouseMove = "mouse_move";
    public const string MouseClick = "mouse_click";
    public const string MouseDown = "mouse_down";
    public const string MouseUp = "mouse_up";
    public const string MouseScroll = "mouse_scroll";
    public const string TypeText = "type_text";
    public const string SendKeys = "send_keys";

    // observation: events, hooks, recording, user-input recording
    public const string GetEvents = "get_events";
    public const string WaitForProcess = "wait_for_process";
    public const string RecordStart = "record_start";
    public const string RecordStop = "record_stop";
    public const string RecordStatus = "record_status";
    public const string RecordRead = "record_read";
    public const string InputStart = "input_start";
    public const string InputStop = "input_stop";
    public const string InputGet = "input_get";
    public const string RdpInstallAgent = "rdp_install_agent";
    public const string RegistryBrowse = "registry_browse";
    public const string DumpProcess = "dump_process";
    public const string ListApps = "list_apps";
    public const string ListDesktops = "list_desktops";
    public const string MoveWindowToDesktop = "move_window_to_desktop";
    public const string BrowseFiles = "browse_files";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string DeletePath = "delete_path";
    public const string RenamePath = "rename_path";
    public const string MovePath = "move_path";
    public const string CopyPath = "copy_path";
    public const string ZipPaths = "zip";
    public const string UnzipPath = "unzip";
    public const string RunCommand = "run_command";
    public const string SystemInfo = "system_info";
    public const string DumpList = "dump_list";
    public const string DumpRead = "dump_read";
}
