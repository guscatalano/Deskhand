using Deskhand.Core;
using Deskhand.Core.Governance;
using Deskhand.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Deskhand MCP server — the same IAutomationBackend as the HTTP server, exposed over MCP (stdio).
// Per-Monitor-v2 DPI awareness must be set before the backend touches any windows or pixels.
DpiHelper.EnablePerMonitorV2();

var builder = Host.CreateApplicationBuilder(args);

// stdio carries the MCP protocol on stdout, so ALL logs must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var controlState = ControlState.FromEnvironment();
var auditLog = new AuditLog();
var captureNotifier = new ToastNotifier();
var macroRecorder = new Deskhand.Core.Macros.MacroRecorder();
var eventHub = new Deskhand.Core.Events.EventHub();
var localBackend = new LocalAutomationBackend();
localBackend.StartEvents(eventHub);
var processWatcher = new Deskhand.Core.Events.ProcessWatcher(eventHub);
var screenRecorder = new Deskhand.Core.Services.ScreenRecorder(auditLog);
var inputRecorder = new Deskhand.Core.Services.InputRecorder((x, y) =>
{ try { return localBackend.GetElementFromPoint(x, y); } catch { return null; } });
builder.Services.AddSingleton(controlState);
builder.Services.AddSingleton(auditLog);
builder.Services.AddSingleton(macroRecorder);
builder.Services.AddSingleton(eventHub);
builder.Services.AddSingleton(processWatcher);
builder.Services.AddSingleton(screenRecorder);
builder.Services.AddSingleton(inputRecorder);
builder.Services.AddSingleton<IAutomationBackend>(_ =>
    new GovernedBackend(localBackend, controlState, auditLog, captureNotifier, macroRecorder));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(Deskhand.McpTools.DeskhandTools).Assembly);

var app = builder.Build();
using var killSwitch = new KillSwitch(controlState, auditLog);
using var _notifier = captureNotifier;
await app.RunAsync();
