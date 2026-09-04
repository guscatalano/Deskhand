using System.Text.Json;
using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>Trajectory recording: start → auto steps per action (with screenshots) → stop, written as
/// meta.json + steps.jsonl + NNN.jpg. Uses a fake capture delegate so it's deterministic.</summary>
public class EpisodeRecorderTests
{
    [Fact]
    public void Records_a_trajectory_with_screenshots_and_labels_it()
    {
        var fakeShot = new byte[] { 1, 2, 3, 4 };
        var prev = EpisodeRecorder.CaptureFn;
        EpisodeRecorder.CaptureFn = () => fakeShot;
        try
        {
            string id = EpisodeRecorder.Start("demo task", "test-model");
            Assert.True(EpisodeRecorder.Status().Active);

            EpisodeRecorder.OnAction("mouse_click", "100,200 left x1", "ok");
            EpisodeRecorder.OnAction("send_keys", "ctrl+s", "ok");
            EpisodeRecorder.OnAction("mouse_move", "5,5", "ok");   // noisy → skipped, not a step
            EpisodeRecorder.OnAction("episode_start", "x", "ok");  // meta action → skipped

            var summary = EpisodeRecorder.Stop(success: true, note: "finished");
            Assert.False(summary.Active);
            Assert.True(summary.Success);
            Assert.Equal(2, summary.Steps);                        // only the two real actions

            var dir = EpisodeRecorder.DirFor(id);
            Assert.NotNull(dir);
            Assert.True(File.Exists(Path.Combine(dir!, "meta.json")));
            Assert.True(File.Exists(Path.Combine(dir!, "000.jpg")));   // initial observation
            Assert.True(File.Exists(Path.Combine(dir!, "001.jpg")));   // after action 1
            Assert.True(File.Exists(Path.Combine(dir!, "002.jpg")));

            var lines = File.ReadAllLines(Path.Combine(dir!, "steps.jsonl")).Where(l => l.Trim().Length > 0).ToList();
            // start + 2 actions + stop = 4 lines; none of them the skipped ones.
            Assert.Equal(4, lines.Count);
            var actions = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString()).ToList();
            Assert.Equal(new[] { "start", "mouse_click", "send_keys", "stop" }, actions);
            Assert.DoesNotContain("mouse_move", actions);

            var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "meta.json"))).RootElement;
            Assert.Equal("demo task", meta.GetProperty("task").GetString());
            Assert.Equal("test-model", meta.GetProperty("model").GetString());
            Assert.True(meta.GetProperty("success").GetBoolean());

            Assert.Contains(id, EpisodeRecorder.List());
        }
        finally { EpisodeRecorder.CaptureFn = prev; if (EpisodeRecorder.Status().Active) EpisodeRecorder.Stop(false, null); }
    }

    [Fact]
    public void Actions_outside_an_episode_are_ignored()
    {
        Assert.False(EpisodeRecorder.Status().Active);
        EpisodeRecorder.OnAction("mouse_click", "x", "ok");   // must not throw or start anything
        Assert.False(EpisodeRecorder.Status().Active);
    }
}
