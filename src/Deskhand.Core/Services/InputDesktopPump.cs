using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Deskhand.Core.Interop;

namespace Deskhand.Core.Services;

/// <summary>
/// A single dedicated background thread that stays attached to whichever desktop currently owns
/// input, re-attaching (<c>OpenInputDesktop</c> + <c>SetThreadDesktop</c>) whenever it changes, so
/// synthetic input (SendInput) lands on the <b>active</b> desktop rather than only the one this
/// process launched on. That lets interactive control follow a desktop switch automatically — a
/// screensaver or custom desktop always, and the secure desktop (UAC / lock / logon) when the
/// process is SYSTEM. Actions run one at a time on this thread; exceptions marshal back to the caller.
/// A dedicated MTA thread is used so the process's UIA/STA thread is never re-desktop'd.
/// </summary>
public sealed class InputDesktopPump : IDisposable
{
    private readonly BlockingCollection<(Action act, TaskCompletionSource tcs)> _q = new();
    private readonly Thread _thread;
    private string _attached = "";        // name of the desktop this thread is currently attached to
    private IntPtr _held = IntPtr.Zero;    // handle we SetThreadDesktop'd to (closed when it changes)

    public InputDesktopPump()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Deskhand-InputDesktop" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Run <paramref name="act"/> on the input-desktop thread, blocking until it finishes and
    /// rethrowing any exception (e.g. <see cref="DesktopUnavailableException"/> on the secure desktop).</summary>
    public void Run(Action act)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _q.Add((act, tcs));
        tcs.Task.GetAwaiter().GetResult();
    }

    private void Loop()
    {
        foreach (var (act, tcs) in _q.GetConsumingEnumerable())
        {
            try { EnsureAttachedToInputDesktop(); act(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }
    }

    private void EnsureAttachedToInputDesktop()
    {
        IntPtr hInput = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_ATTACH_ACCESS);
        if (hInput == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new DesktopUnavailableException(
                $"OpenInputDesktop failed (Win32 {err}). The secure desktop (UAC / lock / logon) is active and " +
                "this user-session process is not SYSTEM, so its input cannot be driven. Run the Secure Helper as SYSTEM.");
        }

        string name = DesktopName(hInput);
        if (name.Length > 0 && name == _attached) { NativeMethods.CloseDesktop(hInput); return; } // already attached

        if (!NativeMethods.SetThreadDesktop(hInput))
        {
            int err = Marshal.GetLastWin32Error();
            NativeMethods.CloseDesktop(hInput);
            throw new DesktopUnavailableException($"SetThreadDesktop('{name}') failed (Win32 {err}).");
        }

        if (_held != IntPtr.Zero) NativeMethods.CloseDesktop(_held);
        _held = hInput;
        _attached = name;
    }

    private static string DesktopName(IntPtr hDesktop)
    {
        NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, null, 0, out uint needed);
        if (needed == 0) return "";
        var buffer = new byte[needed];
        if (!NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, buffer, needed, out _)) return "";
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public void Dispose() => _q.CompleteAdding();
}
