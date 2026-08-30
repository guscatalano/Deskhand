using System.Management;
using Microsoft.Win32;

namespace Deskhand.Core.Services;

public record InstalledProgramDto(string Name, string? Version, string? Publisher, string? InstallDate, string Scope);
public record ServiceInfoDto(string Name, string? DisplayName, string? State, string? StartMode, string? Account);
public record StartupItemDto(string Name, string? Command, string Location);
public record EnvVarDto(string Name, string Value, string Scope);
public record PrinterDto(string? Name, string? Driver, string? Port, bool Default, string? Status);
public record ShareDto(string? Name, string? Path, string? Description, string? Type);
public record ScheduledTaskDto(string? Path, string? Name, string? State, bool Enabled, string? LastRun, string? NextRun);

/// <summary>Read-only software / configuration inventory: installed programs (registry uninstall keys),
/// Windows services, startup/autorun items, environment variables, printers, network shares, and scheduled
/// tasks. Nothing is changed.</summary>
public static class SoftwareService
{
    // ---- installed programs (Add/Remove Programs, from the registry — not Win32_Product, which is slow) ----
    public static IReadOnlyList<InstalledProgramDto> InstalledPrograms()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<InstalledProgramDto>();
        void Scan(RegistryKey root, string sub, string scope)
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k is null) return;
                foreach (var name in k.GetSubKeyNames())
                {
                    try
                    {
                        using var e = k.OpenSubKey(name);
                        var disp = e?.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrWhiteSpace(disp)) continue;
                        if ((e!.GetValue("SystemComponent") as int?) == 1) continue;
                        if (e.GetValue("ParentKeyName") is not null) continue;   // update entries
                        if (!seen.Add(disp + "|" + e.GetValue("DisplayVersion"))) continue;
                        list.Add(new InstalledProgramDto(disp, e.GetValue("DisplayVersion")?.ToString(),
                            e.GetValue("Publisher")?.ToString(), FmtInstallDate(e.GetValue("InstallDate")?.ToString()), scope));
                    }
                    catch { }
                }
            }
            catch { }
        }
        Scan(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "machine");
        Scan(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "machine (32-bit)");
        Scan(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "user");
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FmtInstallDate(string? s) =>
        s is { Length: 8 } && s.All(char.IsDigit) ? $"{s[..4]}-{s.Substring(4, 2)}-{s.Substring(6, 2)}" : s;

    // ---- services ----
    public static IReadOnlyList<ServiceInfoDto> Services()
    {
        var list = new List<ServiceInfoDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode, StartName FROM Win32_Service");
            foreach (ManagementObject o in s.Get())
                list.Add(new ServiceInfoDto(o["Name"]?.ToString() ?? "", o["DisplayName"]?.ToString(),
                    o["State"]?.ToString(), o["StartMode"]?.ToString(), o["StartName"]?.ToString()));
        }
        catch { }
        return list.OrderBy(x => x.DisplayName ?? x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- startup / autorun items (Run keys + Startup folders) ----
    public static IReadOnlyList<StartupItemDto> StartupItems()
    {
        var list = new List<StartupItemDto>();
        void RunKey(RegistryKey root, string sub, string label)
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k is null) return;
                foreach (var n in k.GetValueNames())
                    list.Add(new StartupItemDto(n, k.GetValue(n)?.ToString(), label));
            }
            catch { }
        }
        RunKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM\\…\\Run");
        RunKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM\\…\\Run (32-bit)");
        RunKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU\\…\\Run");
        foreach (var (dir, label) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder (user)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)"),
        })
        {
            try { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir)) list.Add(new StartupItemDto(Path.GetFileName(f), f, label)); }
            catch { }
        }
        return list;
    }

    // ---- environment variables (machine + user + process) ----
    public static IReadOnlyList<EnvVarDto> EnvironmentVariables()
    {
        var list = new List<EnvVarDto>();
        void Add(EnvironmentVariableTarget t, string scope)
        {
            try { foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(t)) list.Add(new EnvVarDto(e.Key.ToString() ?? "", e.Value?.ToString() ?? "", scope)); }
            catch { }
        }
        Add(EnvironmentVariableTarget.Machine, "machine");
        Add(EnvironmentVariableTarget.User, "user");
        return list.OrderBy(v => v.Scope).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- printers ----
    public static IReadOnlyList<PrinterDto> Printers()
    {
        var list = new List<PrinterDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverName, PortName, Default, PrinterStatus FROM Win32_Printer");
            foreach (ManagementObject o in s.Get())
                list.Add(new PrinterDto(o["Name"]?.ToString(), o["DriverName"]?.ToString(), o["PortName"]?.ToString(),
                    o["Default"] is bool b && b, o["PrinterStatus"]?.ToString()));
        }
        catch { }
        return list;
    }

    // ---- network shares ----
    public static IReadOnlyList<ShareDto> Shares()
    {
        var list = new List<ShareDto>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, Path, Description, Type FROM Win32_Share");
            foreach (ManagementObject o in s.Get())
                list.Add(new ShareDto(o["Name"]?.ToString(), o["Path"]?.ToString(), o["Description"]?.ToString(), o["Type"]?.ToString()));
        }
        catch { }
        return list;
    }

    // ---- scheduled tasks (Task Scheduler COM) ----
    public static IReadOnlyList<ScheduledTaskDto> ScheduledTasks()
    {
        var list = new List<ScheduledTaskDto>();
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null) return list;
            dynamic svc = Activator.CreateInstance(t)!;
            svc.Connect();
            void Walk(dynamic folder)
            {
                foreach (dynamic task in folder.GetTasks(1 /* include hidden */))
                {
                    try
                    {
                        string? last = null, next = null;
                        try { last = ((DateTime)task.LastRunTime).Year > 1900 ? ((DateTime)task.LastRunTime).ToString("yyyy-MM-dd HH:mm") : null; } catch { }
                        try { next = ((DateTime)task.NextRunTime).Year > 1900 ? ((DateTime)task.NextRunTime).ToString("yyyy-MM-dd HH:mm") : null; } catch { }
                        list.Add(new ScheduledTaskDto((string?)task.Path, (string?)task.Name, TaskState((int)task.State), (bool)task.Enabled, last, next));
                    }
                    catch { }
                    if (list.Count >= 3000) return;
                }
                foreach (dynamic sub in folder.GetFolders(0)) { Walk(sub); if (list.Count >= 3000) return; }
            }
            Walk(svc.GetFolder("\\"));
        }
        catch { }
        return list.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string TaskState(int s) => s switch { 0 => "Unknown", 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => s.ToString() };
}
