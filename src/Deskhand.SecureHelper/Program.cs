using System.Security.Principal;
using Deskhand.Core;
using Deskhand.Core.Services;
using Deskhand.SecureHelper;

// Deskhand Secure Helper
// Captures and drives whichever desktop currently owns input, by attaching to it. Run this as SYSTEM
// inside the console session (via Deskhand.Broker) to reach the SECURE desktop (UAC / lock / logon);
// run as a normal user and it works on Winsta0\Default (which proves the mechanism).
//
//   deskhand-secure capture <out.png> [--jpeg]      one-shot capture of the input desktop
//   deskhand-secure serve [--pipe <name>]           persistent IPC server (capture + input) for the main server

DpiHelper.EnablePerMonitorV2();

static string WhoAmI()
{
    try { using var id = WindowsIdentity.GetCurrent(); return $"{id.Name} (system={id.IsSystem})"; }
    catch { return Environment.UserName; }
}

if (args.Length >= 1 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
{
    int pi = Array.IndexOf(args, "--pipe");
    string pipe = pi >= 0 && pi + 1 < args.Length ? args[pi + 1] : SecureHelperProtocol.DefaultPipeName;
    return SecureHelperServer.Run(pipe, WhoAmI());
}

if (args.Length >= 1 && args[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
{
    string outPath = args.Length >= 2 && !args[1].StartsWith("--")
        ? args[1]
        : Path.Combine(Environment.CurrentDirectory, "input-desktop.png");
    bool jpeg = args.Contains("--jpeg", StringComparer.OrdinalIgnoreCase);

    var res = SecureCapture.CaptureInputDesktop(jpeg ? ImageFormat.Jpeg : ImageFormat.Png, 90);
    Console.WriteLine($"running as : {WhoAmI()}");
    Console.WriteLine($"desktop    : {(string.IsNullOrEmpty(res.DesktopName) ? "<inaccessible>" : res.DesktopName)} ({res.Kind})");
    Console.WriteLine($"note       : {res.Note}");
    if (res.Success && res.Capture is not null)
    {
        File.WriteAllBytes(outPath, res.Capture.Bytes);
        Console.WriteLine($"saved      : {outPath} ({res.Capture.Bytes.Length:N0} bytes, {res.Capture.Rect.Width}x{res.Capture.Rect.Height})");
        return 0;
    }
    Console.Error.WriteLine("capture failed");
    return 1;
}

Console.WriteLine("Deskhand Secure Helper");
Console.WriteLine($"  running as: {WhoAmI()}");
Console.WriteLine();
Console.WriteLine("Usage:");
Console.WriteLine("  deskhand-secure capture <out.png> [--jpeg]");
Console.WriteLine("  deskhand-secure serve [--pipe <name>]");
Console.WriteLine();
Console.WriteLine("To reach the SECURE desktop, run as SYSTEM in the console session via Deskhand.Broker:");
Console.WriteLine("  deskhand-broker deskhand-secure.exe serve");
return 2;
