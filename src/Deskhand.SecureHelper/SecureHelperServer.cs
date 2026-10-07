using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Deskhand.Core;
using Deskhand.Core.Services;

namespace Deskhand.SecureHelper;

/// <summary>
/// Persistent named-pipe server exposing secure-desktop capture + input to the main Deskhand server.
/// Capture uses <see cref="SecureCapture"/>; input runs on an <see cref="InputDesktopPump"/> so every
/// action attaches to whichever desktop currently owns input (the secure desktop when this runs as
/// SYSTEM). The pipe grants Administrators access so the elevated main server can connect.
/// </summary>
public static class SecureHelperServer
{
    // Exit this (SYSTEM) process after a stretch with no client, so a lingering helper can't hold a lock on
    // C:\Deskhand and block an in-place update. The main server simply relaunches it (via the broker) the
    // next time it needs the secure desktop. Overridable with DESKHAND_SECURE_IDLE_SEC (0 = never exit).
    public static int Run(string pipeName, string whoAmI)
    {
        Console.WriteLine($"Deskhand secure helper serving on \\\\.\\pipe\\{pipeName} as {whoAmI}");
        Log($"serve start: as {whoAmI}, process desktop = {CurrentDesktopName()}, uiAccess set: {TrySetUiAccess()}");
        var pump = new InputDesktopPump();
        int idleSec = int.TryParse(Environment.GetEnvironmentVariable("DESKHAND_SECURE_IDLE_SEC"), out var s) ? s : 90;

        while (true)
        {
            NamedPipeServerStream server;
            try { server = CreatePipe(pipeName); }
            catch (Exception ex) { Console.Error.WriteLine($"pipe create failed: {ex.Message}"); return 1; }

            try
            {
                if (idleSec > 0)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(idleSec));
                    try { server.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"no client for {idleSec}s — exiting so updates aren't blocked.");
                        server.Dispose(); pump.Dispose();
                        return 0;
                    }
                }
                else server.WaitForConnection();

