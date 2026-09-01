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

// Allow running as a Windows Service (integrates with the SCM). No-op when launched normally, so the same exe
// still runs as a console app / dashboard. Install with installer/install-service.ps1.
builder.Host.UseWindowsService(o => o.ServiceName = "Deskhand");

int port = int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_PORT"), out var p) ? p : 8791;

// Token is OPTIONAL. The browser dashboard never needs one (it is same-origin).
// If DESKHAND_TOKEN is set, non-browser clients (curl/scripts) must present it.
string? token = Environment.GetEnvironmentVariable("DESKHAND_TOKEN")?.Trim();
bool requireToken = !string.IsNullOrWhiteSpace(token);

// Optional HTTPS: DESKHAND_TLS_CERT=<pfx> (+ DESKHAND_TLS_PASSWORD) or DESKHAND_TLS=self-signed.
var tlsCert = Deskhand.Core.TlsSupport.FromEnvironment("DESKHAND_");
bool tls = tlsCert is not null;
string scheme = tls ? "https" : "http";
var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    $"{scheme}://127.0.0.1:{port}", $"{scheme}://localhost:{port}",
};

// Loopback by default. DESKHAND_BIND opens the port to the network on demand ("sometimes"):
//   (unset)            -> loopback only (127.0.0.1); the browser dashboard needs no token
//   any | 0.0.0.0 | *  -> all interfaces
//   <ip>               -> that specific local address
// Binding to a non-loopback address REQUIRES a DESKHAND_TOKEN — otherwise anyone on the network
// gets full desktop control with no auth. We refuse to start otherwise.
string? bind = Environment.GetEnvironmentVariable("DESKHAND_BIND")?.Trim();
bool external = !string.IsNullOrWhiteSpace(bind)
    && bind is not ("127.0.0.1" or "localhost" or "::1" or "[::1]");
if (external && !requireToken)
{
    Console.Error.WriteLine(
        "REFUSING TO START: DESKHAND_BIND opens a non-loopback port, but DESKHAND_TOKEN is not set.\n" +
        "  Anyone on the network would get full desktop control with no authentication.\n" +
        "  Fix: set DESKHAND_TOKEN to a strong secret, or unset DESKHAND_BIND to stay loopback-only.");
    Environment.Exit(3);
}
builder.WebHost.ConfigureKestrel(k =>
{
    void Https(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions o) { if (tls) o.UseHttps(tlsCert!); }
    if (!external) { k.ListenLocalhost(port, Https); return; }
    if (bind is "any" or "0.0.0.0" or "*") k.ListenAnyIP(port, Https);
    else if (System.Net.IPAddress.TryParse(bind, out var ip)) k.Listen(new System.Net.IPEndPoint(ip, port), Https);
    else { Console.Error.WriteLine($"Invalid DESKHAND_BIND '{bind}'; using all interfaces."); k.ListenAnyIP(port, Https); }
});
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
var screenRecorder = new Deskhand.Core.Services.ScreenRecorder(auditLog);
var processDumper = new Deskhand.Core.Services.ProcessDumper(auditLog);
var screenshotStore = new Deskhand.Core.Services.ScreenshotStore(auditLog);
// Records the USER's physical input; resolves each click's element via the raw backend (unaudited,
// so per-click resolution doesn't flood the audit log). While it runs, a persistent on-screen banner
// (recordingIndicator) + a toast make sure the user knows they're being observed.
var recordingIndicator = new Deskhand.Ui.RecordingIndicator();
var inputRecorder = new Deskhand.Core.Services.InputRecorder(
    (x, y) => { try { return localBackend.GetElementFromPoint(x, y); } catch { return null; } },
    captureNotifier, recordingIndicator);
builder.Services.AddSingleton(controlState);
builder.Services.AddSingleton(auditLog);
builder.Services.AddSingleton(captureNotifier);
builder.Services.AddSingleton(macroRecorder);
builder.Services.AddSingleton(eventHub);
builder.Services.AddSingleton(processWatcher);
builder.Services.AddSingleton(screenRecorder);
builder.Services.AddSingleton(processDumper);
builder.Services.AddSingleton(screenshotStore);
builder.Services.AddSingleton(inputRecorder);
builder.Services.AddSingleton(new Deskhand.Core.Services.WebhookService());
builder.Services.AddHostedService<WebhookForwarder>();
builder.Services.AddSingleton<IAutomationBackend>(_ =>
    new GovernedBackend(localBackend, controlState, auditLog, captureNotifier, macroRecorder));

// Also serve MCP over Streamable HTTP at /mcp, sharing the SAME backend + governance + events, so
// the dashboard reflects and controls whatever an MCP client does through this one process.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(Deskhand.McpTools.DeskhandTools).Assembly);

// OpenAPI: a machine-readable description of the HTTP surface at /swagger/v1/swagger.json, with an
// interactive Swagger UI at /swagger. Loopback browsers reach it without a token (same-origin).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
{
    Title = "Deskhand HTTP API",
    Version = Deskhand.Core.BuildInfo.Version,
    Description = "Local Windows desktop-automation server: UI Automation, capture, input, OCR, clipboard, "
                + "windows, files, shell, firewall, and machine info. Many actions require the kill switch armed.",
}));

var app = builder.Build();

// Kick off a one-shot update check in the background so the dashboard can show an "update available" banner
// (served fast from the cache at /update/status). Never blocks startup; failures are swallowed.
_ = Deskhand.Core.Services.UpdateService.CheckAsync();

app.UseSwagger();
app.UseSwaggerUI(o => { o.SwaggerEndpoint("/swagger/v1/swagger.json", "Deskhand v1"); o.DocumentTitle = "Deskhand API"; });

// The dashboard: static files (index.html at "/") are served before auth runs.
app.UseDefaultFiles();
// The dashboard is a single-file app updated in place; tell browsers not to cache it, so a plain
// refresh always shows the latest UI (no more stale-dashboard confusion / forced Ctrl+F5).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var p = ctx.File.Name;
        if (p.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    }
});

// ---- security middleware: loopback Host + cross-site Origin block + token ----
// Loopback bind: same-origin browser is trusted without a token (you're already on the box);
//   a token, if set, is only demanded of non-browser clients. (Original behavior — unchanged.)
// External bind (DESKHAND_BIND): a token is mandatory for EVERY client, browser included —
//   Sec-Fetch-Site can be forged off-loopback, so it grants no trust; the dashboard sends the
//   token as a Bearer header (it reads it from ?token= on first load).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path is "/health" or "/metrics") { await next(); return; }   // liveness + Prometheus scrape: no token
    if (path.StartsWith("/mcp", StringComparison.Ordinal))
    {
        // MCP has no same-origin browser; when the port is exposed it must carry the token.
        if (external && !FixedEquals(BearerOf(ctx), token!))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "MCP over an exposed port requires Authorization: Bearer <DESKHAND_TOKEN>.", type = "unauthorized" });
            return;
        }
        await next(); // MCP transport handles its own protocol
        return;
    }

    if (!external)
    {
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
        if (trustedBrowser || !requireToken) { await next(); return; }
    }

    // Token required: non-browser clients on loopback, and EVERY client when externally bound.
    if (requireToken && FixedEquals(BearerOf(ctx), token!)) { await next(); return; }

    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsJsonAsync(new { error = "This client requires Authorization: Bearer <DESKHAND_TOKEN>.", type = "unauthorized" });
});

