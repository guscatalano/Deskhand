using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record ProcControlDto(bool Ok, int Pid, string? Name, string Action, string? Detail = null, string? Error = null);

/// <summary>Control a running process by pid: terminate (optionally the whole tree), suspend/resume (via
/// NtSuspendProcess/NtResumeProcess), or change priority class. Operating on protected or other-user processes
/// needs elevation — those return a clear access error rather than throwing.</summary>
public static class ProcessControlService
{
    public static ProcControlDto Kill(int pid, bool tree = true) => Do(pid, "kill", p => p.Kill(entireProcessTree: tree));

    public static ProcControlDto Suspend(int pid) => Do(pid, "suspend", p =>
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
