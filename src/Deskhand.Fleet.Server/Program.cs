using Deskhand.Core;
using Deskhand.Core.Fleet;

// Deskhand Fleet Server: accepts outbound agent connections and exposes the full automation surface,
// routed to a selected agent. Agents authenticate with a shared token (DESKHAND_FLEET_TOKEN); the
// same token gates the client API. Bind loopback by default, or DESKHAND_FLEET_BIND=any for remote
// agents (put TLS in front / behind a reverse proxy for production).

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
int port = int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_FLEET_PORT"), out var p) ? p : 8799;
bool bindAny = string.Equals(Environment.GetEnvironmentVariable("DESKHAND_FLEET_BIND"), "any", StringComparison.OrdinalIgnoreCase);
string? token = Environment.GetEnvironmentVariable("DESKHAND_FLEET_TOKEN");

builder.WebHost.ConfigureKestrel(k => { if (bindAny) k.ListenAnyIP(port); else k.ListenLocalhost(port); });
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Services.AddSingleton<AgentRegistry>();

var app = builder.Build();
app.UseWebSockets();
app.UseDefaultFiles();   // the fleet dashboard (wwwroot/index.html) — served before auth
app.UseStaticFiles();
var registry = app.Services.GetRequiredService<AgentRegistry>();

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
    registry.Add(link);
    app.Logger.LogInformation("agent connected: {id} ({machine})", hello.AgentId, hello.MachineName);
    try { await link.ReadLoopAsync(); }
    finally { registry.Remove(hello.AgentId); link.Dispose(); app.Logger.LogInformation("agent disconnected: {id}", hello.AgentId); }
});

// ---- client auth (bearer, when a token is configured) ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path is "/health" || path.StartsWith("/agent/connect")) { await next(); return; }
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

IAutomationBackend A(string id) =>
    new RemoteAgentBackend(registry.Get(id) ?? throw new ArgumentException($"No agent '{id}' is connected."));

static IResult Cap(CaptureResultDto c) =>
    Results.Ok(new { c.Desktop, c.Rect, c.Monitor, c.DpiScale, c.Format, imageBase64 = Convert.ToBase64String(c.Bytes) });
static ImageFormat Fmt(string? f) => f?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "deskhand-fleet" }));
app.MapGet("/agents", () => Results.Ok(registry.All.Select(a => new
{
    id = a.AgentId, machine = a.MachineName,
    monitors = a.Info?.Monitors.Count ?? 0, desktop = a.Info?.DesktopState.Desktop, elevated = a.Info?.IsElevated ?? false,
})));

// ---- orientation ----
app.MapGet("/agents/{id}/machine", (string id) => Results.Ok(A(id).GetMachineInfo()));
app.MapGet("/agents/{id}/desktop-state", (string id) => Results.Ok(A(id).GetDesktopState()));
app.MapGet("/agents/{id}/foreground", (string id) => Results.Ok(A(id).GetForegroundWindow()));
app.MapGet("/agents/{id}/focused", (string id) => Results.Ok(A(id).GetFocusedElement()));
app.MapGet("/agents/{id}/windows", (string id) => Results.Ok(A(id).GetTopLevelWindows()));
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
app.MapPost("/agents/{id}/keyboard/type", (string id, TypeReq r) => { A(id).TypeText(r.Text); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/keyboard/keys", (string id, KeysReq r) => { A(id).SendKeys(r.Chord); return Results.Ok(new { ok = true }); });

Console.WriteLine();
Console.WriteLine($"  Deskhand Fleet Server   ·   {(bindAny ? "0.0.0.0" : "127.0.0.1")}:{port}   ·   token {(token is null ? "OFF" : "required")}");
Console.WriteLine($"  agents dial: ws://<host>:{port}/agent/connect");
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
