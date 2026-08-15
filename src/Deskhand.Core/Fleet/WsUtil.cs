using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Deskhand.Core.Fleet;

public static class WsUtil
{
    public static readonly JsonElement NullElement = JsonSerializer.SerializeToElement<object?>(null, FleetJson.Options);

    public static async Task SendTextAsync(WebSocket ws, string text, SemaphoreSlim gate, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await gate.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct); }
        finally { gate.Release(); }
    }

    public static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, ct); }
            catch { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
