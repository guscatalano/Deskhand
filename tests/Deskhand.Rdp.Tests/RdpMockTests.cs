using System.Diagnostics;
using Xunit;

namespace Deskhand.Rdp.Tests;

[CollectionDefinition("mock-rdp")]
public class MockRdpCollection : ICollectionFixture<MockRdpFixture> { }

/// <summary>
/// End-to-end checks that drive the shipping <c>deskhand-rdp.exe</c> against the mock RDP server, over the
/// real mstscax wire (TLS, no NLA): it must connect and capture a valid frame. The exe is run as a
/// subprocess on purpose — the RDP ActiveX control throws a native SEHException when its message loop runs
/// inside the test host, so hosting it in-process crashes the runner; the exe hosts it correctly in its own
/// STA process. These SKIP when the mock repo or the built exe isn't present, so CI (and a checkout without
/// the sibling repo) stays green.
/// </summary>
[Collection("mock-rdp")]
public class RdpMockTests
{
    private readonly MockRdpFixture _mock;
    public RdpMockTests(MockRdpFixture mock) => _mock = mock;

    private (string stdout, string stderr, bool exited, int code) RunRdp(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _mock.RdpExe!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outT = p.StandardOutput.ReadToEndAsync();
        var errT = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit(45000);
        if (!exited) { try { p.Kill(entireProcessTree: true); } catch { } }
        return (outT.GetAwaiter().GetResult(), errT.GetAwaiter().GetResult(), exited, exited ? p.ExitCode : -1);
    }

    [SkippableFact]
    public void Connects_and_captures_a_frame_over_tls()
    {
        Skip.IfNot(_mock.Available, "mock-rdp not available: " + _mock.Reason);
        Skip.If(_mock.RdpExe is null, "deskhand-rdp.exe not found (build Deskhand.Rdp first)");

        var png = Path.Combine(Path.GetTempPath(), $"dh-rdp-{Guid.NewGuid():N}.png");
        try
        {
            var (stdout, stderr, exited, _) = RunRdp(
                "127.0.0.1", "test", "test", "--no-nla", "--port", _mock.Port.ToString(),
                "--capture", png, "--timeout", "20000");

            Assert.True(exited, "deskhand-rdp did not exit in time.\nSTDOUT:\n" + stdout + "\nSTDERR:\n" + stderr);
            Assert.Contains("CONNECTED", stdout);
            Assert.DoesNotContain("SEHException", stderr);

            Assert.True(File.Exists(png), "no capture file was written.\nSTDOUT:\n" + stdout);
            var bytes = File.ReadAllBytes(png);
            Assert.True(bytes.Length > 1000, $"capture unexpectedly small ({bytes.Length} bytes)");
            // PNG magic: 89 50 4E 47
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal(0x50, bytes[1]);
            Assert.Equal(0x4E, bytes[2]);
            Assert.Equal(0x47, bytes[3]);
        }
        finally { try { File.Delete(png); } catch { } }
    }
}
