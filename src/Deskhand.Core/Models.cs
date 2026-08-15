namespace Deskhand.Core;

/// <summary>A rectangle in physical (device) pixels of the virtual desktop.</summary>
public record RectDto(int X, int Y, int Width, int Height);

/// <summary>Which desktop surface currently owns input.</summary>
public record DesktopStateDto(
    string Desktop,          // "default" | "secure" | "screensaver" | "unknown"
    string RawDesktopName,   // e.g. "Default", "Winlogon", or "" when inaccessible
    bool InputAvailable,     // false on the secure/locked desktop from a user-session process
    string Note);

public record MonitorDto(int Index, RectDto Bounds, bool Primary, double DpiScale);

public record MachineInfoDto(
    string MachineName,
    string UserName,
    string OsVersion,
    bool IsElevated,
    IReadOnlyList<MonitorDto> Monitors,
    RectDto VirtualScreen,
    DesktopStateDto DesktopState);

public record ElementInfoDto(
    string Ref,
    string? Name,
    string ControlType,
    string? AutomationId,
    string? ClassName,
    string? RuntimeId,
    RectDto? BoundingRect,
    bool IsEnabled,
    bool IsOffscreen,
    long NativeWindowHandle,
    int? ProcessId,
    IReadOnlyList<string> Patterns);

public record TreeNodeDto(
    ElementInfoDto Element,
    IReadOnlyList<TreeNodeDto> Children);

/// <summary>Query for <c>find_elements</c>. Conditions are AND-combined; null fields are ignored.</summary>
public record FindQuery(
    string? Name = null,
    string? AutomationId = null,
    string? ControlType = null,
    string? ClassName = null,
    string Scope = "descendants",   // "children" | "descendants" | "subtree"
    int Max = 100);

public enum ImageFormat { Png, Jpeg }

public record CaptureResultDto(
    string Desktop,
    RectDto Rect,
    int Monitor,
    double DpiScale,
    string Format,
    byte[] Bytes);
