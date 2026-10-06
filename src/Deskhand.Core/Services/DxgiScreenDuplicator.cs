using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DImageFormat = System.Drawing.Imaging.ImageFormat;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Deskhand.Core.Services;

/// <summary>
/// GPU screen capture via DXGI Desktop Duplication. Far faster than the GDI grab (the desktop image is
/// read from the compositor on the GPU), and it reports whether anything actually changed each frame —
/// so the live stream can skip encoding/sending entirely while the screen is static and spend its whole
/// budget on motion. One output (monitor) per instance. Not available everywhere (some headless/VM
/// adapters, and never the secure desktop); callers fall back to <see cref="ScreenCapture"/> then.
/// </summary>
public sealed class DxgiScreenDuplicator : IDisposable
{
    public sealed class UnavailableException(string msg) : Exception(msg);
    /// <summary>Thrown when the duplication is lost (desktop switch, resolution/mode change). Recreate.</summary>
    public sealed class AccessLostException(string msg) : Exception(msg);

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutputDuplication _dup = null!;
    private readonly Rectangle _bounds;
    private bool _holdingFrame;

    public Rectangle Bounds => _bounds;

    /// <summary>Set up duplication for the output covering <paramref name="monitorBounds"/>, or the primary
    /// output when null. Throws <see cref="UnavailableException"/> if DXGI duplication can't be used here.</summary>
    public DxgiScreenDuplicator(Rectangle? monitorBounds)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Success; ai++)
            {
                using (adapter)
                {
                    for (uint oi = 0; adapter.EnumOutputs(oi, out IDXGIOutput output).Success; oi++)
                    {
                        using (output)
                        {
                            var od = output.Description;
                            var r = od.DesktopCoordinates;
                            var rect = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                            bool match = monitorBounds is { } mb ? rect == mb : (rect.X == 0 && rect.Y == 0);
                            if (!match) continue;

                            var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
                            var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                                out ID3D11Device? dev, out _, out ID3D11DeviceContext? ctx);
                            if (hr.Failure || dev is null || ctx is null) throw new UnavailableException($"D3D11CreateDevice failed ({hr}).");

                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            IDXGIOutputDuplication dup;
                            try { dup = output1.DuplicateOutput(dev); }
                            catch (Exception ex) { dev.Dispose(); ctx.Dispose(); throw new UnavailableException($"DuplicateOutput failed: {ex.Message}"); }

                            _device = dev; _context = ctx; _dup = dup; _bounds = rect;
                            return;
                        }
                    }
                }
            }
            throw new UnavailableException(monitorBounds is null ? "No primary DXGI output found." : "No DXGI output matches the requested monitor.");
        }
        catch (UnavailableException) { throw; }
        catch (Exception ex) { throw new UnavailableException("DXGI init failed: " + ex.Message); }
    }

    /// <summary>Acquire the next frame. Returns a scaled JPEG when the desktop changed, or null when nothing
    /// changed within <paramref name="timeoutMs"/> (the client keeps the last frame). Throws
    /// <see cref="AccessLostException"/> if the duplication must be recreated.</summary>
    public byte[]? TryNextJpeg(int timeoutMs, int maxWidth, int quality)
    {
        ReleaseHeldFrame();
        var hr = _dup.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out IDXGIResource? resource);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return null;             // nothing changed
        if (hr == Vortice.DXGI.ResultCode.AccessLost) throw new AccessLostException("DXGI access lost.");
        if (hr.Failure || resource is null) throw new AccessLostException($"AcquireNextFrame failed ({hr}).");

        _holdingFrame = true;
        try
        {
            // LastPresentTime == 0 means only the mouse moved — no desktop pixels changed, so skip it.
            if (info.LastPresentTime == 0) return null;

            using var tex = resource.QueryInterface<ID3D11Texture2D>();
            var desc = tex.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;
            using var staging = _device.CreateTexture2D(desc);
            _context.CopyResource(staging, tex);

            int w = (int)desc.Width, h = (int)desc.Height;
            var mapped = _context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try { return EncodeScaledJpeg(mapped.DataPointer, (int)mapped.RowPitch, w, h, maxWidth, quality); }
            finally { _context.Unmap(staging, 0); }
        }
        finally { resource.Dispose(); ReleaseHeldFrame(); }
    }

    private void ReleaseHeldFrame()
    {
        if (!_holdingFrame) return;
        _holdingFrame = false;
        try { _dup.ReleaseFrame(); } catch { }
    }

    private static unsafe byte[] EncodeScaledJpeg(IntPtr data, int rowPitch, int w, int h, int maxWidth, int quality)
    {
        using var full = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = full.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* src = (byte*)data; byte* dst = (byte*)bd.Scan0; int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + (long)y * rowPitch, dst + (long)y * bd.Stride, rowBytes, rowBytes);
        }
        finally { full.UnlockBits(bd); }

        Bitmap toEncode = full;
        Bitmap? scaled = null;
        if (maxWidth > 0 && w > maxWidth)
        {
            int dw = maxWidth, dh = Math.Max(1, (int)Math.Round(h * (double)maxWidth / w));
            scaled = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(full, 0, 0, dw, dh);
            }
            toEncode = scaled;
        }
        try
        {
            using var ms = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == DImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            toEncode.Save(ms, codec, ep);
            return ms.ToArray();
        }
        finally { scaled?.Dispose(); }
    }

    public void Dispose()
    {
        ReleaseHeldFrame();
        try { _dup?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
