using System.Diagnostics;

namespace Deskhand.Core.Services;

public record ShellResultDto(
    string Shell, string Command, string Cwd, int ExitCode,
    string Stdout, string Stderr, long DurationMs, bool TimedOut, bool Truncated, string? Error = null);

/// <summary>
/// One-shot command execution: run a single command in PowerShell or cmd and return its output. Stateless —
/// each call is a fresh process, so working directory / variables do NOT persist between calls (pass cwd for
/// a starting directory). This is the most powerful capability in Deskhand (arbitrary code as the current
/// user), so it is OFF unless <c>DESKHAND_ENABLE_SHELL</c> is set, and the host layer additionally requires
/// the kill switch to be armed and audits every command.
/// </summary>
public static class ShellService
{
    private const int MaxOutputChars = 200_000;   // cap each stream so one command can't flood the response
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 600_000;

    /// <summary>Shell execution is opt-in: set DESKHAND_ENABLE_SHELL=1 (or true/yes/on) to allow it.</summary>
    public static bool Enabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DESKHAND_ENABLE_SHELL")?.Trim().ToLowerInvariant();
            return v is "1" or "true" or "yes" or "on";
        }
    }

    public static ShellResultDto Run(string? shell, string? command, string? cwd, int? timeoutMs)
    {
        shell = Normalize(shell);
        command ??= "";
        cwd = (cwd ?? "").Trim().Trim('"');
        int timeout = Math.Clamp(timeoutMs ?? DefaultTimeoutMs, 1_000, MaxTimeoutMs);

        if (!Enabled)
            return Err(shell, command, cwd, "Shell is disabled. Set DESKHAND_ENABLE_SHELL=1 to enable it.");
        if (string.IsNullOrWhiteSpace(command))
            return Err(shell, command, cwd, "No command given.");
        if (cwd.Length > 0 && !Directory.Exists(cwd))
            return Err(shell, command, cwd, $"Working directory not found: {cwd}");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (cwd.Length > 0) psi.WorkingDirectory = cwd;

        if (shell == "cmd")
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");   // skip AutoRun
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = shell == "pwsh" ? "pwsh.exe" : "powershell.exe";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }

        var sw = Stopwatch.StartNew();
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex) { return Err(shell, command, cwd, "Failed to start shell: " + ex.Message); }

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        bool exited = proc.WaitForExit(timeout);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(2000); } catch { }
        }
        sw.Stop();

        string stdout = Safe(outTask), stderr = Safe(errTask);
        bool truncated = false;
        (stdout, truncated) = Cap(stdout, truncated);
        (stderr, truncated) = Cap(stderr, truncated);
        int code = exited ? SafeExit(proc) : -1;
        proc.Dispose();

        return new ShellResultDto(shell, command, cwd, code, stdout, stderr, sw.ElapsedMilliseconds,
            TimedOut: !exited, Truncated: truncated,
            Error: exited ? null : $"Timed out after {timeout} ms (process killed).");
    }

    private static string Normalize(string? shell) => (shell ?? "").Trim().ToLowerInvariant() switch
    {
        "cmd" or "cmd.exe" => "cmd",
        "pwsh" or "pwsh.exe" or "powershell7" or "core" => "pwsh",
        _ => "powershell",
    };

    private static (string, bool) Cap(string s, bool already) =>
        s.Length > MaxOutputChars ? (s[..MaxOutputChars] + $"\n…[truncated, {s.Length - MaxOutputChars} more chars]", true) : (s, already);

    private static string Safe(Task<string> t) { try { return t.GetAwaiter().GetResult() ?? ""; } catch { return ""; } }
    private static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }
    private static ShellResultDto Err(string shell, string cmd, string cwd, string error) =>
        new(shell, cmd, cwd, -1, "", "", 0, false, false, error);
}
