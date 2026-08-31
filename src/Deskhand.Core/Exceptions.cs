namespace Deskhand.Core;

/// <summary>The requested element ref is unknown or was never registered.</summary>
public sealed class UnknownElementException(string reference)
    : Exception($"Unknown element ref '{reference}'. Re-query the tree to obtain a fresh ref.")
{
    public string Reference { get; } = reference;
}

/// <summary>The element existed but is gone and could not be re-resolved.</summary>
public sealed class StaleElementException(string reference)
    : Exception($"Element ref '{reference}' is stale and could not be re-resolved. Re-query the tree.")
{
    public string Reference { get; } = reference;
}

/// <summary>A requested UIA control pattern is not supported by the target element.</summary>
public sealed class PatternNotSupportedException(string pattern, string reference)
    : Exception($"Element ref '{reference}' does not support the '{pattern}' pattern.")
{
    public string Pattern { get; } = pattern;
    public string Reference { get; } = reference;
}

/// <summary>An action was requested that cannot be performed on the current desktop.</summary>
public sealed class DesktopUnavailableException(string message) : Exception(message);

/// <summary>The server is disarmed (kill switch engaged); invasive actions are refused.</summary>
public sealed class DisarmedException(string action)
    : Exception($"Deskhand is disarmed — '{action}' refused. Re-arm to allow input and capture.")
{
    public string Action { get; } = action;
}

/// <summary>A capability (input or capture) is disabled by policy/config.</summary>
public sealed class CapabilityDisabledException(string capability)
    : Exception($"The '{capability}' capability is disabled on this server.")
{
    public string Capability { get; } = capability;
}

/// <summary>A UI Automation operation exceeded the STA-executor timeout and was abandoned; the automation
/// worker was restarted so subsequent calls work. Transient — retry (after re-finding any element refs).</summary>
public sealed class BackendTimeoutException(string message) : Exception(message);
