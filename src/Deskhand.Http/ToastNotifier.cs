using System.Drawing.Drawing2D;
using Deskhand.Core.Governance;

namespace Deskhand.Http;

/// <summary>
/// Draws a brief, non-activating on-screen toast in the bottom-right corner whenever a screenshot
/// is taken, so the user always knows their screen was captured. Self-contained (no notification
/// registration / packaging), running its own STA message loop.
/// </summary>
public sealed class ToastNotifier : ICaptureNotifier, IDisposable
{
    private readonly Thread _thread;
    private ToastForm? _form;
    private volatile bool _ready;

    public ToastNotifier()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Deskhand-Toast" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        _form = new ToastForm();
        _ = _form.Handle; // force handle creation on this STA thread
        _ready = true;
        Application.Run(); // message loop until ExitThread
    }

    public void Notify(string message)
    {
        var f = _form;
        if (!_ready || f is null) return;
        try { if (f.IsHandleCreated) f.BeginInvoke(new Action(() => f.ShowToast(message))); } catch { }
    }

    public void Dispose()
    {
        var f = _form;
        try { if (f is not null && f.IsHandleCreated) f.BeginInvoke(new Action(Application.ExitThread)); } catch { }
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}

internal sealed class ToastForm : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _timer;
    private int _shownTick;
    private const int HoldMs = 2600, FadeMs = 700;
    private const double MaxOpacity = 0.96;

    public ToastForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(16, 22, 29);
        Size = new Size(340, 66);
        Opacity = 0;
        DoubleBuffered = true;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(232, 237, 242),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(48, 0, 14, 0),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
        };
        Controls.Add(_label);
        Paint += OnPaint;

        _timer = new System.Windows.Forms.Timer { Interval = 70 };
        _timer.Tick += OnTick;
        Region = RoundedRegion(Size, 12);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public void ShowToast(string message)
    {
        _label.Text = message;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);
        Opacity = MaxOpacity;
        if (!Visible) Show();
        BringToFront();
        _shownTick = Environment.TickCount;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        int el = Environment.TickCount - _shownTick;
        if (el < HoldMs) { Opacity = MaxOpacity; return; }
        double o = MaxOpacity * (1.0 - (double)(el - HoldMs) / FadeMs);
        if (o <= 0) { _timer.Stop(); Opacity = 0; Hide(); }
        else Opacity = o;
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var accent = new SolidBrush(Color.FromArgb(36, 186, 191));
        e.Graphics.FillRectangle(accent, 0, 0, 4, Height);
        using var glyphFont = new Font("Segoe UI Emoji", 13f);
        e.Graphics.DrawString("\U0001F4F7", glyphFont, Brushes.White, 14, 20);
    }

    private static Region RoundedRegion(Size size, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size.Width - d, 0, d, d, 270, 90);
        path.AddArc(size.Width - d, size.Height - d, d, d, 0, 90);
        path.AddArc(0, size.Height - d, d, d, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }
}
