using Deskhand.Core;
using Deskhand.Core.Fleet;
using Deskhand.Core.Governance;

// Deskhand Fleet Agent: runs in a user's interactive session, dials OUT to the fleet server, and
// serves automation commands against the local desktop. No inbound port is opened on this machine.

DpiHelper.EnablePerMonitorV2();

string server = args.FirstOrDefault(a => a.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
    ?? Environment.GetEnvironmentVariable("DESKHAND_FLEET_URL")
    ?? "ws://127.0.0.1:8799/agent/connect";
// Agent id: a non-ws positional arg (used by the launcher for per-session ids), else env, else machine.
string? argAgentId = args.FirstOrDefault(a => !a.StartsWith("ws", StringComparison.OrdinalIgnoreCase));
string agentId = argAgentId ?? Environment.GetEnvironmentVariable("DESKHAND_AGENT_ID") ?? Environment.MachineName;
string? token = Environment.GetEnvironmentVariable("DESKHAND_FLEET_TOKEN");

// Toast on this PC whenever the fleet captures/controls it — so the local user knows they're watched.
var notifier = new Deskhand.Ui.ToastNotifier();
var indicator = new Deskhand.Ui.RecordingIndicator();
var audit = new AuditLog();
var local = new LocalAutomationBackend();
var backend = new GovernedBackend(local, ControlState.FromEnvironment(), audit, notifier);

// Observation services, so the fleet can pull this PC's events and drive its recorders remotely.
var hub = new Deskhand.Core.Events.EventHub();
local.StartEvents(hub);                                   // focus_changed, window_opened
var processes = new Deskhand.Core.Events.ProcessWatcher(hub);        // process_started/exited
var recorder = new Deskhand.Core.Services.ScreenRecorder(audit);
var input = new Deskhand.Core.Services.InputRecorder(
    (x, y) => { try { return local.GetElementFromPoint(x, y); } catch { return null; } },
    notifier, indicator);                                // banner + toast on THIS PC when the fleet records its user
var services = new AgentServices { Backend = backend, Events = hub, Processes = processes, Recorder = recorder, Input = input };

Console.WriteLine($"Deskhand agent '{agentId}'  ->  {server}{(token is null ? "" : "  (authenticated)")}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await AgentConnection.RunForeverAsync(server, agentId, services, m => Console.WriteLine("  " + m), cts.Token, token);
