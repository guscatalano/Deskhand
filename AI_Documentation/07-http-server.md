# 07 — HTTP Server (`Deskhand.Http`)

An ASP.NET Core **minimal API** host (`Microsoft.NET.Sdk.Web`, `AssemblyName=deskhand-http`). One file,
`Program.cs` (top-level statements). Serves the dashboard from `wwwroot/index.html` and the JSON API.

## Startup sequence

```csharp
DpiHelper.EnablePerMonitorV2();                                   // FIRST — before anything touches pixels
var builder = WebApplication.CreateBuilder(args);

int port = int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_PORT"), out var p) ? p : 8791;
string? token = Environment.GetEnvironmentVariable("DESKHAND_TOKEN")?.Trim();
bool requireToken = !string.IsNullOrWhiteSpace(token);
var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    $"http://127.0.0.1:{port}", $"http://localhost:{port}" };

builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));   // LOOPBACK ONLY (127.0.0.1 and ::1)
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

builder.Services.ConfigureHttpJsonOptions(o => {
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// Governance wired once at the seam:
var controlState = ControlState.FromEnvironment();
var auditLog = new AuditLog();
var captureNotifier = new ToastNotifier();
builder.Services.AddSingleton(controlState);
builder.Services.AddSingleton(auditLog);
builder.Services.AddSingleton(captureNotifier);
builder.Services.AddSingleton<IAutomationBackend>(_ =>
    new GovernedBackend(new LocalAutomationBackend(), controlState, auditLog, captureNotifier));

var app = builder.Build();
app.UseDefaultFiles();     // serves wwwroot/index.html at "/"
app.UseStaticFiles();      // BEFORE the auth middleware, so the dashboard loads without a token
```

At the end, after mapping endpoints, it prints the URL / token hint / audit path / kill-switch hint,
constructs `using var killSwitch = new KillSwitch(controlState, auditLog);`, and calls `app.Run()`.

## Security model

`ListenLocalhost` binds only `127.0.0.1` / `::1`; **no external interface is ever bound.** On top of that,
a middleware (running after static files) enforces, in order:

1. **`/health` bypass** — liveness needs no checks.
2. **Loopback Host check** (DNS-rebinding defense) — `ctx.Request.Host.Host` must be one of
   `localhost`, `127.0.0.1`, `[::1]`, `::1`; else `403 { type: "forbidden" }`.
3. **Cross-site Origin block** — if an `Origin` header is present and not in `allowedOrigins`, `403`. This
   stops a malicious web page in your browser from reaching the server. **No CORS headers are ever emitted.**
4. **Trusted-browser shortcut** — a request is trusted without a token if its `Origin` is allowed, or its
   `Sec-Fetch-Site` is `same-origin` or `none`. The same-origin dashboard therefore needs no token.
5. **Optional bearer token** — if `DESKHAND_TOKEN` is set and the caller is *not* a trusted browser, it must
   send `Authorization: Bearer <token>`; else `401 { type: "unauthorized" }`. Comparison is constant-time
   over SHA-256 of both sides (`FixedEquals` → `CryptographicOperations.FixedTimeEquals`).

So: the browser dashboard never needs a token; `curl`/scripts need one only if `DESKHAND_TOKEN` is set.
There is no anti-detection behavior; input is honest `SendInput`.

## Error → status mapping

A second middleware wraps the pipeline in `try/catch` and maps exception types to status + `type` string:

| Exception | Status | `type` |
|---|---|---|
| `UnknownElementException`, `StaleElementException` | 404 | `stale_element` |
| `PatternNotSupportedException` | 409 | `pattern_not_supported` |
| `DesktopUnavailableException` | 409 | `desktop_unavailable` |
| `DisarmedException` | 403 | `disarmed` |
| `CapabilityDisabledException` | 403 | `capability_disabled` |
| `ArgumentException` | 400 | `bad_request` |
| (anything else) | 500 | `internal` |

