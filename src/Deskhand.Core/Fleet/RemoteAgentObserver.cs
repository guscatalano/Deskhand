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

    public JsonElement InstallAgent(string? agentPath) => Call(FleetMethods.RdpInstallAgent, new { agentPath });
    public JsonElement RegistryBrowse(string? path) => Call(FleetMethods.RegistryBrowse, new { path });
    public JsonElement DumpProcess(int pid) => Call(FleetMethods.DumpProcess, new { pid });
    public JsonElement DumpList() => Call(FleetMethods.DumpList, null);
    public JsonElement DumpRead(string name) => Call(FleetMethods.DumpRead, new { name });
    public JsonElement ListApps() => Call(FleetMethods.ListApps, null);
    public JsonElement ListDesktops() => Call(FleetMethods.ListDesktops, null);
    public JsonElement MoveWindowToDesktop(long hwnd, string? desktopId) => Call(FleetMethods.MoveWindowToDesktop, new { hwnd, desktopId });

    // Files + shell (native agents only).
    public JsonElement BrowseFiles(string? path) => Call(FleetMethods.BrowseFiles, new { path });
    public JsonElement ReadFile(string? path) => Call(FleetMethods.ReadFile, new { path });
    public JsonElement WriteFile(string? path, string? contentBase64, bool overwrite) => Call(FleetMethods.WriteFile, new { path, contentBase64, overwrite });
    public JsonElement DeletePath(string? path, bool permanent) => Call(FleetMethods.DeletePath, new { path, permanent });
    public JsonElement RenamePath(string? path, string? newName) => Call(FleetMethods.RenamePath, new { path, newName });
    public JsonElement MovePath(string? source, string? dest, bool overwrite) => Call(FleetMethods.MovePath, new { source, dest, overwrite });
    public JsonElement CopyPath(string? source, string? dest, bool overwrite) => Call(FleetMethods.CopyPath, new { source, dest, overwrite });
    public JsonElement Zip(string[]? sources, string? dest, bool overwrite) => Call(FleetMethods.ZipPaths, new { sources, dest, overwrite });
    public JsonElement Unzip(string? zipPath, string? dest, bool overwrite) => Call(FleetMethods.UnzipPath, new { zipPath, dest, overwrite });
    public JsonElement RunCommand(string? shell, string? command, string? cwd, int? timeoutMs) => Call(FleetMethods.RunCommand, new { shell, command, cwd, timeoutMs });
    public JsonElement LaunchProcessAs(string path, string? args, string? workingDir, int? sessionId, string? desktop, string? @as, string? user, string? domain, string? password, bool noWindow)
        => Call(FleetMethods.LaunchAs, new { path, args, workingDir, sessionId, desktop, @as, user, domain, password, noWindow });
    public JsonElement SystemInfo() => Call(FleetMethods.SystemInfo, null);
    public JsonElement FirewallRules(string? direction, int? port, bool? enabledOnly, string? contains, bool managedOnly, int max)
        => Call(FleetMethods.FirewallRules, new { direction, port, enabledOnly, contains, managedOnly, max });
    public JsonElement FirewallOpen(int port, string? protocol, string? direction, string? remoteAddresses, string? name)
        => Call(FleetMethods.FirewallOpen, new { port, protocol, direction, remoteAddresses, name });
    public JsonElement FirewallClose(int port, string? protocol, string? direction, bool all)
        => Call(FleetMethods.FirewallClose, new { port, protocol, direction, all });
    public JsonElement ClipboardGet() => Call(FleetMethods.ClipboardGet, null);
    public JsonElement ClipboardSet(string? text) => Call(FleetMethods.ClipboardSet, new { text });
    public JsonElement ClipboardClear() => Call(FleetMethods.ClipboardClear, null);
    public JsonElement WindowAction(long hwnd, string action, int? x, int? y, int? width, int? height)
        => Call(FleetMethods.WindowAction, new { hwnd, action, x, y, width, height });
    public JsonElement OcrScreen(int? monitor) => Call(FleetMethods.OcrScreen, new { monitor });
    public JsonElement OcrRegion(int x, int y, int width, int height) => Call(FleetMethods.OcrRegion, new { x, y, width, height });
    public JsonElement OcrWindow(long? hwnd, string? reference) => Call(FleetMethods.OcrWindow, new { hwnd, reference });
    public JsonElement FindImage(string templateBase64, string? target, int? monitor, int? x, int? y, int? width, int? height, long? hwnd, string? reference, double? threshold, int? maxResults)
        => Call(FleetMethods.FindImage, new { templateBase64, target, monitor, x, y, width, height, hwnd, reference, threshold, maxResults });
}
