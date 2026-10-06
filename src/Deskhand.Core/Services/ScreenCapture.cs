using System.Drawing;
using System.Drawing.Imaging;
using Deskhand.Core.Interop;
using DImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Deskhand.Core.Services;

/// <summary>
/// GDI-based screen capture. Chosen for Phase 1 because it is dependency-free, works
/// per-region and per-window, and is the same code path the SYSTEM Secure Helper will
/// reuse for the secure desktop (where WGC/DXGI are restricted). Windows.Graphics.Capture
/// is the planned upgrade for GPU/occluded content on the Default desktop.
/// </summary>
public static class ScreenCapture
{
    private static byte[] Encode(Bitmap bmp, ImageFormat format, int jpegQuality)
    {
        using var ms = new MemoryStream();
        if (format == ImageFormat.Jpeg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == DImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
            bmp.Save(ms, codec, ep);
        }
        else
        {
            bmp.Save(ms, DImageFormat.Png);
        }
        return ms.ToArray();
    }

    private static byte[] CaptureRectBytes(Rectangle rect, ImageFormat format, int jpegQuality)
    {
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        return Encode(bmp, format, jpegQuality);
    }

    // GDI StretchBlt capture that downscales DURING the blit, so the JPEG encoder works on the final
    // (smaller) pixels instead of encoding a full-res frame and then resizing — much cheaper per frame,
    // which is what the live stream needs. Returns JPEG bytes plus the output dimensions.
    public static byte[] CaptureScaledJpeg(Rectangle src, int maxWidth, int quality, out int outW, out int outH)
    {
        double scale = maxWidth > 0 && src.Width > maxWidth ? (double)maxWidth / src.Width : 1.0;
        outW = Math.Max(1, (int)Math.Round(src.Width * scale));
        outH = Math.Max(1, (int)Math.Round(src.Height * scale));

        IntPtr screenDC = Gdi.GetDC(IntPtr.Zero);
        IntPtr memDC = Gdi.CreateCompatibleDC(screenDC);
        IntPtr hbmp = Gdi.CreateCompatibleBitmap(screenDC, outW, outH);
        IntPtr old = Gdi.SelectObject(memDC, hbmp);
        try
        {
            Gdi.SetStretchBltMode(memDC, Gdi.HALFTONE);
            Gdi.StretchBlt(memDC, 0, 0, outW, outH, screenDC, src.X, src.Y, src.Width, src.Height, Gdi.SRCCOPY);
            using var bmp = Image.FromHbitmap(hbmp);
            return Encode(bmp, ImageFormat.Jpeg, quality);
        }
        finally
        {
            Gdi.SelectObject(memDC, old);
            Gdi.DeleteObject(hbmp);
            Gdi.DeleteDC(memDC);
            Gdi.ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    private static class Gdi
    {
        public const int HALFTONE = 4;
        public const int SRCCOPY = 0x00CC0020;
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd, IntPtr src, int xs, int ys, int ws, int hs, int rop);
    }

    public static CaptureResultDto CaptureRegion(int x, int y, int w, int h, ImageFormat format, int jpegQuality)
    {
        if (w <= 0 || h <= 0) throw new ArgumentException("Region width and height must be positive.");
        var rect = new Rectangle(x, y, w, h);
        var bytes = CaptureRectBytes(rect, format, jpegQuality);
        return Result(rect, MonitorIndexFor(rect), format, bytes);
    }

    public static CaptureResultDto CaptureScreen(int? monitor, ImageFormat format, int jpegQuality)
    {
        var monitors = DesktopInfo.Monitors();
        Rectangle rect;
        int idx;
        if (monitor is null)
        {
            var v = DesktopInfo.VirtualScreen();
            rect = new Rectangle(v.X, v.Y, v.Width, v.Height);
            idx = -1; // whole virtual desktop
        }
        else
        {
            var m = monitors.FirstOrDefault(mm => mm.Index == monitor.Value)
                    ?? throw new ArgumentException($"No monitor with index {monitor}.");
            rect = new Rectangle(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height);
            idx = m.Index;
        }
        var bytes = CaptureRectBytes(rect, format, jpegQuality);
        return Result(rect, idx, format, bytes);
    }

    public static CaptureResultDto CaptureWindow(IntPtr hwnd, ImageFormat format, int jpegQuality)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var wr))
            throw new ArgumentException("Invalid window handle.");
        var rect = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("Window has no visible area.");

        // Preferred: Windows.Graphics.Capture — faithfully captures GPU/occluded/unfocused windows
        // without raising them. Falls through to PrintWindow when unavailable.
        var wgc = WgcCapture.TryCaptureWindow(hwnd, format, jpegQuality);
        if (wgc is not null)
        {
            var (wgcBytes, w, h) = wgc.Value;
            return Result(new Rectangle(rect.X, rect.Y, w, h), MonitorIndexFor(rect), format, wgcBytes);
        }

        byte[]? bytes = null;
        try
        {
            using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT))
                    {
                        g.ReleaseHdc(hdc);
                        bytes = Encode(bmp, format, jpegQuality);
                    }
                    else
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
                catch { g.ReleaseHdc(hdc); }
            }
        }
        catch { /* fall through to screen copy */ }

        // Fallback: copy the window's on-screen rectangle (misses occluded pixels but always works).
        bytes ??= CaptureRectBytes(rect, format, jpegQuality);
        return Result(rect, MonitorIndexFor(rect), format, bytes);
    }

    public static CaptureResultDto CaptureBounds(Rectangle rect, ImageFormat format, int jpegQuality)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("Element has no on-screen area to capture.");
        var bytes = CaptureRectBytes(rect, format, jpegQuality);
        return Result(rect, MonitorIndexFor(rect), format, bytes);
    }

    private static int MonitorIndexFor(Rectangle rect)
    {
        var center = new System.Drawing.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        foreach (var m in DesktopInfo.Monitors())
        {
            var b = new Rectangle(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height);
            if (b.Contains(center)) return m.Index;
        }
        return -1;
    }

    private static CaptureResultDto Result(Rectangle rect, int monitor, ImageFormat format, byte[] bytes)
    {
        double scale = 1.0;
        var m = DesktopInfo.Monitors().FirstOrDefault(mm => mm.Index == monitor);
        if (m is not null) scale = m.DpiScale;
        return new CaptureResultDto(
            Desktop: DesktopInfo.GetDesktopState().Desktop,
            Rect: new RectDto(rect.X, rect.Y, rect.Width, rect.Height),
            Monitor: monitor,
            DpiScale: scale,
            Format: format == ImageFormat.Jpeg ? "jpeg" : "png",
            Bytes: bytes);
    }
}
