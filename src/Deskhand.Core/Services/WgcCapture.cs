using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using DImageFormat = System.Drawing.Imaging.ImageFormat;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Deskhand.Core.Services;

/// <summary>
/// Windows.Graphics.Capture window capture. Unlike PrintWindow, WGC captures a window's real
/// composited pixels — including GPU/DWM-accelerated content (Chrome, Firefox, Electron) and
/// while the window is unfocused or occluded — <b>without bringing it to the foreground</b>.
/// Requires Windows 10 1903+. Falls back to PrintWindow (in ScreenCapture) when unavailable.
/// </summary>
public static class WgcCapture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    /// <summary>Capture a window by HWND. Returns encoded image bytes and pixel size, or null on failure.</summary>
    public static (byte[] bytes, int width, int height)? TryCaptureWindow(IntPtr hwnd, ImageFormat format, int jpegQuality)
    {
        if (hwnd == IntPtr.Zero || !IsSupported) return null;
        try { return Capture(hwnd, format, jpegQuality); }
        catch { return null; }
    }

    private static (byte[], int, int)? Capture(IntPtr hwnd, ImageFormat format, int jpegQuality)
    {
        // 1. D3D11 device
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, null, out ID3D11Device? d3dDevice).CheckError();
        if (d3dDevice is null) return null;
        using var device = d3dDevice;
        using var context = device.ImmediateContext;

        // 2. WinRT IDirect3DDevice from the DXGI device
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
        var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);

        // 3. Capture item from the HWND
        var item = CreateItemForWindow(hwnd);
        if (item is null) return null;
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0) return null;

        // 4. Frame pool + session (free-threaded so we can pull a frame synchronously)
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = framePool.CreateCaptureSession(item);
        session.StartCapture();

        // 5. Pull one frame (retry briefly; the first frame can lag a beat)
        Direct3D11CaptureFrame? frame = null;
        for (int i = 0; i < 60 && frame is null; i++)
        {
            frame = framePool.TryGetNextFrame();
            if (frame is null) Thread.Sleep(16);
        }
        if (frame is null) return null;

        try
        {
            using var surfaceTex = GetTexture(frame.Surface);

            // 6. Copy to a CPU-readable staging texture
            var desc = surfaceTex.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(desc);
            context.CopyResource(staging, surfaceTex);

            int w = (int)desc.Width, h = (int)desc.Height;
            var mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var bytes = Encode(mapped.DataPointer, (int)mapped.RowPitch, w, h, format, jpegQuality);
                return (bytes, w, h);
            }
            finally { context.Unmap(staging, 0); }
        }
        finally { frame.Dispose(); }
    }

    private static unsafe byte[] Encode(IntPtr data, int rowPitch, int w, int h, ImageFormat format, int jpegQuality)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* src = (byte*)data;
            byte* dst = (byte*)bd.Scan0;
            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * rowPitch, dst + (long)y * bd.Stride, rowBytes, rowBytes);
        }
        finally { bmp.UnlockBits(bd); }

        using var ms = new MemoryStream();
        if (format == ImageFormat.Jpeg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == DImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
            bmp.Save(ms, codec, ep);
        }
        else bmp.Save(ms, DImageFormat.Png);
        return ms.ToArray();
    }

    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        IntPtr ptr = access.GetInterface(ref iid);
        return new ID3D11Texture2D(ptr);
    }

    private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        IntPtr itemPtr = factory.CreateForWindow(hwnd, ref iid);
        if (itemPtr == IntPtr.Zero) return null;
        var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        Marshal.Release(itemPtr);
        return item;
    }
}
