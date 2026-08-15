namespace Deskhand.Core.Fleet;

/// <summary>
/// Everything an agent exposes to the fleet: the automation backend plus the observation services
/// (event feed, process watcher, screen recorder, user-input recorder). Bundled so
/// <see cref="AgentDispatcher"/> can route both automation and observation commands from one context.
/// </summary>
public sealed class AgentServices
{
    public required IAutomationBackend Backend { get; init; }
    public Events.EventHub? Events { get; init; }
    public Events.ProcessWatcher? Processes { get; init; }
    public Services.ScreenRecorder? Recorder { get; init; }
    public Services.InputRecorder? Input { get; init; }
}
