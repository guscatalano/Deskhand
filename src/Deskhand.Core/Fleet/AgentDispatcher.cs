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

    public static T? Obj<T>(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? FleetJson.Deserialize<T>(v.GetRawText()) : default;
}
