using Deskhand.Core;
using Deskhand.Core.Fleet;
using Deskhand.Fleet.Server;

// Deskhand Fleet Server: accepts outbound agent connections and exposes the full automation surface,
// routed to a selected agent. Agents authenticate with a shared token (DESKHAND_FLEET_TOKEN); the
// same token gates the client API. Bind loopback by default, or DESKHAND_FLEET_BIND=any for remote
// agents (put TLS in front / behind a reverse proxy for production).

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
int port = int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_FLEET_PORT"), out var p) ? p : 8799;
bool bindAny = string.Equals(Environment.GetEnvironmentVariable("DESKHAND_FLEET_BIND"), "any", StringComparison.OrdinalIgnoreCase);
string? token = Environment.GetEnvironmentVariable("DESKHAND_FLEET_TOKEN");

// Binding to the network without a shared token would let anyone reach the fleet + drive every
// connected PC. Refuse to start in that state (matches the local server's DESKHAND_BIND rule).
if (bindAny && string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine(
        "REFUSING TO START: DESKHAND_FLEET_BIND=any exposes the fleet to the network, but\n" +
        "  DESKHAND_FLEET_TOKEN is not set. Anyone could reach the fleet and drive every connected PC.\n" +
        "  Fix: set DESKHAND_FLEET_TOKEN to a strong secret, or unset DESKHAND_FLEET_BIND.");
    Environment.Exit(3);
}

// Optional HTTPS: DESKHAND_FLEET_TLS_CERT=<pfx> (+ _PASSWORD) or DESKHAND_FLEET_TLS=self-signed.
var tlsCert = Deskhand.Core.TlsSupport.FromEnvironment("DESKHAND_FLEET_");
bool tls = tlsCert is not null;
string scheme = tls ? "https" : "http";
builder.WebHost.ConfigureKestrel(k =>
{
    void Https(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions o) { if (tls) o.UseHttps(tlsCert!); }
    if (bindAny) k.ListenAnyIP(port, Https); else k.ListenLocalhost(port, Https);
});
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Services.AddSingleton<AgentRegistry>();
var rdpManager = new RdpConnectorManager(port, token);
builder.Services.AddSingleton(rdpManager);

// Fleet-aware MCP over Streamable HTTP at /mcp: list + drive any connected PC by agentId.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(Deskhand.Fleet.Server.FleetTools).Assembly);

builder.Services.AddSingleton<FleetAudit>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();
app.UseWebSockets();
app.UseDefaultFiles();   // the fleet dashboard (wwwroot/index.html) — served before auth
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    }
});
var registry = app.Services.GetRequiredService<AgentRegistry>();
var audit = app.Services.GetRequiredService<FleetAudit>();
app.Logger.LogInformation("Fleet audit -> {dir}", audit.Directory);

static string Bearer(HttpContext ctx)
{
    var a = ctx.Request.Headers.Authorization.ToString();
    return a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a["Bearer ".Length..].Trim() : "";
}

// ---- agent link endpoint (agents dial in here) ----
app.Map("/agent/connect", async (HttpContext ctx) =>
{
    if (token is not null && Bearer(ctx) != token) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    var helloMsg = await WsUtil.ReceiveTextAsync(ws, ctx.RequestAborted);
    var hello = helloMsg is null ? null : FleetJson.Deserialize<AgentHello>(helloMsg);
    if (hello is null) return;

    var link = new ServerAgentLink(ws, hello);
    var ip = ctx.Connection.RemoteIpAddress?.ToString();
    registry.Add(link);
    audit.Record("agent_connect", ip, hello.AgentId, hello.MachineName);
    app.Logger.LogInformation("agent connected: {id} ({machine})", hello.AgentId, hello.MachineName);
    try { await link.ReadLoopAsync(); }
    finally
    {
        registry.Remove(hello.AgentId);
        link.Dispose();
        audit.Record("agent_disconnect", ip, hello.AgentId, hello.MachineName);
        app.Logger.LogInformation("agent disconnected: {id}", hello.AgentId);
    }
});

// ---- client auth (bearer, when a token is configured) ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    // /agent/connect authenticates itself (above). /health is always open. /mcp normally rides the
    // loopback bind, but once the port is exposed (bindAny) it must present the token like any client.
    if (path is "/health" || path.StartsWith("/agent/connect")) { await next(); return; }
    if (path.StartsWith("/mcp") && !bindAny) { await next(); return; }
    if (token is not null && Bearer(ctx) != token)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Fleet token required.", type = "unauthorized" });
        return;
    }
    await next();
});