static string BearerOf(HttpContext ctx)
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth["Bearer ".Length..].Trim();
    // Fallback for callers that cannot set headers (EventSource /events, an <img src=...>): ?token=.
    return ctx.Request.Query["token"].ToString().Trim();
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
            BackendTimeoutException => (StatusCodes.Status503ServiceUnavailable, "backend_timeout"),
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

// ---- screen recording (GIF / MJPEG-AVI) with a hard max-duration auto-stop ----
api.MapPost("/record/start", (Deskhand.Core.Services.ScreenRecorder rec, ControlState st, AuditLog al, ToastNotifier tn, RecordStartRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    if (!st.CaptureEnabled) return Results.Json(new { error = "capture disabled", type = "capability_disabled" }, statusCode: 403);
    var opt = new Deskhand.Core.Services.RecordingOptions(r.Monitor, r.Format ?? "gif", r.Fps ?? 10, r.Scale ?? 100, r.Quality ?? 75, r.MaxDurationMs ?? 30000);
    var s = rec.Start(opt);
    al.Record("record_start", $"{s.Format} mon={r.Monitor?.ToString() ?? "all"} {s.Width}x{s.Height} fps={s.Fps} max={s.MaxDurationMs}ms", s.Id);
    if (st.NotifyOnCapture) { try { tn.Notify($"Deskhand is recording the screen · {s.Format} · {s.Width}×{s.Height}"); } catch { } }
    return Results.Ok(s);
});
api.MapPost("/record/stop", (Deskhand.Core.Services.ScreenRecorder rec, AuditLog al, RefRequest r) =>
{ var s = rec.Stop(r.Reference); al.Record("record_stop", $"{s.State} frames={s.Frames} {s.SizeBytes}B", r.Reference); return Results.Ok(s); });
api.MapGet("/record/status/{id}", (Deskhand.Core.Services.ScreenRecorder rec, string id) => Results.Ok(rec.GetStatus(id)));
api.MapGet("/record/list", (Deskhand.Core.Services.ScreenRecorder rec) => Results.Ok(rec.List()));
api.MapGet("/recordings/{id}", (Deskhand.Core.Services.ScreenRecorder rec, string id) =>
{ var (bytes, mime, name) = rec.Read(id); return Results.File(bytes, mime, name); });

// ---- record the USER's own mouse/keyboard, noting the element each click hit ----
api.MapPost("/input/record/start", (Deskhand.Core.Services.InputRecorder ir, AuditLog al, InputRecordRequest? r) =>
{ var s = ir.Start(r?.CaptureText ?? true); al.Record("user_input_record_start", $"captureText={r?.CaptureText ?? true}", "recording"); return Results.Ok(s); });
api.MapPost("/input/record/stop", (Deskhand.Core.Services.InputRecorder ir, AuditLog al) =>
{ var s = ir.Stop(); al.Record("user_input_record_stop", null, "stopped"); return Results.Ok(new { status = s, events = ir.Since(0) }); });
api.MapGet("/input/record/status", (Deskhand.Core.Services.InputRecorder ir) => Results.Ok(ir.Status()));
api.MapGet("/input/record/events", (Deskhand.Core.Services.InputRecorder ir, long since) =>
    Results.Ok(new { lastId = ir.LastId, events = ir.Since(since) }));

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
api.MapGet("/health", () => Results.Ok(new { ok = true, service = "deskhand-http", version = Deskhand.Core.BuildInfo.Version, requiresToken = requireToken, tls }));
api.MapGet("/machine", (IAutomationBackend b) => Results.Ok(b.GetMachineInfo()));
api.MapGet("/desktop/state", (IAutomationBackend b) => Results.Ok(b.GetDesktopState()));
api.MapGet("/foreground", (IAutomationBackend b) => Results.Ok(b.GetForegroundWindow()));
api.MapGet("/focused", (IAutomationBackend b) => Results.Ok(b.GetFocusedElement()));
api.MapGet("/windows", (IAutomationBackend b) => Results.Ok(b.GetTopLevelWindows()));
api.MapGet("/processes", (IAutomationBackend b) => Results.Ok(b.GetProcesses()));

// Full-memory process dump (MiniDumpWriteDump). Gated on the kill switch; audited; auto-deleted after 24h.
api.MapPost("/process/dump", (Deskhand.Core.Services.ProcessDumper d, ControlState st, PidRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var dmp = d.Dump(r.Pid);   // dumps are huge, so they always save on the box; hand back the download URL.
    return Results.Ok(new { dmp.ProcessId, dmp.Name, dmp.File, dmp.FileName, dmp.SizeBytes, dmp.Ts, dmp.DurationMs, url = $"/dumps/{dmp.FileName}" });
});
api.MapGet("/dumps", (Deskhand.Core.Services.ProcessDumper d) => Results.Ok(d.List()));

// The access token, for the dashboard's Connect page to show + bake into client configs. This route goes
// through the auth middleware, so it only returns the token to a trusted same-origin loopback browser or a
// caller that already presents it — it is NOT public like /health (which reports only the boolean).
api.MapGet("/token", () => Results.Ok(new { token }));

// About this machine (read-only): Windows version/buildlab, uptime, CPU, memory, disks, network, firewall.
api.MapGet("/system", () => Results.Ok(Deskhand.Core.Services.SystemInfoService.Get()));

// Hardware / software inventory (read-only, via WMI): physical disks+partitions+volumes, installed Windows
// updates (KBs), PnP devices (optional ?class=Net|Display|Media…), drivers, audio devices.
api.MapGet("/hardware/disks", () => Results.Ok(Deskhand.Core.Services.HardwareInfoService.Disks()));
api.MapGet("/hardware/updates", () => Results.Ok(Deskhand.Core.Services.HardwareInfoService.WindowsUpdates()));
api.MapGet("/hardware/devices", (string? @class) => Results.Ok(Deskhand.Core.Services.HardwareInfoService.Devices(@class)));
api.MapGet("/hardware/drivers", () => Results.Ok(Deskhand.Core.Services.HardwareInfoService.Drivers()));
api.MapGet("/hardware/audio", () => Results.Ok(Deskhand.Core.Services.HardwareInfoService.Audio()));
// Detailed hardware: computer model, BIOS, motherboard, GPUs, monitors, RAM sticks.
api.MapGet("/hardware/detail", () => Results.Ok(Deskhand.Core.Services.HardwareInfoService.Detail()));
// Logon sessions (WTS): console + RDP sessions, their state, user, client.
api.MapGet("/sessions", () => Results.Ok(Deskhand.Core.Services.SessionsService.List()));
// Default audio endpoints (Core Audio): playback + recording device, volume %, mute.
api.MapGet("/audio/default", () => Results.Ok(Deskhand.Core.Services.AudioService.Defaults()));

// Software / configuration inventory (read-only).
api.MapGet("/software/programs", () => Results.Ok(Deskhand.Core.Services.SoftwareService.InstalledPrograms()));
api.MapGet("/software/services", () => Results.Ok(Deskhand.Core.Services.SoftwareService.Services()));
api.MapGet("/software/startup", () => Results.Ok(Deskhand.Core.Services.SoftwareService.StartupItems()));
api.MapGet("/software/env", () => Results.Ok(Deskhand.Core.Services.SoftwareService.EnvironmentVariables()));
api.MapGet("/software/printers", () => Results.Ok(Deskhand.Core.Services.SoftwareService.Printers()));
api.MapGet("/software/shares", () => Results.Ok(Deskhand.Core.Services.SoftwareService.Shares()));
api.MapGet("/software/tasks", () => Results.Ok(Deskhand.Core.Services.SoftwareService.ScheduledTasks()));

