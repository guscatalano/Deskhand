using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>The auto-dismisser's safety rails: match-all rules are rejected (never "close anything"), config is
/// opt-in, and a disarmed tick does nothing.</summary>
public class AutoDismissTests
{
    [Fact]
    public void Match_all_rules_are_rejected()
    {
        var s = AutoDismissService.Configure(new[] { new AutoRule(null, null, "hide"), new AutoRule("", "  ", "close") }, enabled: false);
        Assert.Equal(0, s.RuleCount);   // both are match-all → dropped
        AutoDismissService.Configure(System.Array.Empty<AutoRule>(), false);   // reset
    }

    [Fact]
    public void A_titled_rule_is_kept_and_enable_toggles()
    {
        try
        {
            var s = AutoDismissService.Configure(new[] { new AutoRule("Trial", null, "hide") }, enabled: true);
            Assert.Equal(1, s.RuleCount);
            Assert.True(s.Enabled);
            Assert.Contains("armed", s.Note, System.StringComparison.OrdinalIgnoreCase);
        }
        finally { AutoDismissService.Configure(System.Array.Empty<AutoRule>(), false); }
    }

    [Fact]
    public void Disarmed_tick_never_acts()
    {
        AutoDismissService.Configure(new[] { new AutoRule("zzz-no-such-window", null, "hide") }, enabled: true);
        try
        {
            var before = AutoDismissService.Status().Acted;
            AutoDismissService.Tick(armed: false);              // kill-switch bound: must be a no-op
            Assert.Equal(before, AutoDismissService.Status().Acted);
        }
        finally { AutoDismissService.Configure(System.Array.Empty<AutoRule>(), false); }
    }

    [Fact]
    public void Log_is_readable()
    {
        Assert.NotNull(AutoDismissService.Log(10));
    }
}
