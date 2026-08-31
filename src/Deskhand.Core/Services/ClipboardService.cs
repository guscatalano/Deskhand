using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record ClipboardResultDto(bool Ok, string? Text, int Length, bool HasText, string? Error = null);

/// <summary>
/// Read/write the Windows clipboard (Unicode text). The clipboard is a shared, STA-affine resource and can be
/// briefly locked by another app, so every op runs on a short-lived STA thread and retries the open a few times
/// rather than failing on a transient lock. Text only — images/files are out of scope here.
/// </summary>
public static class ClipboardService
{
    private const uint CF_UNICODETEXT = 13;
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
}