                while (server.IsConnected)
                {
                    byte[] reqBytes;
                    try { reqBytes = SecureHelperProtocol.ReadMessage(server).json; }
                    catch { break; } // client disconnected

                    byte[] respJson; byte[]? blob = null;
                    try { (respJson, blob) = Handle(reqBytes, whoAmI, pump); }
                    catch (Exception ex) { respJson = Err(ex.Message); }

                    try { SecureHelperProtocol.WriteMessage(server, respJson, blob); }
                    catch { break; }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"session error: {ex.Message}"); }
            finally { try { server.Dispose(); } catch { } }
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var sec = new PipeSecurity();
        // SYSTEM (us) + Administrators (the elevated main server) get full control; nobody else.
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
    }

    private static (byte[] json, byte[]? blob) Handle(byte[] reqBytes, string whoAmI, InputDesktopPump pump)
    {
        var r = JsonDocument.Parse(reqBytes).RootElement;
        string op = Str(r, "op");

        switch (op)
        {
            case "ping":
            {
                var ds = DesktopInfo.GetDesktopState();
                return (Json(new { ok = true, runningAs = whoAmI, desktop = ds.Desktop, rawDesktopName = ds.RawDesktopName }), null);
            }
            case "desktop":
            {
                var ds = DesktopInfo.GetDesktopState();
                return (Json(new { ok = true, desktop = ds.Desktop, rawDesktopName = ds.RawDesktopName, inputAvailable = ds.InputAvailable }), null);
            }
            case "capture":
            {
                bool jpeg = !Str(r, "format").Equals("png", StringComparison.OrdinalIgnoreCase);
                int q = Int(r, "quality", 60);
                var res = SecureCapture.CaptureInputDesktop(jpeg ? ImageFormat.Jpeg : ImageFormat.Png, q);
                if (!res.Success || res.Capture is null) return (Err(res.Note), null);
                return (Json(new { ok = true, desktopName = res.DesktopName, kind = res.Kind,
                    format = res.Capture.Format, width = res.Capture.Rect.Width, height = res.Capture.Rect.Height,
                    bytes = res.Capture.Bytes.Length }), res.Capture.Bytes);
            }
            case "move": return DoInput(pump, "move", () => InputInjector.MouseMove(Int(r, "x", 0), Int(r, "y", 0)));
            case "down": return DoInput(pump, "down", () => InputInjector.MouseDown(Str(r, "button", "left"), NInt(r, "x"), NInt(r, "y")));
            case "up":   return DoInput(pump, "up", () => InputInjector.MouseUp(Str(r, "button", "left"), NInt(r, "x"), NInt(r, "y")));
            case "click": return DoInput(pump, "click", () => InputInjector.MouseClick(Str(r, "button", "left"), NInt(r, "x"), NInt(r, "y"), Int(r, "count", 1)));
            case "scroll": return DoInput(pump, "scroll", () => InputInjector.MouseScroll(Int(r, "dx", 0), Int(r, "dy", 0)));
            case "drag": return DoInput(pump, "drag", () => InputInjector.Drag(Int(r, "fromX", 0), Int(r, "fromY", 0), Int(r, "toX", 0), Int(r, "toY", 0),
                Str(r, "button", "left"), Int(r, "steps", 20), Int(r, "holdMs", 60)));
            case "type": { string t = Str(r, "text"); return DoInput(pump, "type", () => InputInjector.TypeText(t)); }
            case "keys": { string c = Str(r, "chord"); return DoInput(pump, "keys", () => InputInjector.SendKeys(c)); }
            default: return (Err($"unknown op '{op}'"), null);
        }
    }

    // Runs an input action on the pump (which attaches to the current input desktop) and logs exactly what
    // happened — input desktop, the thread's actually-attached desktop, and the SendInput result — so we can
    // tell a real Windows block apart from an attach problem on our side.
    private static (byte[], byte[]?) DoInput(InputDesktopPump pump, string op, Action act)
    {
        string inputDesk; try { inputDesk = DesktopInfo.GetDesktopState().RawDesktopName; } catch { inputDesk = "?"; }
        try
        {
            pump.Run(act);
            Log($"{op}: inputDesktop={inputDesk} threadDesktop={pump.LastAttached} -> OK");
            return (Json(new { ok = true, inputDesktop = inputDesk, threadDesktop = pump.LastAttached }), null);
        }
        catch (Exception ex)
        {
            Log($"{op}: inputDesktop={inputDesk} threadDesktop={pump.LastAttached} -> FAIL {ex.Message}");
            return (Json(new { ok = false, error = ex.Message, inputDesktop = inputDesk, threadDesktop = pump.LastAttached }), null);
        }
    }

    // Make this SYSTEM process a uiAccess process so its SendInput is accepted on the secure / UAC-consent
    // UI (which rejects injected input from non-uiAccess processes). Setting TokenUIAccess needs SeTcbPrivilege,
    // which SYSTEM holds — so no code-signing/uiAccess-manifest path is required. The process is already
    // launched on the Winlogon desktop, so it can both attach there and (now) inject there.
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr tok);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? host, string name, out long luid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr tok, int cls, ref int info, uint len);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }
    private static string TrySetUiAccess()
    {
        try
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x0020 | 0x0008 | 0x0080 /*ADJUST_PRIVILEGES|QUERY|ADJUST_DEFAULT*/, out IntPtr tok)) return "OpenProcessToken failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (LookupPrivilegeValue(null, "SeTcbPrivilege", out long luid))
            {
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 0x00000002 /*ENABLED*/ };
                AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            int one = 1;
            bool ok = SetTokenInformation(tok, 26 /*TokenUIAccess*/, ref one, 4);
            return ok ? "OK" : "SetTokenInformation failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
        catch (Exception ex) { return "exc " + ex.Message; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int index, byte[]? info, uint len, out uint needed);
    private static string CurrentDesktopName()
    {
        try
        {
            var h = GetThreadDesktop(GetCurrentThreadId());
            GetUserObjectInformation(h, 2 /*UOI_NAME*/, null, 0, out uint need);
            if (need == 0) return "?";
            var buf = new byte[need];
            return GetUserObjectInformation(h, 2, buf, need, out _) ? System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0') : "?";
        }
        catch { return "?"; }
    }

    private static readonly object _logGate = new();
    private static void Log(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Deskhand");
            Directory.CreateDirectory(dir);
            lock (_logGate) File.AppendAllText(Path.Combine(dir, "secure-helper.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }

    private static (byte[], byte[]?) Ok() => (Json(new { ok = true }), null);
    private static byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);
    private static byte[] Err(string msg) => JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = msg });

    private static string Str(JsonElement e, string n, string dflt = "") =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? dflt) : dflt;
    private static int Int(JsonElement e, string n, int dflt) =>
        e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : dflt;
    private static int? NInt(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
