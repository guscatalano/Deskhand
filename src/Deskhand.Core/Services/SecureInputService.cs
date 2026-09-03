using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Deskhand.Core.Services;

public record SasStatusDto(bool IsSystem, int? SoftwareSasGeneration, string SasPolicy, bool CanSendSas, string Note);
public record SecureActionDto(bool Ok, string Action, string? Error = null, string? Hint = null);

/// <summary>
/// The keystrokes plain SendInput can't forge. <b>Ctrl+Alt+Del</b> (the Secure Attention Sequence) is blocked
/// for synthetic input by design; the supported way to raise it is the <c>SendSAS</c> API, which works when the
/// caller runs as <b>LocalSystem</b> (AsUser=false) or when the <c>SoftwareSASGeneration</c> policy allows app-
/// generated SAS (AsUser=true). This service exposes SendSAS, sets that policy (needs elevation), reports
/// status, and offers LockWorkstation. NOTE: SendSAS raises the secure desktop; <i>clicking</i> its options
/// (Task Manager, Lock, Change password) still needs the SYSTEM secure-desktop input path.
/// </summary>
public static class SecureInputService
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static SasStatusDto Status()
    {
        bool system = IsSystem();
        int? p = SoftwareSas();
        bool can = system || p is 2 or 3;
        string policy = p switch { 0 => "none", 1 => "services", 2 => "ease-of-access apps", 3 => "services + apps", null => "unset (default none)", _ => "unknown(" + p + ")" };
        string note = can
            ? "Ctrl+Alt+Del can be sent from here."
            : "Ctrl+Alt+Del is blocked: run Deskhand as LocalSystem, OR set SoftwareSASGeneration to allow apps (configure_sas, needs elevation).";
        return new SasStatusDto(system, p, policy, can, note);
    }

    /// <summary>Raise Ctrl+Alt+Del. AsUser is auto-picked (false when running as SYSTEM, else true); override if needed.</summary>
    public static SecureActionDto SendCtrlAltDel(bool? asUser = null)
    {
        bool system = IsSystem();
        bool au = asUser ?? !system;
        int policy = SoftwareSas() ?? 0;

        if (au && policy is not (2 or 3))
            return new SecureActionDto(false, "ctrl_alt_del",
                "SoftwareSASGeneration doesn't allow app-generated SAS (current: " + policy + ").",
                "Run configure_sas to allow it (needs elevation), or run Deskhand as LocalSystem and use asUser=false.");
        if (!au && !system)
            return new SecureActionDto(false, "ctrl_alt_del", "asUser=false requires running as LocalSystem.",
                "Run Deskhand as SYSTEM (e.g. via the Fleet Launcher service).");
        try { SendSAS(au); return new SecureActionDto(true, "ctrl_alt_del"); }
        catch (Exception ex) { return new SecureActionDto(false, "ctrl_alt_del", ex.Message, "sas.dll is present on desktop Windows; ensure the SoftwareSASGeneration policy or SYSTEM context."); }
    }

    public static SecureActionDto LockWorkstation()
    {
        try { return LockWorkStation() ? new SecureActionDto(true, "lock") : new SecureActionDto(false, "lock", $"LockWorkStation failed (Win32 {Marshal.GetLastWin32Error()})."); }
        catch (Exception ex) { return new SecureActionDto(false, "lock", ex.Message); }
    }

    /// <summary>Set SoftwareSASGeneration (0 none · 1 services · 2 ease-of-access apps · 3 both). Needs elevation.</summary>
    public static SecureActionDto ConfigureSas(int level)
    {
        if (level is < 0 or > 3) return new SecureActionDto(false, "configure_sas", "level must be 0..3.");
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true) ?? throw new InvalidOperationException("Policy key not found.");
            k.SetValue("SoftwareSASGeneration", level, RegistryValueKind.DWord);
            return new SecureActionDto(true, "configure_sas");
        }
        catch (UnauthorizedAccessException) { return new SecureActionDto(false, "configure_sas", "Access denied — setting the SAS policy requires running elevated."); }
        catch (Exception ex) { return new SecureActionDto(false, "configure_sas", ex.Message); }
    }

    private static int? SoftwareSas()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(PolicyKey); return k?.GetValue("SoftwareSASGeneration") as int?; }
        catch { return null; }
    }

    private static bool IsSystem()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return id.IsSystem || id.User?.Value == "S-1-5-18"; }
        catch { return false; }
    }

    [DllImport("sas.dll", SetLastError = true)] private static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool AsUser);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
}
