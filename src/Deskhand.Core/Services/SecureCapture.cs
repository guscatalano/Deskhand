using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Deskhand.Core.Interop;
using DImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Deskhand.Core.Services;

/// <summary>
/// Phase-2 primitive: capture whichever desktop currently owns input by attaching a
/// throwaway thread to it (<c>OpenInputDesktop</c> + <c>SetThreadDesktop</c>) and reading its
/// device context with GDI. As a normal user this captures <c>Winsta0\Default</c>. Run inside
/// the console session as <b>SYSTEM</b> (the Secure Helper), it also captures the secure
/// desktop <c>Winsta0\Winlogon</c> — UAC, the lock screen, and the logon UI — which WGC/DXGI
/// cannot. A dedicated thread is used so the process's UIA/STA thread is never re-desktop'd.
/// </summary>
public static class SecureCapture
{
    public sealed record InputDesktopResult(
        bool Success,
        string DesktopName,   // "Default", "Winlogon", "" if inaccessible
        string Kind,          // "default" | "secure" | "screensaver" | "unknown"
        CaptureResultDto? Capture,
        string Note);

    public static InputDesktopResult CaptureInputDesktop(ImageFormat format, int jpegQuality)
    {
        InputDesktopResult result = new(false, "", "unknown", null, "not run");

        // MTA: an STA thread creates a hidden OLE window, which makes SetThreadDesktop fail
        // with ERROR_BUSY. A clean MTA thread with no windows can switch desktops freely.
        var thread = new Thread(() => result = Run(format, jpegQuality))
        {
            IsBackground = true,
            Name = "Deskhand-DesktopAttach",
        };
        thread.Start();
        thread.Join();
        return result;
    }

    private static InputDesktopResult Run(ImageFormat format, int jpegQuality)
    {
        IntPtr hInput = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_ATTACH_ACCESS);
        if (hInput == IntPtr.Zero)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return new InputDesktopResult(false, "", "secure", null,
                $"OpenInputDesktop failed (Win32 {err}). The secure desktop is active and this process " +
                "is not privileged enough to attach. Run the Secure Helper as SYSTEM in the console session.");
        }

        IntPtr original = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        try
        {
            if (!NativeMethods.SetThreadDesktop(hInput))
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return new InputDesktopResult(false, "", "unknown", null,
                    $"SetThreadDesktop failed (Win32 {err}). Another thread in this process likely has windows " +
                    "on a different desktop; the Secure Helper runs this in a clean process.");
            }

            string name = DesktopName(hInput);
            string kind = name switch
            {
                "Default" => "default",
                "Winlogon" => "secure",
                "Screen-saver" => "screensaver",
                _ => "unknown",
            };

            var v = DesktopInfo.VirtualScreen();
            var rect = new Rectangle(v.X, v.Y, v.Width, v.Height);
            byte[] bytes;
            using (var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
                using var ms = new MemoryStream();
                if (format == ImageFormat.Jpeg)
                {
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == DImageFormat.Jpeg.Guid);
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
                    bmp.Save(ms, codec, ep);
                }
                else bmp.Save(ms, DImageFormat.Png);
                bytes = ms.ToArray();
            }

            var cap = new CaptureResultDto(kind, new RectDto(rect.X, rect.Y, rect.Width, rect.Height),
                -1, 1.0, format == ImageFormat.Jpeg ? "jpeg" : "png", bytes);
            return new InputDesktopResult(true, name, kind, cap,
                kind == "secure" ? "Captured the secure desktop." : $"Captured input desktop '{name}'.");
        }
        finally
        {
            if (original != IntPtr.Zero) NativeMethods.SetThreadDesktop(original);
            NativeMethods.CloseDesktop(hInput);
        }
    }

    private static string DesktopName(IntPtr hDesktop)
    {
        NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, null, 0, out uint needed);
        if (needed == 0) return "";
        var buffer = new byte[needed];
        if (!NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, buffer, needed, out _))
            return "";
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }
}