// Security posture (read-only): TPM, Secure Boot, BitLocker, activation, Defender/AV, pending reboot.
api.MapGet("/security", () => Results.Ok(Deskhand.Core.Services.SecurityService.Get()));

// Local users & groups, power/battery, network connections, diagnostics (read-only).
api.MapGet("/users", () => Results.Ok(Deskhand.Core.Services.UsersService.Users()));
api.MapGet("/groups", () => Results.Ok(Deskhand.Core.Services.UsersService.Groups()));
api.MapGet("/power", () => Results.Ok(Deskhand.Core.Services.PowerService.Get()));
api.MapGet("/net/connections", () => Results.Ok(Deskhand.Core.Services.NetConnectionsService.List()));
api.MapGet("/diagnostics/events", (int? count) => Results.Ok(Deskhand.Core.Services.DiagnosticsService.RecentErrors(count ?? 50)));
api.MapGet("/diagnostics/disk-health", () => Results.Ok(Deskhand.Core.Services.DiagnosticsService.DiskHealth()));

// Read-only file browser. path = "" (drive roots) | a folder like "C:\Users". Open a file by handing
// its path to /process/launch (shell-execute), which also opens documents and URLs.
api.MapGet("/fs", (string? path) => Results.Ok(Deskhand.Core.Services.FileSystemService.Browse(path)));

// Download a single file (stream). SENSITIVE (reads real file bytes) — gated on armed + audited.
api.MapGet("/fs/download", (ControlState st, AuditLog al, string? path) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var (full, err) = Deskhand.Core.Services.FileSystemService.ResolveForDownload(path);
    if (full is null) return Results.Json(new { error = err, type = "bad_request" }, statusCode: 400);
    al.Record("file_download", full, new FileInfo(full).Length + "B");
    return Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
});

// Download multiple files as a zip. Body: { paths: ["C:\\a.txt", ...] }. Gated + audited.
api.MapPost("/fs/download-zip", (ControlState st, AuditLog al, FsPathsRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var paths = r.Paths ?? Array.Empty<string>();
    if (paths.Count == 0) return Results.Json(new { error = "no paths", type = "bad_request" }, statusCode: 400);
    using var ms = new MemoryStream();
    long total = 0; int added = 0;
    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var (full, _) = Deskhand.Core.Services.FileSystemService.ResolveForDownload(p);
            if (full is null) continue;
            var entryName = Path.GetFileName(full);
            for (int i = 1; !used.Add(entryName); i++)   // de-dupe same-named files from different folders
                entryName = $"{Path.GetFileNameWithoutExtension(full)} ({i}){Path.GetExtension(full)}";
            try
            {
                var e = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Fastest);
                using var es = e.Open();
                using var fs = File.OpenRead(full);
                fs.CopyTo(es); total += fs.Length; added++;
            }
            catch { /* skip a file we can't read; the rest still zip */ }
        }
    }
    if (added == 0) return Results.Json(new { error = "none of the paths were readable files", type = "bad_request" }, statusCode: 400);
    al.Record("file_download_zip", $"{added} files", total + "B");
    return Results.File(ms.ToArray(), "application/zip", $"deskhand-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
});

// Upload one or more files into a target directory (multipart/form-data: field "dir" + one or more "files").
// SENSITIVE (writes real files) — gated on armed + audited.
api.MapPost("/fs/upload", async (ControlState st, AuditLog al, HttpRequest req) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    if (!req.HasFormContentType) return Results.Json(new { error = "expected multipart/form-data", type = "bad_request" }, statusCode: 400);
    var form = await req.ReadFormAsync();
    var dir = form["dir"].ToString().Trim().Trim('"');
    if (dir.Length == 0) return Results.Json(new { error = "no target dir", type = "bad_request" }, statusCode: 400);
    string full;
    try { full = Path.GetFullPath(dir); Directory.CreateDirectory(full); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message, type = "bad_request" }, statusCode: 400); }
    var written = new List<object>();
    foreach (var f in form.Files)
    {
        var target = Path.Combine(full, Path.GetFileName(f.FileName));
        try
        {
            await using var outStream = File.Create(target);
            await f.CopyToAsync(outStream);
            written.Add(new { path = target, size = f.Length });
        }
        catch (Exception ex) { written.Add(new { path = target, error = ex.Message }); }
    }
    al.Record("file_upload", full, $"{form.Files.Count} files");
    return Results.Ok(new { dir = full, written });
});

// File-manager mutations. DESTRUCTIVE: all gated on armed + audited. Delete goes to the Recycle Bin
// unless permanent=true.
static IResult FsOp(ControlState st, AuditLog al, string auditAction, Func<Deskhand.Core.Services.FsOpResultDto> op)
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = op();
    if (res.Ok) al.Record(auditAction, res.Dest is null ? res.Path : $"{res.Path} -> {res.Dest}", res.Detail ?? "");
    return res.Ok ? Results.Ok(res) : Results.Json(res, statusCode: 400);
}
api.MapPost("/fs/delete", (ControlState st, AuditLog al, FsDeleteRequest r) =>
    FsOp(st, al, "file_delete", () => Deskhand.Core.Services.FileSystemService.Delete(r.Path, r.Permanent ?? false)));
api.MapPost("/fs/rename", (ControlState st, AuditLog al, FsRenameRequest r) =>
    FsOp(st, al, "file_rename", () => Deskhand.Core.Services.FileSystemService.Rename(r.Path, r.NewName)));
api.MapPost("/fs/move", (ControlState st, AuditLog al, FsMoveRequest r) =>
    FsOp(st, al, "file_move", () => Deskhand.Core.Services.FileSystemService.Move(r.Source, r.Dest, r.Overwrite ?? false)));
api.MapPost("/fs/copy", (ControlState st, AuditLog al, FsCopyRequest r) =>
    FsOp(st, al, "file_copy", () => Deskhand.Core.Services.FileSystemService.Copy(r.Source, r.Dest, r.Overwrite ?? false)));
api.MapPost("/fs/zip", (ControlState st, AuditLog al, FsZipRequest r) =>
    FsOp(st, al, "file_zip", () => Deskhand.Core.Services.FileSystemService.Zip(r.Sources, r.Dest, r.Overwrite ?? false)));
api.MapPost("/fs/unzip", (ControlState st, AuditLog al, FsUnzipRequest r) =>
    FsOp(st, al, "file_unzip", () => Deskhand.Core.Services.FileSystemService.Unzip(r.ZipPath, r.Dest, r.Overwrite ?? false)));

// One-shot shell: run a command in PowerShell/cmd and return its output. MOST POWERFUL capability
// (arbitrary code as the current user) — OFF unless DESKHAND_ENABLE_SHELL is set, gated on armed, audited.
api.MapPost("/shell/run", (ControlState st, AuditLog al, ShellRunRequest r) =>
{
    if (!Deskhand.Core.Services.ShellService.Enabled)
        return Results.Json(new { error = "Shell is disabled. Set DESKHAND_ENABLE_SHELL=1.", type = "shell_disabled" }, statusCode: 403);
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = Deskhand.Core.Services.ShellService.Run(r.Shell, r.Command, r.Cwd, r.TimeoutMs);
    al.Record("shell_run", $"{res.Shell}: {Trunc(res.Command, 160)}", res.TimedOut ? "TIMEOUT" : $"exit {res.ExitCode} in {res.DurationMs}ms");
    return Results.Ok(res);
});

