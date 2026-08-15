using System.Text.Json;

namespace Deskhand.Core.Fleet;

/// <summary>
/// Server-side companion to <see cref="RemoteAgentBackend"/> for the observation surface (events, hooks,
/// recording, user-input recording) — the parts that live outside <see cref="IAutomationBackend"/>. Each
/// call forwards to the agent over its link and returns the agent's raw JSON reply, which the fleet's HTTP
/// endpoints and MCP tools pass straight through.
/// </summary>
public sealed class RemoteAgentObserver(IAgentLink link)
{
    private JsonElement Call(string method, object? args)
    {
        var res = link.SendAsync(method, args).GetAwaiter().GetResult();
        if (!res.Ok) throw new RemoteAutomationException(res.ErrorType ?? "internal", res.Error ?? "remote error");
        return res.Result;
    }

    public JsonElement GetEvents(long since) => Call(FleetMethods.GetEvents, new { sinceId = since });
    public JsonElement WaitForProcess(string ev, string? name, int? pid, int timeoutMs) =>
        Call(FleetMethods.WaitForProcess, new { @event = ev, name, pid, timeoutMs });

    public JsonElement RecordStart(int? monitor, string format, int fps, int scale, int quality, int maxDurationMs) =>
        Call(FleetMethods.RecordStart, new { monitor, format, fps, scale, quality, maxDurationMs });
    public JsonElement RecordStop(string id) => Call(FleetMethods.RecordStop, new { id });
    public JsonElement RecordStatus(string? id) => Call(FleetMethods.RecordStatus, new { id });
    public JsonElement RecordRead(string id) => Call(FleetMethods.RecordRead, new { id });

    public JsonElement InputStart(bool captureText) => Call(FleetMethods.InputStart, new { captureText });
    public JsonElement InputStop() => Call(FleetMethods.InputStop, null);
    public JsonElement InputGet(long since) => Call(FleetMethods.InputGet, new { sinceId = since });
}