// ---- error mapping (preserve the agent's error type -> status) ----
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        var (status, type) = Map(ex);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = status; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message, type }); }
    }
});

// Record every routed /agents/{id}/... action (dashboard + HTTP API) with the caller's address.
app.Use(async (ctx, next) =>
{
    await next();
    var p = ctx.Request.Path.Value ?? "";
    if (p.StartsWith("/agents/", StringComparison.Ordinal))
    {
        var seg = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length >= 3)
            audit.Record("action", ctx.Connection.RemoteIpAddress?.ToString(), seg[1],
                $"{ctx.Request.Method} /{string.Join('/', seg.Skip(2))} -> {ctx.Response.StatusCode}");
    }
});

IAutomationBackend A(string id) =>
    new RemoteAgentBackend(registry.Get(id) ?? throw new ArgumentException($"No agent '{id}' is connected."));
RemoteAgentObserver O(string id) =>
    new(registry.Get(id) ?? throw new ArgumentException($"No agent '{id}' is connected."));

static IResult Cap(CaptureResultDto c) =>
    Results.Ok(new { c.Desktop, c.Rect, c.Monitor, c.DpiScale, c.Format, imageBase64 = Convert.ToBase64String(c.Bytes) });
static ImageFormat Fmt(string? f) => f?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "deskhand-fleet" }));
app.MapGet("/agents", () => Results.Ok(registry.All.Select(a => new
{
    id = a.AgentId, machine = a.MachineName,
    monitors = a.Info?.Monitors.Count ?? 0, desktop = a.Info?.DesktopState.Desktop, elevated = a.Info?.IsElevated ?? false,
})));
app.MapGet("/fleet/audit", (FleetAudit a, long since) => Results.Ok(new { lastId = a.LastId, dir = a.Directory, entries = a.Since(since) }));

// ---- add a machine to the fleet over RDP, straight from the web (spawns deskhand-rdp --fleet) ----
app.MapPost("/fleet/rdp/connect", (RdpConnectorManager m, FleetAudit fa, RdpConnectReq r) =>
{
    var c = m.Connect(r.Host, r.User, r.Password, r.Domain, r.Size, r.Id);
    fa.Record("rdp_connect", "web", c.Id, $"{r.User}@{r.Host} pid={c.Pid}");
    return Results.Ok(c);
});
app.MapGet("/fleet/rdp/list", (RdpConnectorManager m) => Results.Ok(m.List()));
app.MapPost("/fleet/rdp/disconnect", (RdpConnectorManager m, FleetAudit fa, IdReq r) =>
{
    var ok = m.Disconnect(r.Id);
    fa.Record("rdp_disconnect", "web", r.Id, ok ? "killed" : "not found");
    return Results.Ok(new { ok });
});
// Bootstrap-install the native agent on an RDP target (over its RDP session). See RdpInstallAgent.
app.MapPost("/fleet/rdp/install", (FleetAudit fa, RdpInstallReq r) =>
{
    var j = O(r.Id).InstallAgent(r.AgentPath);
    fa.Record("rdp_install_agent", "web", r.Id, "bootstrap native agent over RDP");
    return Results.Ok(j);
});
app.Lifetime.ApplicationStopping.Register(rdpManager.DisposeAll);

// ---- orientation ----
app.MapGet("/agents/{id}/machine", (string id) => Results.Ok(A(id).GetMachineInfo()));
app.MapGet("/agents/{id}/desktop-state", (string id) => Results.Ok(A(id).GetDesktopState()));
app.MapGet("/agents/{id}/foreground", (string id) => Results.Ok(A(id).GetForegroundWindow()));
app.MapGet("/agents/{id}/focused", (string id) => Results.Ok(A(id).GetFocusedElement()));
app.MapGet("/agents/{id}/windows", (string id) => Results.Ok(A(id).GetTopLevelWindows()));
app.MapGet("/agents/{id}/processes", (string id) => Results.Ok(A(id).GetProcesses()));
app.MapPost("/agents/{id}/process/launch", (string id, LaunchReq r) => Results.Ok(A(id).LaunchProcess(r.Path, r.Args, r.WorkingDir, r.WaitForWindowMs ?? 4000)));

