using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deskhand.Core;
using Deskhand.Core.Governance;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Deskhand.McpTools;

/// <summary>
/// The Deskhand MCP tool surface. Every tool delegates to the shared <see cref="IAutomationBackend"/>
/// (injected from DI), so this server and the HTTP server expose identical capabilities. UIA reads
/// return JSON text; captures return an MCP image plus a one-line text summary.
/// </summary>
[McpServerToolType]
public static class DeskhandTools
{
    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    // Every tool result funnels through here. If it exceeds the per-result char budget, spill the full text to
    // the OutputStore and return a small, VALID envelope (head preview + id + URL) instead of letting the
    // client truncate a huge blob mid-token (which corrupts JSON/base64). Page the rest with deskhand_read_output.
    private static string Json(object? o)
    {
        string s = JsonSerializer.Serialize(o, J);
        int budget = Deskhand.Core.Services.OutputStore.MaxChars;
        if (s.Length <= budget) return s;
        string id = Deskhand.Core.Services.OutputStore.Save(s);
        int headLen = Math.Min(2000, budget / 4);
        return JsonSerializer.Serialize(new
        {
            truncated = true,
            totalChars = s.Length,
            budget,
            outputId = id,
            url = $"/outputs/{id}",
            head = s[..headLen],
            note = $"Result ({s.Length} chars) exceeds the {budget}-char tool-output budget and was saved whole. " +
                   $"Page it with deskhand_read_output(outputId=\"{id}\", offset, limit), GET /outputs/{id}, or refine the query.",
        }, J);
    }

