using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record ClipboardResultDto(bool Ok, string? Text, int Length, bool HasText, string? Error = null);
public record ClipboardImageDto(bool Ok, bool HasImage, int Width, int Height, string? Base64Png, string? Error = null);
public record ClipboardFilesDto(bool Ok, IReadOnlyList<string> Files, string? Error = null);

/// <summary>
/// Read/write the Windows clipboard (Unicode text). The clipboard is a shared, STA-affine resource and can be
/// briefly locked by another app, so every op runs on a short-lived STA thread and retries the open a few times
/// rather than failing on a transient lock. Text only — images/files are out of scope here.
/// </summary>
public static class ClipboardService
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static ClipboardResultDto GetText()
    {
        try
        {
            string? text = RunSta(() =>
            {
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
                if (!OpenClip()) throw new InvalidOperationException("Could not open the clipboard (locked by another app).");
                try
                {
                    IntPtr h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return null;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(p); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            });
            return new ClipboardResultDto(true, text, text?.Length ?? 0, text is not null);
        }
        catch (Exception ex) { return new ClipboardResultDto(false, null, 0, false, ex.Message); }
    }

    public static ClipboardResultDto SetText(string? text)
    {
        text ??= "";
        try
        {
            RunSta<object?>(() =>
            {
                if (!OpenClip()) throw new InvalidOperationException("Could not open the clipboard (locked by another app).");
                try
                {
                    EmptyClipboard();
                    int bytes = (text.Length + 1) * 2;                 // UTF-16 + null terminator
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed.");
                    IntPtr target = GlobalLock(hGlobal);
                    try { Marshal.Copy((text + '\0').ToCharArray(), 0, target, text.Length + 1); }
                    finally { GlobalUnlock(hGlobal); }
                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);                            // ownership not transferred on failure
                        throw new InvalidOperationException("SetClipboardData failed.");
                    }
                    return null;                                        // on success the OS owns hGlobal
                }
                finally { CloseClipboard(); }
            });
            return new ClipboardResultDto(true, text, text.Length, text.Length > 0);
        }
        catch (Exception ex) { return new ClipboardResultDto(false, null, 0, false, ex.Message); }
    }

    public static ClipboardResultDto Clear()
    {
        try
        {
            RunSta<object?>(() =>
            {
                if (!OpenClip()) throw new InvalidOperationException("Could not open the clipboard (locked by another app).");
                try { EmptyClipboard(); return null; } finally { CloseClipboard(); }
            });
            return new ClipboardResultDto(true, "", 0, false);
        }
        catch (Exception ex) { return new ClipboardResultDto(false, null, 0, false, ex.Message); }
    }

    /// <summary>Read an image from the clipboard (CF_DIB) as a base64 PNG. HasImage=false when the clipboard
    /// holds no image. (A 14-byte BITMAPFILEHEADER is prepended to the DIB so System.Drawing can re-encode it.)</summary>
    public static ClipboardImageDto GetImage()
    {
        try
        {
            byte[]? dib = RunSta(() =>
            {
                if (!IsClipboardFormatAvailable(CF_DIB)) return null;
                if (!OpenClip()) throw new InvalidOperationException("Could not open the clipboard (locked by another app).");
                try
                {
                    IntPtr h = GetClipboardData(CF_DIB);
                    if (h == IntPtr.Zero) return null;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { int size = (int)GlobalSize(h).ToUInt64(); var buf = new byte[size]; Marshal.Copy(p, buf, 0, size); return buf; }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            });
            if (dib is null || dib.Length < 40) return new ClipboardImageDto(true, false, 0, 0, null);
            int biSize = BitConverter.ToInt32(dib, 0);
            short bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            int palette = bitCount <= 8 ? (clrUsed != 0 ? clrUsed : (1 << bitCount)) : (compression == 3 ? 3 : 0);  // BI_BITFIELDS=3
            int offBits = 14 + biSize + palette * 4;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(offBits).CopyTo(bmp, 10);
            dib.CopyTo(bmp, 14);
            using var ms = new MemoryStream(bmp);
            using var img = new System.Drawing.Bitmap(ms);
            using var outMs = new MemoryStream();
            img.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
            return new ClipboardImageDto(true, true, img.Width, img.Height, Convert.ToBase64String(outMs.ToArray()));
        }
        catch (Exception ex) { return new ClipboardImageDto(false, false, 0, 0, null, ex.Message); }
    }

    /// <summary>Put an image on the clipboard (CF_DIB) from a base64 PNG (or any format System.Drawing reads).</summary>
    public static ClipboardImageDto SetImage(string? base64Png)
    {
        if (string.IsNullOrWhiteSpace(base64Png)) return new ClipboardImageDto(false, false, 0, 0, null, "No image data.");
        byte[] png; try { png = Convert.FromBase64String(base64Png); } catch { return new ClipboardImageDto(false, false, 0, 0, null, "Not valid base64."); }
        try
        {
            int w, h; byte[] dib;
            using (var ms = new MemoryStream(png))
            using (var bmp = new System.Drawing.Bitmap(ms))
            {
                w = bmp.Width; h = bmp.Height;
                using var bms = new MemoryStream();
                bmp.Save(bms, System.Drawing.Imaging.ImageFormat.Bmp);
                dib = bms.ToArray()[14..];  // strip the 14-byte BITMAPFILEHEADER -> CF_DIB payload
            }
            RunSta<object?>(() =>
            {
                if (!OpenClip()) throw new InvalidOperationException("Could not open the clipboard (locked by another app).");
                try
                {
                    EmptyClipboard();
                    IntPtr hg = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dib.Length);
                    if (hg == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed.");
                    IntPtr t = GlobalLock(hg);
                    try { Marshal.Copy(dib, 0, t, dib.Length); } finally { GlobalUnlock(hg); }
                    if (SetClipboardData(CF_DIB, hg) == IntPtr.Zero) { GlobalFree(hg); throw new InvalidOperationException("SetClipboardData failed."); }
                    return null;
                }
                finally { CloseClipboard(); }
            });
            return new ClipboardImageDto(true, true, w, h, null);
        }
        catch (Exception ex) { return new ClipboardImageDto(false, false, 0, 0, null, ex.Message); }
    }

    /// <summary>Read the list of files copied to the clipboard (CF_HDROP). Empty when none are present.</summary>
    public static ClipboardFilesDto GetFiles()
    {
        try
        {
            var files = RunSta(() =>
            {
                if (!IsClipboardFormatAvailable(CF_HDROP)) return (IReadOnlyList<string>)Array.Empty<string>();
                if (!OpenClip()) throw new InvalidOperationException("Could not open the clipboard (locked by another app).");
                try
                {
                    IntPtr h = GetClipboardData(CF_HDROP);
                    if (h == IntPtr.Zero) return Array.Empty<string>();
                    uint count = DragQueryFile(h, 0xFFFFFFFF, null, 0);
                    var list = new List<string>((int)count);
                    for (uint i = 0; i < count; i++)
                    {
                        uint len = DragQueryFile(h, i, null, 0);
                        var sb = new System.Text.StringBuilder((int)len + 1);
                        DragQueryFile(h, i, sb, len + 1);
                        list.Add(sb.ToString());
                    }
                    return (IReadOnlyList<string>)list;
                }
                finally { CloseClipboard(); }
            });
            return new ClipboardFilesDto(true, files);
        }
        catch (Exception ex) { return new ClipboardFilesDto(false, Array.Empty<string>(), ex.Message); }
    }

    /// <summary>Put a list of files on the clipboard (CF_HDROP) — paste targets (Explorer, chat, mail) receive
    /// them as copied files. Paths should be absolute and exist on this machine.</summary>
    public static ClipboardFilesDto SetFiles(IReadOnlyList<string>? paths)
    {
        var files = (paths ?? Array.Empty<string>()).Select(p => (p ?? "").Trim().Trim('"')).Where(p => p.Length > 0).ToList();
        if (files.Count == 0) return new ClipboardFilesDto(false, Array.Empty<string>(), "No file paths given.");
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in files) { sb.Append(f); sb.Append('\0'); }
            sb.Append('\0');                                   // double-NUL terminates the list
            byte[] listBytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());
            const int headerSize = 20;                         // sizeof(DROPFILES)
            int total = headerSize + listBytes.Length;
            RunSta<object?>(() =>
            {
                if (!OpenClip()) throw new InvalidOperationException("Could not open the clipboard (locked by another app).");
                try
                {
                    EmptyClipboard();
                    IntPtr hg = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)total);
                    if (hg == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed.");
                    IntPtr p = GlobalLock(hg);
                    try
                    {
                        Marshal.WriteInt32(p, 0, headerSize);                    // DROPFILES.pFiles (offset to list)
                        Marshal.WriteInt32(p, 4, 0); Marshal.WriteInt32(p, 8, 0); // pt.x, pt.y
                        Marshal.WriteInt32(p, 12, 0);                           // fNC
                        Marshal.WriteInt32(p, 16, 1);                           // fWide = true (Unicode)
                        Marshal.Copy(listBytes, 0, p + headerSize, listBytes.Length);
                    }
                    finally { GlobalUnlock(hg); }
                    if (SetClipboardData(CF_HDROP, hg) == IntPtr.Zero) { GlobalFree(hg); throw new InvalidOperationException("SetClipboardData failed."); }
                    return null;
                }
                finally { CloseClipboard(); }
            });
            return new ClipboardFilesDto(true, files);
        }
        catch (Exception ex) { return new ClipboardFilesDto(false, Array.Empty<string>(), ex.Message); }
    }

    // Open with a few short retries — the clipboard is often momentarily held by another process.
    private static bool OpenClip()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? err = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { err = e; } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder? lpszFile, uint cch);
}
