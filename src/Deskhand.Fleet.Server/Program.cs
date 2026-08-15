using Deskhand.Core;
using Deskhand.Core.Fleet;

// Deskhand Fleet Server: accepts outbound agent connections and exposes the same automation surface,
// routed to a selected agent. NOTE: this test-slice binds loopback and has no transport auth yet —
// production needs TLS + mTLS/agent auth and AnyIP binding (design Phase 4 hardening).

var builder = WebApplication.CreateBuilder(args);
int port = int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_FLEET_PORT"), out var p) ? p : 8799;
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Services.AddSingleton<AgentRegistry>();

var app = builder.Build();
app.UseWebSockets();

var registry = app.Services.GetRequiredService<AgentRegistry>();

// ---- agent link endpoint (agents dial in here) ----
app.Map("/agent/connect", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

    var helloMsg = await WsUtil.ReceiveTextAsync(ws, ctx.RequestAborted);
    var hello = helloMsg is null ? null : FleetJson.Deserialize<AgentHello>(helloMsg);
    if (hello is null) return;

    var link = new ServerAgentLink(ws, hello);
    registry.Add(link);
    app.Logger.LogInformation("agent connected: {id} ({machine})", hello.AgentId, hello.MachineName);
    try { await link.ReadLoopAsync(); }
    finally
    {
        registry.Remove(hello.AgentId);
        link.Dispose();
        app.Logger.LogInformation("agent disconnected: {id}", hello.AgentId);
    }
});

// ---- error mapping (preserve the agent's error type -> status) ----
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        var (status, type) = Map(ex);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message, type });
        }
    }
});

IAutomationBackend Agent(string id) =>
    new RemoteAgentBackend(registry.Get(id) ?? throw new ArgumentException($"No agent '{id}' is connected."));

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "deskhand-fleet" }));
app.MapGet("/agents", () => Results.Ok(registry.All.Select(a => new
{
    id = a.AgentId,
    machine = a.MachineName,
    monitors = a.Info?.Monitors.Count ?? 0,
    desktop = a.Info?.DesktopState.Desktop,
    elevated = a.Info?.IsElevated ?? false,
})));

// ---- routed automation (representative subset; RemoteAgentBackend implements the full surface) ----
app.MapGet("/agents/{id}/machine", (string id) => Results.Ok(Agent(id).GetMachineInfo()));
app.MapGet("/agents/{id}/foreground", (string id) => Results.Ok(Agent(id).GetForegroundWindow()));
app.MapGet("/agents/{id}/windows", (string id) => Results.Ok(Agent(id).GetTopLevelWindows()));
app.MapPost("/agents/{id}/tree", (string id, TreeReq r) => Results.Ok(Agent(id).GetTree(r.RootRef, r.Depth ?? 2, r.MaxChildren ?? 40)));
app.MapPost("/agents/{id}/capture/screen", (string id, ScreenReq? r) =>
{
    var fmt = r?.Format?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;
    var c = Agent(id).CaptureScreen(r?.Monitor, fmt, 80);
    return Results.Ok(new { c.Desktop, c.Rect, c.Monitor, c.DpiScale, c.Format, imageBase64 = Convert.ToBase64String(c.Bytes) });
});
app.MapPost("/agents/{id}/mouse/move", (string id, MoveReq r) => { Agent(id).MouseMove(r.X, r.Y); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/mouse/click", (string id, ClickReq r) => { Agent(id).MouseClick(r.Button ?? "left", r.X, r.Y, r.Count ?? 1); return Results.Ok(new { ok = true }); });
app.MapPost("/agents/{id}/keyboard/type", (string id, TypeReq r) => { Agent(id).TypeText(r.Text); return Results.Ok(new { ok = true }); });

Console.WriteLine();
Console.WriteLine("  Deskhand Fleet Server (loopback test build)");
Console.WriteLine($"      http://127.0.0.1:{port}   ·   agents dial ws://127.0.0.1:{port}/agent/connect");
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
record ScreenReq(int? Monitor, string? Format);
record MoveReq(int X, int Y);
record ClickReq(string? Button, int? X, int? Y, int? Count);
record TypeReq(string Text);
