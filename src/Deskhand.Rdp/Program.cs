using Deskhand.Core.Fleet;
using Deskhand.Rdp;

// Zero-install RDP client: connect to a host over the RDP wire and capture/drive it, with nothing
// installed on the target. Capture + input only (no UIA over pure RDP).
//   deskhand-rdp <host> <user> <password> [--domain D] [--size 1280x800] [--capture out.png] [--timeout 15000]
//   deskhand-rdp <host> <user> <password> --fleet ws://server:8799/agent/connect [--id NAME]
//        → connects over RDP and joins the fleet as a capture+input agent for that machine.

if (args.Length < 3)
{
    Console.WriteLine("usage: deskhand-rdp <host> <user> <password> [--domain D] [--size WxH] [--capture out.png] [--timeout ms]");
    Console.WriteLine("       deskhand-rdp <host> <user> <password> --fleet <ws-url> [--id NAME]   (join the fleet over RDP)");
    return;
}

string host = args[0], user = args[1];
// Password may come from the env (used by the fleet server's web "add over RDP", so it isn't on the
// command line / process list); the positional arg is the fallback for manual CLI use.
string password = Environment.GetEnvironmentVariable("DESKHAND_RDP_PASSWORD") ?? args[2];
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

string? domain = Opt("--domain");
int width = 1280, height = 800;
if (Opt("--size") is string s && s.Split('x') is { Length: 2 } parts && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) { width = w; height = h; }
string? capture = Opt("--capture");
string? fleet = Opt("--fleet");
int timeout = int.TryParse(Opt("--timeout"), out var t) ? t : 15000;
int port = int.TryParse(Opt("--port"), out var pp) ? pp : 0;   // 0 = RDP default (3389); set for a mock/non-standard port

bool nla = !args.Contains("--no-nla");   // disable CredSSP/NLA for mock/legacy RDP servers (e.g. rdpy)
using var rdp = new RdpHost(width, height);
Console.WriteLine($"connecting to {host}{(port > 0 ? ":" + port : "")} as {user} ({width}x{height}){(nla ? "" : " [no-NLA]")}...");
bool ok = await rdp.ConnectAsync(host, user, domain, password, timeout, nla, port);
Console.WriteLine(ok ? "CONNECTED" : $"not connected: {rdp.LastReason}");

if (fleet is not null)
{
    if (!ok) { Console.WriteLine("cannot join fleet: RDP is not connected."); return; }
    string agentId = Opt("--id") ?? host;
    string? token = Environment.GetEnvironmentVariable("DESKHAND_FLEET_TOKEN");
    // The RDP session becomes a fleet agent whose backend is the RDP wire. No observation services:
    // events/hooks/recording/user-input are local-machine features and don't apply to a pure-RDP target,
    // so those fleet calls return a clean "not available" error. Capture + coordinate input work.
    var services = new AgentServices
    {
        Backend = new RdpBackend(rdp, host),
        // Bootstrap-install the native agent ON the remote over this RDP session: drive redirection
        // exposes this connector's folder as \\tsclient, and Run (Win+R) launches the self-contained
        // agent there, pointed back at the fleet. It then reconnects as a full native agent.
        RdpInstallAgent = agentPath =>
        {
            string local = !string.IsNullOrWhiteSpace(agentPath) ? agentPath!
                : (Environment.GetEnvironmentVariable("DESKHAND_AGENT_PATH")
                   ?? System.IO.Path.Combine(AppContext.BaseDirectory, "deskhand-agent.exe"));
            if (!System.IO.File.Exists(local))
                throw new FileNotFoundException($"Self-contained deskhand-agent.exe not found at '{local}'. Publish it next to deskhand-rdp.exe (see installer/publish-agent.ps1) or set DESKHAND_AGENT_PATH.");
            string cmd = $"\"{RdpHost.ToTsClient(local)}\" {fleet}";
            rdp.RunCommand(cmd);
            return new { ok = true, launched = cmd, note = "Sent to the remote via Run. If it reconnects as a native agent, remove this RDP connector." };
        }
    };
    Console.WriteLine($"joining fleet {fleet} as '{agentId}'  (RDP -> {host})  — capture + input only, no UIA");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await AgentConnection.RunForeverAsync(fleet, agentId, services, m => Console.WriteLine("  " + m), cts.Token, token);
    rdp.Disconnect();
    return;
}

if (args.Contains("--diag"))
{
    if (ok) await Task.Delay(1500);
    var (chosen, all) = rdp.DumpChildren();
    Console.WriteLine($"input target: {chosen}");
    Console.WriteLine($"child windows ({all.Count}):");
    foreach (var line in all) Console.WriteLine("  " + line);
}

if (capture is not null)
{
    if (ok) await Task.Delay(1500); // let the first frame render
    var png = rdp.Capture();
    File.WriteAllBytes(capture, png);
    Console.WriteLine($"captured {png.Length:N0} bytes ({width}x{height}) -> {capture}");
}

rdp.Disconnect();
