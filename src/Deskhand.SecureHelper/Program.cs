using System.Security.Principal;
using Deskhand.Core;
using Deskhand.Core.Services;

// Deskhand Secure Helper
// Captures whichever desktop currently owns input, by attaching to it. Run this as SYSTEM
// inside the console session to capture the SECURE desktop (UAC / lock / logon); run as a
// normal user and it captures Winsta0\Default (which proves the mechanism).

DpiHelper.EnablePerMonitorV2();

string whoami;
try
{
    using var id = WindowsIdentity.GetCurrent();
    whoami = $"{id.Name} (system={id.IsSystem})";
}
catch { whoami = Environment.UserName; }

if (args.Length >= 1 && args[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
{
    string outPath = args.Length >= 2 && !args[1].StartsWith("--")
        ? args[1]
        : Path.Combine(Environment.CurrentDirectory, "input-desktop.png");
    bool jpeg = args.Contains("--jpeg", StringComparer.OrdinalIgnoreCase);

    var res = SecureCapture.CaptureInputDesktop(jpeg ? ImageFormat.Jpeg : ImageFormat.Png, 90);

    Console.WriteLine($"running as : {whoami}");
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
Console.WriteLine($"  running as: {whoami}");
Console.WriteLine();
Console.WriteLine("Usage:");
Console.WriteLine("  deskhand-secure capture <out.png> [--jpeg]");
Console.WriteLine();
Console.WriteLine("To capture the SECURE desktop, run as SYSTEM in the console session, e.g. with PsExec:");
Console.WriteLine("  psexec -s -i <consoleSessionId> deskhand-secure.exe capture C:\\temp\\secure.png");
Console.WriteLine("(find the console session id with: query session)");
return 2;
