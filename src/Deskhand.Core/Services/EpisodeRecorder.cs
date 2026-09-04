using System.Text;
using System.Text.Json;

namespace Deskhand.Core.Services;

public record EpisodeSummaryDto(
    bool Active, string? Id, string? Task, int Steps, string? StartedAt, string? EndedAt, bool? Success, string? Dir, string Note);

/// <summary>
/// Records a task as a <b>trajectory</b>: the ordered sequence of (screenshot observation, action, result) an
/// agent produced, so it can be replayed, evaluated, or used as training data — the one capability a computer-use
/// platform like c/ua has that a bare driver doesn't. It reuses what Deskhand already produces: a screenshot per
/// step (via a host-supplied capture delegate) and the action/detail/status from the <b>audit stream</b>, so
/// every governed action becomes a step automatically while an episode is active.
///
/// <para>On disk per episode: <c>meta.json</c> (task, model, timing, success), <c>steps.jsonl</c> (one step per
/// line), and <c>NNN.jpg</c> screenshots (000 = initial observation). Download the folder as a zip.</para>
/// </summary>
public static class EpisodeRecorder
{
    // Host sets this to a cheap, downscaled screen grab (raw/local backend — NOT audited, to avoid reentrancy).
    public static Func<byte[]?>? CaptureFn;

    // Noisy/low-signal audited actions that shouldn't each become a trajectory step.
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    { "mouse_move", "mouse_down", "mouse_up", "control", "output_budget", "webhook_add", "webhook_remove", "episode_start", "episode_stop" };

    private static readonly object _gate = new();
    private static readonly string BaseDir = Path.Combine(Path.GetTempPath(), "deskhand-episodes");
    private static bool _active;
    private static string? _id, _task, _model, _dir, _startedAt;
    private static int _steps;

    public static string Start(string? task, string? model)
    {
        lock (_gate)
        {
            if (_active) Finish(null, "superseded by a new episode");
            _id = "ep_" + Guid.NewGuid().ToString("N")[..10];
            _task = task; _model = model; _startedAt = DateTime.Now.ToString("o"); _steps = 0; _active = true;
            _dir = Path.Combine(BaseDir, _id);
            System.IO.Directory.CreateDirectory(_dir);
            WriteMeta(null, null);
            // step 000 = the initial observation, before any action.
            var shot = SafeCapture();
            if (shot is not null) File.WriteAllBytes(Path.Combine(_dir, "000.jpg"), shot);
            AppendStep(new { i = 0, ts = _startedAt, action = "start", detail = task, status = "ok", screenshot = shot is null ? null : "000.jpg" });
            return _id!;
        }
    }

    /// <summary>Audit-stream handler: one trajectory step per governed action (screenshot = state AFTER it).</summary>
    public static void OnAction(string action, string? detail, string status)
    {
        if (!_active || Skip.Contains(action)) return;
        lock (_gate)
        {
            if (!_active) return;
            _steps++;
            var shot = SafeCapture();
            string? name = null;
            if (shot is not null) { name = $"{_steps:000}.jpg"; try { File.WriteAllBytes(Path.Combine(_dir!, name), shot); } catch { name = null; } }
            AppendStep(new { i = _steps, ts = DateTime.Now.ToString("o"), action, detail, status, screenshot = name });
        }
    }

    public static EpisodeSummaryDto Stop(bool? success, string? note)
    {
        lock (_gate)
        {
            if (!_active) return Status();
            return Finish(success, note);
        }
    }

    public static EpisodeSummaryDto Status()
    {
        lock (_gate)
            return new EpisodeSummaryDto(_active, _id, _task, _steps, _startedAt, null, null, _dir,
                _active ? $"Recording '{_task}' ({_steps} steps so far)." : "No episode recording.");
    }

    public static IReadOnlyList<string> List()
    {
        try { return System.IO.Directory.Exists(BaseDir) ? System.IO.Directory.GetDirectories(BaseDir).Select(Path.GetFileName).OfType<string>().OrderByDescending(x => x).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    public static string? DirFor(string id)
    {
        var name = Path.GetFileName(id ?? "");
        var d = Path.Combine(BaseDir, name);
        return System.IO.Directory.Exists(d) ? d : null;
    }

    // ---- internals ----
    private static EpisodeSummaryDto Finish(bool? success, string? note)
    {
        string ended = DateTime.Now.ToString("o");
        AppendStep(new { i = _steps + 1, ts = ended, action = "stop", detail = note, status = success == false ? "fail" : "ok", screenshot = (string?)null });
        WriteMeta(success, ended);
        var summary = new EpisodeSummaryDto(false, _id, _task, _steps, _startedAt, ended, success, _dir,
            $"Episode '{_id}' saved with {_steps} steps → {_dir}");
        _active = false; _id = null; _task = null; _model = null; _dir = null; _startedAt = null; _steps = 0;
        return summary;
    }

    private static void WriteMeta(bool? success, string? endedAt)
    {
        try
        {
            var meta = new { id = _id, task = _task, model = _model, startedAt = _startedAt, endedAt, success, steps = _steps };
            File.WriteAllText(Path.Combine(_dir!, "meta.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void AppendStep(object step)
    {
        try { File.AppendAllText(Path.Combine(_dir!, "steps.jsonl"), JsonSerializer.Serialize(step) + "\n", new UTF8Encoding(false)); }
        catch { }
    }

    private static byte[]? SafeCapture() { try { return CaptureFn?.Invoke(); } catch { return null; } }
}
