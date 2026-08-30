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

    private static string Json(object? o) => JsonSerializer.Serialize(o, J);

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

    private static IEnumerable<ContentBlock> AsImage(CaptureResultDto c)
    {
        yield return new TextContentBlock
        {
            Text = $"desktop={c.Desktop} rect={c.Rect.Width}x{c.Rect.Height}@({c.Rect.X},{c.Rect.Y}) " +
                   $"monitor={c.Monitor} dpi={c.DpiScale} format={c.Format}",
        };
        yield return new ImageContentBlock
        {
            Data = c.Bytes,
            MimeType = c.Format == "jpeg" ? "image/jpeg" : "image/png",
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

    [McpServerTool(Name = "deskhand_dump_process"), Description("Write a FULL-MEMORY crash dump (.dmp, via MiniDumpWriteDump — like Task Manager's 'Create dump file') of a process by pid, for debugging/forensics. Blocks until written (seconds–minutes; the file can be large). Saved on the host and downloadable at /dumps/{name}; auto-deleted after 24h. SENSITIVE: the dump contains the process's memory (may include secrets). Dumping protected/other-user processes needs elevation. Requires the kill switch to be armed.")]
    public static string DumpProcess(Deskhand.Core.Services.ProcessDumper d, ControlState state,
        [Description("Process id to dump.")] int pid)
    {
        if (!state.Armed) return "{\"error\":\"disarmed\"}";
        return Json(d.Dump(pid));
    }

    [McpServerTool(Name = "deskhand_launch_process"), Description("Launch a program by path or shell name/URL (e.g. \"notepad\", \"C:\\\\app.exe\", \"https://...\"). Waits up to waitForWindowMs for its main window and returns it if it appears.")]
    public static string LaunchProcess(IAutomationBackend b, string path, string? args = null, string? workingDir = null, int waitForWindowMs = 10000)
        => Try(() => Json(b.LaunchProcess(path, args, workingDir, waitForWindowMs)));

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

    // ---------- capture (returns MCP image content) ----------

    [McpServerTool(Name = "deskhand_capture_screen"), Description("Screenshot a monitor (by index) or the whole virtual desktop (omit monitor). Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureScreen(IAutomationBackend b, int? monitor = null, string? format = null)
        => AsImage(b.CaptureScreen(monitor, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_capture_region"), Description("Screenshot an arbitrary rectangle in virtual-desktop pixels. Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureRegion(IAutomationBackend b, int x, int y, int width, int height, string? format = null)
        => AsImage(b.CaptureRegion(x, y, width, height, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_capture_window"), Description("Screenshot one window by element ref (its host window). Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureWindow(IAutomationBackend b, string reference, string? format = null)
        => AsImage(b.CaptureWindowByRef(reference, Fmt(format), 80));

    [McpServerTool(Name = "deskhand_capture_element"), Description("Screenshot a single element's bounding rectangle. Returns an image.")]
    public static IEnumerable<ContentBlock> CaptureElement(IAutomationBackend b, string reference, string? format = null)
        => AsImage(b.CaptureElement(reference, Fmt(format), 80));

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

    [McpServerTool(Name = "deskhand_type_text"), Description("Type a literal Unicode string via synthetic keyboard input.")]
    public static string TypeText(IAutomationBackend b, string text) { b.TypeText(text); return "ok"; }

    [McpServerTool(Name = "deskhand_send_keys"), Description("Send a key chord, e.g. \"ctrl+shift+s\", \"alt+F4\", \"enter\", \"tab\".")]
    public static string SendKeys(IAutomationBackend b, string chord) { b.SendKeys(chord); return "ok"; }
}
