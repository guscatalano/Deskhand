using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deskhand.Core;
using Deskhand.Core.Governance;
using Deskhand.Ui;

// Per-Monitor-v2 DPI awareness MUST be set before anything touches windows or pixels.
DpiHelper.EnablePerMonitorV2();

// Pin ContentRoot to the exe directory so wwwroot (the dashboard) is found no matter what the
// current working directory is when the exe is launched (shortcut, service, another folder).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

int port = int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_PORT"), out var p) ? p : 8791;

// Token is OPTIONAL. The browser dashboard never needs one (it is same-origin).
// If DESKHAND_TOKEN is set, non-browser clients (curl/scripts) must present it.
string? token = Environment.GetEnvironmentVariable("DESKHAND_TOKEN")?.Trim();
bool requireToken = !string.IsNullOrWhiteSpace(token);
var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    $"http://127.0.0.1:{port}", $"http://localhost:{port}",
};

// Loopback only. No external interface is ever bound.
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// Governance: kill switch + capability gates + audit, enforced at the backend seam.
var controlState = ControlState.FromEnvironment();
var auditLog = new AuditLog();
var captureNotifier = new ToastNotifier();
var macroRecorder = new Deskhand.Core.Macros.MacroRecorder();
var eventHub = new Deskhand.Core.Events.EventHub();
var localBackend = new LocalAutomationBackend();
localBackend.StartEvents(eventHub);                          // UIA events: focus_changed, window_opened
var processWatcher = new Deskhand.Core.Events.ProcessWatcher(eventHub);  // process_started / process_exited
builder.Services.AddSingleton(controlState);
builder.Services.AddSingleton(auditLog);
builder.Services.AddSingleton(captureNotifier);
builder.Services.AddSingleton(macroRecorder);
builder.Services.AddSingleton(eventHub);
builder.Services.AddSingleton(processWatcher);
builder.Services.AddSingleton<IAutomationBackend>(_ =>
    new GovernedBackend(localBackend, controlState, auditLog, captureNotifier, macroRecorder));

// Also serve MCP over Streamable HTTP at /mcp, sharing the SAME backend + governance + events, so
// the dashboard reflects and controls whatever an MCP client does through this one process.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(Deskhand.McpTools.DeskhandTools).Assembly);

var app = builder.Build();

// The dashboard: static files (index.html at "/") are served before auth runs.
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- security middleware: loopback Host + cross-site Origin block + optional token ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path is "/health" || path.StartsWith("/mcp", StringComparison.Ordinal))
    {
        await next(); // MCP transport handles its own protocol; loopback binding still applies
        return;
    }

    // Defense in depth against DNS-rebinding: require a loopback Host header.
    var host = ctx.Request.Host.Host;
    if (host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "Non-loopback Host header rejected.", type = "forbidden" });
        return;
    }

    // Block any cross-site caller (e.g. a malicious web page fetching localhost).
    var origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !allowedOrigins.Contains(origin))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "Cross-origin request rejected.", type = "forbidden" });
        return;
    }

    // Trust our own same-origin dashboard without a token.
    var secFetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
    bool trustedBrowser = (origin.Length > 0 && allowedOrigins.Contains(origin))
        || string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase);

    if (trustedBrowser || !requireToken)
    {
        await next();
        return;
    }

    // Non-browser client and a token is required.
    var auth = ctx.Request.Headers.Authorization.ToString();
    var provided = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : "";
    if (FixedEquals(provided, token!))
    {
        await next();
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsJsonAsync(new { error = "This non-browser client requires Authorization: Bearer <DESKHAND_TOKEN>.", type = "unauthorized" });
});

