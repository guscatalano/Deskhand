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
