namespace Deskhand.Core.Governance;

/// <summary>
/// Runtime safety switches, shared across a host. <see cref="Armed"/> is the master kill switch;
/// when false, all input and capture are refused. <see cref="InputEnabled"/> / <see cref="CaptureEnabled"/>
/// gate those capabilities independently (e.g. a read-only/observability deployment).
/// </summary>
public sealed class ControlState
{
    private volatile bool _armed = true;
    private volatile bool _input = true;
    private volatile bool _capture = true;
    private volatile bool _notify = true;

    public bool Armed { get => _armed; set => _armed = value; }
    public bool InputEnabled { get => _input; set => _input = value; }
    public bool CaptureEnabled { get => _capture; set => _capture = value; }

    /// <summary>Show a visible toast whenever a screenshot is taken (default on).</summary>
    public bool NotifyOnCapture { get => _notify; set => _notify = value; }

    public bool InputAllowed => _armed && _input;
    public bool CaptureAllowed => _armed && _capture;

    public static ControlState FromEnvironment()
    {
        static bool Off(string name) =>
            string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal) ||
            string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

        return new ControlState
        {
            InputEnabled = !Off("DESKHAND_DISABLE_INPUT"),
            CaptureEnabled = !Off("DESKHAND_DISABLE_CAPTURE"),
            Armed = !Off("DESKHAND_START_DISARMED"),
            NotifyOnCapture = !Off("DESKHAND_DISABLE_CAPTURE_TOAST"),
        };
    }
}
