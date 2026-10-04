using System.Management;

namespace Deskhand.Core.Services;

public record ServiceControlDto(bool Ok, string Name, string Action, string? State = null, string? Error = null);

/// <summary>Start / stop / restart a Windows service by name, via WMI (Win32_Service) — same source the
/// read-only service inventory uses. Most service changes require elevation; the WMI return code is mapped to a
/// readable error (e.g. access denied) rather than surfaced as a bare number.</summary>
public static class ServiceControlService
{
    public static ServiceControlDto Start(string name) => Invoke(name, "start", "StartService");
    public static ServiceControlDto Stop(string name) => Invoke(name, "stop", "StopService");

    /// <summary>Create a Windows service (via sc.exe). binPath is the full command line for the service
    /// executable. startMode: auto|demand|disabled (default demand). Needs elevation.</summary>
    public static ServiceControlDto Create(string name, string? binPath, string? displayName, string? startMode)
    {
        name = (name ?? "").Trim();
        binPath = (binPath ?? "").Trim();
        if (name.Length == 0) return new ServiceControlDto(false, name, "create", Error: "No service name.");
        if (binPath.Length == 0) return new ServiceControlDto(false, name, "create", Error: "No binPath (the service executable command line).");
        string start = (startMode ?? "demand").Trim().ToLowerInvariant() switch
        {
            "auto" or "automatic" => "auto",
            "disabled" => "disabled",
            "boot" => "boot",
            "system" => "system",
            _ => "demand",
        };
        var args = new List<string> { "create", name, "binPath=", binPath, "start=", start };
        if (!string.IsNullOrWhiteSpace(displayName)) { args.Add("DisplayName="); args.Add(displayName!); }
        return Sc(name, "create", args);
    }

    public static ServiceControlDto Delete(string name) => Sc(name, "delete", new List<string> { "delete", (name ?? "").Trim() });

    private static ServiceControlDto Sc(string name, string action, List<string> args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            string outp = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(20000);
            return p.ExitCode == 0
                ? new ServiceControlDto(true, name, action, State(name))
                : new ServiceControlDto(false, name, action, Error: outp.Length > 0 ? outp : $"sc exited {p.ExitCode} (service changes usually need elevation).");
        }
        catch (Exception ex) { return new ServiceControlDto(false, name, action, Error: ex.Message); }
    }

    public static ServiceControlDto Restart(string name)
    {
        var stop = Invoke(name, "restart", "StopService");
        // Ignore "not started" when restarting; then wait for Stopped and start again.
        WaitForState(name, "Stopped", 15000);
        var start = Invoke(name, "restart", "StartService");
        return start.Ok ? start : new ServiceControlDto(false, name, "restart", start.State, start.Error ?? stop.Error);
    }

    private static ServiceControlDto Invoke(string name, string action, string method)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new ServiceControlDto(false, name, action, Error: "No service name.");
        try
        {
            using var svc = new ManagementObject($"Win32_Service.Name='{name.Replace("'", "\\'")}'");
            svc.Get();                          // throws if the service doesn't exist
            // Self-protection: don't let Deskhand stop/restart the service that is hosting it.
            if (method == "StopService")
            {
                var hostPid = SafePid(svc);
                if (hostPid == Environment.ProcessId)
                    return new ServiceControlDto(false, name, action, SafeState(svc), "Refusing to stop the service that is hosting Deskhand itself.");
            }
            uint rc = (uint)svc.InvokeMethod(method, null);
            string? state = SafeState(svc);
            return rc == 0
                ? new ServiceControlDto(true, name, action, state)
                : new ServiceControlDto(false, name, action, state, ServiceReturn(rc));
        }
        catch (ManagementException me) when (me.Message.Contains("Not found", StringComparison.OrdinalIgnoreCase))
        { return new ServiceControlDto(false, name, action, Error: $"Service '{name}' not found."); }
        catch (Exception ex) { return new ServiceControlDto(false, name, action, Error: ex.Message); }
    }

    public static string? State(string name)
    {
        try { using var svc = new ManagementObject($"Win32_Service.Name='{name.Replace("'", "\\'")}'"); svc.Get(); return SafeState(svc); }
        catch { return null; }
    }

    private static void WaitForState(string name, string want, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (string.Equals(State(name), want, StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(300);
        }
    }

    private static string? SafeState(ManagementObject svc) { try { return svc["State"]?.ToString(); } catch { return null; } }
    private static int SafePid(ManagementObject svc) { try { return Convert.ToInt32(svc["ProcessId"] ?? 0); } catch { return 0; } }

    // Win32_Service method return codes (subset that matters operationally).
    private static string ServiceReturn(uint rc) => rc switch
    {
        2 => "Access denied — run Deskhand elevated.",
        3 => "Dependent services are running.",
        5 => "Service cannot accept control at this time.",
        7 => "The service request timed out.",
        10 => "Service already running.",
        14 => "Service disabled.",
        _ => $"WMI return code {rc}.",
    };
}
