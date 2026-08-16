using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record ProcessDumpDto(int ProcessId, string Name, string File, string FileName, long SizeBytes, string Ts, long DurationMs);

/// <summary>
/// Writes a full-memory crash dump (.dmp) of a running process via <c>MiniDumpWriteDump</c> — the same
/// mechanism as Task Manager's "Create dump file". Dumps land in one predefined dir, are audited, and are
/// auto-deleted after <see cref="RetentionHours"/> (they can be large and contain secrets from process
/// memory, so treat them as sensitive). Dumping another user's / a protected process needs elevation
/// (SeDebugPrivilege) — enabled best-effort on startup; otherwise the caller gets an access-denied error.
/// </summary>
public sealed class ProcessDumper : IDisposable
{
    public const int RetentionHours = 24;

    // MiniDumpWithFullMemory + memory-info + handles + thread-info + unloaded-modules = a complete dump.
    private const uint DumpFlags = 0x00000002 | 0x00000800 | 0x00000004 | 0x00001000 | 0x00000020;

    private readonly string _dir;
    private readonly Deskhand.Core.Governance.AuditLog? _audit;
    private readonly System.Threading.Timer _janitor;

    public ProcessDumper(Deskhand.Core.Governance.AuditLog? audit = null)
    {
        _audit = audit;
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deskhand", "dumps");
        System.IO.Directory.CreateDirectory(_dir);
        TryEnableDebugPrivilege();
        CleanupExpired();
        _janitor = new System.Threading.Timer(_ => CleanupExpired(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    public string Directory => _dir;

    /// <summary>Full-memory dump of <paramref name="pid"/>. Blocks until written (seconds–minutes for large
    /// processes). Throws ArgumentException if not running, Win32Exception (access denied) if not permitted.</summary>
    public ProcessDumpDto Dump(int pid)
    {
        using var proc = Process.GetProcessById(pid);           // ArgumentException if the pid isn't running
        string name = proc.ProcessName;
        string fileName = $"{name}_{pid}_{DateTime.Now:yyyyMMdd-HHmmss}.dmp";
        string path = Path.Combine(_dir, fileName);
        var sw = Stopwatch.StartNew();
        try
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                if (!MiniDumpWriteDump(proc.Handle, (uint)pid, fs.SafeFileHandle, DumpFlags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"MiniDumpWriteDump failed for {name} ({pid}). Protected/other-user processes need elevation (run as admin).");
            }
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
        long size = new FileInfo(path).Length;
        _audit?.Record("process_dump", $"{name} ({pid})", $"{size}B -> {fileName} (auto-delete {RetentionHours}h)");
        return new ProcessDumpDto(pid, name, path, fileName, size, DateTimeOffset.Now.ToString("o"), sw.ElapsedMilliseconds);
    }

    /// <summary>Resolve a dump file name to its full path (path-traversal safe), for streaming download.</summary>
    public string PathFor(string fileName)
    {
        string path = Path.Combine(_dir, Path.GetFileName(fileName));
        if (!File.Exists(path)) throw new FileNotFoundException("No such dump.", fileName);
        return path;
    }

    public IEnumerable<object> List() =>
        System.IO.Directory.EnumerateFiles(_dir, "*.dmp")
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Select(fi => new { name = fi.Name, sizeBytes = fi.Length, ts = fi.LastWriteTimeUtc.ToString("o") });

    private void CleanupExpired()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "*.dmp"))
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) { File.Delete(f); _audit?.Record("process_dump_expired", Path.GetFileName(f), $"deleted (>{RetentionHours}h)"); } }
                catch { }
        }
        catch { }
    }

    private static void TryEnableDebugPrivilege()
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x20 /*ADJUST*/ | 0x8 /*QUERY*/, out var token)) return;
            try
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid)) return;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 0x2 /*ENABLED*/ };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
        catch { /* not elevated / not permitted — dumps of own processes still work */ }
    }

    public void Dispose() { try { _janitor.Dispose(); } catch { } }

    // ---- native ----
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeHandle hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? sys, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
}
