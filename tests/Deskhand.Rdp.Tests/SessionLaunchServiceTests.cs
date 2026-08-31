using System.Diagnostics;
using Deskhand.Core.Services;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>
/// Tests for launching into a specific session/desktop/user. Cross-session and cross-user launches need
/// LocalSystem, so those aren't exercised here — but the gating, argument parsing, Win32 error mapping (which
/// proves the P/Invoke signatures marshal correctly), and the non-privileged same-session fast path are.
/// </summary>
public class SessionLaunchServiceTests : IDisposable
{
    private const string Flag = "DESKHAND_ENABLE_SESSION_LAUNCH";
    public SessionLaunchServiceTests() => Environment.SetEnvironmentVariable(Flag, null);
    public void Dispose() => Environment.SetEnvironmentVariable(Flag, null);

    private static void Enable() => Environment.SetEnvironmentVariable(Flag, "1");

    [Fact]
    public void Disabled_by_default_returns_a_clear_opt_in_error()
    {
        var r = SessionLaunchService.Launch("notepad.exe", null, null, null, null, LaunchAs.SessionUser, null, null, null, false);
        Assert.False(r.Ok);
        Assert.Contains(Flag, r.Error);
    }

    [Fact]
    public void ParseAs_maps_the_user_modes()
    {
        Assert.Equal(LaunchAs.System, SessionLaunchService.ParseAs("system"));
        Assert.Equal(LaunchAs.Credentials, SessionLaunchService.ParseAs("credentials"));
        Assert.Equal(LaunchAs.SessionUser, SessionLaunchService.ParseAs(null));
        Assert.Equal(LaunchAs.SessionUser, SessionLaunchService.ParseAs("whatever"));
    }

    [Fact]
    public void Credentials_mode_requires_a_username()
    {
        Enable();
        var r = SessionLaunchService.Launch("cmd.exe", "/c exit", null, null, null, LaunchAs.Credentials, null, null, null, true);
        Assert.False(r.Ok);
        Assert.Contains("requires a user", r.Error);
    }

    [Fact]
    public void Credentials_with_a_bogus_account_maps_to_a_logon_failure()
    {
        Enable();
        // A non-existent account fails at LogonUser identically on any host (privileged or not), so this is a
        // deterministic exercise of the credentials token path + Win32 error mapping.
        var r = SessionLaunchService.Launch("cmd.exe", "/c exit", null, null, null, LaunchAs.Credentials,
            "deskhand_no_such_user_" + Guid.NewGuid().ToString("N")[..8], ".", "not-a-real-password", true);
        Assert.False(r.Ok);
        Assert.Contains("LogonUser", r.Error);
        Assert.NotEqual(0, r.Win32);          // a real Win32 code came back through the P/Invoke
    }

    [Fact]
    public void Fast_path_same_session_launches_on_the_default_desktop()
    {
        Enable();
        var r = SessionLaunchService.Launch("cmd.exe", "/c exit", null, null, null, LaunchAs.SessionUser, null, null, null, true);

        // On an interactive host this launches (Ok, pid). On a headless/Session-0 CI host it may be refused with
        // a structured Win32 error — either way it must not throw and must report a valid, non-disabled result.
        Assert.Equal("SessionUser", r.As);
        Assert.False(r.Error?.Contains(Flag) ?? false, "should be past the opt-in gate");
        if (r.Ok)
        {
            Assert.True(r.ProcessId > 0);
            try { Process.GetProcessById(r.ProcessId).Kill(); } catch { /* already exited */ }
        }
        else
        {
            Assert.NotEqual(0, r.Win32);       // failed for a concrete OS reason, not a crash
            Assert.NotEqual(2, r.Win32);       // NOT ERROR_FILE_NOT_FOUND — cmd.exe must resolve via PATH
        }
    }
}
