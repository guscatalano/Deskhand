using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using static Deskhand.Broker.Interop;

// Deskhand Broker — the privilege launcher (design §03, Phase 2).
//
// Runs the Secure Helper as SYSTEM inside the interactive console session, which is the only
// context that can attach to and capture the secure desktop (Winsta0\Winlogon). It does this
// by borrowing winlogon.exe's SYSTEM token from the console session and CreateProcessAsUser'ing
// the helper with it onto Winsta0\Default.
//
// MUST be run elevated (Administrator). This code path requires elevation + SeDebugPrivilege
// and was therefore not exercised in the build sandbox; run it on a real elevated console.
//
// Usage:  deskhand-broker <path-to-deskhand-secure.exe> capture <out.png>

int Fail(string msg) { Console.Error.WriteLine("error: " + msg); return 1; }

if (args.Length < 2)
{
    Console.WriteLine("Deskhand Broker (run elevated)");
    Console.WriteLine("  deskhand-broker <deskhand-secure.exe> <helper-args...>");
    Console.WriteLine("  e.g. deskhand-broker deskhand-secure.exe capture C:\\temp\\secure.png");
    return 2;
}

using (var id = WindowsIdentity.GetCurrent())
{
    if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
        return Fail("must run elevated (Administrator).");
}

string helper = Path.GetFullPath(args[0]);
if (!File.Exists(helper)) return Fail($"helper not found: {helper}");
string helperArgs = string.Join(' ', args.Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

uint session = WTSGetActiveConsoleSessionId();
if (session == 0xFFFFFFFF) return Fail("no active console session.");
Console.WriteLine($"console session = {session}");

if (!EnableSeDebug()) Console.WriteLine("warning: could not enable SeDebugPrivilege (continuing).");

// winlogon.exe runs as SYSTEM in every interactive session — borrow its token.
var winlogon = Process.GetProcessesByName("winlogon").FirstOrDefault(p => SafeSession(p) == session);
if (winlogon is null) return Fail($"winlogon.exe not found in session {session}.");
Console.WriteLine($"winlogon pid = {winlogon.Id}");

if (!OpenProcessToken(winlogon.Handle, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out IntPtr srcTok))
    return Fail($"OpenProcessToken failed (Win32 {Marshal.GetLastWin32Error()}). Elevation + SeDebugPrivilege required.");

try
{
    if (!DuplicateTokenEx(srcTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr dupTok))
        return Fail($"DuplicateTokenEx failed (Win32 {Marshal.GetLastWin32Error()}).");

    try
    {
        CreateEnvironmentBlock(out IntPtr env, dupTok, false);
        // Which desktop the helper's process runs ON. For secure-desktop INPUT the injecting process should
        // genuinely live on Winsta0\Winlogon (re-pointing a thread after the fact isn't always enough for
        // SendInput). Defaults to Default; the main server sets DESKHAND_HELPER_DESKTOP=Winsta0\Winlogon.
        string desktop = Environment.GetEnvironmentVariable("DESKHAND_HELPER_DESKTOP") is { Length: > 0 } d ? d : @"Winsta0\Default";
        Console.WriteLine($"helper desktop = {desktop}");
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
        string cmd = $"\"{helper}\" {helperArgs}";
        uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
        string? dir = Path.GetDirectoryName(helper);

        // Preferred: CreateProcessWithTokenW (needs SeImpersonatePrivilege, which an elevated admin has).
        bool ok = CreateProcessWithTokenW(dupTok, 0, helper, cmd, flags, env, dir, ref si, out var pi);
        int err = Marshal.GetLastWin32Error();
        if (!ok)
        {
            Console.WriteLine($"CreateProcessWithTokenW failed (Win32 {err}); trying CreateProcessAsUser…");
            ok = CreateProcessAsUser(dupTok, helper, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, env, dir, ref si, out pi);
            err = Marshal.GetLastWin32Error();
        }

        if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
        if (!ok) return Fail($"could not launch helper as SYSTEM (Win32 {err}).");

        Console.WriteLine($"launched helper as SYSTEM, pid = {pi.dwProcessId}");
        WaitForSingleObject(pi.hProcess, INFINITE);
        GetExitCodeProcess(pi.hProcess, out uint code);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        Console.WriteLine($"helper exit code = {code}");
        return (int)code;
    }
    finally { CloseHandle(dupTok); }
}
finally { CloseHandle(srcTok); }

static uint SafeSession(Process p) { try { return (uint)p.SessionId; } catch { return 0xFFFFFFFF; } }

static bool EnableSeDebug()
{
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok)) return false;
    try
    {
        if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out LUID luid)) return false;
        var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
        return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
               && Marshal.GetLastWin32Error() == 0;
    }
    finally { CloseHandle(tok); }
}
