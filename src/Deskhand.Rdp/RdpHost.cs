using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AxMSTSCLib;

namespace Deskhand.Rdp;

/// <summary>
/// Hosts the Microsoft RDP ActiveX control (mstscax.dll) headlessly on its own STA thread and drives
/// a remote host over the RDP wire — no software installed on the target. Provides screen capture
/// (PrintWindow of the control surface) and synthetic input (posted to the control's render window).
/// There is no UIA over pure RDP: nothing runs in the remote session to expose an accessibility tree.
/// </summary>
public sealed class RdpHost : IDisposable
{
    private readonly Thread _thread;
    private Form? _form;
    private AxMsRdpClient10NotSafeForScripting? _rdp;
    private volatile bool _ready;
    private TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Width { get; }
    public int Height { get; }
    public bool Connected { get; private set; }
    public string LastReason { get; private set; } = "";

    public RdpHost(int width = 1280, int height = 800)
    {
        Width = width; Height = height;
        _thread = new Thread(Run) { IsBackground = true, Name = "Deskhand-RDP" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        while (!_ready) Thread.Sleep(10);
    }

    private void Run()
    {
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        _form = new Form
        {
            Text = "deskhand-rdp",
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000), // off-screen, but a real window so the control renders
            ClientSize = new Size(Width, Height),
        };
        _rdp = new AxMsRdpClient10NotSafeForScripting();
        ((ISupportInitialize)_rdp).BeginInit();
        _rdp.Dock = DockStyle.Fill;
        _form.Controls.Add(_rdp);
        ((ISupportInitialize)_rdp).EndInit();

        _rdp.OnConnected += (_, _) => { Connected = true; _connected.TrySetResult(true); };
        _rdp.OnDisconnected += (_, e) => { Connected = false; LastReason = $"disconnect reason {e.discReason}"; _connected.TrySetResult(false); };

        _form.Show();     // create handle + let the control initialize
        _ready = true;
        Application.Run(_form);
    }

    private T OnUi<T>(Func<T> f) => (T)_form!.Invoke(f);
    private void OnUi(Action a) => _form!.Invoke(a);

    /// <summary>Connect to a host. Returns true on connect, false on disconnect/failure (see LastReason).</summary>
    public async Task<bool> ConnectAsync(string host, string user, string? domain, string password, int timeoutMs = 15000)
    {
        _connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            _rdp!.Server = host;
            _rdp.UserName = user;
            if (!string.IsNullOrEmpty(domain)) _rdp.Domain = domain;
            _rdp.DesktopWidth = Width;
            _rdp.DesktopHeight = Height;
            var secured = (MSTSCLib.IMsTscNonScriptable)_rdp.GetOcx();
            secured.ClearTextPassword = password;
            _rdp.AdvancedSettings9.EnableCredSspSupport = true;   // NLA
            _rdp.AdvancedSettings9.AuthenticationLevel = 0;        // connect even if server auth can't be verified
            _rdp.Connect();
        });

        var done = await Task.WhenAny(_connected.Task, Task.Delay(timeoutMs));
        if (done != _connected.Task) { LastReason = "timeout"; return false; }
        return _connected.Task.Result;
    }

    public void Disconnect() { try { OnUi(() => { if (_rdp!.Connected != 0) _rdp.Disconnect(); }); } catch { } }

    /// <summary>Capture the remote desktop surface as PNG bytes.</summary>
    public byte[] Capture() => OnUi(() =>
    {
        var hwnd = _rdp!.Handle;
        NativeMethods.GetClientRect(hwnd, out var rc);
        int w = Math.Max(1, rc.Right - rc.Left), h = Math.Max(1, rc.Bottom - rc.Top);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    });

    // ---- input: posted to the control's deepest render child (headless-friendly) ----
    private IntPtr InputTarget()
    {
        IntPtr host = _rdp!.Handle, best = host; int bestArea = 0;
        NativeMethods.EnumChildWindows(host, (child, _) =>
        {
            if (NativeMethods.GetClientRect(child, out var r))
            {
                int area = (r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = child; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public void MouseMove(int x, int y) => OnUi(() => NativeMethods.PostMessage(InputTarget(), NativeMethods.WM_MOUSEMOVE, (IntPtr)0, Lp(x, y)));

    public void MouseClick(string button, int x, int y)
    {
        OnUi(() =>
        {
            var t = InputTarget();
            (uint down, uint up, IntPtr mk) = button.ToLowerInvariant() switch
            {
                "right" => (NativeMethods.WM_RBUTTONDOWN, NativeMethods.WM_RBUTTONUP, (IntPtr)0x2),
                "middle" => (NativeMethods.WM_MBUTTONDOWN, NativeMethods.WM_MBUTTONUP, (IntPtr)0x10),
                _ => (NativeMethods.WM_LBUTTONDOWN, NativeMethods.WM_LBUTTONUP, (IntPtr)0x1),
            };
            NativeMethods.PostMessage(t, NativeMethods.WM_MOUSEMOVE, (IntPtr)0, Lp(x, y));
            NativeMethods.PostMessage(t, down, mk, Lp(x, y));
            NativeMethods.PostMessage(t, up, (IntPtr)0, Lp(x, y));
        });
    }

    public void TypeText(string text) => OnUi(() =>
    {
        var t = InputTarget();
        foreach (char c in text) NativeMethods.PostMessage(t, NativeMethods.WM_CHAR, (IntPtr)c, (IntPtr)0);
    });

    public void SendKey(ushort vk) => OnUi(() =>
    {
        var t = InputTarget();
        NativeMethods.PostMessage(t, NativeMethods.WM_KEYDOWN, (IntPtr)vk, (IntPtr)0);
        NativeMethods.PostMessage(t, NativeMethods.WM_KEYUP, (IntPtr)vk, (IntPtr)0);
    });

    private static IntPtr Lp(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    public void Dispose()
    {
        try { Disconnect(); } catch { }
        try { _form?.Invoke(() => Application.ExitThread()); } catch { }
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

internal static class NativeMethods
{
    public const uint PW_RENDERFULLCONTENT = 0x2;
    public const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
        WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
