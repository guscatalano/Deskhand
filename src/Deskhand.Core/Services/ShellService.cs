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
        // Timeout policy: null -> default (30s); <= 0 -> NO limit (wait indefinitely, for long installers /
        // downloads); otherwise exactly as requested, with no upper cap — the caller owns the wait.
        int? timeout = timeoutMs switch { null => DefaultTimeoutMs, <= 0 => null, _ => timeoutMs.Value };

        if (!Enabled)
            return Err(shell, command, cwd, "Shell is disabled. Set DESKHAND_ENABLE_SHELL=1 to enable it.");
        if (string.IsNullOrWhiteSpace(command))
            return Err(shell, command, cwd, "No command given.");
        if (cwd.Length > 0 && !Directory.Exists(cwd))
            return Err(shell, command, cwd, $"Working directory not found: {cwd}");

        var sw = Stopwatch.StartNew();
        Process proc;
        try { proc = Process.Start(BuildPsi(shell, command, cwd))!; }
        catch (Exception ex) { return Err(shell, command, cwd, "Failed to start shell: " + ex.Message); }
        try { proc.StandardInput.Close(); } catch { }   // no stdin -> prompts get EOF, not a hang

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        bool exited;
        if (timeout is null) { proc.WaitForExit(); exited = true; }   // no limit
        else exited = proc.WaitForExit(timeout.Value);
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
            Error: exited ? null : $"Timed out after {timeout} ms (process killed). Pass timeoutMs:0 to wait with no limit.");
    }

    /// <summary>Validate and start a shell process for an async job. The caller owns output collection (via the
    /// process's redirected streams) and lifetime. Returns the normalized shell name on success.</summary>
    public static (Process? proc, string shell, string? error) StartProcess(string? shell, string? command, string? cwd)
    {
        shell = Normalize(shell);
        command ??= "";
        cwd = (cwd ?? "").Trim().Trim('"');
        if (!Enabled) return (null, shell, "Shell is disabled. Set DESKHAND_ENABLE_SHELL=1 to enable it.");
        if (string.IsNullOrWhiteSpace(command)) return (null, shell, "No command given.");
        if (cwd.Length > 0 && !Directory.Exists(cwd)) return (null, shell, $"Working directory not found: {cwd}");
        try
        {
            var proc = Process.Start(BuildPsi(shell, command, cwd))!;
            try { proc.StandardInput.Close(); } catch { }   // no stdin -> prompts get EOF, not a hang
            return (proc, shell, null);
        }
        catch (Exception ex) { return (null, shell, "Failed to start shell: " + ex.Message); }
    }

    public static string NormalizeShell(string? shell) => Normalize(shell);

    /// <summary>Start a shell running a SCRIPT BODY written to a temp file and invoked with <c>-File</c> — so the
    /// script is never re-parsed/interpolated by a -Command layer (<c>$_</c>, <c>$env:</c>, <c>%VAR%</c> survive
    /// verbatim). Args are passed as real argv entries. Caller owns output/lifetime via the returned process.</summary>
    public static (Process? proc, string shell, string? error, string? scriptPath) StartScriptProcess(string? shell, string? scriptBody, string[]? args, string? cwd)
    {
        shell = Normalize(shell);
        scriptBody ??= "";
        cwd = (cwd ?? "").Trim().Trim('"');
        if (!Enabled) return (null, shell, "Shell is disabled. Set DESKHAND_ENABLE_SHELL=1 to enable it.", null);
        if (string.IsNullOrWhiteSpace(scriptBody)) return (null, shell, "No script given.", null);
        if (cwd.Length > 0 && !Directory.Exists(cwd)) return (null, shell, $"Working directory not found: {cwd}", null);
        try
        {
            string ext = shell == "cmd" ? ".cmd" : ".ps1";
            string path = Path.Combine(Path.GetTempPath(), "deskhand-" + Guid.NewGuid().ToString("N")[..8] + ext);
            File.WriteAllText(path, scriptBody);
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            if (cwd.Length > 0) psi.WorkingDirectory = cwd;
            if (shell == "cmd")
            {
                psi.FileName = "cmd.exe"; psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(path);
            }
            else
            {
                psi.FileName = shell == "pwsh" ? "pwsh.exe" : "powershell.exe";
                psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(path);
            }
            foreach (var a in args ?? Array.Empty<string>()) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi)!;
            try { proc.StandardInput.Close(); } catch { }
            return (proc, shell, null, path);
        }
        catch (Exception ex) { return (null, shell, "Failed to start script: " + ex.Message, null); }
    }

    private static ProcessStartInfo BuildPsi(string shell, string command, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // closed immediately after start so interactive prompts hit EOF and
                                            // fail fast instead of hanging the command until its timeout
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
        return psi;
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
