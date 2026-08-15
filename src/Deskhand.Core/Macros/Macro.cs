using System.Text.Json;
using Deskhand.Core.Fleet;

namespace Deskhand.Core.Macros;

/// <summary>A recorded, re-resolvable selector for a UIA element (refs are volatile, so we store
/// how to find the element again at playback time).</summary>
public sealed record ElementSelectorDto(string? Name, string? AutomationId, string? ControlType, string? ClassName);

/// <summary>One recorded action. <c>Kind</c> is "input" (coordinate/keyboard, replayed verbatim) or
/// "uia" (re-resolved via <see cref="Selector"/> then acted on). <c>TMs</c> is ms since record start.</summary>
public sealed record MacroStep(long TMs, string Kind, string Method, JsonElement Args, ElementSelectorDto? Selector);

/// <summary>An ordered list of recorded steps.</summary>
public sealed record Macro(IReadOnlyList<MacroStep> Steps);

/// <summary>
/// Captures actions taken through the governed backend into a <see cref="Macro"/>. Only state-changing
/// actions are recorded (input + UIA act); reads and captures are ignored. Thread-safe.
/// </summary>
public sealed class MacroRecorder
{
    private readonly object _lock = new();
    private List<MacroStep>? _steps;
    private long _startTick;

    public Macro? LastMacro { get; private set; }

    public bool IsRecording { get { lock (_lock) return _steps is not null; } }

    public int CurrentCount { get { lock (_lock) return _steps?.Count ?? 0; } }

    public long ElapsedMs { get { lock (_lock) return _steps is null ? 0 : Environment.TickCount64 - _startTick; } }

    public void Start()
    {
        lock (_lock) { _steps = new List<MacroStep>(); _startTick = Environment.TickCount64; }
    }

    public Macro Stop()
    {
        lock (_lock)
        {
            var macro = new Macro(_steps ?? new List<MacroStep>());
            _steps = null;
            LastMacro = macro;
            return macro;
        }
    }

    public void RecordInput(string method, object args) => Add(method, "input", args, null);

    public void RecordUia(string method, ElementInfoDto info, object? extra)
        => Add(method, "uia", extra ?? new { }, new ElementSelectorDto(info.Name, info.AutomationId, info.ControlType, info.ClassName));

    /// <summary>Insert an explicit expectation: playback blocks here until the element appears.</summary>
    public void RecordWait(ElementSelectorDto selector, int timeoutMs)
        => Add(FleetMethods.WaitForElement, "wait", new { timeoutMs }, selector);

    private void Add(string method, string kind, object args, ElementSelectorDto? selector)
    {
        lock (_lock)
        {
            if (_steps is null) return;
            long t = Environment.TickCount64 - _startTick;
            _steps.Add(new MacroStep(t, kind, method, JsonSerializer.SerializeToElement(args, FleetJson.Options), selector));
        }
    }
}
