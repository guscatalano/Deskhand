using System.Diagnostics;
using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>Process / service / environment / UAC control. Privileged mutations (machine env, service changes,
/// UAC registry) aren't performed here — those return clean access errors off-box — but the process lifecycle,
/// env round-trip, validation, and read paths are exercised.</summary>
public class SystemControlTests
{
    [Fact]
    public void Process_kill_terminates_a_child()
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var r = ProcessControlService.Kill(p.Id, tree: true);
            Assert.True(r.Ok, r.Error);
            Assert.True(p.WaitForExit(5000), "process should have exited after kill");
        }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
    }

    [Fact]
    public void Process_suspend_and_resume_succeed_on_a_child()
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.True(ProcessControlService.Suspend(p.Id).Ok);
            Assert.True(ProcessControlService.Resume(p.Id).Ok);
        }
        finally { try { p.Kill(true); } catch { } p.Dispose(); }
    }

    [Fact]
    public void Process_control_rejects_unknown_pid_and_bad_priority()
    {
        Assert.False(ProcessControlService.Kill(999_999).Ok);
        var bad = ProcessControlService.SetPriority(999_999, "turbo");
        Assert.False(bad.Ok);
        Assert.Contains("level must", bad.Error);
    }

    [Fact]
    public void Env_round_trips_at_process_scope_and_deletes()
    {
        string name = "DESKHAND_TEST_" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(EnvironmentService.Set(name, "hello-42", "process").Ok);
        Assert.Equal("hello-42", EnvironmentService.Get(name, "process").Value);
        Assert.True(EnvironmentService.Set(name, null, "process").Ok);   // delete
        Assert.Null(EnvironmentService.Get(name, "process").Value);
    }

    [Fact]
    public void Service_state_reads_a_known_service()
    {
        // Every Windows box has the Windows Event Log service ("EventLog"); reading its state needs no elevation.
        var state = ServiceControlService.State("EventLog");
        Assert.False(string.IsNullOrEmpty(state));
    }

    [Fact]
    public void Service_control_rejects_a_bad_action()
    {
        var r = ServiceControlService.Restart("");   // empty name
        Assert.False(r.Ok);
    }

    [Fact]
    public void Uac_status_reports_a_summary()
    {
        var s = UacService.Status();
        Assert.False(string.IsNullOrEmpty(s.Summary));
    }

    [Fact]
    public void Uac_admin_behavior_validates_range()
    {
        var bad = UacService.SetAdminBehavior(9);
        Assert.False(bad.Ok);
        Assert.Contains("0..5", bad.Error);
    }
}
