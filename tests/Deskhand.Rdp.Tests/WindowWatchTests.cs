using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>The complete Win32 enumeration + the report-only appearance watcher (no clicking).</summary>
public class WindowWatchTests
{
    [Fact]
    public void Enumeration_finds_real_top_level_windows()
    {
        var wins = Win32Windows.List();
        Assert.NotEmpty(wins);                                  // the test host has windows
        Assert.All(wins, w => Assert.NotEqual(0, w.Hwnd));
        Assert.Contains(wins, w => !string.IsNullOrEmpty(w.Title) || !string.IsNullOrEmpty(w.Class));
    }

    [Fact]
    public void Baseline_then_immediate_changes_reports_nothing_new()
    {
        var snap = WindowWatchService.Baseline();
        Assert.True(snap.Count > 0);
        var changes = WindowWatchService.Changes(snap.BaselineId);
        Assert.False(changes.BaselineJustCreated);
        Assert.Equal(0, changes.AppearedCount);                 // nothing spawned in-between
    }

    [Fact]
    public void Unknown_baseline_establishes_one_and_reports_nothing()
    {
        var changes = WindowWatchService.Changes("wb_nonexistent_" + System.Guid.NewGuid().ToString("N")[..6]);
        Assert.True(changes.BaselineJustCreated);
        Assert.Equal(0, changes.AppearedCount);
        Assert.Contains("baseline", changes.Note, System.StringComparison.OrdinalIgnoreCase);
    }
}
