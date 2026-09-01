using Microsoft.Win32;

namespace Deskhand.Core.Services;

public record UacStatusDto(
    bool? Enabled, int? AdminConsentBehavior, string? AdminConsentDescription, bool? PromptOnSecureDesktop,
    bool Automatable, string Summary, string? Error = null);

public record UacConfigDto(bool Ok, string Setting, object? Value, bool RebootRequired, string? Error = null);

public record UacRespondDto(bool Found, bool Acted, string? Window, long WaitedMs, string Note);

/// <summary>
/// Read and configure Windows UAC, and respond to a live consent prompt.
///
/// <para><b>Configure</b> (registry under <c>HKLM\…\Policies\System</c>, needs elevation): turn UAC on/off
/// (<c>EnableLUA</c> — reboot required), move prompts off the secure desktop (<c>PromptOnSecureDesktop</c>), or
/// set the admin consent behavior (<c>ConsentPromptBehaviorAdmin</c>) — where <b>0 = elevate silently, no
/// prompt</b>, the reliable "auto-approve" for admin accounts.</para>
///
/// <para><b>Respond</b> to a prompt that is already up: this only works when the prompt is on the <i>normal</i>
/// desktop (secure desktop disabled) AND Deskhand runs elevated (same-or-higher integrity) — otherwise Windows
/// isolates the dialog by design and no automation can reach it. Use the config knobs to make prompts reachable
/// (or to remove them entirely), then this can press Yes/No.</para>
/// </summary>
public static class UacService
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static UacStatusDto Status()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Key);
            bool? enabled = ToBool(k?.GetValue("EnableLUA"));
            int? behavior = k?.GetValue("ConsentPromptBehaviorAdmin") as int?;
            bool? secure = ToBool(k?.GetValue("PromptOnSecureDesktop"));
            bool automatable = enabled == true && secure == false;   // prompt is on the normal desktop
            string summary = enabled == false ? "UAC is OFF (EnableLUA=0)."
                : behavior == 0 ? "UAC on; admins elevate silently (no prompt)."
                : secure == false ? "UAC on; prompts on the normal desktop (automatable if elevated)."
                : "UAC on; prompts on the secure desktop (not automatable).";
            return new UacStatusDto(enabled, behavior, BehaviorText(behavior), secure, automatable, summary);
        }
        catch (Exception ex) { return new UacStatusDto(null, null, null, null, false, "unreadable", ex.Message); }
    }

    /// <summary>Turn UAC on/off (EnableLUA). Takes effect after a reboot.</summary>
    public static UacConfigDto SetEnabled(bool on) => SetDword("EnableLUA", on ? 1 : 0, "uac_enabled", rebootRequired: true);

    /// <summary>Move admin consent prompts off (false) or on (true) the secure desktop.</summary>
    public static UacConfigDto SetSecureDesktop(bool on) => SetDword("PromptOnSecureDesktop", on ? 1 : 0, "prompt_on_secure_desktop", rebootRequired: false);

    /// <summary>Admin consent behavior 0..5 (0 = elevate silently / auto-approve, 5 = prompt for consent).</summary>
    public static UacConfigDto SetAdminBehavior(int level)
    {
        if (level is < 0 or > 5) return new UacConfigDto(false, "consent_prompt_behavior_admin", level, false, "level must be 0..5.");
        return SetDword("ConsentPromptBehaviorAdmin", level, "consent_prompt_behavior_admin", rebootRequired: false);
    }

    /// <summary>Convenience: auto-approve admin elevation with no prompt (behavior 0), or restore the default prompt (5).</summary>
    public static UacConfigDto SetAutoApprove(bool on) => SetAdminBehavior(on ? 0 : 5);

    private static UacConfigDto SetDword(string name, int value, string setting, bool rebootRequired)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Key, writable: true)
                ?? throw new InvalidOperationException("Policy key not found.");
            k.SetValue(name, value, RegistryValueKind.DWord);
            return new UacConfigDto(true, setting, value, rebootRequired);
        }
        catch (UnauthorizedAccessException)
        { return new UacConfigDto(false, setting, value, rebootRequired, "Access denied — configuring UAC requires running Deskhand elevated."); }
        catch (Exception ex) { return new UacConfigDto(false, setting, value, rebootRequired, ex.Message); }
    }

    /// <summary>Best-effort: wait for a UAC consent window, then press Yes (accept) or No/Esc (reject).
    /// Only reaches the dialog when it's on the normal desktop and Deskhand is elevated (see class remarks).</summary>
    public static UacRespondDto Respond(bool accept, int timeoutMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 0, 120_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? title = null;
        do
        {
            title = FindConsentWindow();
            if (title is not null)
            {
                try { InputInjector.SendKeys(accept ? "enter" : "esc"); }
                catch { return new UacRespondDto(true, false, title, sw.ElapsedMilliseconds, RespondNote(false)); }
                return new UacRespondDto(true, true, title, sw.ElapsedMilliseconds, RespondNote(true));
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(200);
        } while (true);
        return new UacRespondDto(false, false, null, sw.ElapsedMilliseconds,
            "No consent prompt was reachable. If one is showing, it's on the secure desktop — disable PromptOnSecureDesktop (and run Deskhand elevated), or set admin behavior to 0 to skip prompts entirely.");
    }

    private static string RespondNote(bool acted) => acted
        ? "Sent Enter/Esc to the consent dialog. It only takes effect if the prompt is on the normal desktop and Deskhand is elevated; otherwise Windows isolates it."
        : "Found a consent window but could not send input (likely a higher-integrity/secure-desktop dialog).";

    // Look for the UAC consent dialog on the current desktop (consent.exe / a "User Account Control" window).
    private static string? FindConsentWindow()
    {
        string? found = null;
        Interop.EnumWindows((h, _) =>
        {
            if (!Interop.IsWindowVisible(h)) return true;
            Interop.GetWindowThreadProcessId(h, out uint pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                if (string.Equals(p.ProcessName, "consent", StringComparison.OrdinalIgnoreCase))
                { found = Title(h) ?? "User Account Control"; return false; }
            }
            catch { }
            var t = Title(h);
            if (t is not null && t.Contains("User Account Control", StringComparison.OrdinalIgnoreCase)) { found = t; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string? Title(IntPtr h)
    {
        int len = Interop.GetWindowTextLength(h);
        if (len <= 0) return null;
        var sb = new System.Text.StringBuilder(len + 1);
        Interop.GetWindowText(h, sb, sb.Capacity);
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static bool? ToBool(object? v) => v is int i ? i != 0 : null;
    private static string? BehaviorText(int? b) => b switch
    {
        0 => "Elevate without prompting (silent)",
        1 => "Prompt for credentials on the secure desktop",
        2 => "Prompt for consent on the secure desktop",
        3 => "Prompt for credentials",
        4 => "Prompt for consent",
        5 => "Prompt for consent for non-Windows binaries (default)",
        _ => null,
    };

    private static class Interop
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    }
}
