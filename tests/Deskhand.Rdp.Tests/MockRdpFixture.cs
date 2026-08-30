using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Deskhand.Rdp.Tests;

/// <summary>
/// Stands up the sibling <c>mock-rdp</c> server (a hand-rolled MS-RDPBCGR mock, TLS-only, NLA deferred) on a
/// free loopback port for the duration of a test class, so Deskhand's real <see cref="RdpHost"/> (mstscax) can
/// connect to it end-to-end. If the mock repo isn't present (e.g. CI, or a checkout without the sibling repo),
/// <see cref="Available"/> is false and the tests skip cleanly — nothing here fails a build.
///
/// Locate order: <c>MOCK_RDP_DIR</c> env var, else <c>%USERPROFILE%\source\repos\mock-rdp</c>. The server is
/// launched with <c>dotnet run</c> (it builds itself the first time), so the .NET SDK it targets (net10) must
/// be installed for the tests to actually run; otherwise they skip.
/// </summary>
public sealed class MockRdpFixture : IDisposable
{
    public bool Available { get; }
    public int Port { get; }
    public string Reason { get; } = "";
    /// <summary>Path to the built deskhand-rdp.exe, or null if it isn't built. The tests drive this exe
    /// as a subprocess rather than hosting the mstscax ActiveX control inside the test host (the control
    /// throws a native SEHException in a non-app message loop, crashing the runner).</summary>
    public string? RdpExe { get; }
    private readonly Process? _proc;

    public MockRdpFixture()
    {
        RdpExe = LocateRdpExe();
        var dir = Locate();
        if (dir is null) { Reason = "mock-rdp repo not found (set MOCK_RDP_DIR to its path)"; return; }

        Port = FreePort();
        try
        {
            var project = Path.Combine(dir, "src", "MockRdp");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{project}\" -c Release -- --port {Port} --log-level warn",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir,
            };
            _proc = Process.Start(psi);
            if (_proc is null) { Reason = "failed to start the mock server (dotnet run)"; return; }

            // First run builds mock-rdp (net10) before it listens — allow generous time, then poll the port.
            if (WaitForPort(Port, TimeSpan.FromSeconds(120))) Available = true;
            else { Reason = "mock server did not open its port within 120s"; TryKill(); }
        }
        catch (Exception ex) { Reason = "launch error: " + ex.Message; }
    }

    private static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("MOCK_RDP_DIR");
        if (!string.IsNullOrWhiteSpace(env) && HasProject(env)) return env;
        var sibling = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos", "mock-rdp");
        return HasProject(sibling) ? sibling : null;
    }

    private static bool HasProject(string dir) => File.Exists(Path.Combine(dir, "src", "MockRdp", "MockRdp.csproj"));

    // Walk up from the test's output dir to the repo root (holds Deskhand.slnx), then find the newest
    // built deskhand-rdp.exe under src/Deskhand.Rdp/bin.
    private static string? LocateRdpExe()
    {
        var env = Environment.GetEnvironmentVariable("DESKHAND_RDP_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Deskhand.slnx"))) d = d.Parent;
        if (d is null) return null;
        var binDir = Path.Combine(d.FullName, "src", "Deskhand.Rdp", "bin");
        if (!Directory.Exists(binDir)) return null;
        return Directory.EnumerateFiles(binDir, "deskhand-rdp.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return true; }
            catch { Thread.Sleep(500); }
        }
        return false;
    }

    private void TryKill() { try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { } }

    public void Dispose() { TryKill(); _proc?.Dispose(); }
}
