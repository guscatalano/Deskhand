using System.Management;
using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record PowerDto(string AcLine, bool HasBattery, int? BatteryPercent, int? MinutesRemaining,
    int? WearPercent, long? DesignCapacityMwh, long? FullChargeCapacityMwh, string? PowerPlan);

/// <summary>Read-only power / battery state: AC vs battery, charge %, estimated runtime, battery wear
/// (design vs full-charge capacity), and the active power plan. On a desktop with no battery, HasBattery
/// is false and the battery fields are null.</summary>
public static class PowerService
{
    public static PowerDto Get()
    {
        string ac = "Unknown"; bool hasBattery = false; int? pct = null, mins = null;
        if (GetSystemPowerStatus(out var s))
        {
            ac = s.ACLineStatus switch { 0 => "Battery", 1 => "AC", _ => "Unknown" };
            hasBattery = (s.BatteryFlag & 128) == 0;   // bit 7 set = no system battery
            if (hasBattery)
            {
                if (s.BatteryLifePercent != 255) pct = s.BatteryLifePercent;
                if (s.BatteryLifeTime >= 0) mins = s.BatteryLifeTime / 60;
            }
        }

        long? design = null, full = null; int? wear = null;
        if (hasBattery)
        {
            design = WmiFirstLong(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
            full = WmiFirstLong(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
            if (design is long d && d > 0 && full is long f && f > 0) wear = Math.Max(0, (int)Math.Round((d - f) * 100.0 / d));
        }

        string? plan = null;
        try
        {
            // WQL "WHERE IsActive=TRUE" is unreliable for Win32_PowerPlan (calculated property); check in code.
            using var sp = new ManagementObjectSearcher(@"root\cimv2\power", "SELECT ElementName, IsActive FROM Win32_PowerPlan");
            foreach (ManagementObject o in sp.Get())
                if (o["IsActive"] is bool a && a) { plan = o["ElementName"]?.ToString(); break; }
        }
        catch { }

        return new PowerDto(ac, hasBattery, pct, mins, wear, design, full, plan);
    }

    private static long? WmiFirstLong(string scope, string query, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject o in s.Get())
                return o[prop] is null ? null : Convert.ToInt64(o[prop]);
        }
        catch { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