// ---- uniform error mapping ----
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        var (status, type) = ex switch
        {
            UnknownElementException or StaleElementException => (StatusCodes.Status404NotFound, "stale_element"),
            PatternNotSupportedException => (StatusCodes.Status409Conflict, "pattern_not_supported"),
            DesktopUnavailableException => (StatusCodes.Status409Conflict, "desktop_unavailable"),
            DisarmedException => (StatusCodes.Status403Forbidden, "disarmed"),
            CapabilityDisabledException => (StatusCodes.Status403Forbidden, "capability_disabled"),
            ArgumentException => (StatusCodes.Status400BadRequest, "bad_request"),
            _ => (StatusCodes.Status500InternalServerError, "internal"),
        };
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message, type });
        }
    }
});

var api = app;

// ---- governance / kill switch ----
api.MapGet("/control", (ControlState s, AuditLog a) =>
    Results.Ok(new { armed = s.Armed, inputEnabled = s.InputEnabled, captureEnabled = s.CaptureEnabled, notifyOnCapture = s.NotifyOnCapture, auditDir = a.Directory }));
api.MapPost("/control", (ControlState s, AuditLog a, ControlRequest r) =>
{
    if (r.Armed.HasValue) s.Armed = r.Armed.Value;
    if (r.InputEnabled.HasValue) s.InputEnabled = r.InputEnabled.Value;
    if (r.CaptureEnabled.HasValue) s.CaptureEnabled = r.CaptureEnabled.Value;
    if (r.NotifyOnCapture.HasValue) s.NotifyOnCapture = r.NotifyOnCapture.Value;
    a.Record("control", $"armed={s.Armed} input={s.InputEnabled} capture={s.CaptureEnabled} notify={s.NotifyOnCapture}", "set");
    return Results.Ok(new { armed = s.Armed, inputEnabled = s.InputEnabled, captureEnabled = s.CaptureEnabled, notifyOnCapture = s.NotifyOnCapture });
});

// ---- record & playback ----
api.MapPost("/macro/start", (Deskhand.Core.Macros.MacroRecorder rec, AuditLog a) =>
{
    rec.Start(); a.Record("macro_record", null, "start");
    return Results.Ok(new { recording = true });
});
api.MapPost("/macro/stop", (Deskhand.Core.Macros.MacroRecorder rec, AuditLog a) =>
{
    var macro = rec.Stop(); a.Record("macro_record", $"steps={macro.Steps.Count}", "stop");
    return Results.Ok(macro);
});
api.MapGet("/macro/status", (Deskhand.Core.Macros.MacroRecorder rec) =>
    Results.Ok(new { recording = rec.IsRecording, count = rec.CurrentCount, elapsedMs = rec.ElapsedMs, hasLast = rec.LastMacro is not null, lastCount = rec.LastMacro?.Steps.Count ?? 0 }));
api.MapPost("/macro/expect", (Deskhand.Core.Macros.MacroRecorder rec, MacroExpectRequest r) =>
{
    if (!rec.IsRecording) throw new ArgumentException("Not recording — start a recording before adding an expectation.");
    rec.RecordWait(new Deskhand.Core.Macros.ElementSelectorDto(r.Name, r.AutomationId, r.ControlType, r.ClassName), r.TimeoutMs ?? 5000);
    return Results.Ok(new { added = true, count = rec.CurrentCount });
});
api.MapPost("/macro/play", (IAutomationBackend b, Deskhand.Core.Macros.MacroRecorder rec, AuditLog a, MacroPlayRequest? r) =>
{
    var macro = r?.Macro ?? rec.LastMacro ?? throw new ArgumentException("No macro to play — record one first or supply a macro.");
    a.Record("macro_play", $"steps={macro.Steps.Count} speed={r?.Speed ?? 1}", "start");
    int played = Deskhand.Core.Macros.MacroPlayer.Play(macro, b, r?.Speed ?? 1.0, r?.MaxStepDelayMs ?? 3000);
    return Results.Ok(new { played });
});

// ---- UIA events (focus, window-open) ----
api.MapGet("/events/poll", (Deskhand.Core.Events.EventHub hub, long since) =>
    Results.Ok(new { lastId = hub.LastId, events = hub.Since(since) }));

