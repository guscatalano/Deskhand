# 05 — Screen Capture

Deskhand carries **three** capture strategies and picks per request. All produce a `CaptureResultDto`
(`Desktop`, `Rect`, `Monitor`, `DpiScale`, `Format`, `Bytes`), encoded PNG (default) or JPEG.

| Strategy | Code | Used for | Notes |
|---|---|---|---|
| **GDI `CopyFromScreen`** | `ScreenCapture.CaptureRectBytes` | screen, region, element, and window *fallback* | dependency-free; reads the on-screen rectangle |
| **`PrintWindow` PW_RENDERFULLCONTENT** | `ScreenCapture.CaptureWindow` fallback | a window when WGC is unavailable | misses occluded pixels; **near-black on GPU windows** |
| **Windows.Graphics.Capture (WGC)** via Vortice D3D11 | `WgcCapture` | window capture (preferred) | GPU/DWM content, occluded or unfocused, **without raising** |

Plus the Phase-2 **secure-desktop** GDI path in `SecureCapture` (documented in `11-secure-desktop.md`),
which is GDI because WGC/DXGI are restricted on the secure desktop.

## GDI path — screen / region / element

`ScreenCapture` (static class). Core primitive:

```csharp
private static byte[] CaptureRectBytes(Rectangle rect, ImageFormat format, int jpegQuality)
{
    using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
        g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
    return Encode(bmp, format, jpegQuality);
}
```

- `CaptureScreen(monitor)` — `monitor == null` → the whole virtual desktop (`DesktopInfo.VirtualScreen()`,
  reported `Monitor: -1`); otherwise the given monitor's bounds.
- `CaptureRegion(x,y,w,h)` — arbitrary rectangle (throws `ArgumentException` if `w<=0 || h<=0`).
- `CaptureBounds(rect)` — an element's bounds (used by `CaptureElement` and the window-ref fallback).

