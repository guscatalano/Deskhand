using System.Text.Json;

namespace Deskhand.Core.Fleet;

/// <summary>Agent side: turns an incoming <see cref="FleetCommand"/> into a call on the local backend.</summary>
public static class AgentDispatcher
{
    public static object? Dispatch(FleetCommand cmd, AgentServices svc)
    {
        var a = cmd.Args;
        var b = svc.Backend;
        switch (cmd.Method)
        {
            // ---- observation: events, hooks, recording, user-input (require the extra services) ----
            case FleetMethods.GetEvents:
            {
                var h = Req(svc.Events, "events");
                return new { lastId = h.LastId, events = h.Since(a.Long("sinceId", 0)) };
            }
            case FleetMethods.WaitForProcess:
                return Req(svc.Processes, "process watcher")
                    .WaitForProcess(a.Str("event") ?? "start", a.Str("name"), a.IntN("pid"), a.Int("timeoutMs", 30000));
            case FleetMethods.RecordStart:
                return Req(svc.Recorder, "recorder").Start(new Services.RecordingOptions(
                    a.IntN("monitor"), a.Str("format") ?? "gif", a.Int("fps", 10), a.Int("scale", 100),
                    a.Int("quality", 75), a.Int("maxDurationMs", 30000)));
            case FleetMethods.RecordStop:
                return Req(svc.Recorder, "recorder").Stop(a.Str("id")!);
            case FleetMethods.RecordStatus:
                return a.Str("id") is { } rid ? Req(svc.Recorder, "recorder").GetStatus(rid)
                                              : (object)Req(svc.Recorder, "recorder").List();
            case FleetMethods.RecordRead:
            {
                var (bytes, mime, name) = Req(svc.Recorder, "recorder").Read(a.Str("id")!);
                return new { name, mime, base64 = Convert.ToBase64String(bytes) };
            }
            case FleetMethods.InputStart:
                return Req(svc.Input, "input recorder").Start(a.Bool("captureText", true));
            case FleetMethods.InputStop:
            {
                var ir = Req(svc.Input, "input recorder");
                return new { status = ir.Stop(), events = ir.Since(0) };
            }
            case FleetMethods.InputGet:
            {
                var ir = Req(svc.Input, "input recorder");
                return new { lastId = ir.LastId, recording = ir.IsRecording, events = ir.Since(a.Long("sinceId", 0)) };
            }
            case FleetMethods.RdpInstallAgent:
                return (svc.RdpInstallAgent ?? throw new InvalidOperationException("This agent isn't an RDP connector, so it can't install the native agent over RDP."))(a.Str("agentPath"));
            case FleetMethods.DumpProcess:
                return Req(svc.Dumper, "process dumper").Dump(a.Int("pid"));
            case FleetMethods.DumpList:
                return Req(svc.Dumper, "process dumper").List();
            case FleetMethods.DumpRead:
            {
                var path = Req(svc.Dumper, "process dumper").PathFor(a.Str("name")!);
                if (!System.IO.File.Exists(path)) throw new InvalidOperationException($"Dump not found: {a.Str("name")}");
                long len = new System.IO.FileInfo(path).Length;
                // A .dmp can be many GB; base64 over the WS RPC would OOM. Cap it and tell the operator to
                // pull huge dumps off the agent directly.
                if (len > 1_500_000_000L) throw new InvalidOperationException($"Dump is {len:N0} bytes — too large to stream through the fleet. Retrieve it from the agent at {path}.");
                return new { name = System.IO.Path.GetFileName(path), sizeBytes = len, base64 = Convert.ToBase64String(System.IO.File.ReadAllBytes(path)) };
            }
            case FleetMethods.RegistryBrowse:
                if (svc.Dumper is null) throw new InvalidOperationException("Registry browsing isn't available on an RDP agent (it would read the connector's machine, not the target).");
                return Services.RegistryService.Browse(a.Str("path"));
            // ---- files + shell (native agents only: on an RDP connector they'd touch the connector, not the target) ----
            case FleetMethods.BrowseFiles:
                if (svc.Dumper is null) throw new InvalidOperationException("File browsing isn't available on an RDP agent (it would read the connector's machine, not the target).");
                return Services.FileSystemService.Browse(a.Str("path"));
            case FleetMethods.ReadFile:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would read the connector's machine).");
                return Services.FileSystemService.ReadFileBase64(a.Str("path"));
            case FleetMethods.WriteFile:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would write to the connector's machine).");
                return Services.FileSystemService.WriteFileBase64(a.Str("path"), a.Str("contentBase64"), a.Bool("overwrite"));
            case FleetMethods.DeletePath:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would delete on the connector's machine).");
                return Services.FileSystemService.Delete(a.Str("path"), a.Bool("permanent"));
            case FleetMethods.RenamePath:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.FileSystemService.Rename(a.Str("path"), a.Str("newName"));
            case FleetMethods.MovePath:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.FileSystemService.Move(a.Str("source"), a.Str("dest"), a.Bool("overwrite"));
            case FleetMethods.CopyPath:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.FileSystemService.Copy(a.Str("source"), a.Str("dest"), a.Bool("overwrite"));
            case FleetMethods.ZipPaths:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.FileSystemService.Zip(a.Obj<string[]>("sources"), a.Str("dest"), a.Bool("overwrite"));
            case FleetMethods.UnzipPath:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.FileSystemService.Unzip(a.Str("zipPath"), a.Str("dest"), a.Bool("overwrite"));
            case FleetMethods.RunCommand:
                if (svc.Dumper is null) throw new InvalidOperationException("Shell isn't available on an RDP agent (it would run on the connector, not the target).");
                return Services.ShellService.Run(a.Str("shell"), a.Str("command"), a.Str("cwd"), a.IntN("timeoutMs"));
            case FleetMethods.LaunchAs:
                if (svc.Dumper is null) throw new InvalidOperationException("Session launch isn't available on an RDP agent (it would run on the connector, not the target).");
                return Services.SessionLaunchService.Launch(a.Str("path")!, a.Str("args"), a.Str("workingDir"), a.IntN("sessionId"),
                    a.Str("desktop"), Services.SessionLaunchService.ParseAs(a.Str("as")), a.Str("user"), a.Str("domain"), a.Str("password"), a.Bool("noWindow"));
            case FleetMethods.FirewallRules:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would read the connector's firewall, not the target).");
                return Services.FirewallService.List(a.Str("direction"), a.IntN("port"), a.Bool("enabledOnly"), a.Str("contains"), a.Bool("managedOnly"), a.Int("max", 200));
            case FleetMethods.FirewallOpen:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would change the connector's firewall, not the target).");
                return Services.FirewallService.OpenPort(a.Int("port", 0), a.Str("protocol"), a.Str("direction"), a.Str("remoteAddresses"), a.Str("name"));
            case FleetMethods.FirewallClose:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would change the connector's firewall, not the target).");
                return a.Bool("all")
                    ? Services.FirewallService.CloseAllManaged()
                    : Services.FirewallService.ClosePort(a.Int("port", 0), a.Str("protocol"), a.Str("direction"));
            case FleetMethods.ClipboardGet:
                if (svc.Dumper is null) throw new InvalidOperationException("Clipboard isn't available on an RDP agent (it would read the connector's clipboard).");
                return Services.ClipboardService.GetText();
            case FleetMethods.ClipboardSet:
                if (svc.Dumper is null) throw new InvalidOperationException("Clipboard isn't available on an RDP agent (it would set the connector's clipboard).");
                return Services.ClipboardService.SetText(a.Str("text"));
            case FleetMethods.ClipboardClear:
                if (svc.Dumper is null) throw new InvalidOperationException("Clipboard isn't available on an RDP agent.");
                return Services.ClipboardService.Clear();
            case FleetMethods.WindowAction:
            {
                if (svc.Dumper is null) throw new InvalidOperationException("Window management isn't available on an RDP agent (it would act on the connector's windows).");
                long hwnd = a.Long("hwnd", 0);
                return (a.Str("action") ?? "").ToLowerInvariant() switch
                {
                    "activate" or "focus" => Services.WindowService.Activate(hwnd),
                    "minimize" => Services.WindowService.Minimize(hwnd),
                    "maximize" => Services.WindowService.Maximize(hwnd),
                    "restore" => Services.WindowService.Restore(hwnd),
                    "close" => Services.WindowService.Close(hwnd),
                    "move" => Services.WindowService.Move(hwnd, a.Int("x", 0), a.Int("y", 0)),
                    "resize" => Services.WindowService.Resize(hwnd, a.Int("width", 0), a.Int("height", 0)),
                    "bounds" or "set_bounds" => Services.WindowService.SetBounds(hwnd, a.Int("x", 0), a.Int("y", 0), a.Int("width", 0), a.Int("height", 0)),
                    _ => new Services.WindowActionResultDto(false, hwnd, a.Str("action") ?? "", Error: "Unknown action."),
                };
            }
            case FleetMethods.OcrScreen:
            {
                var cap = b.CaptureScreen(a.IntN("monitor"), ImageFormat.Png, 100);
                return Services.OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y);
            }
            case FleetMethods.OcrRegion:
            {
                var cap = b.CaptureRegion(a.Int("x", 0), a.Int("y", 0), a.Int("width", 0), a.Int("height", 0), ImageFormat.Png, 100);
                return Services.OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y);
            }
            case FleetMethods.OcrWindow:
            {
                var cap = a.Str("reference") is { } rf ? b.CaptureWindowByRef(rf, ImageFormat.Png, 100)
                        : b.CaptureWindow(a.Long("hwnd", 0), ImageFormat.Png, 100);
                return Services.OcrService.Recognize(cap.Bytes, cap.Rect.X, cap.Rect.Y);
            }
            case FleetMethods.FindImage:
            {
                byte[] needle;
                try { needle = Convert.FromBase64String(a.Str("templateBase64") ?? ""); }
                catch { throw new ArgumentException("templateBase64 is not valid base64."); }
                var cap = (a.Str("target") ?? "screen").ToLowerInvariant() switch
                {
                    "region" => b.CaptureRegion(a.Int("x", 0), a.Int("y", 0), a.Int("width", 0), a.Int("height", 0), ImageFormat.Png, 100),
                    "window" => a.Str("reference") is { } wr ? b.CaptureWindowByRef(wr, ImageFormat.Png, 100) : b.CaptureWindow(a.Long("hwnd", 0), ImageFormat.Png, 100),
                    _ => b.CaptureScreen(a.IntN("monitor"), ImageFormat.Png, 100),
                };
                return Services.TemplateMatchService.Find(cap.Bytes, needle, a.DblN("threshold") ?? 0.85, a.Int("maxResults", 10), cap.Rect.X, cap.Rect.Y);
            }
            case FleetMethods.WaitForImage:
                return Services.VisionOps.WaitForImage(b, B64(a, "templateBase64"), SpecOf(a), a.DblN("threshold") ?? 0.85, a.Int("timeoutMs", 5000), !a.Bool("absent"), a.Int("pollMs", 250));
            case FleetMethods.WaitForText:
                return Services.VisionOps.WaitForText(b, a.Str("text") ?? "", SpecOf(a), a.Int("timeoutMs", 5000), !a.Bool("absent"), a.Int("pollMs", 250));
            case FleetMethods.WaitStable:
                return Services.VisionOps.WaitStable(b, SpecOf(a), a.Int("settleMs", 700), a.Int("timeoutMs", 8000), a.Int("pollMs", 250), a.DblN("epsilon") ?? 0.01, a.Bool("waitForChange"));
            case FleetMethods.ClickImage:
                return Services.VisionOps.ClickImage(b, B64(a, "templateBase64"), SpecOf(a), a.DblN("threshold") ?? 0.85, a.Str("button") ?? "left", a.Int("count", 1), a.Int("timeoutMs", 0));
            case FleetMethods.ClickText:
                return Services.VisionOps.ClickText(b, a.Str("text") ?? "", SpecOf(a), a.Str("button") ?? "left", a.Int("count", 1), a.Int("timeoutMs", 0));
            case FleetMethods.GetPixel:
                return Services.VisionOps.GetPixel(b, a.Int("x"), a.Int("y"));
            case FleetMethods.ExploreUx:
                return Services.UxExplorer.Explore(b, a.Str("reference"), a.Bool("uia", true), a.Bool("text", true), a.Bool("includeOffscreen"), a.Int("max", 200), a.Bool("includePopups", true));
            case FleetMethods.CrawlUx:
                return Services.UxCrawler.Crawl(b, a.Str("reference"), a.Int("depth", 3), a.Int("maxNodes", 1500), a.Bool("selectTabs"), a.Bool("useCache"));
            case FleetMethods.DismissModals:
                return Services.DismissService.Dismiss(b, a.Bool("acceptOk", true), a.Bool("acceptYes"), a.Int("maxPasses", 4), a.Obj<string[]>("titleContains"), a.Bool("includePopups"));
            case FleetMethods.PressKeys:
            {
                var chords = a.Obj<string[]>("chords") ?? Array.Empty<string>();
                int between = a.Int("betweenMs", 40), rep = Math.Clamp(a.Int("repeat", 1), 1, 100);
                for (int i = 0; i < rep; i++)
                    foreach (var c in chords) { b.SendKeys(c); if (between > 0) System.Threading.Thread.Sleep(Math.Clamp(between, 0, 5000)); }
                return new { ok = true, chords = chords.Length };
            }
            case FleetMethods.SecureAttention:
                return Services.SecureInputService.SendCtrlAltDel(a.BoolN("asUser"));
            case FleetMethods.LockWorkstation:
                return Services.SecureInputService.LockWorkstation();
            case FleetMethods.Paste:
            {
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                var set = Services.ClipboardService.SetText(a.Str("text") ?? "");
                if (set.Ok) b.SendKeys("ctrl+v");
                return set;
            }
            case FleetMethods.ProcessControl:
            {
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                int pid = a.Int("pid");
                return (a.Str("action") ?? "").ToLowerInvariant() switch
                {
                    "kill" or "terminate" => Services.ProcessControlService.Kill(pid, a.Bool("tree", true), a.Bool("force")),
                    "suspend" => Services.ProcessControlService.Suspend(pid, a.Bool("force")),
                    "resume" => Services.ProcessControlService.Resume(pid),
                    "priority" => Services.ProcessControlService.SetPriority(pid, a.Str("level") ?? ""),
                    _ => new Services.ProcControlDto(false, pid, null, a.Str("action") ?? "", Error: "action must be kill|suspend|resume|priority."),
                };
            }
            case FleetMethods.ServiceControl:
            {
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                var n = a.Str("name") ?? "";
                return (a.Str("action") ?? "").ToLowerInvariant() switch
                {
                    "start" => Services.ServiceControlService.Start(n),
                    "stop" => Services.ServiceControlService.Stop(n),
                    "restart" => Services.ServiceControlService.Restart(n),
                    _ => new Services.ServiceControlDto(false, n, a.Str("action") ?? "", Error: "action must be start|stop|restart."),
                };
            }
            case FleetMethods.EnvGet:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.EnvironmentService.Get(a.Str("name") ?? "", a.Str("scope"));
            case FleetMethods.EnvSet:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.EnvironmentService.Set(a.Str("name") ?? "", a.Str("value"), a.Str("scope"));
            case FleetMethods.TaskAction:
            {
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                var tk = a.Str("task") ?? "";
                return (a.Str("action") ?? "").ToLowerInvariant() switch
                {
                    "run" => Services.ScheduledTaskService.Run(tk),
                    "end" => Services.ScheduledTaskService.End(tk),
                    "enable" => Services.ScheduledTaskService.Enable(tk),
                    "disable" => Services.ScheduledTaskService.Disable(tk),
                    _ => new Services.TaskActionDto(false, tk, a.Str("action") ?? "", -1, Error: "action must be run|end|enable|disable."),
                };
            }
            case FleetMethods.UacStatus:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.UacService.Status();
            case FleetMethods.UacConfig:
            {
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return a.BoolN("enabled") is bool en ? Services.UacService.SetEnabled(en)
                    : a.BoolN("promptOnSecureDesktop") is bool sd ? Services.UacService.SetSecureDesktop(sd)
                    : a.BoolN("autoApprove") is bool aa ? Services.UacService.SetAutoApprove(aa)
                    : a.IntN("adminBehavior") is int lvl ? Services.UacService.SetAdminBehavior(lvl)
                    : new Services.UacConfigDto(false, "none", null, false, "Provide one of: enabled, promptOnSecureDesktop, autoApprove, adminBehavior.");
            }
            case FleetMethods.UacRespond:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.UacService.Respond(a.Bool("accept", true), a.Int("timeoutMs", 5000));
            case FleetMethods.Fetch:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                return Services.FetchService.DownloadAsync(a.Str("url"), a.Str("path"), a.LongN("maxBytes")).GetAwaiter().GetResult();
            case FleetMethods.SystemInfo:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would report the connector's machine, not the target).");
                return Services.SystemInfoService.Get();
            case FleetMethods.ListApps:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would read the connector's machine).");
                return Services.StartMenuService.List();
            case FleetMethods.ListDesktops:
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent (it would read the connector's machine).");
                return Services.VirtualDesktopService.ListByWindow();
            case FleetMethods.MoveWindowToDesktop:
            {
                if (svc.Dumper is null) throw new InvalidOperationException("Not available on an RDP agent.");
                var did = a.Str("desktopId");
                bool ok = did is null
                    ? Services.VirtualDesktopService.MoveWindowToCurrent((IntPtr)a.Long("hwnd"))
                    : Services.VirtualDesktopService.MoveWindowToDesktop((IntPtr)a.Long("hwnd"), did);
                return new { ok };
            }
        }
        return cmd.Method switch
        {
            FleetMethods.MachineInfo => b.GetMachineInfo(),
            FleetMethods.DesktopState => b.GetDesktopState(),
            FleetMethods.ForegroundWindow => b.GetForegroundWindow(),
            FleetMethods.FocusedElement => b.GetFocusedElement(),
            FleetMethods.ListWindows => b.GetTopLevelWindows(),
            FleetMethods.ListProcesses => b.GetProcesses(),
            FleetMethods.Launch => b.LaunchProcess(a.Str("path")!, a.Str("args"), a.Str("workingDir"), a.Int("waitForWindowMs", 0)),
            FleetMethods.GetTree => b.GetTree(a.Str("rootRef"), a.Int("depth", 2), a.Int("maxChildren", 40)),
            FleetMethods.Find => b.Find(a.Str("rootRef"), a.Obj<FindQuery>("query")!),
            FleetMethods.WaitForElement => b.WaitForElement(a.Str("rootRef"), a.Obj<FindQuery>("query")!, a.Int("timeoutMs", 5000)),
            FleetMethods.GetElement => b.GetElement(a.Str("reference")!),
            FleetMethods.GetAllProperties => b.GetAllProperties(a.Str("reference")!),
            FleetMethods.ElementFromPoint => b.GetElementFromPoint(a.Int("x"), a.Int("y")),
            FleetMethods.Invoke => Void(() => b.Invoke(a.Str("reference")!)),
            FleetMethods.SetValue => Void(() => b.SetValue(a.Str("reference")!, a.Str("text") ?? "")),
            FleetMethods.Toggle => Void(() => b.Toggle(a.Str("reference")!)),
            FleetMethods.ExpandCollapse => Void(() => b.ExpandCollapse(a.Str("reference")!, a.Bool("expand"))),
            FleetMethods.Select => Void(() => b.Select(a.Str("reference")!)),
            FleetMethods.SetFocus => Void(() => b.SetFocus(a.Str("reference")!)),
            FleetMethods.CaptureScreen => b.CaptureScreen(a.IntN("monitor"), Fmt(a), a.Int("quality", 80)),
            FleetMethods.CaptureRegion => b.CaptureRegion(a.Int("x"), a.Int("y"), a.Int("width"), a.Int("height"), Fmt(a), a.Int("quality", 80)),
            FleetMethods.CaptureWindow => b.CaptureWindow(a.Long("hwnd"), Fmt(a), a.Int("quality", 80)),
            FleetMethods.CaptureWindowByRef => b.CaptureWindowByRef(a.Str("reference")!, Fmt(a), a.Int("quality", 80)),
            FleetMethods.CaptureElement => b.CaptureElement(a.Str("reference")!, Fmt(a), a.Int("quality", 80)),
            FleetMethods.CaptureInputDesktop => b.CaptureInputDesktop(Fmt(a), a.Int("quality", 80)),
            FleetMethods.Drag => Void(() => b.Drag(a.Int("fromX"), a.Int("fromY"), a.Int("toX"), a.Int("toY"), a.Str("button") ?? "left", a.Int("steps", 20), a.Int("holdMs", 60))),
            FleetMethods.MouseMove => Void(() => b.MouseMove(a.Int("x"), a.Int("y"))),
            FleetMethods.MouseClick => Void(() => b.MouseClick(a.Str("button") ?? "left", a.IntN("x"), a.IntN("y"), a.Int("count", 1))),
            FleetMethods.MouseDown => Void(() => b.MouseDown(a.Str("button") ?? "left", a.IntN("x"), a.IntN("y"))),
            FleetMethods.MouseUp => Void(() => b.MouseUp(a.Str("button") ?? "left", a.IntN("x"), a.IntN("y"))),
            FleetMethods.MouseScroll => Void(() => b.MouseScroll(a.Int("dx"), a.Int("dy"))),
            FleetMethods.TypeText => Void(() => b.TypeText(a.Str("text") ?? "")),
            FleetMethods.SendKeys => Void(() => b.SendKeys(a.Str("chord") ?? "")),
            _ => throw new ArgumentException($"Unknown fleet method '{cmd.Method}'."),
        };
    }

    private static T Req<T>(T? svc, string name) where T : class =>
        svc ?? throw new InvalidOperationException($"This agent has no {name} service.");

    private static object Void(Action act) { act(); return new { ok = true }; }

    private static byte[] B64(JsonElement a, string name)
    {
        try { return Convert.FromBase64String(a.Str(name) ?? ""); }
        catch { throw new ArgumentException($"{name} is not valid base64."); }
    }

    private static Services.CaptureSpec SpecOf(JsonElement a) =>
        new(a.Str("target"), a.IntN("monitor"), a.IntN("x"), a.IntN("y"), a.IntN("width"), a.IntN("height"), a.Long("hwnd", 0), a.Str("reference"));

    private static ImageFormat Fmt(JsonElement a) =>
        a.Str("format")?.ToLowerInvariant() is "jpeg" or "jpg" ? ImageFormat.Jpeg : ImageFormat.Png;
}

internal static class JsonArgs
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null ? v.GetString() : null;

    public static int Int(this JsonElement e, string name, int def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    public static int? IntN(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    public static long Long(this JsonElement e, string name, long def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : def;

    public static bool Bool(this JsonElement e, string name, bool def = false) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    public static double? DblN(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static long? LongN(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    public static bool? BoolN(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static T? Obj<T>(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? FleetJson.Deserialize<T>(v.GetRawText()) : default;
}
