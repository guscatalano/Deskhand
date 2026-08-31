using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>
/// Firewall rule listing + the close-only-what-we-opened safety guard. Adding/removing rules needs Administrator
/// so the happy path isn't exercised here, but listing (COM enumeration) and the guard that refuses to remove a
/// rule Deskhand didn't create are — and the guard runs without elevation because no managed rule matches.
/// </summary>
public class FirewallServiceTests : IDisposable
{
    private const string Flag = "DESKHAND_ENABLE_FIREWALL_ADMIN";
    public FirewallServiceTests() => Environment.SetEnvironmentVariable(Flag, null);
    public void Dispose() => Environment.SetEnvironmentVariable(Flag, null);

    [Fact]
    public void List_enumerates_rules_via_the_firewall_com_api()
    {
        var r = FirewallService.List(max: 10);
        Assert.Null(r.Error);
        Assert.True(r.Total > 0, "a Windows box has firewall rules");
        Assert.True(r.Returned <= 10);
        Assert.All(r.Rules, rule => Assert.Contains(rule.Direction, new[] { "in", "out" }));
    }

    [Fact]
    public void Port_filter_narrows_the_result()
    {
        var r = FirewallService.List(port: 3389, max: 50);   // RDP; may be 0 on a stripped host, but must not throw
        Assert.Null(r.Error);
        Assert.All(r.Rules, rule => Assert.Contains("3389", rule.LocalPorts ?? ""));
    }

    [Fact]
    public void Open_is_disabled_until_opted_in()
    {
        var r = FirewallService.OpenPort(48000, "tcp", "in");
        Assert.False(r.Ok);
        Assert.Contains(Flag, r.Error);
    }

    [Fact]
    public void Close_refuses_a_rule_deskhand_did_not_open()
    {
        Environment.SetEnvironmentVariable(Flag, "1");
        // 3389 (RDP) is a system rule, never Deskhand-managed — the guard must refuse it (no elevation needed
        // to reach this decision: enumeration finds no managed match, so Remove is never called).
        var r = FirewallService.ClosePort(3389, "tcp", "in");
        Assert.False(r.Ok);
        Assert.Equal(0, r.Removed);
        Assert.Contains("only closes ports it opened", r.Error);
    }

    [Fact]
    public void Managed_listing_only_returns_deskhand_rules()
    {
        var r = FirewallService.ListManaged();
        Assert.Null(r.Error);
        Assert.All(r.Rules, rule => Assert.True(rule.Managed));
    }
}
