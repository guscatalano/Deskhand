using System.Drawing.Drawing2D;
using Deskhand.Core.Governance;

namespace Deskhand.Ui;

/// <summary>
/// A persistent, always-on-top red banner pinned to the top-centre of the primary screen while a
/// sensitive observation is active (the user's mouse/keyboard being recorded). It stays up from
/// <see cref="Begin"/> to <see cref="End"/> — so the user can never be watched without a visible sign —
/// and pulses to draw the eye. Runs its own STA message loop, like <see cref="ToastNotifier"/>.
/// </summary>
public sealed class RecordingIndicator : IActivityIndicator, IDisposable
{
    private readonly Thread _thread;
    private IndicatorForm? _form;
    private volatile bool _ready;

    public RecordingIndicator()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Deskhand-Indicator" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        _form = new IndicatorForm();
        _ = _form.Handle;
        _ready = true;
        Application.Run();
    }

    public void Begin(string message)
    {
        var f = _form;
        if (!_ready || f is null) return;
        try { if (f.IsHandleCreated) f.BeginInvoke(new Action(() => f.ShowBanner(message))); } catch { }
    }

    public void End()
    {
        var f = _form;
        if (f is null) return;
        try { if (f.IsHandleCreated) f.BeginInvoke(new Action(f.HideBanner)); } catch { }
    }

    public void Dispose()
    {
        var f = _form;
        try { if (f is not null && f.IsHandleCreated) f.BeginInvoke(new Action(Application.ExitThread)); } catch { }
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}

internal sealed class IndicatorForm : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _pulse;
    private int _tick;

    public IndicatorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(140, 20, 24);
        Size = new Size(420, 40);
        DoubleBuffered = true;
        Visible = false;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(40, 0, 14, 0),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        };
        Controls.Add(_label);
        Paint += OnPaint;
        Region = RoundedRegion(Size, 10);

        _pulse = new System.Windows.Forms.Timer { Interval = 60 };
        _pulse.Tick += (_, _) => { _tick++; Invalidate(); };
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

    public void ShowBanner(string message)
    {
        _label.Text = message;
        Reposition();
        if (!Visible) Show();
        TopMost = true; BringToFront();
        _pulse.Start();
    }

    public void HideBanner()
    {
        _pulse.Stop();
        if (Visible) Hide();
    }

    private void Reposition()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 10);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Reposition(); }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // pulsing record dot
        double phase = (Math.Sin(_tick * 0.18) + 1) / 2;          // 0..1
        int alpha = 120 + (int)(135 * phase);
        using var dot = new SolidBrush(Color.FromArgb(alpha, 255, 90, 90));
        e.Graphics.FillEllipse(dot, 16, Height / 2 - 7, 14, 14);
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