// ---- uia read ----
app.MapPost("/agents/{id}/uia/tree", (string id, TreeReq r) => Results.Ok(A(id).GetTree(r.RootRef, r.Depth ?? 2, r.MaxChildren ?? 40)));
app.MapPost("/agents/{id}/uia/find", (string id, FindReq r) => Results.Ok(A(id).Find(r.RootRef, new FindQuery(r.Name, r.AutomationId, r.ControlType, r.ClassName, r.Scope ?? "descendants", r.Max ?? 100))));
app.MapPost("/agents/{id}/uia/wait", (string id, WaitReq r) =>
{
    var f = A(id).WaitForElement(r.RootRef, new FindQuery(r.Name, r.AutomationId, r.ControlType, r.ClassName, r.Scope ?? "descendants", 1), r.TimeoutMs ?? 5000);
    return f is null ? Results.Json(new { error = "wait_timeout", type = "wait_timeout" }, statusCode: 404) : Results.Ok(f);
});
app.MapPost("/agents/{id}/uia/element", (string id, RefReq r) => Results.Ok(A(id).GetElement(r.Reference)));
app.MapPost("/agents/{id}/uia/properties", (string id, RefReq r) => Results.Ok(A(id).GetAllProperties(r.Reference)));
app.MapPost("/agents/{id}/uia/element-from-point", (string id, PointReq r) => Results.Ok(A(id).GetElementFromPoint(r.X, r.Y)));

// ---- observation: events, hooks, recording, user-input (routed to the agent's services) ----
app.MapGet("/agents/{id}/events", (string id, long since) => Results.Ok(O(id).GetEvents(since)));
app.MapPost("/agents/{id}/process/wait", (string id, ProcWaitReq r) => Results.Ok(O(id).WaitForProcess(r.Event ?? "start", r.Name, r.Pid, r.TimeoutMs ?? 30000)));
app.MapPost("/agents/{id}/record/start", (string id, RecReq r) => Results.Ok(O(id).RecordStart(r.Monitor, r.Format ?? "gif", r.Fps ?? 10, r.Scale ?? 100, r.Quality ?? 75, r.MaxDurationMs ?? 30000)));
app.MapPost("/agents/{id}/record/stop", (string id, RefReq r) => Results.Ok(O(id).RecordStop(r.Reference)));
app.MapGet("/agents/{id}/record/status", (string id, string? recId) => Results.Ok(O(id).RecordStatus(recId)));
app.MapGet("/agents/{id}/recordings/{recId}", (string id, string recId) =>
{
    var j = O(id).RecordRead(recId);
    var bytes = Convert.FromBase64String(j.GetProperty("base64").GetString()!);
    return Results.File(bytes, j.GetProperty("mime").GetString(), j.GetProperty("name").GetString());
});
app.MapPost("/agents/{id}/input/record/start", (string id, InputRecReq r) => Results.Ok(O(id).InputStart(r.CaptureText ?? true)));
app.MapPost("/agents/{id}/input/record/stop", (string id) => Results.Ok(O(id).InputStop()));
app.MapGet("/agents/{id}/input/record/events", (string id, long since) => Results.Ok(O(id).InputGet(since)));
app.MapGet("/agents/{id}/registry", (string id, string? path) => Results.Ok(O(id).RegistryBrowse(path)));
app.MapPost("/agents/{id}/process/dump", (string id, PidReq r) => Results.Ok(O(id).DumpProcess(r.Pid)));   // .dmp saved on the agent
app.MapGet("/agents/{id}/dumps", (string id) => Results.Ok(O(id).DumpList()));
app.MapGet("/agents/{id}/dumps/{name}", (string id, string name) =>
{
    var j = O(id).DumpRead(name);
    if (!j.TryGetProperty("base64", out var b64) || b64.ValueKind != System.Text.Json.JsonValueKind.String)
        return Results.Json(j, statusCode: 400);
    var bytes = Convert.FromBase64String(b64.GetString()!);
    return Results.File(bytes, "application/octet-stream", j.GetProperty("name").GetString() ?? name);
});
app.MapGet("/agents/{id}/apps", (string id) => Results.Ok(O(id).ListApps()));
app.MapGet("/agents/{id}/desktops", (string id) => Results.Ok(O(id).ListDesktops()));
app.MapPost("/agents/{id}/desktops/move-window", (string id, MoveWinReq r) => Results.Ok(O(id).MoveWindowToDesktop(r.Hwnd, r.DesktopId)));