`Encode` writes PNG via `Bitmap.Save(ms, ImageFormat.Png)`, or JPEG by locating the JPEG `ImageCodecInfo`
and setting `Encoder.Quality` (clamped 1..100). `MonitorIndexFor(rect)` picks the monitor whose bounds
contain the rectangle's center (else `-1`). `Result(...)` fills in `Desktop` (from `DesktopInfo`),
`DpiScale` (the monitor's scale, else 1.0), and `Format` string.

## Window capture — WGC preferred, PrintWindow fallback

`ScreenCapture.CaptureWindow(IntPtr hwnd, ...)`:

```csharp
if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var wr))
    throw new ArgumentException("Invalid window handle.");
var rect = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);

// 1) Preferred: WGC
var wgc = WgcCapture.TryCaptureWindow(hwnd, format, jpegQuality);
if (wgc is not null) { var (bytes, w, h) = wgc.Value;
    return Result(new Rectangle(rect.X, rect.Y, w, h), MonitorIndexFor(rect), format, bytes); }

// 2) Fallback: PrintWindow with PW_RENDERFULLCONTENT into a memory DC
byte[]? bytes = null;
using (var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
using (var g = Graphics.FromImage(bmp)) {
    IntPtr hdc = g.GetHdc();
    try { if (NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT)) {
            g.ReleaseHdc(hdc); bytes = Encode(bmp, format, jpegQuality); }
          else g.ReleaseHdc(hdc); }
    catch { g.ReleaseHdc(hdc); }
}
// 3) Last resort: copy the on-screen rectangle
bytes ??= CaptureRectBytes(rect, format, jpegQuality);
return Result(rect, MonitorIndexFor(rect), format, bytes);
```

`PW_RENDERFULLCONTENT` is `0x00000002` — the flag that makes `PrintWindow` render DWM/browser content.

> **Gotcha — PrintWindow returns near-black for GPU-accelerated windows.** Chrome, Firefox, Electron, and
> other DWM/GPU-composited apps often print all-black (or a black client area) with `PrintWindow`, even with
> `PW_RENDERFULLCONTENT`. That is exactly why WGC is tried first: WGC reads the real composited pixels.
> PrintWindow is only the fallback for pre-1903 systems where WGC is unavailable.

> **WGC's advantage:** it captures a window's real composited pixels — including GPU content — **while the
> window is unfocused or occluded, without bringing it to the foreground.** The dashboard exposes this as
> "Capture window (no raise)".

## The WGC pipeline (`WgcCapture`)

Namespaces: `Vortice.Direct3D`, `Vortice.Direct3D11`, `Vortice.DXGI`, `Windows.Graphics.Capture`,
`Windows.Graphics.DirectX`, `Windows.Graphics.DirectX.Direct3D11`, `WinRT`. `MapFlags` is aliased to
`Vortice.Direct3D11.MapFlags` to disambiguate.

Two hand-written COM interop interfaces and one D3D export bridge WinRT and DXGI:

```csharp
[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private interface IGraphicsCaptureItemInterop {
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}
[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }

[DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
public static bool IsSupported {
    get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
}
```

`TryCaptureWindow` returns `(byte[] bytes, int width, int height)?` — `null` on any failure or when
`!IsSupported`. The pipeline in `Capture`:

1. **Create a D3D11 device.**
   `D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? d3dDevice).CheckError();`
   Keep `device` and `device.ImmediateContext`. `BgraSupport` is required for WGC.
2. **Make a WinRT `IDirect3DDevice` from the DXGI device.**
   `using var dxgiDevice = device.QueryInterface<IDXGIDevice>();`
   `CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);`
   `var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); Marshal.Release(inspectable);`
3. **Create the capture item for the HWND** via the interop factory:
   ```csharp
   var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
   var iid = GraphicsCaptureItemIid;
   IntPtr itemPtr = factory.CreateForWindow(hwnd, ref iid);
   var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr); Marshal.Release(itemPtr);
   ```
   Bail if `item.Size` has non-positive width/height.
4. **Create a free-threaded frame pool + session and start capture.**
   ```csharp
   using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
       winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
   using var session = framePool.CreateCaptureSession(item);
   session.StartCapture();
   ```
   `CreateFreeThreaded` is what lets us pull a frame **synchronously** (no dispatcher/message pump), which
   suits the STA-thread execution model.
5. **Pull one frame, retrying** (the first frame can lag):
   ```csharp
   Direct3D11CaptureFrame? frame = null;
   for (int i = 0; i < 60 && frame is null; i++) { frame = framePool.TryGetNextFrame(); if (frame is null) Thread.Sleep(16); }
   if (frame is null) return null;
   ```
6. **Get the D3D texture out of the WinRT surface** via `IDirect3DDxgiInterfaceAccess`:
   ```csharp
   var access = surface.As<IDirect3DDxgiInterfaceAccess>();
   var iid = typeof(ID3D11Texture2D).GUID;
   IntPtr ptr = access.GetInterface(ref iid);
   return new ID3D11Texture2D(ptr);
   ```
7. **Copy to a CPU-readable staging texture, then `Map` it:**
   ```csharp
   var desc = surfaceTex.Description;
   desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
   desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
   using var staging = device.CreateTexture2D(desc);
   context.CopyResource(staging, surfaceTex);
   int w = (int)desc.Width, h = (int)desc.Height;                 // NOTE: desc.Width/Height are uint
   var mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
   try { var bytes = Encode(mapped.DataPointer, (int)mapped.RowPitch, w, h, format, jpegQuality); ... }
   finally { context.Unmap(staging, 0); }
   ```
8. **Copy row by row into a `Bitmap`** (because `RowPitch` ≠ `width*4`), then PNG/JPEG-encode:
   ```csharp
   var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
   byte* src = (byte*)data; byte* dst = (byte*)bd.Scan0; int rowBytes = w * 4;
   for (int y = 0; y < h; y++)
       Buffer.MemoryCopy(src + (long)y * rowPitch, dst + (long)y * bd.Stride, rowBytes, rowBytes);
   ```

> **Gotcha — Vortice exposes texture dimensions as `uint`.** `Texture2DDescription.Width` / `.Height` are
> `uint`, so the code casts `(int)desc.Width` / `(int)desc.Height` (and `(int)mapped.RowPitch`). Assigning
> them straight into `int` variables/params is a compile error.

> **Gotcha — always free the WinRT ABI pointers.** After `FromAbi`, call `Marshal.Release(...)` on the raw
> pointer (done for both `inspectable` and `itemPtr`). The `frame`, `framePool`, `session`, `device`,
> `context`, `staging`, and `surfaceTex` are all `using`-disposed; `frame.Dispose()` is in a `finally`.

## Encoding notes shared by all paths

- `Bitmap` is always `PixelFormat.Format32bppArgb` (BGRA byte order, matching `B8G8R8A8UIntNormalized`).
- JPEG uses `ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid)` with an
  `EncoderParameter(Encoder.Quality, clampedQuality)`.
- `System.Drawing.Imaging.ImageFormat` is aliased `DImageFormat` in the capture files to avoid clashing
  with Deskhand's own `ImageFormat` enum.

## Response shapes

See `07-http-server.md` (HTTP returns JSON+base64 or raw bytes) and `09-mcp-server.md` (MCP returns an
`ImageContentBlock`). One MCP-specific gotcha is called out there and repeated here because it bites:

> **Gotcha — `ImageContentBlock.Data` takes raw bytes, not base64.** In the MCP SDK 2.2.0, set
> `Data = c.Bytes` (the `byte[]`). Do **not** `Convert.ToBase64String` first — the SDK base64-encodes for
> the wire itself, and passing an already-encoded string double-encodes and breaks the image. The HTTP host,
> by contrast, *does* base64 the bytes for its JSON body.
