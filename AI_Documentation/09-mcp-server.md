# 09 — MCP Server (`Deskhand.Mcp`)

A console `Exe` (`AssemblyName=deskhand-mcp`) that exposes the **same `IAutomationBackend`** as **MCP tools
over stdio**, using `ModelContextProtocol` 2.2.0 + `Microsoft.Extensions.Hosting` 9.0.0. Because it shares
the governed backend, it has identical capabilities and safety to the HTTP host — 26 tools.

## Host bootstrap (`Program.cs`)

```csharp
DpiHelper.EnablePerMonitorV2();                      // FIRST — before the backend touches windows/pixels

var builder = Host.CreateApplicationBuilder(args);   // Generic Host, not WebApplication

// stdio carries the MCP protocol on STDOUT, so ALL logs MUST go to STDERR.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var controlState = ControlState.FromEnvironment();
var auditLog = new AuditLog();
var captureNotifier = new ToastNotifier();
builder.Services.AddSingleton(controlState);
builder.Services.AddSingleton(auditLog);
builder.Services.AddSingleton<IAutomationBackend>(_ =>
    new GovernedBackend(new LocalAutomationBackend(), controlState, auditLog, captureNotifier));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();                         // discovers [McpServerToolType]/[McpServerTool]

var app = builder.Build();
using var killSwitch = new KillSwitch(controlState, auditLog);
using var _notifier = captureNotifier;
await app.RunAsync();
```

> **Gotcha — never write logs to stdout in a stdio MCP server.** The JSON-RPC protocol owns stdout. Any
> stray `Console.WriteLine` corrupts the stream and breaks the client. Hence `ClearProviders()` +
> `AddConsole(LogToStandardErrorThreshold = Trace)` routes *everything* to stderr. (The HTTP host, which
> has a normal console, uses `AddSimpleConsole` instead.)

Note `controlState`/`auditLog` are registered as singletons but `captureNotifier` (the `ToastNotifier`) is
constructed locally and captured by the backend factory; it's disposed via `using _notifier`.

## Tool surface (`DeskhandTools.cs`)

A single `static class` marked `[McpServerToolType]`; each tool is a `static` method marked
`[McpServerTool(Name = "...")]` with a `[Description]`. The `IAutomationBackend` (and, for governance tools,
`ControlState`) parameters are **injected from DI** by the SDK — they are not part of the tool's JSON schema.
Other parameters (with `[Description]` and defaults) become the tool's input schema.

```csharp
[McpServerTool(Name = "deskhand_invoke"), Description("Invoke an element ... via its UIA Invoke pattern.")]
public static string Invoke(IAutomationBackend b, string reference) { b.Invoke(reference); return "ok"; }

[McpServerTool(Name = "deskhand_get_tree"), Description("Walk the UI Automation tree...")]
public static string GetTree(IAutomationBackend b,
    [Description("Element ref to start from; omit for the desktop root.")] string? rootRef = null,
    [Description("How many levels deep to expand (default 2).")] int depth = 2,
    [Description("Max children per node (default 40).")] int maxChildren = 40)
    => Json(b.GetTree(rootRef, depth, maxChildren));
```

Reads return **JSON text** (`System.Text.Json`, camelCase, indented, string enums, null-ignoring). Actions
return the literal string `"ok"`.

### The 26 tools

Orientation: `deskhand_machine_info`, `deskhand_desktop_state`, `deskhand_list_windows`,
`deskhand_foreground_window`, `deskhand_focused_element`.
Governance: `deskhand_control_status`, `deskhand_disarm`, `deskhand_arm`.
UIA read: `deskhand_get_tree`, `deskhand_find_elements`, `deskhand_wait_for_element`, `deskhand_get_element`,
`deskhand_get_all_properties`, `deskhand_element_from_point` (resolve the element under a screen pixel —
the reliable "find element" for thin/unstable trees; see `04-uia.md`).
UIA act: `deskhand_invoke`, `deskhand_set_value`, `deskhand_toggle`, `deskhand_expand_collapse`,
`deskhand_select`, `deskhand_set_focus`.
Capture: `deskhand_capture_screen`, `deskhand_capture_region`, `deskhand_capture_window`,
`deskhand_capture_element`, `deskhand_capture_input_desktop`.
Input: `deskhand_mouse_move`, `deskhand_mouse_click`, `deskhand_mouse_scroll`, `deskhand_type_text`,
`deskhand_send_keys`.

(`deskhand_disarm`/`deskhand_arm` flip `ControlState.Armed`; `deskhand_control_status` reports it. These
give the model a way to engage/release the kill switch itself.)

## Returning images to the model

Capture tools return `IEnumerable<ContentBlock>` — a text summary line **plus** an image block:

```csharp
private static IEnumerable<ContentBlock> AsImage(CaptureResultDto c)
{
    yield return new TextContentBlock { Text =
        $"desktop={c.Desktop} rect={c.Rect.Width}x{c.Rect.Height}@({c.Rect.X},{c.Rect.Y}) " +
        $"monitor={c.Monitor} dpi={c.DpiScale} format={c.Format}" };
    yield return new ImageContentBlock {
        Data = c.Bytes,                                              // RAW bytes — see gotcha
        MimeType = c.Format == "jpeg" ? "image/jpeg" : "image/png" };
}
```

> **Gotcha — `ImageContentBlock.Data` takes the raw `byte[]`, not a base64 string.** The MCP SDK 2.2.0
> base64-encodes the bytes for the wire itself. Passing `Convert.ToBase64String(c.Bytes)` double-encodes and
> the client shows a broken image. (Contrast the HTTP host, whose JSON body *does* need base64.) `MimeType`
> must match the actual encoding.

`deskhand_capture_input_desktop` similarly yields a status `TextContentBlock` and, only on success, an
`ImageContentBlock` from `r.Capture.Bytes`.

Because captures are real MCP image content, the model sees screenshots directly.

## Registering with a client (`mcp.json`)

Point the client at the built exe (path from the repo's README):

```json
{
  "mcpServers": {
    "deskhand": {
      "command": "C:\\Users\\crimson\\source\\repos\\uia_mcp\\src\\Deskhand.Mcp\\bin\\x64\\Release\\net9.0-windows10.0.19041.0\\deskhand-mcp.exe"
    }
  }
}
```

You can also launch via `dotnet run --project src/Deskhand.Mcp -c Release` during development. The server
runs in your user session and covers the Default desktop, exactly like the HTTP host. Optional env vars
(`DESKHAND_DISABLE_INPUT`, `DESKHAND_DISABLE_CAPTURE`, `DESKHAND_START_DISARMED`,
`DESKHAND_DISABLE_CAPTURE_TOAST`) can be set in the client's `env` block.