// ---- files + shell on a fleet PC (native agents only; RDP agents return a clean error) ----
app.MapGet("/agents/{id}/fs", (string id, string? path) => Results.Ok(O(id).BrowseFiles(path)));
app.MapGet("/agents/{id}/fs/download", (string id, string path) =>
{
    var j = O(id).ReadFile(path);
    if (!j.TryGetProperty("base64", out var b64) || b64.ValueKind != System.Text.Json.JsonValueKind.String)
        return Results.Json(j, statusCode: 400);   // error/not-a-file — pass the agent's JSON through
    var bytes = Convert.FromBase64String(b64.GetString()!);
    var name = System.IO.Path.GetFileName(j.GetProperty("path").GetString() ?? "download");
    return Results.File(bytes, "application/octet-stream", name);
});
app.MapPost("/agents/{id}/fs/write", (string id, AgentWriteReq r) => Results.Ok(O(id).WriteFile(r.Path, r.ContentBase64, r.Overwrite ?? false)));
app.MapPost("/agents/{id}/fs/delete", (string id, AgentDeleteReq r) => Results.Ok(O(id).DeletePath(r.Path, r.Permanent ?? false)));
app.MapPost("/agents/{id}/fs/rename", (string id, AgentRenameReq r) => Results.Ok(O(id).RenamePath(r.Path, r.NewName)));
app.MapPost("/agents/{id}/fs/move", (string id, AgentMoveReq r) => Results.Ok(O(id).MovePath(r.Source, r.Dest, r.Overwrite ?? false)));
app.MapPost("/agents/{id}/fs/copy", (string id, AgentCopyReq r) => Results.Ok(O(id).CopyPath(r.Source, r.Dest, r.Overwrite ?? false)));
app.MapPost("/agents/{id}/fs/zip", (string id, AgentZipReq r) => Results.Ok(O(id).Zip(r.Sources, r.Dest, r.Overwrite ?? false)));
app.MapPost("/agents/{id}/fs/unzip", (string id, AgentUnzipReq r) => Results.Ok(O(id).Unzip(r.ZipPath, r.Dest, r.Overwrite ?? false)));
app.MapPost("/agents/{id}/shell/run", (string id, AgentShellReq r) => Results.Ok(O(id).RunCommand(r.Shell, r.Command, r.Cwd, r.TimeoutMs)));
app.MapPost("/agents/{id}/process/launch-as", (string id, AgentLaunchAsReq r) =>
    Results.Ok(O(id).LaunchProcessAs(r.Path, r.Args, r.WorkingDir, r.SessionId, r.Desktop, r.As, r.User, r.Domain, r.Password, r.NoWindow ?? false)));
app.MapGet("/agents/{id}/system", (string id) => Results.Ok(O(id).SystemInfo()));
app.MapGet("/agents/{id}/firewall/rules", (string id, string? direction, int? port, bool? enabledOnly, string? contains, bool? managedOnly, int? max) =>
    Results.Ok(O(id).FirewallRules(direction, port, enabledOnly, contains, managedOnly ?? false, max ?? 200)));
app.MapPost("/agents/{id}/firewall/open", (string id, AgentFwOpenReq r) =>
    Results.Ok(O(id).FirewallOpen(r.Port, r.Protocol, r.Direction, r.RemoteAddresses, r.Name)));
app.MapPost("/agents/{id}/firewall/close", (string id, AgentFwCloseReq r) =>
    Results.Ok(O(id).FirewallClose(r.Port, r.Protocol, r.Direction, r.All ?? false)));
