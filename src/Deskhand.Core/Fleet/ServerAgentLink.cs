using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Deskhand.Core.Fleet;

/// <summary>Server side of one agent's link: pushes commands down the socket and correlates the
/// results that come back by id. Owns the read loop for this agent's socket.</summary>
public sealed class ServerAgentLink(WebSocket ws, AgentHello hello) : IAgentLink, IDisposable
{
    private readonly WebSocket _ws = ws;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FleetResult>> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    public string AgentId { get; } = hello.AgentId;
    public string MachineName { get; } = hello.MachineName;
    public MachineInfoDto? Info { get; } = hello.Info;

    public async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await WsUtil.ReceiveTextAsync(_ws, _cts.Token);
                if (msg is null) break;
                var res = FleetJson.Deserialize<FleetResult>(msg);
                if (res is not null && _pending.TryRemove(res.Id, out var tcs)) tcs.TrySetResult(res);
            }
        }
        catch { /* fall through to fail pending */ }
        finally
        {
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("Agent disconnected."));
            _pending.Clear();
        }
    }

    public async Task<FleetResult> SendAsync(string method, object? args, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<FleetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = FleetJson.Serialize(new { id, method, args = args ?? new { } });
        await WsUtil.SendTextAsync(_ws, payload, _sendGate, ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _cts.Token);
        using var reg = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
                t.TrySetException(new TimeoutException("Agent did not respond in time."));
        });
        return await tcs.Task;
    }

    public void Dispose() => _cts.Cancel();
}
