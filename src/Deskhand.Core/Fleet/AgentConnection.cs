using System.Net.WebSockets;
using System.Text.Json;

namespace Deskhand.Core.Fleet;

/// <summary>
/// Agent side of the fleet link. Dials OUT to the server, announces itself, then serves commands
/// pushed down the socket by executing them against the local backend and replying with results.
/// Reconnects with backoff if the socket drops.
/// </summary>
public static class AgentConnection
{
    public static async Task RunForeverAsync(string serverWsUrl, string agentId, IAutomationBackend backend,
        Action<string>? log, CancellationToken ct, string? token = null)
    {
        int backoffMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(serverWsUrl, agentId, backend, log, ct, token);
                backoffMs = 1000;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log?.Invoke($"disconnected: {ex.Message}; retrying in {backoffMs}ms");
                try { await Task.Delay(backoffMs, ct); } catch { break; }
                backoffMs = Math.Min(backoffMs * 2, 15000);
            }
        }
    }

    private static async Task ConnectOnceAsync(string serverWsUrl, string agentId, IAutomationBackend backend,
        Action<string>? log, CancellationToken ct, string? token)
    {
        using var ws = new ClientWebSocket();
        if (!string.IsNullOrEmpty(token)) ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await ws.ConnectAsync(new Uri(serverWsUrl), ct);
        var gate = new SemaphoreSlim(1, 1);

        var hello = new AgentHello(agentId, Environment.MachineName, backend.GetMachineInfo());
        await WsUtil.SendTextAsync(ws, FleetJson.Serialize(hello), gate, ct);
        log?.Invoke($"connected to {serverWsUrl} as '{agentId}'");

        while (!ct.IsCancellationRequested)
        {
            var msg = await WsUtil.ReceiveTextAsync(ws, ct);
            if (msg is null) throw new IOException("server closed the connection");

            var cmd = FleetJson.Deserialize<FleetCommand>(msg);
            if (cmd is null) continue;

            // Execute off the receive loop so a slow action doesn't stall other commands.
            _ = Task.Run(async () =>
            {
                FleetResult result;
                try
                {
                    var value = AgentDispatcher.Dispatch(cmd, backend);
                    var element = JsonSerializer.SerializeToElement(value, FleetJson.Options);
                    result = new FleetResult(cmd.Id, true, element, null, null);
                }
                catch (Exception ex)
                {
                    result = new FleetResult(cmd.Id, false, WsUtil.NullElement, ex.Message, ex.GetType().Name);
                }
                try { await WsUtil.SendTextAsync(ws, FleetJson.Serialize(result), gate, ct); } catch { }
            }, ct);
        }
    }
}