    // Run an element operation with actionable errors: name a missing 'reference', and turn any thrown
    // exception into its real message (stale element, pattern not supported, …) instead of the MCP SDK's
    // generic "An error occurred invoking '<tool>'." — so a model can self-correct.
    private static string ElementOp(string? reference, Func<string> op)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Json(new { error = "Missing required argument 'reference' — an element ref like \"el_12\", from deskhand_get_tree / deskhand_find_elements / deskhand_element_from_point.", type = "missing_argument" });
        try { return op(); }
        catch (Exception ex) { return Json(new { error = ex.Message, type = ex.GetType().Name }); }
    }

    // Turn a thrown exception into its real message instead of the MCP SDK's generic
    // "An error occurred invoking '<tool>'." — e.g. a bad launch path ("file not found").
    private static string Try(Func<string> op)
    {
        try { return op(); }
        catch (Exception ex) { return Json(new { error = ex.Message, type = ex.GetType().Name }); }
    }

    private static ImageFormat Fmt(string? f) =>
        f?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;

    private static IEnumerable<ContentBlock> AsImage(CaptureResultDto c, int? maxWidth = null, int? maxBytes = null)
    {
        var s = Deskhand.Core.Services.ImageScaler.Fit(c.Bytes, c.Format, maxWidth, maxBytes);
        string scaleNote = s.Scale < 1.0 ? $" image={s.Width}x{s.Height} scale={s.Scale} (map image px→screen: x/{s.Scale}+{c.Rect.X}, y/{s.Scale}+{c.Rect.Y})" : "";
        yield return new TextContentBlock
        {
            Text = $"desktop={c.Desktop} rect={c.Rect.Width}x{c.Rect.Height}@({c.Rect.X},{c.Rect.Y}) " +
                   $"monitor={c.Monitor} dpi={c.DpiScale} format={s.Format} bytes={s.Bytes.Length}{scaleNote}",
        };
        yield return new ImageContentBlock
        {
            Data = s.Bytes,
            MimeType = s.Format == "jpeg" ? "image/jpeg" : "image/png",
        };
    }

    // ---------- orientation ----------

    [McpServerTool(Name = "deskhand_machine_info"), Description("Machine name, user, elevation, monitors, virtual-screen bounds, and current desktop state.")]
    public static string MachineInfo(IAutomationBackend b) => Json(b.GetMachineInfo());

    [McpServerTool(Name = "deskhand_desktop_state"), Description("Which desktop currently owns input: default, secure (UAC/lock/logon), or screensaver.")]
    public static string DesktopState(IAutomationBackend b) => Json(b.GetDesktopState());

    [McpServerTool(Name = "deskhand_list_windows"), Description("List all top-level windows. The reliable way to target a specific app (foreground is unreliable when a tool has focus).")]
    public static string ListWindows(IAutomationBackend b) => Json(b.GetTopLevelWindows());

    [McpServerTool(Name = "deskhand_list_processes"), Description("List every running process, each with the top-level windows it owns (windowed apps first; background processes have an empty windows list). Each window carries a live ref you can pass straight to deskhand_get_tree to expand its UIA tree — process → windows → elements.")]
    public static string ListProcesses(IAutomationBackend b) => Json(b.GetProcesses());

    [McpServerTool(Name = "deskhand_list_apps"), Description("List Start Menu apps (the .lnk/.url shortcuts under the all-users and per-user Start Menu). Each has name, folder, and path — launch one by passing its path to deskhand_launch_process. (UWP/Store apps aren't shortcuts and aren't listed.)")]
    public static string ListApps() => Json(Deskhand.Core.Services.StartMenuService.List());

    [McpServerTool(Name = "deskhand_list_desktops"), Description("Virtual desktops: the visible top-level windows grouped by the Windows virtual desktop they're on (the current desktop is flagged). NOTE: Windows only documents reading a window's desktop + moving windows — listing/switching/creating desktops is not available (undocumented, breaks across builds).")]
    public static string ListDesktops() => Json(Deskhand.Core.Services.VirtualDesktopService.ListByWindow());

    [McpServerTool(Name = "deskhand_move_window_to_desktop"), Description("Move a top-level window (by hwnd) to a virtual desktop. Omit desktopId to bring it to the CURRENT desktop; or pass a desktop GUID from deskhand_list_desktops. Requires the kill switch armed.")]
    public static string MoveWindowToDesktop(ControlState state, long hwnd, string? desktopId = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\"}";
        bool ok = desktopId is null
            ? Deskhand.Core.Services.VirtualDesktopService.MoveWindowToCurrent((IntPtr)hwnd)
            : Deskhand.Core.Services.VirtualDesktopService.MoveWindowToDesktop((IntPtr)hwnd, desktopId);
        return ok ? "ok" : "{\"error\":\"move_failed\"}";
    }

    [McpServerTool(Name = "deskhand_system_info"), Description("About this machine (read-only): Windows version + BuildLab, uptime, CPU (name/cores/live load %), memory (total/available/load), disks (size/free per drive), network interfaces (IPs/MAC/gateway/DNS), and Windows Firewall per-profile state. Nothing is changed. Takes ~250 ms (samples CPU load).")]
    public static string SystemInfo() => Json(Deskhand.Core.Services.SystemInfoService.Get());

    [McpServerTool(Name = "deskhand_disks"), Description("Physical disks with their partitions and logical volumes (read-only, via WMI): per disk — model, interface, media type, serial, size, partition count; per partition — size, type, bootable; per volume — drive letter, label, file system, size, free space.")]
    public static string Disks() => Json(Deskhand.Core.Services.HardwareInfoService.Disks());

    [McpServerTool(Name = "deskhand_windows_updates"), Description("Installed Windows updates / hotfixes (KBs), newest first: { hotFixId (e.g. KB5034123), description, installedOn, installedBy }. Read-only (Win32_QuickFixEngineering).")]
    public static string WindowsUpdates() => Json(Deskhand.Core.Services.HardwareInfoService.WindowsUpdates());

    [McpServerTool(Name = "deskhand_devices"), Description("PnP devices (Device Manager-like), read-only: { name, class, manufacturer, status, deviceId }. Optionally filter by PNP class (e.g. \"Net\", \"Display\", \"Media\", \"USB\", \"System\"); omit for all. Can be hundreds of rows.")]
    public static string Devices([Description("PNP class filter, e.g. \"Net\" / \"Display\" / \"Media\". Empty = all devices.")] string? classFilter = null)
        => Json(Deskhand.Core.Services.HardwareInfoService.Devices(classFilter));

    [McpServerTool(Name = "deskhand_drivers"), Description("Installed drivers (read-only, Win32_PnPSignedDriver): { device, provider, version, date, infName, signed }. Can be hundreds of rows and take several seconds (the WMI query is slow).")]
    public static string Drivers() => Json(Deskhand.Core.Services.HardwareInfoService.Drivers());

    [McpServerTool(Name = "deskhand_audio_devices"), Description("Audio devices (read-only, Win32_SoundDevice): { name, manufacturer, status }.")]
    public static string AudioDevices() => Json(Deskhand.Core.Services.HardwareInfoService.Audio());

    [McpServerTool(Name = "deskhand_audio_defaults"), Description("Default audio endpoints (read-only, Core Audio): { playback, recording } each with { name, id, volumePercent (0-100), muted }.")]
    public static string AudioDefaults() => Json(Deskhand.Core.Services.AudioService.Defaults());

    [McpServerTool(Name = "deskhand_hardware_detail"), Description("Detailed hardware inventory (read-only, WMI): computer manufacturer/model, BIOS (version/date/serial/SMBIOS), motherboard (manufacturer/product/serial), GPUs (name/driver/VRAM/resolution/refresh), monitors (manufacturer/model/serial/year), and RAM sticks (slot/capacity/speed/manufacturer/part/type e.g. DDR5).")]
    public static string HardwareDetail() => Json(Deskhand.Core.Services.HardwareInfoService.Detail());

    [McpServerTool(Name = "deskhand_sessions"), Description("Logon sessions via the WTS APIs (read-only): the console session, any RDP sessions, and service/listener sessions — each with { sessionId, station, state (Active/Disconnected/Listen/…), user, domain, clientName (RDP client machine, empty for local), isCurrent }.")]
    public static string Sessions() => Json(Deskhand.Core.Services.SessionsService.List());

    [McpServerTool(Name = "deskhand_installed_programs"), Description("Installed programs (read-only, from the registry Add/Remove list): { name, version, publisher, installDate, scope }. Fast and complete (not the slow Win32_Product).")]
    public static string InstalledPrograms() => Json(Deskhand.Core.Services.SoftwareService.InstalledPrograms());

    [McpServerTool(Name = "deskhand_services"), Description("Windows services (read-only): { name, displayName, state (Running/Stopped/…), startMode (Auto/Manual/Disabled), account }.")]
    public static string Services() => Json(Deskhand.Core.Services.SoftwareService.Services());

    [McpServerTool(Name = "deskhand_startup_items"), Description("Startup / autorun items (read-only): registry Run keys + Startup folders — { name, command, location }.")]
    public static string StartupItems() => Json(Deskhand.Core.Services.SoftwareService.StartupItems());

    [McpServerTool(Name = "deskhand_env_vars"), Description("Environment variables (read-only): machine + user scopes — { name, value, scope }.")]
    public static string EnvVars() => Json(Deskhand.Core.Services.SoftwareService.EnvironmentVariables());

    [McpServerTool(Name = "deskhand_printers"), Description("Installed printers (read-only): { name, driver, port, default, status }.")]
    public static string Printers() => Json(Deskhand.Core.Services.SoftwareService.Printers());

    [McpServerTool(Name = "deskhand_shares"), Description("Network shares hosted by this machine (read-only): { name, path, description, type }.")]
    public static string Shares() => Json(Deskhand.Core.Services.SoftwareService.Shares());

    [McpServerTool(Name = "deskhand_scheduled_tasks"), Description("Scheduled tasks (read-only, Task Scheduler): { path, name, state (Ready/Running/Disabled/…), enabled, lastRun, nextRun }.")]
    public static string ScheduledTasks() => Json(Deskhand.Core.Services.SoftwareService.ScheduledTasks());

    [McpServerTool(Name = "deskhand_security_posture"), Description("Security posture (read-only): TPM (present/enabled/version), Secure Boot, BitLocker per volume, Windows activation, Defender status + installed AV products, and pending-reboot with reasons. TPM/BitLocker/Defender need elevation to read fully; unknown/empty when Deskhand runs unelevated.")]
    public static string SecurityPosture() => Json(Deskhand.Core.Services.SecurityService.Get());

    [McpServerTool(Name = "deskhand_local_users"), Description("Local user accounts (read-only): { name, fullName, disabled, lockout, passwordExpires, sid }. Local accounts only (no domain).")]
    public static string LocalUsers() => Json(Deskhand.Core.Services.UsersService.Users());

    [McpServerTool(Name = "deskhand_local_groups"), Description("Local groups with membership (read-only): { name, description, members[] }.")]
    public static string LocalGroups() => Json(Deskhand.Core.Services.UsersService.Groups());

    [McpServerTool(Name = "deskhand_power"), Description("Power / battery state (read-only): { acLine (AC/Battery), hasBattery, batteryPercent, minutesRemaining, wearPercent, designCapacityMwh, fullChargeCapacityMwh, powerPlan }.")]
    public static string Power() => Json(Deskhand.Core.Services.PowerService.Get());

    [McpServerTool(Name = "deskhand_net_connections"), Description("Active network connections + listening ports (read-only, netstat-like, IPv4): { protocol, localAddress, remoteAddress, state, pid, process }.")]
    public static string NetConnections() => Json(Deskhand.Core.Services.NetConnectionsService.List());

    [McpServerTool(Name = "deskhand_event_errors"), Description("Recent error/warning events (read-only) from the System + Application logs, newest first: { log, level, eventId, source, time, message }. count = max per log (default 50).")]
    public static string EventErrors(int count = 50) => Json(Deskhand.Core.Services.DiagnosticsService.RecentErrors(count));

    [McpServerTool(Name = "deskhand_disk_health"), Description("Disk health (read-only): per drive { model, serial, status, predictFailure (SMART) }. SMART predict needs elevation on some drivers.")]
    public static string DiskHealth() => Json(Deskhand.Core.Services.DiagnosticsService.DiskHealth());

    [McpServerTool(Name = "deskhand_browse_files"), Description("Browse the file system (read-only): list the folders and files in a directory. path is empty for the drive roots, or a folder like \"C:\\\\Users\". Returns { path, parent, isRoot, entries[{name, path, isDirectory, size, modified, extension}], error? } with folders first. It only lists metadata — it does NOT read file contents; to OPEN a file with its default app, pass its path to deskhand_launch_process. To read the BYTES of a file, use deskhand_read_file. Folders needing elevation return an access error, not a crash.")]
    public static string BrowseFiles([Description("Directory path, e.g. \"C:\\\\Users\\\\Public\". Empty lists the drives.")] string? path = null)
        => Json(Deskhand.Core.Services.FileSystemService.Browse(path));

    [McpServerTool(Name = "deskhand_read_file"), Description("Download a file's contents as base64 (\"download\"). Returns { path, size, base64, error? }. Refused for files over ~25 MB (use the HTTP /fs/download endpoint for large files). SENSITIVE: returns real file bytes (may include secrets). Requires the kill switch to be armed; audited.")]
    public static string ReadFile(ControlState state, AuditLog audit,
        [Description("Full path of the file to read, e.g. \"C:\\\\Users\\\\me\\\\notes.txt\".")] string path)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.ReadFileBase64(path);
        if (r.Error is null) audit.Record("file_read", r.Path, r.Size + "B");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_write_file"), Description("Upload/write a file from base64 content (\"upload\"). Creates parent folders as needed. overwrite=false (default) fails if the file exists. Returns { path, size, overwritten, error? }. SENSITIVE: writes real files (can plant executables). Requires the kill switch to be armed; audited.")]
    public static string WriteFile(ControlState state, AuditLog audit,
        [Description("Full path to write, e.g. \"C:\\\\Users\\\\me\\\\out.bin\".")] string path,
        [Description("File contents as base64.")] string contentBase64,
        [Description("Replace the file if it already exists (default false).")] bool overwrite = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.WriteFileBase64(path, contentBase64, overwrite);
        if (r.Error is null) audit.Record("file_write", r.Path, $"{r.Size}B{(r.Overwritten ? " (overwrote)" : "")}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_delete_path"), Description("Delete a file or folder (folders delete recursively). By DEFAULT it goes to the Recycle Bin (recoverable); pass permanent=true to delete irreversibly. DESTRUCTIVE. Requires the kill switch to be armed; audited. Refuses to delete a drive root.")]
    public static string DeletePath(ControlState state, AuditLog audit,
        [Description("Path of the file or folder to delete.")] string path,
        [Description("Delete permanently instead of to the Recycle Bin (default false).")] bool permanent = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.Delete(path, permanent);
        if (r.Ok) audit.Record("file_delete", r.Path, r.Detail ?? "");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_rename_path"), Description("Rename a file or folder in place. newName is a bare name (no path separators), placed in the same folder. Requires the kill switch to be armed; audited.")]
    public static string RenamePath(ControlState state, AuditLog audit,
        [Description("Path of the file/folder to rename.")] string path,
        [Description("New name (not a path), e.g. \"report-final.txt\".")] string newName)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.Rename(path, newName);
        if (r.Ok) audit.Record("file_rename", $"{r.Path} -> {r.Dest}", r.Detail ?? "");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_move_path"), Description("Move a file or folder. dest may be an existing folder (moves the source into it) or a full destination path. overwrite=false (default) fails if the destination exists. DESTRUCTIVE. Requires the kill switch to be armed; audited.")]
    public static string MovePath(ControlState state, AuditLog audit,
        [Description("Source file/folder path.")] string source,
        [Description("Destination folder or full path.")] string dest,
        [Description("Replace the destination if it exists (default false).")] bool overwrite = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.Move(source, dest, overwrite);
        if (r.Ok) audit.Record("file_move", $"{r.Path} -> {r.Dest}", r.Detail ?? "");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_copy_path"), Description("Copy a file, or a folder recursively. dest may be an existing folder (copies the source into it) or a full destination path. overwrite=false (default) fails if the destination exists. Requires the kill switch to be armed; audited.")]
    public static string CopyPath(ControlState state, AuditLog audit,
        [Description("Source file/folder path.")] string source,
        [Description("Destination folder or full path.")] string dest,
        [Description("Overwrite existing files at the destination (default false).")] bool overwrite = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.Copy(source, dest, overwrite);
        if (r.Ok) audit.Record("file_copy", $"{r.Path} -> {r.Dest}", r.Detail ?? "");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_zip"), Description("Create a .zip archive from one or more files/folders (folders are added recursively under their own name). overwrite=false (default) fails if the zip exists. Returns { op, path, ok, detail, error? }. Requires the kill switch to be armed; audited.")]
    public static string Zip(ControlState state, AuditLog audit,
        [Description("Files and/or folders to include.")] string[] sources,
        [Description("Destination .zip path, e.g. \"C:\\\\out\\\\bundle.zip\".")] string dest,
        [Description("Overwrite the zip if it already exists (default false).")] bool overwrite = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.Zip(sources, dest, overwrite);
        if (r.Ok) audit.Record("file_zip", r.Path, r.Detail ?? "");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_unzip"), Description("Extract a .zip archive into a folder. dest empty extracts next to the zip into a folder named after it. overwrite=false (default) fails if an output file already exists. Returns { op, path, dest, ok, detail, error? }. Requires the kill switch to be armed; audited.")]
    public static string Unzip(ControlState state, AuditLog audit,
        [Description("Path to the .zip file.")] string zipPath,
        [Description("Destination folder (optional; defaults to a folder named after the zip).")] string? dest = null,
        [Description("Overwrite existing files at the destination (default false).")] bool overwrite = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FileSystemService.Unzip(zipPath, dest, overwrite);
        if (r.Ok) audit.Record("file_unzip", $"{r.Path} -> {r.Dest}", r.Detail ?? "");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_run_command"), Description("Run a single command in a shell (default PowerShell; shell=\"cmd\" or \"pwsh\") and return its output: { shell, command, cwd, exitCode, stdout, stderr, durationMs, timedOut, truncated, error? }. STATELESS — each call is a fresh process, so cd/variables do NOT persist between calls (pass cwd for a starting directory). MOST POWERFUL tool (arbitrary code as the current user): it is OFF unless the host sets DESKHAND_ENABLE_SHELL, and also requires the kill switch to be armed; every command is audited. Output is capped; long-running commands are killed at timeoutMs (default 30000, max 600000).")]
    public static string RunCommand(ControlState state, AuditLog audit,
        [Description("The command line to run, e.g. \"Get-Process | Sort CPU -Desc | Select -First 5\".")] string command,
        [Description("\"powershell\" (default), \"pwsh\" (PowerShell 7), or \"cmd\".")] string? shell = null,
        [Description("Working directory to start in (optional).")] string? cwd = null,
        [Description("Kill the command after this many ms (default 30000, max 600000).")] int? timeoutMs = null)
    {
        if (!Deskhand.Core.Services.ShellService.Enabled) return "{\"error\":\"Shell is disabled. Set DESKHAND_ENABLE_SHELL=1.\",\"type\":\"shell_disabled\"}";
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.ShellService.Run(shell, command, cwd, timeoutMs);
        audit.Record("shell_run", $"{r.Shell}: {(command.Length <= 160 ? command : command[..160] + "…")}", r.TimedOut ? "TIMEOUT" : $"exit {r.ExitCode} in {r.DurationMs}ms");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_registry_browse"), Description("Browse the Windows Registry (read-only): list a key's subkeys and values. path is empty for the hive roots, or \"HKLM\" / \"HKCU\\SOFTWARE\\Microsoft\" etc. (hives: HKLM, HKCU, HKCR, HKU, HKCC). Returns { path, subKeys[], values[{name,kind,value}], error? }. Keys needing elevation return an access error, not a crash.")]
    public static string RegistryBrowse([Description("Registry key path, e.g. \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\". Empty lists the hives.")] string? path = null)
        => Json(Deskhand.Core.Services.RegistryService.Browse(path));

    [McpServerTool(Name = "deskhand_firewall_rules"), Description("List Windows Firewall rules (read-only, no elevation). Filters keep the (often hundreds of) rules manageable. Returns { total, returned, rules[{name, direction, action, protocol, localPorts, remotePorts, enabled, profiles, grouping, applicationName, remoteAddresses, managed}], error? }. 'managed' marks rules Deskhand opened.")]
    public static string FirewallRules(
        [Description("Filter by direction: \"in\" or \"out\" (optional).")] string? direction = null,
        [Description("Only rules whose local ports include this port (optional).")] int? port = null,
        [Description("Only enabled rules (optional).")] bool? enabledOnly = null,
        [Description("Only rules whose name/grouping contains this text (optional).")] string? contains = null,
        [Description("Only rules Deskhand opened (optional).")] bool managedOnly = false,
        [Description("Max rules to return (default 200).")] int max = 200)
        => Json(Deskhand.Core.Services.FirewallService.List(direction, port, enabledOnly, contains, managedOnly, max));

    [McpServerTool(Name = "deskhand_firewall_open_port"), Description("Open a port: add an inbound (or outbound) ALLOW rule for a TCP/UDP port, tagged as Deskhand-managed so it can be cleanly closed later with deskhand_firewall_close_port. Returns { ok, ruleName, port, protocol, direction, action, error?, hint? }. Requires the agent running as Administrator (else a clear access-denied hint). OFF unless the host sets DESKHAND_ENABLE_FIREWALL_ADMIN; requires the kill switch armed; audited.")]
    public static string FirewallOpenPort(ControlState state, AuditLog audit,
        [Description("Port number 1-65535.")] int port,
        [Description("\"tcp\" (default) or \"udp\".")] string? protocol = "tcp",
        [Description("\"in\" (default, inbound) or \"out\".")] string? direction = "in",
        [Description("Scope who may connect, e.g. \"LocalSubnet\" or a CIDR (optional; default any).")] string? remoteAddresses = null,
        [Description("Friendly name suffix for the rule (optional).")] string? name = null)
    {
        if (!Deskhand.Core.Services.FirewallService.AdminEnabled)
            return "{\"error\":\"Firewall admin is disabled. Set DESKHAND_ENABLE_FIREWALL_ADMIN=1.\",\"type\":\"firewall_admin_disabled\"}";
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.FirewallService.OpenPort(port, protocol, direction, remoteAddresses, name);
        audit.Record("firewall_open", $"{r.Protocol} {r.Port} ({r.Direction})", r.Ok ? $"added '{r.RuleName}'" : $"FAIL {r.Error}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_firewall_close_port"), Description("Close a port DESKHAND opened: remove only Deskhand-managed rules matching this port/protocol/direction (set all=true to remove every Deskhand-managed rule). NEVER removes a rule Deskhand didn't create, so it can't take down pre-existing rules (RDP, SSH, etc.). Returns { ok, ruleName, removed, error?, hint? }. Requires Administrator. OFF unless DESKHAND_ENABLE_FIREWALL_ADMIN; requires armed; audited.")]
    public static string FirewallClosePort(ControlState state, AuditLog audit,
        [Description("Port number 1-65535 (ignored when all=true).")] int port = 0,
        [Description("\"tcp\" (default) or \"udp\".")] string? protocol = "tcp",
        [Description("\"in\" (default) or \"out\".")] string? direction = "in",
        [Description("Remove ALL Deskhand-managed rules instead of one port.")] bool all = false)
    {
        if (!Deskhand.Core.Services.FirewallService.AdminEnabled)
            return "{\"error\":\"Firewall admin is disabled. Set DESKHAND_ENABLE_FIREWALL_ADMIN=1.\",\"type\":\"firewall_admin_disabled\"}";
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = all
            ? Deskhand.Core.Services.FirewallService.CloseAllManaged()
            : Deskhand.Core.Services.FirewallService.ClosePort(port, protocol, direction);
        audit.Record("firewall_close", all ? "all managed" : $"{r.Protocol} {r.Port} ({r.Direction})", r.Ok ? $"removed {r.Removed}" : $"FAIL {r.Error}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_clipboard_get"), Description("Read the Windows clipboard text. Returns { ok, text, length, hasText, error? }. Requires the kill switch armed (the clipboard may hold secrets).")]
    public static string ClipboardGet(ControlState state)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        return Json(Deskhand.Core.Services.ClipboardService.GetText());
    }

    [McpServerTool(Name = "deskhand_clipboard_set"), Description("Set the Windows clipboard text (use before a paste). Returns { ok, length, error? }. Requires armed; audited.")]
    public static string ClipboardSet(ControlState state, AuditLog audit, [Description("Text to place on the clipboard.")] string text)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.ClipboardService.SetText(text);
        audit.Record("clipboard_set", $"{r.Length} chars", r.Ok ? "ok" : $"FAIL {r.Error}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_clipboard_clear"), Description("Clear the Windows clipboard. Requires armed; audited.")]
    public static string ClipboardClear(ControlState state, AuditLog audit)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.ClipboardService.Clear();
        audit.Record("clipboard_clear", "", r.Ok ? "ok" : $"FAIL {r.Error}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_window"), Description("Manage a top-level window by its nativeWindowHandle (from deskhand_list_windows): action = activate|minimize|maximize|restore|close|move|resize|bounds. move needs x,y; resize needs width,height; bounds needs all four (screen pixels). Returns { ok, hwnd, action, title, state, bounds, error? }. Requires armed; audited.")]
    public static string Window(ControlState state, AuditLog audit,
        [Description("Native window handle (nativeWindowHandle from list_windows).")] long hwnd,
        [Description("activate|minimize|maximize|restore|close|move|resize|bounds")] string action,
        [Description("X (screen px) for move/bounds.")] int? x = null,
        [Description("Y (screen px) for move/bounds.")] int? y = null,
        [Description("Width (px) for resize/bounds.")] int? width = null,
        [Description("Height (px) for resize/bounds.")] int? height = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var res = (action ?? "").Trim().ToLowerInvariant() switch
        {
            "activate" or "focus" => Deskhand.Core.Services.WindowService.Activate(hwnd),
            "minimize" => Deskhand.Core.Services.WindowService.Minimize(hwnd),
            "maximize" => Deskhand.Core.Services.WindowService.Maximize(hwnd),
            "restore" => Deskhand.Core.Services.WindowService.Restore(hwnd),
            "close" => Deskhand.Core.Services.WindowService.Close(hwnd),
            "move" => Deskhand.Core.Services.WindowService.Move(hwnd, x ?? 0, y ?? 0),
            "resize" => Deskhand.Core.Services.WindowService.Resize(hwnd, width ?? 0, height ?? 0),
            "bounds" or "set_bounds" => Deskhand.Core.Services.WindowService.SetBounds(hwnd, x ?? 0, y ?? 0, width ?? 0, height ?? 0),
            _ => new Deskhand.Core.Services.WindowActionResultDto(false, hwnd, action ?? "", Error: "Unknown action. Use activate|minimize|maximize|restore|close|move|resize|bounds."),
        };
        audit.Record("window", $"{res.Action} hwnd={hwnd}", res.Ok ? (res.State ?? "ok") : $"FAIL {res.Error}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_ocr_screen"), Description("OCR the screen: read on-screen text with the built-in Windows OCR engine — the way to read apps UI Automation can't see (custom-drawn, canvas, games, RDP pixels). Returns { ok, text, words[{text,x,y,width,height}], wordCount, lineCount, error? }. Each word box is in SCREEN coordinates, ready to click. Requires capture enabled.")]
    public static string OcrScreen(IAutomationBackend b, ControlState state, [Description("Monitor index (optional; default the whole virtual desktop / primary).")] int? monitor = null)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        var cap = b.CaptureScreen(monitor, ImageFormat.Png, 100);
        return Json(Deskhand.Core.Services.OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y));
    }

    [McpServerTool(Name = "deskhand_ocr_region"), Description("OCR a screen rectangle (screen pixels). Returns { ok, text, words[{text,x,y,width,height}], wordCount, lineCount }. Word boxes are in SCREEN coordinates. Requires capture enabled.")]
    public static string OcrRegion(IAutomationBackend b, ControlState state, int x, int y, int width, int height)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        var cap = b.CaptureRegion(x, y, width, height, ImageFormat.Png, 100);
        return Json(Deskhand.Core.Services.OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y));
    }

    [McpServerTool(Name = "deskhand_ocr_window"), Description("OCR a window by its nativeWindowHandle (hwnd) or element reference. Returns { ok, text, words[{text,x,y,width,height}], wordCount, lineCount }. Word boxes are in SCREEN coordinates. Requires capture enabled.")]
    public static string OcrWindow(IAutomationBackend b, ControlState state,
        [Description("Native window handle (optional if reference given).")] long? hwnd = null,
        [Description("Element reference of a window (optional if hwnd given).")] string? reference = null)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        var cap = reference is not null ? b.CaptureWindowByRef(reference, ImageFormat.Png, 100)
                : hwnd is not null ? b.CaptureWindow(hwnd.Value, ImageFormat.Png, 100)
                : throw new ArgumentException("Provide either hwnd or reference.");
        return Json(Deskhand.Core.Services.OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y));
    }

    [McpServerTool(Name = "deskhand_update_check"), Description("Check GitHub Releases for a newer Deskhand. Returns { current, latest, updateAvailable, name, notes, publishedAt, assetName, assetSize, enabled }. Read-only.")]
    public static string UpdateCheck()
        => Json(Deskhand.Core.Services.UpdateService.CheckAsync().GetAwaiter().GetResult());

    [McpServerTool(Name = "deskhand_update_apply"), Description("Download the latest release and self-update: stops this server and relaunches it on the new version (a few seconds). Only works on a zip/self-contained install. OFF unless the host sets DESKHAND_ENABLE_SELF_UPDATE; requires the kill switch armed; audited.")]
    public static string UpdateApply(ControlState state, AuditLog audit)
    {
        if (!Deskhand.Core.Services.UpdateService.Enabled)
            return "{\"error\":\"Self-update is disabled. Set DESKHAND_ENABLE_SELF_UPDATE=1.\",\"type\":\"update_disabled\"}";
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.UpdateService.ApplyAsync().GetAwaiter().GetResult();
        audit.Record("update_apply", $"{r.From}->{r.To}", r.Ok ? r.Message ?? "ok" : $"FAIL {r.Error}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_find_image"), Description("Find a template image (an icon/button/cursor — the 'needle', passed as a base64 PNG) on the screen by normalized cross-correlation — the visual way to locate things with no text and no UIA. target: \"screen\" (default), \"region\" (needs x,y,width,height), or \"window\" (needs hwnd or reference). Returns { ok, count, matches[{x,y,width,height,centerX,centerY,score}], threshold, haystackWidth, haystackHeight } sorted best-first, with boxes in SCREEN coordinates — click/drag to centerX,centerY. threshold 0.1–1.0 (default 0.85); NCC tolerates brightness/contrast changes but NOT scaling/rotation of the template. Requires capture enabled.")]
    public static string FindImage(IAutomationBackend b, ControlState state,
        [Description("The template to find, as a base64-encoded PNG.")] string templateBase64,
        [Description("\"screen\" (default), \"region\", or \"window\".")] string target = "screen",
        [Description("Monitor index for target=screen (optional).")] int? monitor = null,
        [Description("Region left (target=region).")] int? x = null,
        [Description("Region top (target=region).")] int? y = null,
        [Description("Region width (target=region).")] int? width = null,
        [Description("Region height (target=region).")] int? height = null,
        [Description("Window handle (target=window).")] long? hwnd = null,
        [Description("Window element reference (target=window).")] string? reference = null,
        [Description("Match threshold 0.1–1.0 (default 0.85).")] double threshold = 0.85,
        [Description("Max matches to return (default 10).")] int maxResults = 10)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        byte[] needle;
        try { needle = Convert.FromBase64String(templateBase64 ?? ""); }
        catch { return "{\"error\":\"templateBase64 is not valid base64\",\"type\":\"bad_request\"}"; }
        var cap = (target ?? "screen").ToLowerInvariant() switch
        {
            "region" => b.CaptureRegion(x ?? 0, y ?? 0, width ?? 0, height ?? 0, ImageFormat.Png, 100),
            "window" => reference is not null ? b.CaptureWindowByRef(reference, ImageFormat.Png, 100) : b.CaptureWindow(hwnd ?? 0, ImageFormat.Png, 100),
            _ => b.CaptureScreen(monitor, ImageFormat.Png, 100),
        };
        return Json(Deskhand.Core.Services.TemplateMatchService.Find(cap.Bytes, needle, threshold, maxResults, cap.Rect.X, cap.Rect.Y));
    }

    [McpServerTool(Name = "deskhand_wait_for_image"), Description("Poll the screen until a template image (base64 PNG) appears (or, with absent=true, disappears), or the timeout elapses — the visual twin of wait_for_element. target screen|region|window. Returns { found, waitedMs, result:{matches[],best} } with SCREEN-coordinate boxes. Requires capture enabled.")]
    public static string WaitForImage(IAutomationBackend b, ControlState state, string templateBase64,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, double threshold = 0.85, int timeoutMs = 5000, bool absent = false, int pollMs = 250)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        byte[] needle; try { needle = Convert.FromBase64String(templateBase64 ?? ""); } catch { return "{\"error\":\"templateBase64 is not valid base64\",\"type\":\"bad_request\"}"; }
        return Json(Deskhand.Core.Services.VisionOps.WaitForImage(b, needle, Spec(target, monitor, x, y, width, height, hwnd, reference), threshold, timeoutMs, !absent, pollMs));
    }

    [McpServerTool(Name = "deskhand_wait_for_text"), Description("Poll the screen with OCR until text appears (or, with absent=true, disappears), or timeout. Matches a word containing the query (case-insensitive). Returns { found, waitedMs, matchText, centerX, centerY } — centerX/Y is the matched word (click-ready). target screen|region|window. Requires capture enabled.")]
    public static string WaitForText(IAutomationBackend b, ControlState state, string text,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, int timeoutMs = 5000, bool absent = false, int pollMs = 250)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        return Json(Deskhand.Core.Services.VisionOps.WaitForText(b, text, Spec(target, monitor, x, y, width, height, hwnd, reference), timeoutMs, !absent, pollMs));
    }

    [McpServerTool(Name = "deskhand_wait_stable"), Description("Block until a screen area stops changing (settles) — kills sleep-based flakiness after a click/navigation. With waitForChange=true, returns as soon as it STARTS changing instead. target screen|region|window. Returns { ok, waitedMs, lastDiff, mode }. Requires capture enabled.")]
    public static string WaitStable(IAutomationBackend b, ControlState state,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, int settleMs = 700, int timeoutMs = 8000, int pollMs = 250, double epsilon = 0.01, bool waitForChange = false)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        return Json(Deskhand.Core.Services.VisionOps.WaitStable(b, Spec(target, monitor, x, y, width, height, hwnd, reference), settleMs, timeoutMs, pollMs, epsilon, waitForChange));
    }

    [McpServerTool(Name = "deskhand_click_image"), Description("Find a template image on screen and click its best match in one call (optionally wait up to timeoutMs for it to appear). button left|right|middle; count 2=double. Returns { clicked, x, y, score, error? }. Requires capture enabled + the kill switch armed (it clicks).")]
    public static string ClickImage(IAutomationBackend b, ControlState state, string templateBase64,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, double threshold = 0.85, string button = "left", int count = 1, int timeoutMs = 0)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        byte[] needle; try { needle = Convert.FromBase64String(templateBase64 ?? ""); } catch { return "{\"error\":\"templateBase64 is not valid base64\",\"type\":\"bad_request\"}"; }
        return Try(() => Json(Deskhand.Core.Services.VisionOps.ClickImage(b, needle, Spec(target, monitor, x, y, width, height, hwnd, reference), threshold, button, count, timeoutMs)));
    }

    [McpServerTool(Name = "deskhand_click_text"), Description("Find on-screen text with OCR and click it in one call (optionally wait up to timeoutMs). Clicks the matched word's center. button left|right|middle; count 2=double. Returns { clicked, x, y, error? }. Requires capture enabled + armed.")]
    public static string ClickText(IAutomationBackend b, ControlState state, string text,
        string target = "screen", int? monitor = null, int? x = null, int? y = null, int? width = null, int? height = null,
        long? hwnd = null, string? reference = null, string button = "left", int count = 1, int timeoutMs = 0)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        return Try(() => Json(Deskhand.Core.Services.VisionOps.ClickText(b, text, Spec(target, monitor, x, y, width, height, hwnd, reference), button, count, timeoutMs)));
    }

    [McpServerTool(Name = "deskhand_list_windows_all"), Description("COMPLETE top-level window enumeration via raw Win32 EnumWindows — catches windows the UIA list_windows misses (owned pop-ups, VCL/Delphi nag windows like TInAppShopForm, tool windows). Returns [{hwnd,title,class,pid,process,x,y,width,height,owned,foreground}]. Read-only. Use this when a window 'appeared out of nowhere' and list_windows/get_tree can't see it.")]
    public static string ListWindowsAll() => Json(Deskhand.Core.Services.WindowWatchService.List());

    [McpServerTool(Name = "deskhand_episode_start"), Description("Begin recording a TRAJECTORY of the task you're about to do: while active, every governed action becomes a step paired with a screenshot (state after the action; step 000 = the starting screen). Use it to capture demonstrations for training/eval or a replayable/inspectable log. Returns { active, id, task, steps }.")]
    public static string EpisodeStart(AuditLog audit, [Description("The task being demonstrated, e.g. \"export the project as WAV in FL Studio\".")] string task, [Description("Optional model/agent name for the metadata.")] string? model = null)
    {
        string id = Deskhand.Core.Services.EpisodeRecorder.Start(task, model);
        audit.Record("episode_start", task, id);
        return Json(Deskhand.Core.Services.EpisodeRecorder.Status());
    }

    [McpServerTool(Name = "deskhand_episode_stop"), Description("Finish the current trajectory recording. Pass success=true/false to label the episode (for training/eval). Returns a summary { id, task, steps, success, dir }; download it with GET /episodes/{id} (zip of meta.json + steps.jsonl + screenshots).")]
    public static string EpisodeStop(AuditLog audit, bool? success = null, string? note = null)
    {
        var s = Deskhand.Core.Services.EpisodeRecorder.Stop(success, note);
        audit.Record("episode_stop", s.Id, s.Success == false ? "fail" : "ok");
        return Json(s);
    }

    [McpServerTool(Name = "deskhand_episode_status"), Description("Whether a trajectory is being recorded, and how many steps so far: { active, id, task, steps, note }.")]
    public static string EpisodeStatus() => Json(Deskhand.Core.Services.EpisodeRecorder.Status());

    [McpServerTool(Name = "deskhand_episode_list"), Description("List recorded episodes (ids). Download one with GET /episodes/{id}.")]
    public static string EpisodeList() => Json(new { episodes = Deskhand.Core.Services.EpisodeRecorder.List() });

    [McpServerTool(Name = "deskhand_autodismiss"), Description("Configure the CONTINUOUS nag auto-dismisser — for windows that pop up between your capture and your click, when you can't act. OPT-IN + ALLOWLISTED: give explicit rules [{titleContains?, className?, action?}]; a rule matches a window by title substring and/or class (a rule with neither is rejected — never 'close anything'). action \"hide\" (default, SW_HIDE — removes it WITHOUT running the app's close handler, can't cascade) or \"close\" (WM_CLOSE). Set enabled=true to run. It only acts while the kill switch is ARMED, and logs everything (deskhand_autodismiss_log). Returns the current { enabled, ruleCount, rules, acted, note }.")]
    public static string AutoDismiss(AuditLog audit, Deskhand.Core.Services.AutoRule[]? rules = null, bool? enabled = null)
    {
        var res = Deskhand.Core.Services.AutoDismissService.Configure(rules, enabled);
        audit.Record("autodismiss_config", $"enabled={res.Enabled} rules={res.RuleCount}", "set");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_autodismiss_log"), Description("What the auto-dismisser hid/closed while you were thinking, newest first: [{ts, hwnd, title, class, action, rule}]. Ask this so your model of the screen stays in sync with reality after a window silently vanished.")]
    public static string AutoDismissLog(int limit = 100) => Json(Deskhand.Core.Services.AutoDismissService.Log(limit));

    [McpServerTool(Name = "deskhand_window_watch"), Description("Report-only detector for windows that appear over your app (the nag/dialog that arrives between your capture and your click). action=\"baseline\" snapshots the current top-level windows; action=\"changes\" (default) reports what APPEARED and CLOSED since the baseline (auto-creates one on first use). Returns { baseline, appeared:[{hwnd,title,class,process,foreground,...}], closed:[...], foregroundTitle, note }. It never clicks or closes anything — it just tells you something showed up so you can handle it. Built on the complete Win32 enumeration.")]
    public static string WindowWatch(string action = "changes", string? baseline = null)
        => (action ?? "changes").Trim().ToLowerInvariant() == "baseline"
            ? Json(Deskhand.Core.Services.WindowWatchService.Baseline())
            : Json(Deskhand.Core.Services.WindowWatchService.Changes(baseline));

    [McpServerTool(Name = "deskhand_dismiss_modals"), Description("Find and close open dialogs / modal pop-ups in one call (the routine cost of driving a real app). Dismisses NON-COMMITTALLY: clicks Cancel/Close/No/Don't-Save before it would ever click OK (and never Yes unless acceptYes=true), so it won't confirm a destructive prompt; falls back to closing the window. Only touches dialog-like windows (owned pop-ups / #32770), never the main window. Runs a few passes to clear stacked dialogs. Returns { count, dismissed:[{window,hwnd,via}], note }. Requires armed; audited.")]
    public static string DismissModals(IAutomationBackend b, ControlState state, AuditLog audit,
        [Description("Allow clicking OK/Okay when no non-committal button exists (default true).")] bool acceptOk = true,
        [Description("Allow clicking Yes (default false — Yes often confirms an action).")] bool acceptYes = false,
        [Description("Max passes to clear stacked dialogs (default 4).")] int maxPasses = 4,
        [Description("Also close any window whose TITLE contains one of these substrings — for focus-stealers that pop over the target app (update/sign-in/notification windows).")] string[]? titleContains = null,
        [Description("Also close popup/menu/flyout/dropdown/tooltip class windows (default false).")] bool includePopups = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var res = Deskhand.Core.Services.DismissService.Dismiss(b, acceptOk, acceptYes, maxPasses, titleContains, includePopups);
        audit.Record("dismiss_modals", $"acceptOk={acceptOk} acceptYes={acceptYes}", $"dismissed {res.Count}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_get_pixel"), Description("Read the RGB color of a single screen pixel at (x,y) virtual-desktop coordinates. Returns { ok, x, y, r, g, b, hex }. Cheap state check (is the indicator green yet?). Requires capture enabled.")]
    public static string GetPixel(IAutomationBackend b, ControlState state, int x, int y)
    {
        if (!state.CaptureEnabled) return "{\"error\":\"capture disabled\",\"type\":\"capability_disabled\"}";
        return Json(Deskhand.Core.Services.VisionOps.GetPixel(b, x, y));
    }

    private static Deskhand.Core.Services.CaptureSpec Spec(string? target, int? mon, int? x, int? y, int? w, int? h, long? hwnd, string? reference)
        => new(target, mon, x, y, w, h, hwnd, reference);

    [McpServerTool(Name = "deskhand_paste_text"), Description("Type text fast and exactly by setting the clipboard and sending Ctrl+V (better than key-by-key for long or non-ASCII text). Pastes into the focused control. Requires the kill switch armed; audited.")]
    public static string PasteText(IAutomationBackend b, ControlState state, AuditLog audit, string text)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var set = Deskhand.Core.Services.ClipboardService.SetText(text ?? "");
        if (!set.Ok) return Json(set);
        b.SendKeys("ctrl+v");
        audit.Record("paste", $"{set.Length} chars", "ok");
        return "{\"ok\":true}";
    }

    [McpServerTool(Name = "deskhand_process_control"), Description("Control a process by pid: action = kill (terminate; tree=true kills children too) | suspend | resume | priority (level idle|belownormal|normal|abovenormal|high|realtime). Returns { ok, pid, name, action, error? }. DESTRUCTIVE actions (kill, suspend) require confirm=true — without it you get { confirmationRequired:true } and nothing happens. Deskhand REFUSES to kill/suspend its own process; OS-critical processes need force=true. Protected/other-user processes need elevation. Requires armed; audited.")]
    public static string ProcessControl(ControlState state, AuditLog audit, int pid, string action, bool tree = true, string? level = null, bool force = false, bool confirm = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var act = (action ?? "").Trim().ToLowerInvariant();
        bool destructive = act is "kill" or "terminate" or "suspend";
        if (destructive && !confirm)
            return Json(new { ok = false, confirmationRequired = true, action = act, pid, message = $"'{act}' on pid {pid} is destructive — re-issue with confirm=true to proceed." });
        var res = act switch
        {
            "kill" or "terminate" => Deskhand.Core.Services.ProcessControlService.Kill(pid, tree, force),
            "suspend" => Deskhand.Core.Services.ProcessControlService.Suspend(pid, force),
            "resume" => Deskhand.Core.Services.ProcessControlService.Resume(pid),
            "priority" => Deskhand.Core.Services.ProcessControlService.SetPriority(pid, level ?? ""),
            _ => new Deskhand.Core.Services.ProcControlDto(false, pid, null, action ?? "", Error: "action must be kill|suspend|resume|priority."),
        };
        audit.Record("process_control", $"{res.Action} pid={pid}", res.Ok ? "ok" : $"FAIL {res.Error}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_service_control"), Description("Start / stop / restart a Windows service by name (via WMI). Returns { ok, name, action, state, error? }. DESTRUCTIVE actions (stop, restart) require confirm=true. Deskhand REFUSES to stop the service hosting itself. Most service changes need elevation. Requires armed; audited.")]
    public static string ServiceControl(ControlState state, AuditLog audit, string name, string action, bool confirm = false)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var actn = (action ?? "").Trim().ToLowerInvariant();
        if (actn is "stop" or "restart" && !confirm)
            return Json(new { ok = false, confirmationRequired = true, action = actn, name, message = $"'{actn}' on service '{name}' is destructive — re-issue with confirm=true to proceed." });
        var res = actn switch
        {
            "start" => Deskhand.Core.Services.ServiceControlService.Start(name),
            "stop" => Deskhand.Core.Services.ServiceControlService.Stop(name),
            "restart" => Deskhand.Core.Services.ServiceControlService.Restart(name),
            _ => new Deskhand.Core.Services.ServiceControlDto(false, name, action ?? "", Error: "action must be start|stop|restart."),
        };
        audit.Record("service_control", $"{res.Action} {name}", res.Ok ? (res.State ?? "ok") : $"FAIL {res.Error}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_env_get"), Description("Read an environment variable at scope process (default) | user | machine. Returns { ok, name, value, scope }.")]
    public static string EnvGet(string name, string? scope = null) => Json(Deskhand.Core.Services.EnvironmentService.Get(name, scope));

    [McpServerTool(Name = "deskhand_env_set"), Description("Set (or, with value omitted/null, delete) an environment variable. scope process | user | machine (machine needs elevation). User/machine changes persist but the running server won't see them until restart. Requires armed; audited.")]
    public static string EnvSet(ControlState state, AuditLog audit, string name, string? value = null, string? scope = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var res = Deskhand.Core.Services.EnvironmentService.Set(name, value, scope);
        audit.Record("env_set", $"{res.Scope}:{name}", res.Ok ? "ok" : $"FAIL {res.Error}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_task_action"), Description("Run / end / enable / disable a Windows Scheduled Task by name (path), via schtasks. Returns { ok, task, action, exitCode, output, error? }. Requires armed; audited.")]
    public static string TaskAction(ControlState state, AuditLog audit, string task, string action)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var res = (action ?? "").Trim().ToLowerInvariant() switch
        {
            "run" => Deskhand.Core.Services.ScheduledTaskService.Run(task),
            "end" => Deskhand.Core.Services.ScheduledTaskService.End(task),
            "enable" => Deskhand.Core.Services.ScheduledTaskService.Enable(task),
            "disable" => Deskhand.Core.Services.ScheduledTaskService.Disable(task),
            _ => new Deskhand.Core.Services.TaskActionDto(false, task, action ?? "", -1, Error: "action must be run|end|enable|disable."),
        };
        audit.Record("task", $"{res.Action} {task}", res.Ok ? "ok" : $"FAIL {res.Error}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_uac_status"), Description("Read UAC configuration: { enabled, adminConsentBehavior(+description), promptOnSecureDesktop, automatable, summary }. 'automatable' means prompts are on the normal desktop so they can be answered (if Deskhand is elevated).")]
    public static string UacStatus() => Json(Deskhand.Core.Services.UacService.Status());

    [McpServerTool(Name = "deskhand_uac_config"), Description("Configure UAC (registry, needs elevation): pass ONE of enabled (EnableLUA on/off — reboot required), promptOnSecureDesktop (false moves prompts to the normal desktop so they're automatable), autoApprove (true = admins elevate silently with NO prompt), or adminBehavior (0..5; 0=silent, 5=default prompt). Returns { ok, setting, value, rebootRequired, error? }. Requires armed; audited.")]
    public static string UacConfig(ControlState state, AuditLog audit, bool? enabled = null, bool? promptOnSecureDesktop = null, bool? autoApprove = null, int? adminBehavior = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        Deskhand.Core.Services.UacConfigDto res =
            enabled is bool en ? Deskhand.Core.Services.UacService.SetEnabled(en)
            : promptOnSecureDesktop is bool sd ? Deskhand.Core.Services.UacService.SetSecureDesktop(sd)
            : autoApprove is bool aa ? Deskhand.Core.Services.UacService.SetAutoApprove(aa)
            : adminBehavior is int lvl ? Deskhand.Core.Services.UacService.SetAdminBehavior(lvl)
            : new Deskhand.Core.Services.UacConfigDto(false, "none", null, false, "Provide one of: enabled, promptOnSecureDesktop, autoApprove, adminBehavior.");
        audit.Record("uac_config", $"{res.Setting}={res.Value}", res.Ok ? (res.RebootRequired ? "ok (reboot)" : "ok") : $"FAIL {res.Error}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_uac_respond"), Description("Best-effort answer a UAC consent prompt that is currently showing: accept=true presses Yes, false presses No. Only reaches the dialog when it's on the NORMAL desktop (promptOnSecureDesktop=false) AND Deskhand runs elevated — otherwise Windows isolates it (set adminBehavior=0 to skip prompts entirely). Returns { found, acted, window, waitedMs, note }. Requires armed; audited.")]
    public static string UacRespond(ControlState state, AuditLog audit, bool accept = true, int timeoutMs = 5000)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var res = Deskhand.Core.Services.UacService.Respond(accept, timeoutMs);
        audit.Record("uac_respond", accept ? "accept" : "reject", res.Acted ? "acted" : (res.Found ? "found-only" : "none"));
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_fetch_url"), Description("Download an http/https URL to a file ON THIS MACHINE (e.g. pull an installer/asset onto the target). path is a full destination path or a folder (URL filename kept); omit for a temp file. Size-capped. Returns { ok, url, path, bytes, contentType, error? }. Outbound network request. Requires armed; audited.")]
    public static string FetchUrl(ControlState state, AuditLog audit, string url,
        [Description("Destination path or folder (optional; default a temp file).")] string? path = null,
        [Description("Max bytes to download (optional; default/cap 500 MB).")] long? maxBytes = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var res = Deskhand.Core.Services.FetchService.DownloadAsync(url, path, maxBytes).GetAwaiter().GetResult();
        audit.Record("fetch", $"{url} -> {res.Path}", res.Ok ? $"{res.Bytes} bytes" : $"FAIL {res.Error}");
        return Json(res);
    }

    [McpServerTool(Name = "deskhand_explore_ux"), Description("Get a compact, action-oriented MAP of what's on the current window (or element ref) — the way to navigate a UI without wading through a huge UIA tree or a screenshot you can't see. FUSES two layers: UIA interactables (buttons/menus/tabs/edits/… each with a ref, a click-ready screen center, and its actions: invoke/toggle/expand/setValue/select) AND — crucially for custom-drawn/canvas/plugin/game UIs that have NO usable UIA tree — OCR text targets (every on-screen word as a click point at its center). Returns { window, uiaCount, textCount, targets:[{source,ref,label,type,x,y,enabled,actions,value}], note }. Act on a uia target by ref (deskhand_invoke/set_value/toggle) or click any target's (x,y); for OCR-only UIs use deskhand_click_text/find_image. Ranked + capped.")]
    public static string ExploreUx(IAutomationBackend b,
        [Description("Element ref of the window/container to map. Omit for the foreground window.")] string? reference = null,
        [Description("Include UIA interactables (default true).")] bool uia = true,
        [Description("Include OCR text targets (default true) — needed for UIA-blind UIs.")] bool text = true,
        [Description("Include off-screen/scrolled-out elements (default false).")] bool includeOffscreen = false,
        [Description("Max targets to return (default 200).")] int max = 200,
        [Description("Also include controls in OPEN menu/popup/dropdown windows (separate top-level windows) — needed to navigate menu-driven apps (default true).")] bool includePopups = true)
        => Try(() => Json(Deskhand.Core.Services.UxExplorer.Explore(b, reference, uia, text, includeOffscreen, max, includePopups)));

    [McpServerTool(Name = "deskhand_crawl_ux"), Description("Actively explore a window's UX to build a DEEP, cacheable map of its controls — learn 'every command this app has' once, then recall it. SAFE: it only expands collapsed menus/trees/groups and (optionally) selects tabs to reveal structure; it NEVER invokes buttons/commands (no side effects) and skips dangerous labels (delete/quit/format/…); expanded nodes are collapsed back. Returns a tree { appKey, window, nodes, depth, cached, root:{ref,name,type,x,y,actions,children[]} }. Cached per app — pass useCache=true to return the saved map instantly instead of re-crawling. depth 1–8 (default 3).")]
    public static string CrawlUx(IAutomationBackend b,
        [Description("Element ref of the window/container to crawl. Omit for the foreground window.")] string? reference = null,
        [Description("How many levels deep to expand/recurse (1–8, default 3).")] int depth = 3,
        [Description("Max nodes to visit (default 1500).")] int maxNodes = 1500,
        [Description("Also select tabs to reveal their panes (changes the visible tab; default false).")] bool selectTabs = false,
        [Description("Return the cached map for this app if one exists, instead of re-crawling (default false).")] bool useCache = false)
        => Try(() => Json(Deskhand.Core.Services.UxCrawler.Crawl(b, reference, depth, maxNodes, selectTabs, useCache)));

    [McpServerTool(Name = "deskhand_ux_cache"), Description("List cached UX maps (appKeys) from previous deskhand_crawl_ux runs, or fetch/delete one. action = list (default) | get | delete; for get/delete pass appKey. Recalling a map avoids re-crawling an app you've already explored.")]
    public static string UxCache(string action = "list", string? appKey = null)
    {
        switch ((action ?? "list").ToLowerInvariant())
        {
            case "get":
                if (string.IsNullOrWhiteSpace(appKey)) return Json(new { error = "appKey required for get", type = "bad_request" });
                var m = Deskhand.Core.Services.UxCacheStore.Load(appKey);
                return m is { } j ? j.GetRawText() : Json(new { error = "no cached map for that appKey", type = "not_found" });
            case "delete":
                return Json(new { ok = Deskhand.Core.Services.UxCacheStore.Delete(appKey ?? "") });
            default:
                return Json(new { apps = Deskhand.Core.Services.UxCacheStore.List() });
        }
    }

    [McpServerTool(Name = "deskhand_read_output"), Description("Page through a large tool result that was spilled to the OutputStore (when a previous tool returned { truncated:true, outputId }). Returns { outputId, offset, limit, totalChars, nextOffset, done, text }. Read sequentially with offset = the previous nextOffset until done=true. limit is clamped to the tool-output budget.")]
    public static string ReadOutput(string outputId, int offset = 0, int limit = 0)
        => JsonSerializer.Serialize(Deskhand.Core.Services.OutputStore.ReadSlice(outputId, offset, limit), J);

    [McpServerTool(Name = "deskhand_output_budget"), Description("Get or set the per-result tool-output char budget — set this to YOUR context/tool-output limit so Deskhand shrinks or spills results to fit instead of you truncating them. Omit chars (or 0) to just read the current value; pass chars>0 to set it (clamped to [8000, 20000000]); pass a negative to clear the runtime override and fall back to env/default. Returns { budget, source }.")]
    public static string OutputBudget(int chars = 0)
    {
        if (chars != 0) Deskhand.Core.Services.OutputStore.SetBudget(chars);
        return JsonSerializer.Serialize(new { budget = Deskhand.Core.Services.OutputStore.MaxChars, source = Deskhand.Core.Services.OutputStore.BudgetSource }, J);
    }

    [McpServerTool(Name = "deskhand_dump_process"), Description("Write a FULL-MEMORY crash dump (.dmp, via MiniDumpWriteDump — like Task Manager's 'Create dump file') of a process by pid, for debugging/forensics. Blocks until written (seconds–minutes; the file can be large). Saved on the host and downloadable at /dumps/{name}; auto-deleted after 24h. SENSITIVE: the dump contains the process's memory (may include secrets). Dumping protected/other-user processes needs elevation. Requires the kill switch to be armed.")]
    public static string DumpProcess(Deskhand.Core.Services.ProcessDumper d, ControlState state,
        [Description("Process id to dump.")] int pid)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\"}";
        var dmp = d.Dump(pid);
        return Json(new { dmp.ProcessId, dmp.Name, dmp.File, dmp.FileName, dmp.SizeBytes, dmp.Ts, dmp.DurationMs, url = $"/dumps/{dmp.FileName}" });
    }

    [McpServerTool(Name = "deskhand_launch_process"), Description("Launch a program by path or shell name/URL (e.g. \"notepad\", \"C:\\\\app.exe\", \"https://...\"). Waits up to waitForWindowMs for its main window and returns it if it appears.")]
    public static string LaunchProcess(IAutomationBackend b, string path, string? args = null, string? workingDir = null, int waitForWindowMs = 10000)
        => Try(() => Json(b.LaunchProcess(path, args, workingDir, waitForWindowMs)));

    [McpServerTool(Name = "deskhand_launch_process_as"), Description("Launch a program into a SPECIFIC Terminal-Services session, on a SPECIFIC window-station\\desktop, running as a SPECIFIC user (CreateProcessAsUser). as=\"session\" (default: run as whoever is logged into the target session), \"credentials\" (run as user/domain/password), or \"system\" (NT AUTHORITY\\SYSTEM in that session). sessionId defaults to the active console session; desktop defaults to \"winsta0\\default\". Returns { ok, processId, sessionId, desktop, as, user, error?, win32?, hint? }. POWER TOOL: OFF unless the host sets DESKHAND_ENABLE_SESSION_LAUNCH, requires the kill switch armed, audited. Crossing a session/user boundary requires the host to run as LocalSystem (e.g. the Deskhand Fleet Launcher service) — otherwise you get a clear ERROR_PRIVILEGE_NOT_HELD with a hint; the same-session desktop switch works without elevation.")]
    public static string LaunchProcessAs(ControlState state, AuditLog audit,
        [Description("Program path or name, e.g. \"notepad.exe\" or \"C:\\\\app.exe\".")] string path,
        [Description("Command-line arguments (optional).")] string? args = null,
        [Description("Working directory (optional; defaults to the program's folder).")] string? workingDir = null,
        [Description("Target TS session id. Omit for the active console session; find ids via deskhand_list_sessions.")] int? sessionId = null,
        [Description("Window-station\\desktop, e.g. \"winsta0\\\\default\". Default winsta0\\default.")] string? desktop = null,
        [Description("User context: \"session\" (default), \"credentials\", or \"system\".")] string? @as = null,
        [Description("Username for as=credentials.")] string? user = null,
        [Description("Domain for as=credentials (\".\" for local).")] string? domain = null,
        [Description("Password for as=credentials. Never logged/audited.")] string? password = null,
        [Description("Create with no console window (default false, so GUI apps appear on the desktop).")] bool noWindow = false)
    {
        if (!Deskhand.Core.Services.SessionLaunchService.Enabled)
            return "{\"error\":\"Session launch is disabled. Set DESKHAND_ENABLE_SESSION_LAUNCH=1.\",\"type\":\"session_launch_disabled\"}";
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var asUser = Deskhand.Core.Services.SessionLaunchService.ParseAs(@as);
        var r = Deskhand.Core.Services.SessionLaunchService.Launch(path, args, workingDir, sessionId, desktop, asUser, user, domain, password, noWindow);
        audit.Record("launch_as", $"{path} | session={r.SessionId} desktop={r.Desktop} as={r.As} user={r.User}",
            r.Ok ? $"pid {r.ProcessId}" : $"FAIL {r.Error}");
        return Json(r);
    }

    // ---------- governance ----------

    [McpServerTool(Name = "deskhand_control_status"), Description("Report the kill-switch/capability state: armed, inputEnabled, captureEnabled.")]
    public static string ControlStatus(ControlState s) => Json(new { armed = s.Armed, inputEnabled = s.InputEnabled, captureEnabled = s.CaptureEnabled });

    [McpServerTool(Name = "deskhand_disarm"), Description("Engage the kill switch: refuse all input and capture until re-armed.")]
    public static string Disarm(ControlState s) { s.Armed = false; return "disarmed"; }

    [McpServerTool(Name = "deskhand_arm"), Description("Release the kill switch: allow input and capture again.")]
    public static string Arm(ControlState s) { s.Armed = true; return "armed"; }

    // ---------- record & playback ----------

    [McpServerTool(Name = "deskhand_macro_start"), Description("Start recording actions (input + UIA acts) into a macro.")]
    public static string MacroStart(Deskhand.Core.Macros.MacroRecorder r) { r.Start(); return "recording"; }

    [McpServerTool(Name = "deskhand_macro_stop"), Description("Stop recording and return the recorded macro (also kept as the 'last' macro for playback).")]
    public static string MacroStop(Deskhand.Core.Macros.MacroRecorder r) => Json(r.Stop());

    [McpServerTool(Name = "deskhand_macro_status"), Description("Whether a recording is in progress, its step count, and whether a macro is available to play.")]
    public static string MacroStatus(Deskhand.Core.Macros.MacroRecorder r) =>
        Json(new { recording = r.IsRecording, count = r.CurrentCount, hasLast = r.LastMacro is not null, lastCount = r.LastMacro?.Steps.Count ?? 0 });

    [McpServerTool(Name = "deskhand_macro_expect"), Description("While recording, insert an expectation: playback will WAIT for an element matching these conditions to appear before continuing (do X, expect Y, do Z).")]
    public static string MacroExpect(Deskhand.Core.Macros.MacroRecorder r,
        string? name = null, string? automationId = null, string? controlType = null, string? className = null, int timeoutMs = 5000)
    {
        if (!r.IsRecording) return "not recording";
        r.RecordWait(new Deskhand.Core.Macros.ElementSelectorDto(name, automationId, controlType, className), timeoutMs);
        return $"expectation added ({r.CurrentCount} steps)";
    }

    [McpServerTool(Name = "deskhand_macro_play"), Description("Replay the last recorded macro. UIA steps wait for their target element; explicit expectations block until met. speed>1 plays faster.")]
    public static string MacroPlay(IAutomationBackend b, Deskhand.Core.Macros.MacroRecorder r, double speed = 1.0)
    {
        var macro = r.LastMacro ?? throw new ArgumentException("No macro recorded yet.");
        int played = Deskhand.Core.Macros.MacroPlayer.Play(macro, b, speed);
        return $"played {played} steps";
    }

    [McpServerTool(Name = "deskhand_foreground_window"), Description("The current foreground window element.")]
    public static string ForegroundWindow(IAutomationBackend b) => Json(b.GetForegroundWindow());

    [McpServerTool(Name = "deskhand_get_events"), Description("Poll buffered events newer than sinceId — the hook feed. Event types: focus_changed, window_opened (a window was created), process_started, process_exited. Each carries type, name, controlType, processId, ts. Returns lastId to pass next time.")]
    public static string GetEvents(Deskhand.Core.Events.EventHub hub, long sinceId = 0) =>
        Json(new { lastId = hub.LastId, events = hub.Since(sinceId) });

    [McpServerTool(Name = "deskhand_wait_for_process"), Description("Block until a process starts or exits, then return it. event=\"start\" waits for a NEW launch after this call; event=\"exit\" waits for a matching process to exit (returns immediately if the given pid is already gone). Match by name (substring, e.g. \"chrome\") and/or pid. Returns {event, processId, name}, or a wait_timeout message on timeout. For passive monitoring instead of blocking, poll deskhand_get_events for process_started/process_exited.")]
    public static string WaitForProcess(Deskhand.Core.Events.ProcessWatcher w,
        [Description("\"start\" or \"exit\".")] string @event = "start",
        [Description("Process name substring to match (e.g. \"notepad\"); \".exe\" optional.")] string? name = null,
        [Description("Specific process id to match.")] int? pid = null,
        [Description("Timeout in milliseconds (default 30000).")] int timeoutMs = 30000)
    {
        var hit = w.WaitForProcess(@event, name, pid, timeoutMs);
        return hit is null ? "{\"error\":\"wait_timeout\"}" : Json(hit);
    }

    [McpServerTool(Name = "deskhand_focused_element"), Description("The element that currently has keyboard focus.")]
    public static string FocusedElement(IAutomationBackend b) => Json(b.GetFocusedElement());

    // ---------- uia read ----------

    [McpServerTool(Name = "deskhand_get_tree"), Description("Walk the UI Automation tree. Omit rootRef to start at the desktop. Returns nested elements with opaque refs.")]
    public static string GetTree(IAutomationBackend b,
        [Description("Element ref to start from; omit for the desktop root.")] string? rootRef = null,
        [Description("How many levels deep to expand (default 2).")] int depth = 2,
        [Description("Max children per node (default 40).")] int maxChildren = 40)
        => Json(b.GetTree(rootRef, depth, maxChildren));

    [McpServerTool(Name = "deskhand_find_elements"), Description("Find elements under a root by AND-combined conditions (name, automationId, controlType, className).")]
    public static string FindElements(IAutomationBackend b,
        [Description("Root element ref; omit for desktop.")] string? rootRef = null,
        string? name = null, string? automationId = null, string? controlType = null, string? className = null,
        [Description("children | descendants | subtree (default descendants).")] string scope = "descendants",
        int max = 100)
        => Json(b.Find(rootRef, new FindQuery(name, automationId, controlType, className, scope, max)));

    [McpServerTool(Name = "deskhand_wait_for_element"), Description("Poll until an element matching the conditions appears (or the timeout elapses). Returns the element, or a not-found message on timeout.")]
    public static string WaitForElement(IAutomationBackend b,
        string? rootRef = null, string? name = null, string? automationId = null, string? controlType = null, string? className = null,
        [Description("Scope: children | descendants | subtree (default descendants).")] string scope = "descendants",
        [Description("Timeout in milliseconds (default 5000).")] int timeoutMs = 5000)
    {
        var found = b.WaitForElement(rootRef, new FindQuery(name, automationId, controlType, className, scope, 1), timeoutMs);
        return found is null ? "{\"error\":\"wait_timeout\"}" : Json(found);
    }

    [McpServerTool(Name = "deskhand_get_element"), Description("Re-read a single element by ref (summary properties + supported patterns).")]
    public static string GetElement(IAutomationBackend b, string? reference) => ElementOp(reference, () => Json(b.GetElement(reference!)));

    [McpServerTool(Name = "deskhand_get_all_properties"), Description("Every UIA property the element supports, as a name→value map.")]
    public static string GetAllProperties(IAutomationBackend b, string? reference) => ElementOp(reference, () => Json(b.GetAllProperties(reference!)));

    [McpServerTool(Name = "deskhand_element_from_point"), Description("Return the UIA element at a screen coordinate (virtual-desktop pixels). The reliable 'find element' when a window's tree is thin or its refs go stale (Chromium/Electron apps): screenshot the app, pick a pixel on the target, and get the element + a fresh ref to act on.")]
    public static string ElementFromPoint(IAutomationBackend b,
        [Description("X in virtual-desktop pixels.")] int x,
        [Description("Y in virtual-desktop pixels.")] int y)
        => Json(b.GetElementFromPoint(x, y));

    // ---------- uia act ----------

    [McpServerTool(Name = "deskhand_invoke"), Description("Invoke an element (press a button, activate a menu item) via its UIA Invoke pattern.")]
    public static string Invoke(IAutomationBackend b, string? reference) => ElementOp(reference, () => { b.Invoke(reference!); return "ok"; });

    [McpServerTool(Name = "deskhand_set_value"), Description("Set an element's value (e.g. type into a text box) via the UIA Value pattern.")]
    public static string SetValue(IAutomationBackend b, string? reference, string text) => ElementOp(reference, () => { b.SetValue(reference!, text ?? ""); return "ok"; });

    [McpServerTool(Name = "deskhand_toggle"), Description("Toggle a checkbox or switch via the UIA Toggle pattern.")]
    public static string Toggle(IAutomationBackend b, string? reference) => ElementOp(reference, () => { b.Toggle(reference!); return "ok"; });

    [McpServerTool(Name = "deskhand_expand_collapse"), Description("Expand or collapse a tree item / combo box via the UIA ExpandCollapse pattern.")]
    public static string ExpandCollapse(IAutomationBackend b, string? reference, bool expand) => ElementOp(reference, () => { b.ExpandCollapse(reference!, expand); return "ok"; });

    [McpServerTool(Name = "deskhand_select"), Description("Select a list item / tab via the UIA SelectionItem pattern.")]
    public static string Select(IAutomationBackend b, string? reference) => ElementOp(reference, () => { b.Select(reference!); return "ok"; });

    [McpServerTool(Name = "deskhand_set_focus"), Description("Raise the element's window to the foreground (defeating the foreground lock) and give it keyboard focus.")]
    public static string SetFocus(IAutomationBackend b, string? reference) => ElementOp(reference, () => { b.SetFocus(reference!); return "ok"; });

    // ---------- screen recording (GIF / MJPEG-AVI) ----------

    [McpServerTool(Name = "deskhand_record_start"), Description("Start recording the screen to an animated GIF or an MJPEG AVI video. monitor: an index for one monitor, or omit for the whole virtual desktop (all monitors). format: \"gif\" or \"avi\". A hard maxDurationMs auto-stops and finalizes the file even if stop is never called (safety against a runaway recording). Returns a recording id + status; poll deskhand_record_status and call deskhand_record_stop to finish. The finished file is saved on the host and downloadable at /recordings/{id}.")]
    public static string RecordStart(Deskhand.Core.Services.ScreenRecorder rec, ControlState state,
        [Description("Monitor index to record; omit for the whole virtual desktop (all monitors).")] int? monitor = null,
        [Description("\"gif\" (animated, smaller, 256 colours) or \"avi\" (MJPEG video, full colour).")] string format = "gif",
        [Description("Frames per second (1..30, default 10).")] int fps = 10,
        [Description("Output scale percent (10..100, default 100). Lower = smaller file.")] int scale = 100,
        [Description("AVI/JPEG quality 1..100 (default 75); ignored for gif.")] int quality = 75,
        [Description("Hard auto-stop ceiling in ms (default 30000, max 300000).")] int maxDurationMs = 30000)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\"}";
        if (!state.CaptureEnabled) return "{\"error\":\"capture_disabled\"}";
        return Json(rec.Start(new Deskhand.Core.Services.RecordingOptions(monitor, format, fps, scale, quality, maxDurationMs)));
    }

    [McpServerTool(Name = "deskhand_record_stop"), Description("Stop a recording and finalize (encode) its file. Returns the final status incl. the saved file path, size, and frame count.")]
    public static string RecordStop(Deskhand.Core.Services.ScreenRecorder rec, string id) => Json(rec.Stop(id));

    [McpServerTool(Name = "deskhand_record_status"), Description("Status of one recording (id) or, with no id, all recordings this session: state (recording|encoding|completed|error), frames, elapsed, size, file.")]
    public static string RecordStatus(Deskhand.Core.Services.ScreenRecorder rec, string? id = null) =>
        Json(id is null ? (object)rec.List() : rec.GetStatus(id));

    // ---------- record the USER's input (what the human did) ----------

    [McpServerTool(Name = "deskhand_user_input_start"), Description("Start recording the USER's own mouse and keyboard input (global hooks). Each click is annotated with the UIA element it landed on (controlType, name, ref), so you can see WHAT was clicked, not just coordinates. Scrolls and typed text are captured too. This is the human's activity — distinct from macro recording, which records the agent's own actions. PRIVACY: this captures real keystrokes (may include passwords); set captureText=false to record mouse only. Poll deskhand_user_input_get, then deskhand_user_input_stop.")]
    public static string UserInputStart(Deskhand.Core.Services.InputRecorder ir,
        [Description("Also capture typed text/keys (default true). Set false for mouse-only.")] bool captureText = true)
        => Json(ir.Start(captureText));

    [McpServerTool(Name = "deskhand_user_input_stop"), Description("Stop recording the user's input and return the full sequence of events (clicks with their elements, scrolls, typed text, special keys).")]
    public static string UserInputStop(Deskhand.Core.Services.InputRecorder ir)
        => Json(new { status = ir.Stop(), events = ir.Since(0) });

    [McpServerTool(Name = "deskhand_user_input_get"), Description("Get recorded user-input events newer than sinceId (clicks+elements, scrolls, text, keys) while recording is in progress. Returns lastId to pass next time.")]
    public static string UserInputGet(Deskhand.Core.Services.InputRecorder ir, long sinceId = 0)
        => Json(new { lastId = ir.LastId, recording = ir.IsRecording, events = ir.Since(sinceId) });

    // ---------- capture (returns MCP image content, or a saved-file path when save=true) ----------

    // Default (save=false): return an MCP image content block — image-capable clients render it inline.
    // save=true: return the image as BASE64 TEXT in a single call (works in clients that don't handle MCP
    // image blocks) AND save the file on the machine, returning its path + /screenshots/{name} URL. Errors
    // (e.g. disk full) come back as a clear { error } instead of throwing.
    private static IEnumerable<ContentBlock> CaptureOut(Deskhand.Core.Services.ScreenshotStore ss, CaptureResultDto c, bool save, int? maxWidth = null, int? maxBytes = null)
    {
        if (!save) return AsImage(c, maxWidth, maxBytes);
        try
        {
            var img = Deskhand.Core.Services.ImageScaler.Fit(c.Bytes, c.Format, maxWidth, maxBytes);
            var s = ss.Save(img.Bytes, img.Format);
            return new ContentBlock[] { new TextContentBlock { Text = Json(new {
                saved = true, file = s.File, url = $"/screenshots/{s.FileName}", sizeBytes = s.SizeBytes,
                format = img.Format, screenRect = new { c.Rect.Width, c.Rect.Height, c.Rect.X, c.Rect.Y },
                imageWidth = img.Scale < 1.0 ? img.Width : c.Rect.Width, imageHeight = img.Scale < 1.0 ? img.Height : c.Rect.Height, scale = img.Scale,
                base64 = Convert.ToBase64String(img.Bytes) }) } };
        }
        catch (Exception ex)
        {
            return new ContentBlock[] { new TextContentBlock { Text = Json(new { error = "capture save failed: " + ex.Message, type = ex.GetType().Name }) } };
        }
    }

    [McpServerTool(Name = "deskhand_capture_screen"), Description("Screenshot a monitor (by index) or the whole virtual desktop (omit monitor). Returns an MCP image block (image-capable clients render it). IF YOUR CLIENT CAN'T DISPLAY MCP IMAGES (you only see metadata, no image): pass save=true — it returns the screenshot as base64 TEXT in one call, plus a saved file + /screenshots/{name} download URL. TO FIT A SIZE BUDGET: pass maxWidth (cap the resolution) and/or maxBytes (cap the encoded payload; PNG is auto-switched to JPEG and downscaled to fit). PASS withTargets=true to ALSO get the clickable text + UIA controls (label + screen center) in the same call — so you can click-by-text without a second round-trip. The metadata reports the returned image's dimensions + scale so you can map image pixels back to screen coordinates.")]
    public static IEnumerable<ContentBlock> CaptureScreen(IAutomationBackend b, Deskhand.Core.Services.ScreenshotStore ss, int? monitor = null, string? format = null, bool save = false, int? maxWidth = null, int? maxBytes = null, bool withTargets = false,
        [Description("Set-of-Mark: draw numbered boxes over actionable targets and return a legend, so you pick a number (act_mark) instead of guessing a pixel.")] bool marks = false,
        [Description("Max marks to draw (default 60). Dense UIs report a 'total' so you know it's capped — narrow instead of drawing hundreds.")] int maxMarks = 60,
        [Description("Only mark targets whose label contains this text (optional) — the way to cut a dense UI down.")] string? markFilter = null,
        [Description("Restrict marks to \"uia\" (controls) or \"text\" (OCR); default all.")] string? markOnly = null)
    {
        var c = b.CaptureScreen(monitor, Fmt(format), (withTargets || marks) ? 100 : 80);
        return marks ? WithMarks(b, ss, c, save, maxWidth, maxBytes, maxMarks, markFilter, markOnly) : Annotate(CaptureOut(ss, c, save, maxWidth, maxBytes), b, c, withTargets);
    }

    [McpServerTool(Name = "deskhand_capture_region"), Description("Screenshot an arbitrary rectangle in virtual-desktop pixels. Returns the image inline; save=true saves it on the machine and returns a download URL instead. maxWidth/maxBytes fit a size budget; withTargets=true also returns clickable text + controls (see capture_screen).")]
    public static IEnumerable<ContentBlock> CaptureRegion(IAutomationBackend b, Deskhand.Core.Services.ScreenshotStore ss, int x, int y, int width, int height, string? format = null, bool save = false, int? maxWidth = null, int? maxBytes = null, bool withTargets = false, bool marks = false, int maxMarks = 60, string? markFilter = null, string? markOnly = null)
    {
        var c = b.CaptureRegion(x, y, width, height, Fmt(format), (withTargets || marks) ? 100 : 80);
        return marks ? WithMarks(b, ss, c, save, maxWidth, maxBytes, maxMarks, markFilter, markOnly) : Annotate(CaptureOut(ss, c, save, maxWidth, maxBytes), b, c, withTargets);
    }

    [McpServerTool(Name = "deskhand_capture_window"), Description("Screenshot one window by element ref (its host window). Returns the image inline; save=true saves it on the machine and returns a download URL instead. maxWidth/maxBytes fit a size budget; withTargets=true also returns clickable text + controls (see capture_screen).")]
    public static IEnumerable<ContentBlock> CaptureWindow(IAutomationBackend b, Deskhand.Core.Services.ScreenshotStore ss, string reference, string? format = null, bool save = false, int? maxWidth = null, int? maxBytes = null, bool withTargets = false, bool marks = false, int maxMarks = 60, string? markFilter = null, string? markOnly = null)
    {
        var c = b.CaptureWindowByRef(reference, Fmt(format), (withTargets || marks) ? 100 : 80);
        return marks ? WithMarks(b, ss, c, save, maxWidth, maxBytes, maxMarks, markFilter, markOnly) : Annotate(CaptureOut(ss, c, save, maxWidth, maxBytes), b, c, withTargets);
    }

    // Set-of-Mark: draw numbered boxes on the capture, remember the mapping, and return the annotated image plus
    // a legend { markSet, marks:[{id,label,type,ref,x,y,actions}] }. Act on a number with deskhand_act_mark.
    private static IEnumerable<ContentBlock> WithMarks(IAutomationBackend b, Deskhand.Core.Services.ScreenshotStore ss, CaptureResultDto c, bool save, int? maxWidth, int? maxBytes, int maxMarks, string? markFilter, string? markOnly)
    {
        var (annotated, marks, total) = Deskhand.Core.Services.SetOfMarkService.Build(b, c, includeText: true, includePopups: true, max: maxMarks, filter: markFilter, only: markOnly);
        string set = Deskhand.Core.Services.MarkStore.Save(marks);
        var cA = c with { Bytes = annotated, Format = "png" };   // annotated output is always PNG
        var blocks = CaptureOut(ss, cA, save, maxWidth, maxBytes).ToList();
        string note = total > marks.Count
            ? $"Showing {marks.Count} of {total} targets (capped for legibility). Too dense — narrow with markFilter=\"<text>\" or markOnly=\"uia\", capture a smaller region with marks, or raise maxMarks. Act with deskhand_act_mark(id)."
            : "Numbered boxes are drawn on the image. Act on one with deskhand_act_mark(id). uia marks act by ref (invoke/set_value); ocr/text marks click their center.";
        blocks.Add(new TextContentBlock { Text = Json(new
        {
            markSet = set,
            count = marks.Count,
            total,
            note,
            marks = marks.Select(m => new { m.Id, m.Label, m.Type, m.Ref, m.X, m.Y, m.Actions }),
        }) });
        return blocks;
    }

    [McpServerTool(Name = "deskhand_act_mark"), Description("Act on a NUMBERED mark from the most recent capture(marks:true) — you pick the number you saw drawn on the image and Deskhand hits its exact target (no pixel guessing). action: click (default) | double | right | move | invoke | toggle | setValue (pass text) | select. A 'uia' mark prefers its ref; an 'ocr'/'text' mark clicks its center. Pass markSet to act on an older set. Returns { ok, id, action, x, y, ref?, label }. Requires armed; audited.")]
    public static string ActMark(IAutomationBackend b, ControlState state, AuditLog audit, int id,
        string? action = null, string? text = null, string? markSet = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var m = Deskhand.Core.Services.MarkStore.Get(markSet, id);
        if (m is null) return Json(new { error = $"No mark {id} (capture with marks:true first, or pass a valid markSet).", type = "not_found" });
        string act = (action ?? (m.Ref is not null && m.Actions.Contains("invoke") ? "invoke" : "click")).Trim().ToLowerInvariant();
        return Try(() =>
        {
            switch (act)
            {
                case "click": b.MouseClick("left", m.X, m.Y, 1); break;
                case "double": case "doubleclick": b.MouseClick("left", m.X, m.Y, 2); act = "double"; break;
                case "right": case "rightclick": b.MouseClick("right", m.X, m.Y, 1); act = "right"; break;
                case "move": case "moveto": b.MouseMove(m.X, m.Y); break;
                case "invoke": if (m.Ref is not null) b.Invoke(m.Ref); else { b.MouseClick("left", m.X, m.Y, 1); act = "click"; } break;
                case "toggle": if (m.Ref is null) throw new ArgumentException("This mark has no UIA ref to toggle."); b.Toggle(m.Ref); break;
                case "setvalue": if (m.Ref is null) throw new ArgumentException("This mark has no UIA ref to set."); b.SetValue(m.Ref, text ?? ""); break;
                case "select": if (m.Ref is not null) b.Select(m.Ref); else { b.MouseClick("left", m.X, m.Y, 1); act = "click"; } break;
                default: b.MouseClick("left", m.X, m.Y, 1); act = "click"; break;
            }
            audit.Record("act_mark", $"{act} #{id} ({m.Type})", "ok");
            return Json(new { ok = true, id, action = act, m.X, m.Y, m.Ref, m.Label });
        });
    }

    // Append a compact "what's clickable here" block (OCR text + UIA controls) computed from the SAME captured
    // bytes — so a capture can carry the click-by-text/-control targets without a second round-trip.
    private static IEnumerable<ContentBlock> Annotate(IEnumerable<ContentBlock> blocks, IAutomationBackend b, CaptureResultDto c, bool withTargets)
    {
        var list = blocks.ToList();
        if (!withTargets) return list;
        try
        {
            var ocr = Deskhand.Core.Services.OcrService.Recognize(c.Bytes, c.Rect.X, c.Rect.Y);
            var text = ocr.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => new { label = w.Text, x = w.X + w.Width / 2, y = w.Y + w.Height / 2 }).Take(400).ToList();
            var uia = new List<object>();
            try
            {
                var fg = b.GetForegroundWindow();
                foreach (var e in b.Find(fg.Ref, new FindQuery(Scope: "descendants", Max: 400)))
                {
                    if (e.IsOffscreen || e.BoundingRect is not { Width: > 0, Height: > 0 } r) continue;
                    var acts = e.Patterns.Where(p => p is "Invoke" or "Toggle" or "ExpandCollapse" or "Value" or "SelectionItem").ToList();
                    if (acts.Count == 0 && e.ControlType is not ("Button" or "MenuItem" or "TabItem" or "CheckBox" or "RadioButton" or "Edit" or "ComboBox" or "Hyperlink" or "ListItem")) continue;
                    uia.Add(new { e.Ref, name = e.Name, type = e.ControlType, x = r.X + r.Width / 2, y = r.Y + r.Height / 2, enabled = e.IsEnabled });
                }
            }
            catch { }
            list.Add(new TextContentBlock { Text = Json(new { targets = new { uiaCount = uia.Count, textCount = text.Count, uia, text } }) });
        }
        catch { }
        return list;
    }

    [McpServerTool(Name = "deskhand_capture_element"), Description("Screenshot a single element's bounding rectangle. Returns the image inline; save=true saves it on the machine and returns a download URL instead. maxWidth/maxBytes fit a size budget (see capture_screen).")]
    public static IEnumerable<ContentBlock> CaptureElement(IAutomationBackend b, Deskhand.Core.Services.ScreenshotStore ss, string reference, string? format = null, bool save = false, int? maxWidth = null, int? maxBytes = null)
        => CaptureOut(ss, b.CaptureElement(reference, Fmt(format), 80), save, maxWidth, maxBytes);

    [McpServerTool(Name = "deskhand_capture_input_desktop"), Description("Phase 2: screenshot whichever desktop currently owns input (the secure desktop when running as SYSTEM). Returns an image plus a status line.")]
    public static IEnumerable<ContentBlock> CaptureInputDesktop(IAutomationBackend b, string? format = null)
    {
        var r = b.CaptureInputDesktop(Fmt(format), 80);
        yield return new TextContentBlock { Text = $"success={r.Success} desktop={r.DesktopName} kind={r.Kind} note={r.Note}" };
        if (r.Success && r.Capture is not null)
            yield return new ImageContentBlock
            {
                Data = r.Capture.Bytes,
                MimeType = r.Capture.Format == "jpeg" ? "image/jpeg" : "image/png",
            };
    }

    // ---------- input ----------

    [McpServerTool(Name = "deskhand_mouse_move"), Description("Move the mouse to a virtual-desktop pixel coordinate.")]
    public static string MouseMove(IAutomationBackend b, int x, int y) { b.MouseMove(x, y); return "ok"; }

    [McpServerTool(Name = "deskhand_mouse_click"), Description("Click at a point (or at the current cursor if x/y omitted). button: left|right|middle. count: 2 for double-click.")]
    public static string MouseClick(IAutomationBackend b, string button = "left", int? x = null, int? y = null, int count = 1)
    { b.MouseClick(button, x, y, count); return "ok"; }

    [McpServerTool(Name = "deskhand_mouse_scroll"), Description("Scroll the wheel. dy positive scrolls up, dx positive scrolls right (in notches).")]
    public static string MouseScroll(IAutomationBackend b, int dx, int dy) { b.MouseScroll(dx, dy); return "ok"; }

    [McpServerTool(Name = "deskhand_drag"), Description("Drag-and-drop: press the mouse at (fromX,fromY), move smoothly to (toX,toY), release. Virtual-desktop pixel coordinates. button left|right|middle (default left); steps = interpolation points for smoothness (default 20); holdMs = dwell after press and before release (default 60) for drop targets that need it. One atomic gesture.")]
    public static string Drag(IAutomationBackend b, int fromX, int fromY, int toX, int toY,
        string button = "left", int steps = 20, int holdMs = 60)
    { b.Drag(fromX, fromY, toX, toY, button, steps, holdMs); return "ok"; }

    [McpServerTool(Name = "deskhand_type_text"), Description("Type a literal Unicode string via synthetic keyboard input. Keystrokes go to whatever window has focus — if input isn't landing, pass reference (an element ref from find/element_from_point) to focus that element's window FIRST, or click the field before typing. For a plain text box, deskhand_set_value is more reliable (it sets the value via UIA, no focus needed).")]
    public static string TypeText(IAutomationBackend b, string text, string? reference = null)
        => FocusThen(b, reference, () => b.TypeText(text));

    [McpServerTool(Name = "deskhand_send_keys"), Description("Send a key chord to the focused window. Pass reference (an element ref) to focus that element's window FIRST if the chord isn't reaching the right app.")]
    public static string SendKeys(IAutomationBackend b,
        [Description("Key chord, '+'-separated, modifiers first. Modifiers: ctrl, alt, shift, win. Key (last token): a letter/digit/symbol (e.g. s, 1, /), F1–F24, or a named key: enter, return, tab, esc, space, backspace, delete, insert, home, end, pageup, pagedown, up, down, left, right, printscreen. Examples: \"ctrl+s\", \"ctrl+shift+esc\", \"alt+F4\", \"win+d\", \"enter\".")] string chord,
        [Description("Optional element ref whose window to focus before sending.")] string? reference = null)
        => FocusThen(b, reference, () => b.SendKeys(chord));

    [McpServerTool(Name = "deskhand_press_keys"), Description("Send a SEQUENCE of key chords in order — e.g. [\"alt+f\",\"s\"] to walk a File→Save menu, or [\"ctrl+a\",\"delete\"]. betweenMs paces them (default 40); repeat sends the whole sequence N times. Goes to the focused window (pass reference to focus first). Requires armed; audited.")]
    public static string PressKeys(IAutomationBackend b, ControlState state, AuditLog audit, string[] chords,
        int betweenMs = 40, int repeat = 1, string? reference = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        if (chords is null || chords.Length == 0) return "{\"error\":\"no chords\",\"type\":\"bad_request\"}";
        return Try(() =>
        {
            var res = FocusThen(b, reference, () =>
            {
                for (int r = 0; r < Math.Clamp(repeat, 1, 100); r++)
                    foreach (var c in chords) { b.SendKeys(c); if (betweenMs > 0) System.Threading.Thread.Sleep(Math.Clamp(betweenMs, 0, 5000)); }
            });
            audit.Record("press_keys", $"{chords.Length} chords x{repeat}", "ok");
            return res;
        });
    }

    [McpServerTool(Name = "deskhand_hold_key"), Description("Press and HOLD a key (with optional modifiers) for holdMs, then release — for games / key-repeat, e.g. hold \"w\" 1500ms or \"shift+right\" 600ms. Goes to the focused window. Requires armed; audited. (Local only; not recorded into macros.)")]
    public static string HoldKey(ControlState state, AuditLog audit, string key, int holdMs = 500)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        return Try(() =>
        {
            Deskhand.Core.Services.InputInjector.SendKeys(key, Math.Clamp(holdMs, 1, 30000));
            audit.Record("hold_key", $"{key} {holdMs}ms", "ok");
            return "{\"ok\":true}";
        });
    }

    [McpServerTool(Name = "deskhand_secure_attention"), Description("Send Ctrl+Alt+Del (the Secure Attention Sequence) via the SendSAS API — plain key injection can't forge it. Works when Deskhand runs as LocalSystem, OR when the SoftwareSASGeneration policy allows apps (see deskhand_sas_status / deskhand_configure_sas). Raises the secure desktop; clicking its options needs the SYSTEM secure-desktop path. Returns { ok, action, error?, hint? }. Requires armed; audited.")]
    public static string SecureAttention(ControlState state, AuditLog audit,
        [Description("Override AsUser (default auto: false when running as SYSTEM, else true).")] bool? asUser = null)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.SecureInputService.SendCtrlAltDel(asUser);
        audit.Record("secure_attention", "ctrl+alt+del", r.Ok ? "ok" : $"FAIL {r.Error}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_lock_workstation"), Description("Lock the workstation (Win+L equivalent, via LockWorkStation). Requires armed; audited.")]
    public static string LockWorkstation(ControlState state, AuditLog audit)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.SecureInputService.LockWorkstation();
        audit.Record("lock_workstation", "", r.Ok ? "ok" : $"FAIL {r.Error}");
        return Json(r);
    }

    [McpServerTool(Name = "deskhand_sas_status"), Description("Whether Ctrl+Alt+Del can be sent from here: { isSystem, softwareSasGeneration, sasPolicy, canSendSas, note }. Read-only.")]
    public static string SasStatus() => Json(Deskhand.Core.Services.SecureInputService.Status());

    [McpServerTool(Name = "deskhand_configure_sas"), Description("Set the SoftwareSASGeneration policy so apps can send Ctrl+Alt+Del: level 0 none | 1 services | 2 ease-of-access apps | 3 both. Needs elevation. Requires armed; audited.")]
    public static string ConfigureSas(ControlState state, AuditLog audit, int level)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\",\"type\":\"disarmed\"}";
        var r = Deskhand.Core.Services.SecureInputService.ConfigureSas(level);
        audit.Record("configure_sas", $"level={level}", r.Ok ? "ok" : $"FAIL {r.Error}");
        return Json(r);
    }

    // Optionally raise+focus the target element's window before sending input, so keystrokes land where
    // intended (the #1 cause of "input didn't reach the app" is the wrong window being foreground).
    private static string FocusThen(IAutomationBackend b, string? reference, Action send)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(reference)) { b.SetFocus(reference); System.Threading.Thread.Sleep(120); }
            send();
            return reference is null ? "ok" : Json(new { ok = true, focused = reference });
        }
        catch (Exception ex) { return Json(new { error = ex.Message, type = ex.GetType().Name }); }
    }
}
