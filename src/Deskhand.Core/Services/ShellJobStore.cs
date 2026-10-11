using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Deskhand.Core.Services;

/// <summary>Metadata for an async shell job (no output — see <see cref="ShellJobResultDto"/> for that).</summary>
public record ShellJobDto(
    string JobId, int Pid, string Shell, string Command, string Cwd,
    bool Running, int? ExitCode, bool Canceled, long DurationMs,
    string StartedAt, string? FinishedAt, string? Error = null, bool TimedOut = false);

/// <summary>A job snapshot plus its captured output so far (available live as the process runs).</summary>
public record ShellJobResultDto(ShellJobDto Job, string Stdout, string Stderr, bool Truncated);

/// <summary>
/// Tracks background ("async") shell runs started via <c>/shell/run async:true</c>. The caller gets a jobId and
/// pid back immediately; output is captured incrementally and the job can be cancelled (kills the process tree)
/// — which is how a long-running or hung installer is stopped without a dedicated kill call. Finished jobs are
/// kept for an hour (and capped) so results can be collected after the fact.
/// </summary>
public sealed class ShellJobStore
{
    private const int MaxOutputChars = 200_000;
    private const int MaxJobs = 100;
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public ShellJobDto Start(string shell, string command, string cwd, Process proc, int? killAfterMs = null)
    {
        Prune();
        var job = new Job(shell, command, cwd, proc, killAfterMs);
        _jobs[job.Id] = job;
        job.BeginCollect();
        return job.ToDto();
    }

    /// <summary>Block up to <paramref name="waitMs"/> for a job to finish (polling); returns its current result.</summary>
    public ShellJobResultDto? WaitFor(string id, int waitMs)
    {
        int waited = 0;
        while (waited < waitMs)
        {
            var r = Result(id);
            if (r is null || !r.Job.Running) return r;
            System.Threading.Thread.Sleep(150);
            waited += 150;
        }
        return Result(id);
    }

    public IReadOnlyList<ShellJobDto> List() =>
        _jobs.Values.OrderByDescending(j => j.StartedAtUtc).Select(j => j.ToDto()).ToList();

    public ShellJobResultDto? Result(string id) => _jobs.TryGetValue(id, out var j) ? j.ToResult() : null;

    public ShellJobDto? Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var j)) return null;
        j.Cancel();
        return j.ToDto();
    }

    private void Prune()
    {
        foreach (var kv in _jobs)
            if (!kv.Value.Running && kv.Value.FinishedAtUtc is { } f && DateTime.UtcNow - f > KeepFinished)
                _jobs.TryRemove(kv.Key, out _);
        if (_jobs.Count >= MaxJobs)
            foreach (var old in _jobs.Values.Where(j => !j.Running).OrderBy(j => j.StartedAtUtc).Take(_jobs.Count - MaxJobs + 1).ToList())
                _jobs.TryRemove(old.Id, out _);
    }

    private sealed class Job
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public int Pid { get; }
        public string Shell { get; }
        public string Command { get; }
        public string Cwd { get; }
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public DateTime? FinishedAtUtc { get; private set; }
        public volatile bool Running = true;
        public int? ExitCode { get; private set; }
        public bool Canceled { get; private set; }
        public bool TimedOut { get; private set; }
        public string? Error { get; private set; }

        private readonly int? _killAfterMs;
        private readonly Process _proc;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly StringBuilder _out = new();
        private readonly StringBuilder _err = new();
        private readonly object _lock = new();
        private long _durationMs;

        public Job(string shell, string command, string cwd, Process proc, int? killAfterMs = null)
        {
            Shell = shell; Command = command; Cwd = cwd; _proc = proc; _killAfterMs = killAfterMs;
            try { Pid = proc.Id; } catch { Pid = -1; }
        }

        public void BeginCollect()
        {
            _proc.OutputDataReceived += (_, e) => Append(_out, e.Data);
            _proc.ErrorDataReceived += (_, e) => Append(_err, e.Data);
            try { _proc.BeginOutputReadLine(); _proc.BeginErrorReadLine(); } catch { }
            if (_killAfterMs is int k && k > 0)   // background kill deadline (the job's own timeoutMs)
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(k); } catch { return; }
                    if (Running) { TimedOut = true; try { _proc.Kill(entireProcessTree: true); } catch { } }
                });
            _ = Task.Run(async () =>
            {
                try { await _proc.WaitForExitAsync(); } catch { }
                _sw.Stop();
                _durationMs = _sw.ElapsedMilliseconds;
                try { ExitCode = _proc.HasExited ? _proc.ExitCode : null; } catch { ExitCode = null; }
                FinishedAtUtc = DateTime.UtcNow;
                Running = false;
            });
        }

        public void Cancel()
        {
            if (!Running) return;
            Canceled = true;
            try { _proc.Kill(entireProcessTree: true); }
            catch (Exception ex) { Error = "Cancel failed: " + ex.Message; }
        }

        private void Append(StringBuilder sb, string? line)
        {
            if (line is null) return;
            lock (_lock) { if (sb.Length < MaxOutputChars) sb.Append(line).Append('\n'); }
        }

        public ShellJobDto ToDto() => new(
            Id, Pid, Shell, Command, Cwd, Running, ExitCode, Canceled,
            Running ? _sw.ElapsedMilliseconds : _durationMs,
            StartedAtUtc.ToString("o"), FinishedAtUtc?.ToString("o"), Error, TimedOut);

        public ShellJobResultDto ToResult()
        {
            string o, e;
            lock (_lock) { o = _out.ToString(); e = _err.ToString(); }
            bool truncated = false;
            (o, truncated) = Cap(o, truncated);
            (e, truncated) = Cap(e, truncated);
            return new ShellJobResultDto(ToDto(), o, e, truncated);
        }

        private static (string, bool) Cap(string s, bool already) =>
            s.Length > MaxOutputChars ? (s[..MaxOutputChars] + $"\n…[truncated, {s.Length - MaxOutputChars} more chars]", true) : (s, already);
    }
}
