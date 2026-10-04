using System.Diagnostics;

namespace Deskhand.Core.Services;

public record TaskActionDto(bool Ok, string Task, string Action, int ExitCode, string? Output = null, string? Error = null);

/// <summary>Run / end / enable / disable a Windows Scheduled Task by name (path), via <c>schtasks.exe</c>.
/// Complements the read-only scheduled-task inventory. Tasks in protected folders may need elevation.</summary>
public static class ScheduledTaskService
{
    public static TaskActionDto Run(string task) => Exec(task, "run", "/Run", "/TN", task);
    public static TaskActionDto End(string task) => Exec(task, "end", "/End", "/TN", task);
    public static TaskActionDto Enable(string task) => Exec(task, "enable", "/Change", "/TN", task, "/ENABLE");
    public static TaskActionDto Disable(string task) => Exec(task, "disable", "/Change", "/TN", task, "/DISABLE");
    public static TaskActionDto Delete(string task) => Exec(task, "delete", "/Delete", "/TN", task, "/F");

    /// <summary>Create (or overwrite) a scheduled task. <paramref name="schedule"/> is one of
    /// ONCE|MINUTE|HOURLY|DAILY|WEEKLY|MONTHLY|ONLOGON|ONSTART|ONIDLE. ONCE/DAILY typically need a
    /// <paramref name="startTime"/> (HH:mm); ONCE also a <paramref name="startDate"/> (MM/dd/yyyy).</summary>
    public static TaskActionDto Create(string task, string command, string schedule,
        string? startTime = null, string? startDate = null, bool runLevelHighest = false)
    {
        task = (task ?? "").Trim();
        command = (command ?? "").Trim();
        schedule = (schedule ?? "").Trim().ToUpperInvariant();
        if (task.Length == 0) return new TaskActionDto(false, task, "create", -1, Error: "No task name.");
        if (command.Length == 0) return new TaskActionDto(false, task, "create", -1, Error: "No command to run (TR).");
        string[] valid = { "ONCE", "MINUTE", "HOURLY", "DAILY", "WEEKLY", "MONTHLY", "ONLOGON", "ONSTART", "ONIDLE" };
        if (!valid.Contains(schedule)) return new TaskActionDto(false, task, "create", -1, Error: $"schedule must be one of {string.Join("|", valid)}.");

        var args = new List<string> { "/Create", "/TN", task, "/TR", command, "/SC", schedule, "/F" };
        if (!string.IsNullOrWhiteSpace(startTime)) { args.Add("/ST"); args.Add(startTime!.Trim()); }
        if (!string.IsNullOrWhiteSpace(startDate)) { args.Add("/SD"); args.Add(startDate!.Trim()); }
        if (runLevelHighest) { args.Add("/RL"); args.Add("HIGHEST"); }
        return Exec(task, "create", args.ToArray());
    }

    private static TaskActionDto Exec(string task, string action, params string[] args)
    {
        task = (task ?? "").Trim();
        if (task.Length == 0) return new TaskActionDto(false, task, action, -1, Error: "No task name.");
        var psi = new ProcessStartInfo("schtasks.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            string text = (outp + err).Trim();
            return p.ExitCode == 0
                ? new TaskActionDto(true, task, action, p.ExitCode, text)
                : new TaskActionDto(false, task, action, p.ExitCode, text, text.Length > 0 ? text : $"schtasks exited {p.ExitCode}.");
        }
        catch (Exception ex) { return new TaskActionDto(false, task, action, -1, Error: ex.Message); }
    }
}
