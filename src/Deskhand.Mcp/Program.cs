using Deskhand.Core;
using Deskhand.Core.Governance;
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
builder.Services.AddSingleton(controlState);
builder.Services.AddSingleton(auditLog);
builder.Services.AddSingleton<IAutomationBackend>(_ =>
    new GovernedBackend(new LocalAutomationBackend(), controlState, auditLog));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
using var killSwitch = new KillSwitch(controlState, auditLog);
await app.RunAsync();
