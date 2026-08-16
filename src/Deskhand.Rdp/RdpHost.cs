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
            // For the "install native agent over RDP" bootstrap: expose the connector's drives to the
            // remote (\\tsclient\...) and send Windows-key combos to the remote session (KeyboardHookMode=2).
            try { _rdp.AdvancedSettings9.RedirectDrives = true; } catch { }
            try { _rdp.SecuredSettings3.KeyboardHookMode = 2; } catch { }
            _rdp.Connect();
        });

        var done = await Task.WhenAny(_connected.Task, Task.Delay(timeoutMs));
        if (done != _connected.Task) { LastReason = "timeout"; return false; }
        return _connected.Task.Result;
    }

    public void Disconnect() { try { OnUi(() => { if (_rdp!.Connected != 0) _rdp.Disconnect(); }); } catch { } }

    /// <summary>Diagnostic: every descendant window of the RDP control (class + size), plus which one input
    /// is posted to. RDP client versions name their render surface differently; this reveals it.</summary>
    public (string chosen, IReadOnlyList<string> all) DumpChildren() => OnUi(() =>
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder(64);
        NativeMethods.EnumChildWindows(_rdp!.Handle, (child, _) =>
        {
            sb.Clear(); NativeMethods.GetClassName(child, sb, sb.Capacity);
            NativeMethods.GetClientRect(child, out var r);
            list.Add($"{sb} [{r.Right - r.Left}x{r.Bottom - r.Top}] hwnd=0x{child:X}");
            return true;
        }, IntPtr.Zero);
        var t = InputTarget();
        var cb = new System.Text.StringBuilder(64); NativeMethods.GetClassName(t, cb, cb.Capacity);
        return ($"{cb} (0x{t:X})", (IReadOnlyList<string>)list);
    });

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

    // ---- input: posted to the RDP control's actual render/input surface ----
    // The mstscax control renders into a nested child of class "IHWindowClass"; synthetic WM_ input has to
    // go there (posting to the AxHost/OCX container is ignored). Fall back to the largest descendant.
    private IntPtr _inputTarget;
    private int _offX, _offY;   // render-child client origin relative to the control's client (capture space)
    private IntPtr InputTarget()
    {
        if (_inputTarget != IntPtr.Zero && NativeMethods.IsWindow(_inputTarget)) return _inputTarget;
        IntPtr host = _rdp!.Handle, byClass = IntPtr.Zero, byArea = host; int bestArea = 0;
        var sb = new System.Text.StringBuilder(64);
        NativeMethods.EnumChildWindows(host, (child, _) =>
        {
            sb.Clear();
            NativeMethods.GetClassName(child, sb, sb.Capacity);
            if (sb.ToString() == "IHWindowClass") byClass = child;
            if (NativeMethods.GetClientRect(child, out var r))
            {
                int area = (r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; byArea = child; }
            }
            return true;
        }, IntPtr.Zero);
        _inputTarget = byClass != IntPtr.Zero ? byClass : byArea;

        // Capture is PrintWindow of the CONTROL's client area, but input is posted to this render child,
        // which may sit at a small offset inside the control. Translate capture coords into child coords.
        try
        {
            var childOrigin = new NativeMethods.POINT();
            NativeMethods.ClientToScreen(_inputTarget, ref childOrigin);
            var ctrlOrigin = new NativeMethods.POINT();
            NativeMethods.ClientToScreen(host, ref ctrlOrigin);
            _offX = childOrigin.X - ctrlOrigin.X;
            _offY = childOrigin.Y - ctrlOrigin.Y;
        }
        catch { _offX = _offY = 0; }
        return _inputTarget;
    }

    // Map a capture-space (control-client) coordinate to the render child's client space.
    private (int x, int y) Map(int x, int y) { InputTarget(); return (x - _offX, y - _offY); }

    // The control forwards input to the session only once it has focus; give the render window focus first.
    private void FocusInput(IntPtr t)
    {
        try { _rdp!.Focus(); } catch { }
        NativeMethods.SetFocus(t);
    }

    public void MouseMove(int x, int y) => OnUi(() =>
    {
        var t = InputTarget(); var (mx, my) = Map(x, y);
        NativeMethods.PostMessage(t, NativeMethods.WM_MOUSEMOVE, (IntPtr)0, Lp(mx, my));
    });

    public void MouseClick(string button, int x, int y)
    {
        OnUi(() =>
        {
            var t = InputTarget(); FocusInput(t);
            var (mx, my) = Map(x, y);
            (uint down, uint up, IntPtr mk) = button.ToLowerInvariant() switch
            {
                "right" => (NativeMethods.WM_RBUTTONDOWN, NativeMethods.WM_RBUTTONUP, (IntPtr)0x2),
                "middle" => (NativeMethods.WM_MBUTTONDOWN, NativeMethods.WM_MBUTTONUP, (IntPtr)0x10),
                _ => (NativeMethods.WM_LBUTTONDOWN, NativeMethods.WM_LBUTTONUP, (IntPtr)0x1),
            };
            NativeMethods.PostMessage(t, NativeMethods.WM_MOUSEMOVE, (IntPtr)0, Lp(mx, my));
            NativeMethods.PostMessage(t, down, mk, Lp(mx, my));
            NativeMethods.PostMessage(t, up, (IntPtr)0, Lp(mx, my));
        });
    }

    public void TypeText(string text) => OnUi(() =>
    {
        var t = InputTarget();
        FocusInput(t);
        foreach (char c in text) NativeMethods.PostMessage(t, NativeMethods.WM_CHAR, (IntPtr)c, (IntPtr)0);
    });

    public void SendKey(ushort vk) => OnUi(() =>
    {
        var t = InputTarget();
        FocusInput(t);
        uint scan = NativeMethods.MapVirtualKey(vk, 0);
        IntPtr downLp = (IntPtr)(1 | (scan << 16));
        IntPtr upLp = (IntPtr)(1 | (scan << 16) | (1u << 30) | (1u << 31));
        NativeMethods.PostMessage(t, NativeMethods.WM_KEYDOWN, (IntPtr)vk, downLp);
        NativeMethods.PostMessage(t, NativeMethods.WM_KEYUP, (IntPtr)vk, upLp);
    });

    // ---- "install native agent over RDP" bootstrap ----
    private const ushort VK_LWIN = 0x5B, VK_R = 0x52, VK_RETURN = 0x0D;

    /// <summary>Open the remote Run dialog (Win+R — KeyboardHookMode=2 routes Win to the remote), type a
    /// command, and run it. Call off the UI thread; the inter-step sleeps must not block the message loop.</summary>
    public void RunCommand(string command)
    {
        Chord(VK_LWIN, VK_R);
        Thread.Sleep(1200);
        TypeText(command);
        Thread.Sleep(400);
        SendKey(VK_RETURN);
    }

    private void Chord(ushort mod, ushort key) => OnUi(() =>
    {
        var t = InputTarget(); FocusInput(t);
        PostKey(t, mod, true); PostKey(t, key, true); PostKey(t, key, false); PostKey(t, mod, false);
    });

    private static void PostKey(IntPtr t, ushort vk, bool down)
    {
        uint scan = NativeMethods.MapVirtualKey(vk, 0);
        uint lp = 1u | (scan << 16);
        if (!down) lp |= (1u << 30) | (1u << 31);
        NativeMethods.PostMessage(t, down ? NativeMethods.WM_KEYDOWN : NativeMethods.WM_KEYUP, (IntPtr)vk, (IntPtr)lp);
    }

    /// <summary>Translate a local path (C:\dir\app.exe) to its RDP drive-redirection UNC on the remote
    /// (\\tsclient\C\dir\app.exe), so the remote can run the connector's files.</summary>
    public static string ToTsClient(string localPath)
    {
        var root = System.IO.Path.GetPathRoot(localPath) ?? "";
        var drive = root.TrimEnd('\\', ':');
        var rest = localPath.Substring(root.Length);
        return $@"\\tsclient\{drive}\{rest}";
    }

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

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public delegate bool EnumWindowProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
}
