using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record ProcControlDto(bool Ok, int Pid, string? Name, string Action, string? Detail = null, string? Error = null);

/// <summary>Control a running process by pid: terminate (optionally the whole tree), suspend/resume (via
/// NtSuspendProcess/NtResumeProcess), or change priority class. Operating on protected or other-user processes
/// needs elevation — those return a clear access error rather than throwing.
///
/// <para><b>Self-protection.</b> Terminate/suspend refuse to touch Deskhand's own process (so automation can't
/// cut its own legs off) — that block is absolute. OS-critical processes (winlogon, lsass, csrss, …) are also
/// refused unless <c>force</c> is passed. This guard runs in the service, so it protects the fleet path too.</para>
/// </summary>
public static class ProcessControlService
{
    // Deskhand's own executables — never terminate/suspend these by pid.
    private static readonly string[] Own =
        { "deskhand-http", "deskhand-agent", "deskhand-fleet-server", "deskhand-launcher", "deskhand-broker", "deskhand-secure", "deskhand-rdp" };
    // Killing these logs you off or bugchecks the box; blocked unless force=true.
    private static readonly string[] Critical =
        { "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "fontdrvhost", "dwm", "logonui" };

    public static ProcControlDto Kill(int pid, bool tree = true, bool force = false) =>
        Guarded(pid, "kill", force, p => p.Kill(entireProcessTree: tree));

    public static ProcControlDto Suspend(int pid, bool force = false) => Guarded(pid, "suspend", force, p =>
    {
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, p.Id);
        if (h == IntPtr.Zero) throw new InvalidOperationException($"OpenProcess failed (Win32 {Marshal.GetLastWin32Error()}).");
        try { if (NtSuspendProcess(h) != 0) throw new InvalidOperationException("NtSuspendProcess failed."); }
        finally { CloseHandle(h); }
    });

    public static ProcControlDto Resume(int pid) => Do(pid, "resume", p =>
    {
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, p.Id);
        if (h == IntPtr.Zero) throw new InvalidOperationException($"OpenProcess failed (Win32 {Marshal.GetLastWin32Error()}).");
        try { if (NtResumeProcess(h) != 0) throw new InvalidOperationException("NtResumeProcess failed."); }
        finally { CloseHandle(h); }
    });

    public static ProcControlDto SetPriority(int pid, string level)
    {
        var pc = (level ?? "").Trim().ToLowerInvariant() switch
        {
            "idle" => ProcessPriorityClass.Idle,
            "belownormal" or "below" => ProcessPriorityClass.BelowNormal,
            "normal" => ProcessPriorityClass.Normal,
            "abovenormal" or "above" => ProcessPriorityClass.AboveNormal,
            "high" => ProcessPriorityClass.High,
            "realtime" => ProcessPriorityClass.RealTime,
            _ => (ProcessPriorityClass?)null,
        };
        if (pc is null) return new ProcControlDto(false, pid, null, "priority", Error: "level must be idle|belownormal|normal|abovenormal|high|realtime.");
        return Do(pid, "priority", p => p.PriorityClass = pc.Value, pc.ToString());
    }

    // Terminate/suspend go through here so self- and critical-process protection is unavoidable.
    private static ProcControlDto Guarded(int pid, string action, bool force, Action<Process> act)
    {
        var (blocked, name) = Protection(pid, force);
        if (blocked is not null) return new ProcControlDto(false, pid, name, action, Error: blocked);
        return Do(pid, action, act);
    }

    /// <summary>Returns a refusal reason (and the resolved name) if this pid is Deskhand itself or a protected
    /// OS process; null if the action may proceed.</summary>
    private static (string? reason, string? name) Protection(int pid, bool force)
    {
        if (pid == Environment.ProcessId)
            return ("Refusing to act on Deskhand's own process (pid " + pid + ").", "deskhand");
        string? name = TryName(pid);
        var key = name?.ToLowerInvariant();
        if (key is not null && Own.Contains(key))
            return ($"Refusing to act on a Deskhand process ('{name}'). This is protected to avoid killing the automation host.", name);
        if (pid <= 4 || (key is not null && Critical.Contains(key)))
            return force ? (null, name) : ($"'{name ?? pid.ToString()}' is a protected system process — killing it would log off or crash Windows. Pass force=true only if you truly mean it.", name);
        return (null, name);
    }

    private static string? TryName(int pid) { try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return null; } }

    private static ProcControlDto Do(int pid, string action, Action<Process> act, string? detail = null)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch { return new ProcControlDto(false, pid, null, action, Error: $"No process with pid {pid}."); }
        string? name = null;
        try { name = p.ProcessName; } catch { }
        try { act(p); return new ProcControlDto(true, pid, name, action, detail); }
        catch (Exception ex) { return new ProcControlDto(false, pid, name, action, Error: Describe(ex)); }
        finally { p.Dispose(); }
    }

    private static string Describe(Exception ex) =>
        (ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 5) || ex.Message.Contains("Access is denied")
            ? "Access denied — the target may be protected or owned by another user; run Deskhand elevated."
            : ex.Message;

    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
}