app.MapGet("/agents/{id}/clipboard", (string id) => Results.Ok(O(id).ClipboardGet()));
app.MapPost("/agents/{id}/clipboard", (string id, AgentClipReq r) => Results.Ok(O(id).ClipboardSet(r.Text)));
app.MapPost("/agents/{id}/clipboard/clear", (string id) => Results.Ok(O(id).ClipboardClear()));
app.MapPost("/agents/{id}/window", (string id, AgentWindowReq r) => Results.Ok(O(id).WindowAction(r.Hwnd, r.Action, r.X, r.Y, r.Width, r.Height)));
app.MapPost("/agents/{id}/ocr/screen", (string id, AgentOcrScreenReq? r) => Results.Ok(O(id).OcrScreen(r?.Monitor)));
app.MapPost("/agents/{id}/ocr/region", (string id, AgentOcrRegionReq r) => Results.Ok(O(id).OcrRegion(r.X, r.Y, r.Width, r.Height)));
app.MapPost("/agents/{id}/ocr/window", (string id, AgentOcrWindowReq r) => Results.Ok(O(id).OcrWindow(r.Hwnd, r.Reference)));
app.MapPost("/agents/{id}/vision/find", (string id, AgentFindImageReq r) =>
    Results.Ok(O(id).FindImage(r.TemplateBase64 ?? "", r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference, r.Threshold, r.MaxResults)));
app.MapPost("/agents/{id}/vision/wait-image", (string id, AgentWaitImageReq r) =>
    Results.Ok(O(id).WaitForImage(r.TemplateBase64 ?? "", r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference, r.Threshold, r.TimeoutMs, r.Absent ?? false, r.PollMs)));
app.MapPost("/agents/{id}/vision/wait-text", (string id, AgentWaitTextReq r) =>
    Results.Ok(O(id).WaitForText(r.Text ?? "", r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference, r.TimeoutMs, r.Absent ?? false, r.PollMs)));
app.MapPost("/agents/{id}/vision/wait-stable", (string id, AgentStableReq r) =>
    Results.Ok(O(id).WaitStable(r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference, r.SettleMs, r.TimeoutMs, r.PollMs, r.Epsilon, r.WaitForChange ?? false)));
app.MapPost("/agents/{id}/vision/click-image", (string id, AgentClickImageReq r) =>
    Results.Ok(O(id).ClickImage(r.TemplateBase64 ?? "", r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference, r.Threshold, r.Button, r.Count, r.TimeoutMs)));
app.MapPost("/agents/{id}/vision/click-text", (string id, AgentClickTextReq r) =>
    Results.Ok(O(id).ClickText(r.Text ?? "", r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference, r.Button, r.Count, r.TimeoutMs)));
app.MapGet("/agents/{id}/vision/pixel", (string id, int x, int y) => Results.Ok(O(id).GetPixel(x, y)));
app.MapPost("/agents/{id}/input/paste", (string id, AgentClipReq r) => Results.Ok(O(id).Paste(r.Text ?? "")));
app.MapPost("/agents/{id}/process/control", (string id, AgentProcCtrlReq r) =>
{
    var act = (r.Action ?? "").Trim().ToLowerInvariant();
    if (act is "kill" or "terminate" or "suspend" && r.Confirm != true)
        return Results.Json(new { ok = false, confirmationRequired = true, action = act, pid = r.Pid, message = "destructive — resend with confirm=true" }, statusCode: 409);
    return Results.Ok(O(id).ProcessControl(r.Pid, r.Action, r.Tree, r.Level, r.Force ?? false));
});
app.MapGet("/agents/{id}/service/state", (string id, string name) => Results.Ok(new { name, state = "" }));
app.MapPost("/agents/{id}/service/control", (string id, AgentSvcCtrlReq r) =>
{
    var act = (r.Action ?? "").Trim().ToLowerInvariant();
    if (act is "stop" or "restart" && r.Confirm != true)
        return Results.Json(new { ok = false, confirmationRequired = true, action = act, name = r.Name, message = "destructive — resend with confirm=true" }, statusCode: 409);
    return Results.Ok(O(id).ServiceControl(r.Name, r.Action));
});
app.MapGet("/agents/{id}/env", (string id, string name, string? scope) => Results.Ok(O(id).EnvGet(name, scope)));
app.MapPost("/agents/{id}/env", (string id, AgentEnvSetReq r) => Results.Ok(O(id).EnvSet(r.Name, r.Value, r.Scope)));
app.MapPost("/agents/{id}/task", (string id, AgentTaskReq r) => Results.Ok(O(id).TaskAction(r.Task, r.Action)));
app.MapGet("/agents/{id}/uac", (string id) => Results.Ok(O(id).UacStatus()));
app.MapPost("/agents/{id}/uac/config", (string id, AgentUacCfgReq r) => Results.Ok(O(id).UacConfig(r.Enabled, r.PromptOnSecureDesktop, r.AutoApprove, r.AdminBehavior)));
app.MapPost("/agents/{id}/uac/respond", (string id, AgentUacRespReq r) => Results.Ok(O(id).UacRespond(r.Accept ?? true, r.TimeoutMs ?? 5000)));
app.MapPost("/agents/{id}/fetch", (string id, AgentFetchReq r) => Results.Ok(O(id).Fetch(r.Url ?? "", r.Path, r.MaxBytes)));

