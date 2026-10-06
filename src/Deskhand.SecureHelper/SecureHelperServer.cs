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
            case "move": pump.Run(() => InputInjector.MouseMove(Int(r, "x", 0), Int(r, "y", 0))); return Ok();
            case "down": pump.Run(() => InputInjector.MouseDown(Str(r, "button", "left"), NInt(r, "x"), NInt(r, "y"))); return Ok();
            case "up":   pump.Run(() => InputInjector.MouseUp(Str(r, "button", "left"), NInt(r, "x"), NInt(r, "y"))); return Ok();
            case "click": pump.Run(() => InputInjector.MouseClick(Str(r, "button", "left"), NInt(r, "x"), NInt(r, "y"), Int(r, "count", 1))); return Ok();
            case "scroll": pump.Run(() => InputInjector.MouseScroll(Int(r, "dx", 0), Int(r, "dy", 0))); return Ok();
            case "drag": pump.Run(() => InputInjector.Drag(Int(r, "fromX", 0), Int(r, "fromY", 0), Int(r, "toX", 0), Int(r, "toY", 0),
                Str(r, "button", "left"), Int(r, "steps", 20), Int(r, "holdMs", 60))); return Ok();
            case "type": { string t = Str(r, "text"); pump.Run(() => InputInjector.TypeText(t)); return Ok(); }
            case "keys": { string c = Str(r, "chord"); pump.Run(() => InputInjector.SendKeys(c)); return Ok(); }
            default: return (Err($"unknown op '{op}'"), null);
        }
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