// Launch a process into a specific SESSION, on a specific DESKTOP, as a specific USER (CreateProcessAsUser).
// Crossing a session/user boundary needs the host running as LocalSystem; same-session desktop switch does not.
// OFF unless DESKHAND_ENABLE_SESSION_LAUNCH is set, gated on armed, audited (never the password).
api.MapPost("/process/launch-as", (ControlState st, AuditLog al, SessionLaunchRequest r) =>
{
    if (!Deskhand.Core.Services.SessionLaunchService.Enabled)
        return Results.Json(new { error = "Session launch is disabled. Set DESKHAND_ENABLE_SESSION_LAUNCH=1.", type = "session_launch_disabled" }, statusCode: 403);
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var asUser = Deskhand.Core.Services.SessionLaunchService.ParseAs(r.As);
    var res = Deskhand.Core.Services.SessionLaunchService.Launch(
        r.Path, r.Args, r.WorkingDir, r.SessionId, r.Desktop, asUser, r.User, r.Domain, r.Password, r.NoWindow ?? false);
    al.Record("launch_as", $"{Trunc(r.Path, 120)} | session={res.SessionId} desktop={res.Desktop} as={res.As} user={res.User}",
        res.Ok ? $"pid {res.ProcessId}" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});

// Windows Firewall. Listing is read-only. Opening/closing ports adds/removes rules TAGGED as Deskhand-managed,
// and close only ever removes Deskhand's own rules — never a pre-existing one. Write ops need Administrator and
// are OFF unless DESKHAND_ENABLE_FIREWALL_ADMIN is set, gated on armed, audited.
api.MapGet("/firewall/rules", (string? direction, int? port, bool? enabledOnly, string? contains, bool? managedOnly, int? max) =>
    Results.Ok(Deskhand.Core.Services.FirewallService.List(direction, port, enabledOnly, contains, managedOnly ?? false, max ?? 200)));
api.MapGet("/firewall/managed", () => Results.Ok(Deskhand.Core.Services.FirewallService.ListManaged()));
api.MapPost("/firewall/open", (ControlState st, AuditLog al, FirewallOpenRequest r) =>
{
    if (!Deskhand.Core.Services.FirewallService.AdminEnabled)
        return Results.Json(new { error = "Firewall admin is disabled. Set DESKHAND_ENABLE_FIREWALL_ADMIN=1.", type = "firewall_admin_disabled" }, statusCode: 403);
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = Deskhand.Core.Services.FirewallService.OpenPort(r.Port, r.Protocol, r.Direction, r.RemoteAddresses, r.Name);
    al.Record("firewall_open", $"{res.Protocol} {res.Port} ({res.Direction})", res.Ok ? $"added '{res.RuleName}'" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});
api.MapPost("/firewall/close", (ControlState st, AuditLog al, FirewallCloseRequest r) =>
{
    if (!Deskhand.Core.Services.FirewallService.AdminEnabled)
        return Results.Json(new { error = "Firewall admin is disabled. Set DESKHAND_ENABLE_FIREWALL_ADMIN=1.", type = "firewall_admin_disabled" }, statusCode: 403);
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = r.All == true
        ? Deskhand.Core.Services.FirewallService.CloseAllManaged()
        : Deskhand.Core.Services.FirewallService.ClosePort(r.Port, r.Protocol, r.Direction);
    al.Record("firewall_close", r.All == true ? "all managed" : $"{res.Protocol} {res.Port} ({res.Direction})",
        res.Ok ? $"removed {res.Removed}" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});

// Clipboard (Unicode text). Read/write is gated on armed and audited (it can carry secrets).
api.MapGet("/clipboard", (ControlState st) =>
    st.Armed ? Results.Ok(Deskhand.Core.Services.ClipboardService.GetText())
             : Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403));
api.MapPost("/clipboard", (ControlState st, AuditLog al, ClipboardSetRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = Deskhand.Core.Services.ClipboardService.SetText(r.Text);
    al.Record("clipboard_set", $"{res.Length} chars", res.Ok ? "ok" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});
api.MapPost("/clipboard/clear", (ControlState st, AuditLog al) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = Deskhand.Core.Services.ClipboardService.Clear();
    al.Record("clipboard_clear", "", res.Ok ? "ok" : $"FAIL {res.Error}");
    return Results.Ok(res);
});

// Window management by native handle (from /windows). Mutating, so gated on armed + audited.
api.MapPost("/window", (ControlState st, AuditLog al, WindowActionRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = (r.Action ?? "").Trim().ToLowerInvariant() switch
    {
        "activate" or "focus" => Deskhand.Core.Services.WindowService.Activate(r.Hwnd),
        "minimize" => Deskhand.Core.Services.WindowService.Minimize(r.Hwnd),
        "maximize" => Deskhand.Core.Services.WindowService.Maximize(r.Hwnd),
        "restore" => Deskhand.Core.Services.WindowService.Restore(r.Hwnd),
        "close" => Deskhand.Core.Services.WindowService.Close(r.Hwnd),
        "move" => Deskhand.Core.Services.WindowService.Move(r.Hwnd, r.X ?? 0, r.Y ?? 0),
        "resize" => Deskhand.Core.Services.WindowService.Resize(r.Hwnd, r.Width ?? 0, r.Height ?? 0),
        "bounds" or "set_bounds" => Deskhand.Core.Services.WindowService.SetBounds(r.Hwnd, r.X ?? 0, r.Y ?? 0, r.Width ?? 0, r.Height ?? 0),
        _ => new Deskhand.Core.Services.WindowActionResultDto(false, r.Hwnd, r.Action ?? "", Error: "Unknown action. Use activate|minimize|maximize|restore|close|move|resize|bounds."),
    };
    al.Record("window", $"{res.Action} hwnd={r.Hwnd}", res.Ok ? (res.State ?? "ok") : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});

// OCR: read text off the screen for apps UIA can't see. Capture (lossless) then recognize; word boxes come
// back in screen coordinates (click-ready). Capture-class — gated on captureEnabled.
Deskhand.Core.Services.OcrResultDto Ocr(CaptureResultDto cap) =>
    Deskhand.Core.Services.OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y);
api.MapPost("/ocr/screen", (IAutomationBackend b, ControlState st, OcrScreenRequest? r) =>
    !st.CaptureEnabled ? Results.Json(new { error = "capture disabled", type = "capability_disabled" }, statusCode: 403)
    : Results.Ok(Ocr(b.CaptureScreen(r?.Monitor, ImageFormat.Png, 100))));
api.MapPost("/ocr/region", (IAutomationBackend b, ControlState st, RegionRequest r) =>
    !st.CaptureEnabled ? Results.Json(new { error = "capture disabled", type = "capability_disabled" }, statusCode: 403)
    : Results.Ok(Ocr(b.CaptureRegion(r.X, r.Y, r.Width, r.Height, ImageFormat.Png, 100))));
api.MapPost("/ocr/window", (IAutomationBackend b, ControlState st, WindowCaptureRequest r) =>
{
    if (!st.CaptureEnabled) return Results.Json(new { error = "capture disabled", type = "capability_disabled" }, statusCode: 403);
    var cap = r.Reference is not null ? b.CaptureWindowByRef(r.Reference, ImageFormat.Png, 100)
            : r.Hwnd is not null ? b.CaptureWindow(r.Hwnd.Value, ImageFormat.Png, 100)
            : throw new ArgumentException("Provide either 'reference' or 'hwnd'.");
    return Results.Ok(Ocr(cap));
});

// Vision: find a template image (icon/button) on the screen and return click-ready screen coordinates.
// Capture-class (gated on captureEnabled). Capture the target, then normalized-cross-correlation match.
api.MapPost("/vision/find", (IAutomationBackend b, ControlState st, VisionFindRequest r) =>
{
    if (!st.CaptureEnabled) return Results.Json(new { error = "capture disabled", type = "capability_disabled" }, statusCode: 403);
    byte[] needle;
    try { needle = Convert.FromBase64String(r.TemplateBase64 ?? ""); }
    catch { return Results.Json(new { error = "templateBase64 is not valid base64", type = "bad_request" }, statusCode: 400); }
    var cap = (r.Target ?? "screen").ToLowerInvariant() switch
    {
        "region" => b.CaptureRegion(r.X ?? 0, r.Y ?? 0, r.Width ?? 0, r.Height ?? 0, ImageFormat.Png, 100),
        "window" => r.Reference is not null ? b.CaptureWindowByRef(r.Reference, ImageFormat.Png, 100)
                                            : b.CaptureWindow(r.Hwnd ?? 0, ImageFormat.Png, 100),
        _ => b.CaptureScreen(r.Monitor, ImageFormat.Png, 100),
    };
    var res = Deskhand.Core.Services.TemplateMatchService.Find(cap.Bytes, needle, r.Threshold ?? 0.85, r.MaxResults ?? 10, cap.Rect.X, cap.Rect.Y);
    return Results.Json(res, statusCode: res.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});

// Vision waits + click-on-match + pixel probe. Waits/pixel are capture-class; click-* also drive input, so the
// governed backend enforces armed on the click itself.
IResult CapGate(ControlState st) => Results.Json(new { error = "capture disabled", type = "capability_disabled" }, statusCode: 403);
api.MapPost("/vision/wait-image", (IAutomationBackend b, ControlState st, VisionWaitImageRequest r) =>
{
    if (!st.CaptureEnabled) return CapGate(st);
    byte[] needle; try { needle = Convert.FromBase64String(r.TemplateBase64 ?? ""); } catch { return Results.Json(new { error = "bad base64", type = "bad_request" }, statusCode: 400); }
    var res = Deskhand.Core.Services.VisionOps.WaitForImage(b, needle, Spec(r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference), r.Threshold ?? 0.85, r.TimeoutMs ?? 5000, !(r.Absent ?? false), r.PollMs ?? 250);
    return Results.Json(res, statusCode: res.Found ? 200 : 408);
});
api.MapPost("/vision/wait-text", (IAutomationBackend b, ControlState st, VisionWaitTextRequest r) =>
{
    if (!st.CaptureEnabled) return CapGate(st);
    var res = Deskhand.Core.Services.VisionOps.WaitForText(b, r.Text ?? "", Spec(r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference), r.TimeoutMs ?? 5000, !(r.Absent ?? false), r.PollMs ?? 250);
    return Results.Json(res, statusCode: res.Found ? 200 : 408);
});
api.MapPost("/vision/wait-stable", (IAutomationBackend b, ControlState st, VisionStableRequest r) =>
{
    if (!st.CaptureEnabled) return CapGate(st);
    var res = Deskhand.Core.Services.VisionOps.WaitStable(b, Spec(r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference), r.SettleMs ?? 700, r.TimeoutMs ?? 8000, r.PollMs ?? 250, r.Epsilon ?? 0.01, r.WaitForChange ?? false);
    return Results.Json(res, statusCode: res.Ok ? 200 : 408);
});
api.MapPost("/vision/click-image", (IAutomationBackend b, ControlState st, VisionClickImageRequest r) =>
{
    if (!st.CaptureEnabled) return CapGate(st);
    byte[] needle; try { needle = Convert.FromBase64String(r.TemplateBase64 ?? ""); } catch { return Results.Json(new { error = "bad base64", type = "bad_request" }, statusCode: 400); }
    var res = Deskhand.Core.Services.VisionOps.ClickImage(b, needle, Spec(r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference), r.Threshold ?? 0.85, r.Button ?? "left", r.Count ?? 1, r.TimeoutMs ?? 0);
    return Results.Json(res, statusCode: res.Clicked ? 200 : 404);
});
api.MapPost("/vision/click-text", (IAutomationBackend b, ControlState st, VisionClickTextRequest r) =>
{
    if (!st.CaptureEnabled) return CapGate(st);
    var res = Deskhand.Core.Services.VisionOps.ClickText(b, r.Text ?? "", Spec(r.Target, r.Monitor, r.X, r.Y, r.Width, r.Height, r.Hwnd, r.Reference), r.Button ?? "left", r.Count ?? 1, r.TimeoutMs ?? 0);
    return Results.Json(res, statusCode: res.Clicked ? 200 : 404);
});
api.MapGet("/vision/pixel", (IAutomationBackend b, ControlState st, int x, int y) =>
    st.CaptureEnabled ? Results.Ok(Deskhand.Core.Services.VisionOps.GetPixel(b, x, y)) : CapGate(st));

// Paste text: set the clipboard then send Ctrl+V (fast, exact Unicode entry). Armed + audited.
api.MapPost("/input/paste", (IAutomationBackend b, ControlState st, AuditLog al, PasteRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var set = Deskhand.Core.Services.ClipboardService.SetText(r.Text ?? "");
    if (!set.Ok) return Results.Json(new { error = set.Error, type = "clipboard_error" }, statusCode: 400);
    b.SendKeys("ctrl+v");
    al.Record("paste", $"{set.Length} chars", "ok");
    return Ok();
});

// Process control: terminate / suspend / resume / reprioritize by pid. Armed + audited.
api.MapPost("/process/control", (ControlState st, AuditLog al, ProcControlRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var act = (r.Action ?? "").Trim().ToLowerInvariant();
    if (act is "kill" or "terminate" or "suspend" && r.Confirm != true)
        return Results.Json(new { ok = false, confirmationRequired = true, action = act, pid = r.Pid, message = $"'{act}' on pid {r.Pid} is destructive — resend with confirm=true." }, statusCode: 409);
    var res = act switch
    {
        "kill" or "terminate" => Deskhand.Core.Services.ProcessControlService.Kill(r.Pid, r.Tree ?? true, r.Force ?? false),
        "suspend" => Deskhand.Core.Services.ProcessControlService.Suspend(r.Pid, r.Force ?? false),
        "resume" => Deskhand.Core.Services.ProcessControlService.Resume(r.Pid),
        "priority" => Deskhand.Core.Services.ProcessControlService.SetPriority(r.Pid, r.Level ?? ""),
        _ => new Deskhand.Core.Services.ProcControlDto(false, r.Pid, null, r.Action ?? "", Error: "action must be kill|suspend|resume|priority."),
    };
    al.Record("process_control", $"{res.Action} pid={r.Pid}", res.Ok ? "ok" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? 200 : 400);
});

// Service control (WMI). Armed + audited; state read is open.
api.MapGet("/service/state", (string name) => Results.Ok(new { name, state = Deskhand.Core.Services.ServiceControlService.State(name) }));
api.MapPost("/service/control", (ControlState st, AuditLog al, ServiceControlRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var actn = (r.Action ?? "").Trim().ToLowerInvariant();
    if (actn is "stop" or "restart" && r.Confirm != true)
        return Results.Json(new { ok = false, confirmationRequired = true, action = actn, name = r.Name, message = $"'{actn}' on service '{r.Name}' is destructive — resend with confirm=true." }, statusCode: 409);
    var res = actn switch
    {
        "start" => Deskhand.Core.Services.ServiceControlService.Start(r.Name),
        "stop" => Deskhand.Core.Services.ServiceControlService.Stop(r.Name),
        "restart" => Deskhand.Core.Services.ServiceControlService.Restart(r.Name),
        _ => new Deskhand.Core.Services.ServiceControlDto(false, r.Name, r.Action ?? "", Error: "action must be start|stop|restart."),
    };
    al.Record("service_control", $"{res.Action} {r.Name}", res.Ok ? (res.State ?? "ok") : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? 200 : 400);
});

// Environment variables. Get is read; set is armed + audited (user/machine scope persists).
api.MapGet("/env", (string name, string? scope) => Results.Ok(Deskhand.Core.Services.EnvironmentService.Get(name, scope)));
api.MapPost("/env", (ControlState st, AuditLog al, EnvSetRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = Deskhand.Core.Services.EnvironmentService.Set(r.Name, r.Value, r.Scope);
    al.Record("env_set", $"{res.Scope}:{r.Name}", res.Ok ? "ok" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? 200 : 400);
});

// Scheduled tasks: run/end/enable/disable by name. Armed + audited.
api.MapPost("/task", (ControlState st, AuditLog al, TaskActionRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = (r.Action ?? "").Trim().ToLowerInvariant() switch
    {
        "run" => Deskhand.Core.Services.ScheduledTaskService.Run(r.Task),
        "end" => Deskhand.Core.Services.ScheduledTaskService.End(r.Task),
        "enable" => Deskhand.Core.Services.ScheduledTaskService.Enable(r.Task),
        "disable" => Deskhand.Core.Services.ScheduledTaskService.Disable(r.Task),
        _ => new Deskhand.Core.Services.TaskActionDto(false, r.Task, r.Action ?? "", -1, Error: "action must be run|end|enable|disable."),
    };
    al.Record("task", $"{res.Action} {r.Task}", res.Ok ? "ok" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? 200 : 400);
});

// UAC: read status; configure (registry, needs elevation); best-effort respond to a live prompt. Config armed + audited.
api.MapGet("/uac", () => Results.Ok(Deskhand.Core.Services.UacService.Status()));
api.MapPost("/uac/config", (ControlState st, AuditLog al, UacConfigRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    Deskhand.Core.Services.UacConfigDto res =
        r.Enabled is bool en ? Deskhand.Core.Services.UacService.SetEnabled(en)
        : r.PromptOnSecureDesktop is bool sd ? Deskhand.Core.Services.UacService.SetSecureDesktop(sd)
        : r.AutoApprove is bool aa ? Deskhand.Core.Services.UacService.SetAutoApprove(aa)
        : r.AdminBehavior is int lvl ? Deskhand.Core.Services.UacService.SetAdminBehavior(lvl)
        : new Deskhand.Core.Services.UacConfigDto(false, "none", null, false, "Provide one of: enabled, promptOnSecureDesktop, autoApprove, adminBehavior.");
    al.Record("uac_config", $"{res.Setting}={res.Value}", res.Ok ? (res.RebootRequired ? "ok (reboot required)" : "ok") : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? 200 : 400);
});
api.MapPost("/uac/respond", (ControlState st, AuditLog al, UacRespondRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = Deskhand.Core.Services.UacService.Respond(r.Accept ?? true, r.TimeoutMs ?? 5000);
    al.Record("uac_respond", r.Accept ?? true ? "accept" : "reject", res.Acted ? "acted" : (res.Found ? "found-only" : "none"));
    return Results.Ok(res);
});

// Self-update: check GitHub Releases; apply downloads the latest zip and relaunches. Apply is opt-in
// (DESKHAND_ENABLE_SELF_UPDATE), armed, audited — it stops and replaces this running server.
api.MapGet("/update/check", async () => Results.Ok(await Deskhand.Core.Services.UpdateService.CheckAsync()));
api.MapPost("/update/apply", async (ControlState st, AuditLog al) =>
{
    if (!Deskhand.Core.Services.UpdateService.Enabled)
        return Results.Json(new { error = "Self-update is disabled. Set DESKHAND_ENABLE_SELF_UPDATE=1.", type = "update_disabled" }, statusCode: 403);
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = await Deskhand.Core.Services.UpdateService.ApplyAsync();
    al.Record("update_apply", $"{res.From}->{res.To}", res.Ok ? res.Message ?? "ok" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
});
// Fast cached update status (from the startup check) — for the dashboard banner.
api.MapGet("/update/status", () => Results.Ok(Deskhand.Core.Services.UpdateService.Cached ?? new Deskhand.Core.Services.UpdateCheckDto(Deskhand.Core.BuildInfo.Version, null, false, null, null, null, null, 0, Deskhand.Core.Services.UpdateService.Enabled, "not checked yet")));

// Prometheus metrics (text/plain). No token needed for scraping on loopback; harmless read-only gauges.
api.MapGet("/metrics", (ControlState st) => Results.Text(Deskhand.Core.Services.MetricsService.Render(st.Armed, st.CaptureEnabled, Deskhand.Core.BuildInfo.Version), "text/plain; version=0.0.4"));

// Fetch a URL to a file on this machine (outbound). Armed + audited.
api.MapPost("/fetch", async (ControlState st, AuditLog al, FetchRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    var res = await Deskhand.Core.Services.FetchService.DownloadAsync(r.Url, r.Path, r.MaxBytes);
    al.Record("fetch", $"{r.Url} -> {res.Path}", res.Ok ? $"{res.Bytes} bytes" : $"FAIL {res.Error}");
    return Results.Json(res, statusCode: res.Ok ? 200 : 400);
});

// Audit log viewer: tail today's JSONL. Read-only.
api.MapGet("/audit/recent", (AuditLog al, int? limit) => Results.Ok(ReadAudit(al, limit ?? 200)));

// Webhooks: register outbound sinks for UI events. List is read; add/remove armed + audited.
api.MapGet("/webhooks", (Deskhand.Core.Services.WebhookService wh) => Results.Ok(new { urls = wh.List() }));
api.MapPost("/webhooks", (ControlState st, AuditLog al, Deskhand.Core.Services.WebhookService wh, WebhookRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    bool ok = wh.Add(r.Url);
    al.Record("webhook_add", r.Url ?? "", ok ? "ok" : "invalid/duplicate");
    return ok ? Results.Ok(new { ok = true, urls = wh.List() }) : Results.Json(new { error = "invalid or duplicate URL (http/https required)", type = "bad_request" }, statusCode: 400);
});
api.MapDelete("/webhooks", (ControlState st, AuditLog al, Deskhand.Core.Services.WebhookService wh, string url) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    bool ok = wh.Remove(url);
    al.Record("webhook_remove", url, ok ? "ok" : "not found");
    return Results.Ok(new { ok, urls = wh.List() });
});

// Spilled tool outputs: full text of an over-budget MCP result (see deskhand_read_output).
api.MapGet("/outputs/{id}", (string id) =>
{
    var path = Deskhand.Core.Services.OutputStore.PathFor(id);
    return File.Exists(path) ? Results.File(path, "text/plain; charset=utf-8") : Results.NotFound(new { error = "not found (may have expired)", type = "not_found" });
});

// Read-only registry browsing. path = "" (hive roots) | "HKLM" | "HKLM\SOFTWARE\...".
api.MapGet("/registry", (string? path) => Results.Ok(Deskhand.Core.Services.RegistryService.Browse(path)));

// Start Menu apps (launch one via /process/launch with its path).
api.MapGet("/apps", () => Results.Ok(Deskhand.Core.Services.StartMenuService.List()));

// Virtual desktops: windows grouped by desktop; move a window to the current (or a given) desktop.
api.MapGet("/desktops", () => Results.Ok(Deskhand.Core.Services.VirtualDesktopService.ListByWindow()));
api.MapPost("/desktops/move-window", (ControlState st, MoveWindowRequest r) =>
{
    if (!st.Armed) return Results.Json(new { error = "disarmed", type = "disarmed" }, statusCode: 403);
    bool ok = r.DesktopId is null
        ? Deskhand.Core.Services.VirtualDesktopService.MoveWindowToCurrent((IntPtr)r.Hwnd)
        : Deskhand.Core.Services.VirtualDesktopService.MoveWindowToDesktop((IntPtr)r.Hwnd, r.DesktopId);
    return ok ? Ok() : Results.Json(new { error = "move_failed", type = "move_failed" }, statusCode: 400);
});
api.MapGet("/dumps/{name}", (Deskhand.Core.Services.ProcessDumper d, string name) =>
    Results.File(d.PathFor(name), "application/octet-stream", name));
api.MapPost("/process/launch", (IAutomationBackend b, LaunchRequest r) =>
    Results.Ok(b.LaunchProcess(r.Path, r.Args, r.WorkingDir, r.WaitForWindowMs ?? 10000)));

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
// By default the image is returned to the caller (base64 JSON, or raw bytes with ?raw=true / Accept:image/*).
// Pass save=true (body or ?save=true) to instead SAVE it on this machine (screenshots dir, audited, 24h
// auto-delete) and return the file path + a /screenshots/{name} download URL.
api.MapPost("/capture/screen", (IAutomationBackend b, HttpContext ctx, Deskhand.Core.Services.ScreenshotStore ss, ScreenCaptureRequest? r) =>
    WriteCapture(ctx, ss, b.CaptureScreen(r?.Monitor, ParseFormat(r?.Format), r?.Quality ?? 80), r?.Save ?? false, r?.MaxWidth, r?.MaxBytes));

api.MapPost("/capture/region", (IAutomationBackend b, HttpContext ctx, Deskhand.Core.Services.ScreenshotStore ss, RegionRequest r) =>
    WriteCapture(ctx, ss, b.CaptureRegion(r.X, r.Y, r.Width, r.Height, ParseFormat(r.Format), r.Quality ?? 80), r.Save ?? false, r.MaxWidth, r.MaxBytes));

api.MapPost("/capture/window", (IAutomationBackend b, HttpContext ctx, Deskhand.Core.Services.ScreenshotStore ss, WindowCaptureRequest r) =>
{
    var fmt = ParseFormat(r.Format);
    int q = r.Quality ?? 80;
    var result = r.Reference is not null ? b.CaptureWindowByRef(r.Reference, fmt, q)
               : r.Hwnd is not null ? b.CaptureWindow(r.Hwnd.Value, fmt, q)
               : throw new ArgumentException("Provide either 'reference' or 'hwnd'.");
    return WriteCapture(ctx, ss, result, r.Save ?? false, r.MaxWidth, r.MaxBytes);
});

api.MapPost("/capture/element", (IAutomationBackend b, HttpContext ctx, Deskhand.Core.Services.ScreenshotStore ss, ElementCaptureRequest r) =>
    WriteCapture(ctx, ss, b.CaptureElement(r.Reference, ParseFormat(r.Format), r.Quality ?? 80), r.Save ?? false, r.MaxWidth, r.MaxBytes));

// Saved screenshots: list + download.
api.MapGet("/screenshots", (Deskhand.Core.Services.ScreenshotStore ss) => Results.Ok(ss.List()));
api.MapGet("/screenshots/{name}", (Deskhand.Core.Services.ScreenshotStore ss, string name) =>
{
    var path = ss.PathFor(name);
    if (!File.Exists(path)) return Results.NotFound(new { error = "not found", type = "not_found" });
    return Results.File(path, name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png", name);
});

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
api.MapPost("/mouse/drag", (IAutomationBackend b, DragRequest r) => { b.Drag(r.FromX, r.FromY, r.ToX, r.ToY, r.Button ?? "left", r.Steps ?? 20, r.HoldMs ?? 60); return Ok(); });

// ---- input: keyboard ----
api.MapPost("/keyboard/type", (IAutomationBackend b, TypeRequest r) => { b.TypeText(r.Text); return Ok(); });
api.MapPost("/keyboard/keys", (IAutomationBackend b, KeysRequest r) => { b.SendKeys(r.Chord); return Ok(); });

// MCP over Streamable HTTP — same server, same state as the dashboard.
app.MapMcp("/mcp");

var shownHost = external ? (bind is "any" or "0.0.0.0" or "*" ? "<this-machine-ip>" : bind) : "127.0.0.1";
Console.WriteLine();
Console.WriteLine("  Deskhand — one server, two faces:");
Console.WriteLine();
Console.WriteLine($"      dashboard   {scheme}://{shownHost}:{port}");
Console.WriteLine($"      MCP (HTTP)  {scheme}://{shownHost}:{port}/mcp");
Console.WriteLine();
if (tls) Console.WriteLine("  TLS enabled (HTTPS). A self-signed cert will trigger a browser trust warning; import it or use a CA cert.");
if (external)
{
    Console.WriteLine($"  ** PORT EXPOSED TO THE NETWORK (DESKHAND_BIND={bind}). A token is required for ALL clients. **");
    Console.WriteLine($"     Open the dashboard from another machine as:  {scheme}://{shownHost}:{port}/?token=<DESKHAND_TOKEN>");
    if (!tls) Console.WriteLine("     No TLS: the token crosses the wire in cleartext. Set DESKHAND_TLS / DESKHAND_TLS_CERT or use a reverse proxy on untrusted networks.");
}
else Console.WriteLine(requireToken
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

static Deskhand.Core.Services.CaptureSpec Spec(string? target, int? mon, int? x, int? y, int? w, int? h, long? hwnd, string? reference)
    => new(target, mon, x, y, w, h, hwnd, reference);

static object ReadAudit(AuditLog al, int limit)
{
    limit = Math.Clamp(limit, 1, 2000);
    try
    {
        var files = System.IO.Directory.GetFiles(al.Directory, "audit-*.jsonl").OrderByDescending(f => f).ToList();
        var lines = new List<string>();
        foreach (var f in files)
        {
            var all = ReadLinesShared(f);
            for (int i = all.Count - 1; i >= 0 && lines.Count < limit; i--)
                if (all[i].Trim().Length > 0) lines.Add(all[i]);
            if (lines.Count >= limit) break;
        }
        var entries = lines.Select(l =>
        {
            try { return (object)JsonSerializer.Deserialize<JsonElement>(l); }
            catch { return new { raw = l }; }
        }).ToList();
        return new { count = entries.Count, entries };
    }
    catch (Exception ex) { return new { error = ex.Message, entries = Array.Empty<object>() }; }
}

static List<string> ReadLinesShared(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var sr = new StreamReader(fs);
    var list = new List<string>();
    string? line;
    while ((line = sr.ReadLine()) is not null) list.Add(line);
    return list;
}

static ImageFormat ParseFormat(string? f) =>
    f?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;

// Return raw image bytes when the client asks (?raw=true or Accept: image/*); otherwise JSON+base64.
static IResult WriteCapture(HttpContext ctx, Deskhand.Core.Services.ScreenshotStore ss, CaptureResultDto c, bool saveBody, int? maxWidth = null, int? maxBytes = null)
{
    // Fit to the caller's size/resolution budget (no-op when neither is set); keeps c.Rect (screen coords).
    var img = Deskhand.Core.Services.ImageScaler.Fit(c.Bytes, c.Format, maxWidth, maxBytes);

    // save = save the file on this machine + return a download URL, instead of the image inline.
    bool save = saveBody || string.Equals(ctx.Request.Query["save"], "true", StringComparison.OrdinalIgnoreCase);
    if (save)
    {
        var s = ss.Save(img.Bytes, img.Format);
        return Results.Ok(new { c.Desktop, c.Rect, c.Monitor, c.DpiScale, format = img.Format, scale = img.Scale,
            saved = true, file = s.File, sizeBytes = s.SizeBytes, url = $"/screenshots/{s.FileName}" });
    }

    bool wantsRaw = string.Equals(ctx.Request.Query["raw"], "true", StringComparison.OrdinalIgnoreCase)
                    || ctx.Request.Headers.Accept.ToString().Contains("image/", StringComparison.OrdinalIgnoreCase);
    string contentType = img.Format == "jpeg" ? "image/jpeg" : "image/png";
    if (wantsRaw) return Results.Bytes(img.Bytes, contentType);

    return Results.Ok(new CaptureJson(
        c.Desktop, c.Rect, c.Monitor, c.DpiScale, img.Format, Convert.ToBase64String(img.Bytes), img.Scale));
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
record PidRequest(int Pid);
record FsPathsRequest(IReadOnlyList<string>? Paths);
record FsDeleteRequest(string Path, bool? Permanent);
record FsRenameRequest(string Path, string NewName);
record FsMoveRequest(string Source, string Dest, bool? Overwrite);
record FsCopyRequest(string Source, string Dest, bool? Overwrite);
record FsZipRequest(IReadOnlyList<string>? Sources, string Dest, bool? Overwrite);
record FsUnzipRequest(string ZipPath, string? Dest, bool? Overwrite);
record ShellRunRequest(string? Shell, string Command, string? Cwd, int? TimeoutMs);
record SessionLaunchRequest(string Path, string? Args, string? WorkingDir, int? SessionId, string? Desktop,
    string? As, string? User, string? Domain, string? Password, bool? NoWindow);
record FirewallOpenRequest(int Port, string? Protocol, string? Direction, string? RemoteAddresses, string? Name);
record FirewallCloseRequest(int Port, string? Protocol, string? Direction, bool? All);
record ClipboardSetRequest(string? Text);
record WindowActionRequest(long Hwnd, string Action, int? X, int? Y, int? Width, int? Height);
record OcrScreenRequest(int? Monitor);
record VisionFindRequest(string? TemplateBase64, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, double? Threshold, int? MaxResults);
record VisionWaitImageRequest(string? TemplateBase64, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, double? Threshold, int? TimeoutMs, bool? Absent, int? PollMs);
record VisionWaitTextRequest(string? Text, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, int? TimeoutMs, bool? Absent, int? PollMs);
record VisionStableRequest(string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, int? SettleMs, int? TimeoutMs, int? PollMs, double? Epsilon, bool? WaitForChange);
record VisionClickImageRequest(string? TemplateBase64, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, double? Threshold, string? Button, int? Count, int? TimeoutMs);
record VisionClickTextRequest(string? Text, string? Target, int? Monitor, int? X, int? Y, int? Width, int? Height,
    long? Hwnd, string? Reference, string? Button, int? Count, int? TimeoutMs);
record PasteRequest(string? Text);
record ProcControlRequest(int Pid, string Action, bool? Tree, string? Level, bool? Force, bool? Confirm);
record ServiceControlRequest(string Name, string Action, bool? Confirm);
record EnvSetRequest(string Name, string? Value, string? Scope);
record TaskActionRequest(string Task, string Action);
record UacConfigRequest(bool? Enabled, bool? PromptOnSecureDesktop, bool? AutoApprove, int? AdminBehavior);
record UacRespondRequest(bool? Accept, int? TimeoutMs);
record FetchRequest(string? Url, string? Path, long? MaxBytes);
record WebhookRequest(string? Url);

// Forwards live UI events to registered webhook subscribers (outbound event push).
sealed class WebhookForwarder(Deskhand.Core.Events.EventHub hub, Deskhand.Core.Services.WebhookService hooks)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var (reader, dispose) = hub.Subscribe();
        try { await foreach (var e in reader.ReadAllAsync(ct)) await hooks.Deliver(new { type = "ui_event", @event = e }); }
        catch (OperationCanceledException) { }
        finally { dispose(); }
    }
}
record MoveWindowRequest(long Hwnd, string? DesktopId);
record RecordStartRequest(int? Monitor, string? Format, int? Fps, int? Scale, int? Quality, int? MaxDurationMs);
record InputRecordRequest(bool? CaptureText);
record SetValueRequest(string Reference, string Text);
record ExpandRequest(string Reference, bool Expand);
record ScreenCaptureRequest(int? Monitor, string? Format, int? Quality, bool? Save, int? MaxWidth, int? MaxBytes);
record RegionRequest(int X, int Y, int Width, int Height, string? Format, int? Quality, bool? Save, int? MaxWidth, int? MaxBytes);
record WindowCaptureRequest(long? Hwnd, string? Reference, string? Format, int? Quality, bool? Save, int? MaxWidth, int? MaxBytes);
record ElementCaptureRequest(string Reference, string? Format, int? Quality, bool? Save, int? MaxWidth, int? MaxBytes);
record InputDesktopRequest(string? Format, int? Quality);
record ControlRequest(bool? Armed, bool? InputEnabled, bool? CaptureEnabled, bool? NotifyOnCapture);
record MacroPlayRequest(Deskhand.Core.Macros.Macro? Macro, double? Speed, int? MaxStepDelayMs);
record MacroExpectRequest(string? Name, string? AutomationId, string? ControlType, string? ClassName, int? TimeoutMs);
record MouseMoveRequest(int X, int Y);
record MouseClickRequest(string? Button, int? X, int? Y, int? Count);
record MouseButtonRequest(string? Button, int? X, int? Y);
record ScrollRequest(int Dx, int Dy);
record DragRequest(int FromX, int FromY, int ToX, int ToY, string? Button, int? Steps, int? HoldMs);
record TypeRequest(string Text);
record KeysRequest(string Chord);
record CaptureJson(string Desktop, RectDto Rect, int Monitor, double DpiScale, string Format, string ImageBase64, double Scale = 1.0);