// ---- uia act ----
app.MapPost("/agents/{id}/uia/invoke", (string id, RefReq r) => { A(id).Invoke(r.Reference); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/uia/set-value", (string id, SetValueReq r) => { A(id).SetValue(r.Reference, r.Text); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/uia/toggle", (string id, RefReq r) => { A(id).Toggle(r.Reference); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/uia/expand-collapse", (string id, ExpandReq r) => { A(id).ExpandCollapse(r.Reference, r.Expand); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/uia/select", (string id, RefReq r) => { A(id).Select(r.Reference); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/uia/set-focus", (string id, RefReq r) => { A(id).SetFocus(r.Reference); return Results.Ok(new { ok = true }); });

// ---- capture ----
app.MapPost("/agents/{id}/capture/screen", (string id, ScreenReq? r) => Cap(A(id).CaptureScreen(r?.Monitor, Fmt(r?.Format), 80)));
app.MapPost("/agents/{id}/capture/region", (string id, RegionReq r) => Cap(A(id).CaptureRegion(r.X, r.Y, r.Width, r.Height, Fmt(r.Format), 80)));
app.MapPost("/agents/{id}/capture/window", (string id, WindowCapReq r) => Cap(r.Reference is not null ? A(id).CaptureWindowByRef(r.Reference, Fmt(r.Format), 80) : A(id).CaptureWindow(r.Hwnd ?? 0, Fmt(r.Format), 80)));
app.MapPost("/agents/{id}/capture/element", (string id, ElementCapReq r) => Cap(A(id).CaptureElement(r.Reference, Fmt(r.Format), 80)));

// ---- input ----
app.MapPost("/agents/{id}/mouse/move", (string id, MoveReq r) => { A(id).MouseMove(r.X, r.Y); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/mouse/click", (string id, ClickReq r) => { A(id).MouseClick(r.Button ?? "left", r.X, r.Y, r.Count ?? 1); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/mouse/scroll", (string id, ScrollReq r) => { A(id).MouseScroll(r.Dx, r.Dy); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/mouse/drag", (string id, AgentDragReq r) => { A(id).Drag(r.FromX, r.FromY, r.ToX, r.ToY, r.Button ?? "left", r.Steps ?? 20, r.HoldMs ?? 60); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/keyboard/type", (string id, TypeReq r) => { A(id).TypeText(r.Text); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/keyboard/keys", (string id, KeysReq r) => { A(id).SendKeys(r.Chord); return Results.Ok(new { ok = true }); });

// Fleet MCP endpoint (list + drive any agent by agentId).
app.MapMcp("/mcp");

Console.WriteLine();
Console.WriteLine($"  Deskhand Fleet Server   ·   {scheme}://{(bindAny ? "0.0.0.0" : "127.0.0.1")}:{port}   ·   token {(token is null ? "OFF" : "required")}{(tls ? "   ·   TLS on" : "")}");
Console.WriteLine($"  agents dial: {(tls ? "wss" : "ws")}://<host>:{port}/agent/connect");
Console.WriteLine();

app.Run();

static (int, string) Map(Exception ex) => ex switch
{
    RemoteAutomationException rae => (rae.ErrorType switch
    {
        "UnknownElementException" or "StaleElementException" => StatusCodes.Status404NotFound,
        "PatternNotSupportedException" or "DesktopUnavailableException" => StatusCodes.Status409Conflict,
        "DisarmedException" or "CapabilityDisabledException" => StatusCodes.Status403Forbidden,
        "ArgumentException" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    }, rae.ErrorType),
    ArgumentException => (StatusCodes.Status400BadRequest, "bad_request"),
    TimeoutException => (StatusCodes.Status504GatewayTimeout, "agent_timeout"),
    _ => (StatusCodes.Status500InternalServerError, "internal"),
};

record TreeReq(string? RootRef, int? Depth, int? MaxChildren);
record FindReq(string? RootRef, string? Name, string? AutomationId, string? ControlType, string? ClassName, string? Scope, int? Max);
record WaitReq(string? RootRef, string? Name, string? AutomationId, string? ControlType, string? ClassName, string? Scope, int? TimeoutMs);
record RefReq(string Reference);
record SetValueReq(string Reference, string Text);
record ExpandReq(string Reference, bool Expand);
record ScreenReq(int? Monitor, string? Format);
record RegionReq(int X, int Y, int Width, int Height, string? Format);
record WindowCapReq(string? Reference, long? Hwnd, string? Format);
record ElementCapReq(string Reference, string? Format);
record MoveReq(int X, int Y);
record ClickReq(string? Button, int? X, int? Y, int? Count);
record ScrollReq(int Dx, int Dy);
record TypeReq(string Text);
record KeysReq(string Chord);
record LaunchReq(string Path, string? Args, string? WorkingDir, int? WaitForWindowMs);
record PointReq(int X, int Y);
record ProcWaitReq(string? Event, string? Name, int? Pid, int? TimeoutMs);
record RdpConnectReq(string Host, string User, string Password, string? Domain, string? Size, string? Id);
record IdReq(string Id);
record RecReq(int? Monitor, string? Format, int? Fps, int? Scale, int? Quality, int? MaxDurationMs);
record InputRecReq(bool? CaptureText);
record PidReq(int Pid);
record MoveWinReq(long Hwnd, string? DesktopId);
record RdpInstallReq(string Id, string? AgentPath);
record AgentWriteReq(string Path, string ContentBase64, bool? Overwrite);
record AgentDeleteReq(string Path, bool? Permanent);
record AgentRenameReq(string Path, string NewName);
record AgentMoveReq(string Source, string Dest, bool? Overwrite);
record AgentCopyReq(string Source, string Dest, bool? Overwrite);
record AgentZipReq(string[]? Sources, string Dest, bool? Overwrite);
record AgentUnzipReq(string ZipPath, string? Dest, bool? Overwrite);
record AgentShellReq(string? Shell, string Command, string? Cwd, int? TimeoutMs);
record AgentLaunchAsReq(string Path, string? Args, string? WorkingDir, int? SessionId, string? Desktop,
    string? As, string? User, string? Domain, string? Password, bool? NoWindow);
record AgentFwOpenReq(int Port, string? Protocol, string? Direction, string? RemoteAddresses, string? Name);
record AgentFwCloseReq(int Port, string? Protocol, string? Direction, bool? All);
record AgentClipReq(string? Text);
record AgentDragReq(int FromX, int FromY, int ToX, int ToY, string? Button, int? Steps, int? HoldMs);
record AgentWindowReq(long Hwnd, string Action, int? X, int? Y, int? Width, int? Height);
record AgentOcrScreenReq(int? Monitor);
record AgentOcrRegionReq(int X, int Y, int Width, int Height);
record AgentOcrWindowReq(long? Hwnd, string? Reference);
record AgentFindImageReq(string? TemplateBase64, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, double? Threshold, int? MaxResults);
record AgentWaitImageReq(string? TemplateBase64, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, double? Threshold, int? TimeoutMs, bool? Absent, int? PollMs);
record AgentWaitTextReq(string? Text, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, int? TimeoutMs, bool? Absent, int? PollMs);
record AgentStableReq(string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, int? SettleMs, int? TimeoutMs, int? PollMs, double? Epsilon, bool? WaitForChange);
record AgentClickImageReq(string? TemplateBase64, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, double? Threshold, string? Button, int? Count, int? TimeoutMs);
record AgentClickTextReq(string? Text, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, string? Button, int? Count, int? TimeoutMs);
record AgentProcCtrlReq(int Pid, string Action, bool? Tree, string? Level, bool? Force, bool? Confirm);
record AgentSvcCtrlReq(string Name, string Action, bool? Confirm);
record AgentEnvSetReq(string Name, string? Value, string? Scope);
record AgentTaskReq(string Task, string Action);
record AgentUacCfgReq(bool? Enabled, bool? PromptOnSecureDesktop, bool? AutoApprove, int? AdminBehavior);
record AgentUacRespReq(bool? Accept, int? TimeoutMs);
record AgentFetchReq(string? Url, string? Path, long? MaxBytes);
