using System.Management;
using Microsoft.Win32;

namespace Deskhand.Core.Services;

public record TpmDto(bool Present, bool? Enabled, bool? Activated, string? SpecVersion, string? Manufacturer);
public record BitLockerVolumeDto(string? Drive, string ProtectionStatus, string? EncryptionMethod, int? PercentEncrypted);
public record DefenderDto(bool? RealTimeProtection, bool? AntivirusEnabled, int? SignatureAgeDays, string? RunningMode, bool? TamperProtected);
public record AvProductDto(string? Name, bool? Enabled);
public record SecurityPostureDto(
    TpmDto? Tpm, bool? SecureBootEnabled, string? SecureBootNote,
    IReadOnlyList<BitLockerVolumeDto> BitLocker, string? BitLockerNote,
    bool? WindowsActivated, string? ActivationNote,
    DefenderDto? Defender, IReadOnlyList<AvProductDto> AntiVirus,
    bool PendingReboot, IReadOnlyList<string> RebootReasons,
    string Note);

/// <summary>
/// Read-only security posture: TPM, Secure Boot, BitLocker, Windows activation, Defender / installed AV, and
/// pending-reboot. Several items (TPM / BitLocker / Defender) require elevation to read fully; run unelevated
/// (Deskhand's default) and they degrade to "unknown" rather than failing.
/// </summary>
public static class SecurityService
{
    public static SecurityPostureDto Get()
    {
        var (pending, reasons) = PendingReboot();
        return new SecurityPostureDto(
            Tpm(), SecureBoot(out var sbNote), sbNote,
            BitLocker(out var blNote), blNote,
            Activation(out var actNote), actNote,
            Defender(), AntiVirus(),
            pending, reasons,
            "TPM / BitLocker / Defender need elevation to read fully; unknown when Deskhand runs unelevated.");
    }

    private static TpmDto? Tpm()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm", "SELECT * FROM Win32_Tpm");
            foreach (ManagementObject o in s.Get())
            {
                bool? en = null, act = null;
                try { en = Convert.ToBoolean(o.InvokeMethod("IsEnabled", null, null)?["IsEnabled"]); } catch { }
                try { act = Convert.ToBoolean(o.InvokeMethod("IsActivated", null, null)?["IsActivated"]); } catch { }
                return new TpmDto(true, en, act, o["SpecVersion"]?.ToString(), o["ManufacturerIdTxt"]?.ToString()?.Trim());
            }
            return new TpmDto(false, null, null, null, null);   // namespace readable but no TPM instance
        }
        catch { return null; }   // couldn't read (likely needs elevation)
    }

    private static bool? SecureBoot(out string? note)
    {
        note = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (k?.GetValue("UEFISecureBootEnabled") is int v) return v != 0;
            note = "Not reported (legacy BIOS boot, or the key is unavailable).";
            return null;
        }
        catch { note = "unreadable"; return null; }
    }

    private static IReadOnlyList<BitLockerVolumeDto> BitLocker(out string? note)
    {
        note = null;
        var list = new List<BitLockerVolumeDto>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftVolumeEncryption", "SELECT * FROM Win32_EncryptableVolume");
            foreach (ManagementObject o in s.Get())
            {
                string prot = "Unknown"; int? pct = null;
                try { prot = ProtStatus(Convert.ToInt32(o.InvokeMethod("GetProtectionStatus", null, null)?["ProtectionStatus"])); } catch { }
                try { pct = Convert.ToInt32(o.InvokeMethod("GetConversionStatus", null, null)?["EncryptionPercentage"]); } catch { }
                list.Add(new BitLockerVolumeDto(o["DriveLetter"]?.ToString(), prot, EncMethod(o["EncryptionMethod"]), pct));
            }
        }
        catch { note = "Not readable (BitLocker status needs elevation)."; }
        return list;
    }

    private static bool? Activation(out string? note)
    {
        note = null;
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["LicenseStatus"]) == 1;   // 1 = Licensed (activated)
            note = "No activated Windows license product found.";
            return null;
        }
        catch { note = "unreadable"; return null; }
    }

    private static DefenderDto? Defender()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementObject o in s.Get())
                return new DefenderDto(
                    o["RealTimeProtectionEnabled"] as bool?, o["AntivirusEnabled"] as bool?,
                    o["AntivirusSignatureAge"] is not null ? Convert.ToInt32(o["AntivirusSignatureAge"]) : null,
                    o["AMRunningMode"]?.ToString(), o["IsTamperProtected"] as bool?);
            return null;
        }
        catch { return null; }
    }

    private static IReadOnlyList<AvProductDto> AntiVirus()
    {
        var list = new List<AvProductDto>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (ManagementObject o in s.Get())
            {
                bool? enabled = null;
                try { enabled = (Convert.ToInt32(o["productState"]) & 0x1000) != 0; } catch { }
                list.Add(new AvProductDto(o["displayName"]?.ToString(), enabled));
            }
        }
        catch { }
        return list;
    }

    private static (bool, IReadOnlyList<string>) PendingReboot()
    {
        var reasons = new List<string>();
        void KeyExists(string sub, string reason)
        { try { using var k = Registry.LocalMachine.OpenSubKey(sub); if (k is not null) reasons.Add(reason); } catch { } }
        void ValueExists(string sub, string val, string reason)
        { try { using var k = Registry.LocalMachine.OpenSubKey(sub); if (k?.GetValue(val) is not null) reasons.Add(reason); } catch { } }

        KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", "Component-Based Servicing");
        KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", "Windows Update");
        ValueExists(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", "Pending file rename");
        return (reasons.Count > 0, reasons);
    }

    private static string ProtStatus(int s) => s switch { 0 => "Off (unprotected)", 1 => "On (protected)", 2 => "Unknown", _ => s.ToString() };
    private static string? EncMethod(object? v) => v is null ? null : Convert.ToInt32(v) switch
    { 0 => "None", 1 => "AES-128 diffuser", 2 => "AES-256 diffuser", 3 => "AES-128", 4 => "AES-256", 6 => "XTS-AES-128", 7 => "XTS-AES-256", var n => $"method {n}" };
}
