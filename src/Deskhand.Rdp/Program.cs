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

string host = args[0], user = args[1], password = args[2];
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

string? domain = Opt("--domain");
int width = 1280, height = 800;
if (Opt("--size") is string s && s.Split('x') is { Length: 2 } parts && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) { width = w; height = h; }
string? capture = Opt("--capture");
string? fleet = Opt("--fleet");
int timeout = int.TryParse(Opt("--timeout"), out var t) ? t : 15000;

using var rdp = new RdpHost(width, height);
Console.WriteLine($"connecting to {host} as {user} ({width}x{height})...");
bool ok = await rdp.ConnectAsync(host, user, domain, password, timeout);
Console.WriteLine(ok ? "CONNECTED" : $"not connected: {rdp.LastReason}");

if (fleet is not null)
{
    if (!ok) { Console.WriteLine("cannot join fleet: RDP is not connected."); return; }
    string agentId = Opt("--id") ?? host;
    string? token = Environment.GetEnvironmentVariable("DESKHAND_FLEET_TOKEN");
    // The RDP session becomes a fleet agent whose backend is the RDP wire. No observation services:
    // events/hooks/recording/user-input are local-machine features and don't apply to a pure-RDP target,
    // so those fleet calls return a clean "not available" error. Capture + coordinate input work.
    var services = new AgentServices { Backend = new RdpBackend(rdp, host) };
    Console.WriteLine($"joining fleet {fleet} as '{agentId}'  (RDP -> {host})  — capture + input only, no UIA");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await AgentConnection.RunForeverAsync(fleet, agentId, services, m => Console.WriteLine("  " + m), cts.Token, token);
    rdp.Disconnect();
    return;
}

if (capture is not null)
{
    if (ok) await Task.Delay(1500); // let the first frame render
    var png = rdp.Capture();
    File.WriteAllBytes(capture, png);
    Console.WriteLine($"captured {png.Length:N0} bytes ({width}x{height}) -> {capture}");
}

rdp.Disconnect();