// Block until a process starts/exits (event=start|exit), matched by name substring and/or pid.
api.MapPost("/process/wait", (Deskhand.Core.Events.ProcessWatcher w, ProcessWaitRequest r) =>
{
    var hit = w.WaitForProcess(r.Event ?? "start", r.Name, r.Pid, r.TimeoutMs ?? 30000);
    return hit is null
        ? Results.Json(new { error = "No matching process event within the timeout.", type = "wait_timeout" }, statusCode: 404)
        : Results.Ok(hit);
});
api.MapGet("/events", async (HttpContext ctx, Deskhand.Core.Events.EventHub hub) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var (reader, dispose) = hub.Subscribe();
    try
    {
        await foreach (var ev in reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"data: {Deskhand.Core.Fleet.FleetJson.Serialize(ev)}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { }
    finally { dispose(); }
});

// ---- health & orientation ----
api.MapGet("/health", () => Results.Ok(new { ok = true, service = "deskhand-http", version = "0.1" }));
api.MapGet("/machine", (IAutomationBackend b) => Results.Ok(b.GetMachineInfo()));
api.MapGet("/desktop/state", (IAutomationBackend b) => Results.Ok(b.GetDesktopState()));
api.MapGet("/foreground", (IAutomationBackend b) => Results.Ok(b.GetForegroundWindow()));
api.MapGet("/focused", (IAutomationBackend b) => Results.Ok(b.GetFocusedElement()));
api.MapGet("/windows", (IAutomationBackend b) => Results.Ok(b.GetTopLevelWindows()));
api.MapGet("/processes", (IAutomationBackend b) => Results.Ok(b.GetProcesses()));
api.MapPost("/process/launch", (IAutomationBackend b, LaunchRequest r) =>
    Results.Ok(b.LaunchProcess(r.Path, r.Args, r.WorkingDir, r.WaitForWindowMs ?? 4000)));

// ---- uia read ----
api.MapPost("/uia/tree", (IAutomationBackend b, TreeRequest r) =>
    Results.Ok(b.GetTree(r.RootRef, r.Depth ?? 3, r.MaxChildren ?? 40)));

api.MapPost("/uia/find", (IAutomationBackend b, FindRequest r) =>
    Results.Ok(b.Find(r.RootRef, new FindQuery(r.Name, r.AutomationId, r.ControlType, r.ClassName, r.Scope ?? "descendants", r.Max ?? 100))));

api.MapPost("/uia/wait", (IAutomationBackend b, WaitRequest r) =>
{
    var q = new FindQuery(r.Name, r.AutomationId, r.ControlType, r.ClassName, r.Scope ?? "descendants", 1);
    var found = b.WaitForElement(r.RootRef, q, r.TimeoutMs ?? 5000);
    return found is null
        ? Results.Json(new { error = "No matching element appeared within the timeout.", type = "wait_timeout" }, statusCode: 404)
        : Results.Ok(found);
});

api.MapGet("/uia/element/{reference}", (IAutomationBackend b, string reference) =>
    Results.Ok(b.GetElement(reference)));

api.MapGet("/uia/element/{reference}/properties", (IAutomationBackend b, string reference) =>
    Results.Ok(b.GetAllProperties(reference)));

api.MapPost("/uia/element-from-point", (IAutomationBackend b, PointRequest r) =>
    Results.Ok(b.GetElementFromPoint(r.X, r.Y)));

// ---- uia act ----
api.MapPost("/uia/invoke", (IAutomationBackend b, RefRequest r) => { b.Invoke(r.Reference); return Ok(); });
api.MapPost("/uia/set-value", (IAutomationBackend b, SetValueRequest r) => { b.SetValue(r.Reference, r.Text); return Ok(); });
api.MapPost("/uia/toggle", (IAutomationBackend b, RefRequest r) => { b.Toggle(r.Reference); return Ok(); });
api.MapPost("/uia/expand-collapse", (IAutomationBackend b, ExpandRequest r) => { b.ExpandCollapse(r.Reference, r.Expand); return Ok(); });
api.MapPost("/uia/select", (IAutomationBackend b, RefRequest r) => { b.Select(r.Reference); return Ok(); });
api.MapPost("/uia/set-focus", (IAutomationBackend b, RefRequest r) => { b.SetFocus(r.Reference); return Ok(); });

// ---- capture ----
api.MapPost("/capture/screen", (IAutomationBackend b, HttpContext ctx, ScreenCaptureRequest? r) =>
    WriteCapture(ctx, b.CaptureScreen(r?.Monitor, ParseFormat(r?.Format), r?.Quality ?? 80)));

api.MapPost("/capture/region", (IAutomationBackend b, HttpContext ctx, RegionRequest r) =>
    WriteCapture(ctx, b.CaptureRegion(r.X, r.Y, r.Width, r.Height, ParseFormat(r.Format), r.Quality ?? 80)));

api.MapPost("/capture/window", (IAutomationBackend b, HttpContext ctx, WindowCaptureRequest r) =>
{
    var fmt = ParseFormat(r.Format);
    int q = r.Quality ?? 80;
    var result = r.Reference is not null ? b.CaptureWindowByRef(r.Reference, fmt, q)
               : r.Hwnd is not null ? b.CaptureWindow(r.Hwnd.Value, fmt, q)
               : throw new ArgumentException("Provide either 'reference' or 'hwnd'.");
    return WriteCapture(ctx, result);
});

api.MapPost("/capture/element", (IAutomationBackend b, HttpContext ctx, ElementCaptureRequest r) =>
    WriteCapture(ctx, b.CaptureElement(r.Reference, ParseFormat(r.Format), r.Quality ?? 80)));

// Phase 2: capture the current input desktop (secure desktop when run as SYSTEM).
api.MapPost("/capture/input-desktop", (IAutomationBackend b, InputDesktopRequest? r) =>
{
    var res = b.CaptureInputDesktop(ParseFormat(r?.Format), r?.Quality ?? 80);
    return Results.Ok(new
    {
        success = res.Success,
        desktopName = res.DesktopName,
        kind = res.Kind,
        note = res.Note,
        capture = res.Capture is null ? null : new CaptureJson(
            res.Capture.Desktop, res.Capture.Rect, res.Capture.Monitor,
            res.Capture.DpiScale, res.Capture.Format, Convert.ToBase64String(res.Capture.Bytes)),
    });
});

// ---- input: mouse ----
api.MapPost("/mouse/move", (IAutomationBackend b, MouseMoveRequest r) => { b.MouseMove(r.X, r.Y); return Ok(); });
api.MapPost("/mouse/click", (IAutomationBackend b, MouseClickRequest r) => { b.MouseClick(r.Button ?? "left", r.X, r.Y, r.Count ?? 1); return Ok(); });
api.MapPost("/mouse/down", (IAutomationBackend b, MouseButtonRequest r) => { b.MouseDown(r.Button ?? "left", r.X, r.Y); return Ok(); });
api.MapPost("/mouse/up", (IAutomationBackend b, MouseButtonRequest r) => { b.MouseUp(r.Button ?? "left", r.X, r.Y); return Ok(); });
api.MapPost("/mouse/scroll", (IAutomationBackend b, ScrollRequest r) => { b.MouseScroll(r.Dx, r.Dy); return Ok(); });

// ---- input: keyboard ----
api.MapPost("/keyboard/type", (IAutomationBackend b, TypeRequest r) => { b.TypeText(r.Text); return Ok(); });
api.MapPost("/keyboard/keys", (IAutomationBackend b, KeysRequest r) => { b.SendKeys(r.Chord); return Ok(); });

// MCP over Streamable HTTP — same server, same state as the dashboard.
app.MapMcp("/mcp");

Console.WriteLine();
Console.WriteLine("  Deskhand — one server, two faces:");
Console.WriteLine();
Console.WriteLine($"      dashboard   http://127.0.0.1:{port}");
Console.WriteLine($"      MCP (HTTP)  http://127.0.0.1:{port}/mcp");
Console.WriteLine();
Console.WriteLine(requireToken
    ? "  DESKHAND_TOKEN is set: scripts/curl must send 'Authorization: Bearer <token>'. The web UI does not."
    : "  No token needed (loopback only). Set DESKHAND_TOKEN to require one for non-browser clients.");
Console.WriteLine($"  Audit log: {auditLog.Directory}");
Console.WriteLine("  Kill switch: Ctrl+Alt+Pause toggles armed/disarmed.");
Console.WriteLine();

using var killSwitch = new KillSwitch(controlState, auditLog);

app.Run();
return;

// ---- helpers ----

static IResult Ok() => Results.Ok(new { ok = true });

static ImageFormat ParseFormat(string? f) =>
    f?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;

// Return raw image bytes when the client asks (?raw=true or Accept: image/*); otherwise JSON+base64.
static IResult WriteCapture(HttpContext ctx, CaptureResultDto c)
{
    bool wantsRaw = string.Equals(ctx.Request.Query["raw"], "true", StringComparison.OrdinalIgnoreCase)
                    || ctx.Request.Headers.Accept.ToString().Contains("image/", StringComparison.OrdinalIgnoreCase);
    string contentType = c.Format == "jpeg" ? "image/jpeg" : "image/png";
    if (wantsRaw) return Results.Bytes(c.Bytes, contentType);

    return Results.Ok(new CaptureJson(
        c.Desktop, c.Rect, c.Monitor, c.DpiScale, c.Format, Convert.ToBase64String(c.Bytes)));
}

static bool FixedEquals(string a, string b)
{
    var ba = System.Text.Encoding.UTF8.GetBytes(a);
    var bb = System.Text.Encoding.UTF8.GetBytes(b);
    return CryptographicOperations.FixedTimeEquals(
        System.Security.Cryptography.SHA256.HashData(ba),
        System.Security.Cryptography.SHA256.HashData(bb));
}

// ---- request/response DTOs ----
record TreeRequest(string? RootRef, int? Depth, int? MaxChildren);
record FindRequest(string? RootRef, string? Name, string? AutomationId, string? ControlType, string? ClassName, string? Scope, int? Max);
record WaitRequest(string? RootRef, string? Name, string? AutomationId, string? ControlType, string? ClassName, string? Scope, int? TimeoutMs);
record LaunchRequest(string Path, string? Args, string? WorkingDir, int? WaitForWindowMs);
record RefRequest(string Reference);
record PointRequest(int X, int Y);
record ProcessWaitRequest(string? Event, string? Name, int? Pid, int? TimeoutMs);
record SetValueRequest(string Reference, string Text);
record ExpandRequest(string Reference, bool Expand);
record ScreenCaptureRequest(int? Monitor, string? Format, int? Quality);
record RegionRequest(int X, int Y, int Width, int Height, string? Format, int? Quality);
record WindowCaptureRequest(long? Hwnd, string? Reference, string? Format, int? Quality);
record ElementCaptureRequest(string Reference, string? Format, int? Quality);
record InputDesktopRequest(string? Format, int? Quality);
record ControlRequest(bool? Armed, bool? InputEnabled, bool? CaptureEnabled, bool? NotifyOnCapture);
record MacroPlayRequest(Deskhand.Core.Macros.Macro? Macro, double? Speed, int? MaxStepDelayMs);
record MacroExpectRequest(string? Name, string? AutomationId, string? ControlType, string? ClassName, int? TimeoutMs);
record MouseMoveRequest(int X, int Y);
record MouseClickRequest(string? Button, int? X, int? Y, int? Count);
record MouseButtonRequest(string? Button, int? X, int? Y);
record ScrollRequest(int Dx, int Dy);
record TypeRequest(string Text);
record KeysRequest(string Chord);
record CaptureJson(string Desktop, RectDto Rect, int Monitor, double DpiScale, string Format, string ImageBase64);
