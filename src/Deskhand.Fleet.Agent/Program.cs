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

var backend = new GovernedBackend(new LocalAutomationBackend(), ControlState.FromEnvironment(), new AuditLog());

Console.WriteLine($"Deskhand agent '{agentId}'  ->  {server}{(token is null ? "" : "  (authenticated)")}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await AgentConnection.RunForeverAsync(server, agentId, backend, m => Console.WriteLine("  " + m), cts.Token, token);