Body is always `{ "error": <message>, "type": <type> }` (only if the response hasn't started).

## Endpoints

All bodies/responses JSON camelCase. `reference` values (`el_…`) come from any read call.

| Method & path | Body | Purpose |
|---|---|---|
| `GET /health` | — | Liveness (no auth); returns `{ ok, service, version }` |
| `GET /machine` | — | `MachineInfoDto` (monitors, virtual screen, desktop state) |
| `GET /desktop/state` | — | `DesktopStateDto` |
| `GET /control` | — | `{ armed, inputEnabled, captureEnabled, notifyOnCapture, auditDir }` |
| `POST /control` | `{armed?, inputEnabled?, captureEnabled?, notifyOnCapture?}` | set switches, returns new state |
| `GET /foreground` | — | foreground window element |
| `GET /focused` | — | focused element |
| `GET /windows` | — | all top-level windows |
| `POST /uia/tree` | `{rootRef?, depth?=3, maxChildren?=40}` | subtree |
| `POST /uia/find` | `{rootRef?, name?, automationId?, controlType?, className?, scope?=descendants, max?=100}` | query |
| `POST /uia/wait` | `{rootRef?, …, timeoutMs?=5000}` | poll; `404 wait_timeout` on miss |
| `GET /uia/element/{ref}` | — | re-read one element |
| `GET /uia/element/{ref}/properties` | — | every UIA property (name→value) |
| `POST /uia/invoke` | `{reference}` | Invoke pattern |
| `POST /uia/set-value` | `{reference, text}` | Value pattern |
| `POST /uia/toggle` | `{reference}` | Toggle pattern |
| `POST /uia/expand-collapse` | `{reference, expand}` | ExpandCollapse pattern |
| `POST /uia/select` | `{reference}` | SelectionItem pattern |
| `POST /uia/set-focus` | `{reference}` | raise window + focus |
| `POST /capture/screen` | `{monitor?, format?, quality?}` | virtual desktop or one monitor |
| `POST /capture/region` | `{x, y, width, height, format?, quality?}` | rectangle |
| `POST /capture/window` | `{reference? \| hwnd?, format?, quality?}` | one window (WGC→PrintWindow) |
| `POST /capture/element` | `{reference, format?, quality?}` | element bounds |
| `POST /capture/input-desktop` | `{format?, quality?}` | Phase 2 input-desktop (see below) |
| `POST /mouse/move` | `{x, y}` | move |
| `POST /mouse/click` | `{button?=left, x?, y?, count?=1}` | click |
| `POST /mouse/down` · `/mouse/up` | `{button?, x?, y?}` | press / release |
| `POST /mouse/scroll` | `{dx, dy}` | wheel notches |
| `POST /keyboard/type` | `{text}` | type Unicode literal |
| `POST /keyboard/keys` | `{chord}` | chord e.g. `"ctrl+shift+s"` |

Action endpoints that don't return data respond `{ ok: true }` via the local helper `Ok()`. `ParseFormat`
maps `"jpeg"`/`"jpg"` → JPEG, everything else → PNG; default `quality` is `80`.

## Capture responses

`WriteCapture(HttpContext, CaptureResultDto)` decides the shape:

```csharp
bool wantsRaw = ctx.Request.Query["raw"] == "true"
             || ctx.Request.Headers.Accept.ToString().Contains("image/");
if (wantsRaw) return Results.Bytes(c.Bytes, c.Format == "jpeg" ? "image/jpeg" : "image/png");
return Results.Ok(new CaptureJson(c.Desktop, c.Rect, c.Monitor, c.DpiScale, c.Format,
                                  Convert.ToBase64String(c.Bytes)));
```

So default is JSON `{ desktop, rect, monitor, dpiScale, format, imageBase64 }`; add `?raw=true` (or send
`Accept: image/png`) for raw bytes. `POST /capture/input-desktop` returns a richer JSON object
(`success, desktopName, kind, note, capture{…,imageBase64}`) — see `11-secure-desktop.md`.

## Request DTOs

Declared at the bottom of `Program.cs` as `record`s: `TreeRequest`, `FindRequest`, `WaitRequest`,
`RefRequest`, `SetValueRequest`, `ExpandRequest`, `ScreenCaptureRequest`, `RegionRequest`,
`WindowCaptureRequest`, `ElementCaptureRequest`, `InputDesktopRequest`, `ControlRequest`,
`MouseMoveRequest`, `MouseClickRequest`, `MouseButtonRequest`, `ScrollRequest`, `TypeRequest`,
`KeysRequest`, and the response `CaptureJson(string Desktop, RectDto Rect, int Monitor, double DpiScale,
string Format, string ImageBase64)`.

Sample requests are in the repo-root `Deskhand.http` file.
