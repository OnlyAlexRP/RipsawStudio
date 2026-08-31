using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// Base for the small panels that float over the picture. They are opaque on purpose:
/// a swap chain is presented straight to the video window, so nothing underneath a
/// transparent control would ever be composited into it.
/// </summary>
internal abstract class Overlay : Control
{
    private readonly System.Windows.Forms.Timer _hideTimer = new();

    protected Overlay()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Shell;
        Visible = false;
        TabStop = false;
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Visible = false; };
    }

    /// <summary>Shows the overlay, hiding it again after <paramref name="milliseconds"/> (0 = stay).</summary>
    protected void Flash(int milliseconds)
    {
        _hideTimer.Stop();
        Visible = true;
        BringToFront();
        Invalidate();
        if (milliseconds > 0)
        {
            _hideTimer.Interval = milliseconds;
            _hideTimer.Start();
        }
    }

    protected static void RoundedBackground(Graphics g, Rectangle bounds, Color colour, int radius = 8)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        var r = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(colour);
        g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hideTimer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Volume readout that appears on change and fades out, like the one in VGC.</summary>
internal sealed class VolumeOverlay : Overlay
{
    private float _volume = 1f;
    private bool _muted;

    public VolumeOverlay() => Size = new Size(220, 46);

    public void Show(float volume, bool muted)
    {
        _volume = volume;
        _muted = muted;
        Flash(1500);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        RoundedBackground(g, ClientRectangle, Theme.Shell);

        using var text = new SolidBrush(Theme.Text);
        string label = _muted ? "Muted" : $"{_volume * 100:0}%";
        g.DrawString(label, Theme.BodyBold, text, 14, 14);

        var track = new Rectangle(74, 20, 130, 6);
        using (var back = new SolidBrush(Theme.Track)) g.FillRectangle(back, track);
        if (!_muted && _volume > 0)
        {
            int width = (int)(track.Width * Math.Clamp(_volume / 2f, 0f, 1f));
            using var fill = new SolidBrush(Theme.Accent);
            g.FillRectangle(fill, track.X, track.Y, width, track.Height);
        }
    }
}

/// <summary>Transient message line for status and errors.</summary>
internal sealed class ToastOverlay : Overlay
{
    private string _message = "";
    private bool _isError;

    public ToastOverlay() => Size = new Size(560, 34);

    public void Show(string message, bool isError)
    {
        _message = message;
        _isError = isError;
        Flash(isError ? 8000 : 3500);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        RoundedBackground(g, ClientRectangle, _isError ? Color.FromArgb(74, 34, 34) : Theme.Shell);

        using var brush = new SolidBrush(_isError ? Color.FromArgb(255, 176, 168) : Theme.Text);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var rect = new RectangleF(12, 8, Width - 24, Height - 12);
        g.DrawString(_message, Theme.Body, brush, rect, format);
    }
}

/// <summary>Optional always-on readout of what the pipeline is doing.</summary>
internal sealed class StatsOverlay : Overlay
{
    private string _line1 = "";
    private string _line2 = "";

    public StatsOverlay() => Size = new Size(250, 48);

    public void Update(string line1, string line2)
    {
        _line1 = line1;
        _line2 = line2;
        if (Visible) Invalidate();
    }

    public void SetVisible(bool visible)
    {
        if (visible) Flash(0);
        else Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        RoundedBackground(g, ClientRectangle, Theme.Shell);
        using var bright = new SolidBrush(Theme.Text);
        using var dim = new SolidBrush(Theme.TextDim);
        g.DrawString(_line1, Theme.Mono, bright, 12, 8);
        g.DrawString(_line2, Theme.Mono, dim, 12, 26);
    }
}

/// <summary>Recording tally light. Deliberately the only thing on screen while you play.</summary>
internal sealed class RecordOverlay : Overlay
{
    private TimeSpan _elapsed;
    private bool _blinkOn = true;
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 600 };

    public RecordOverlay()
    {
        Size = new Size(112, 30);
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; Invalidate(); };
    }

    public void Start()
    {
        _blink.Start();
        Flash(0);
    }

    public void Stop()
    {
        _blink.Stop();
        Visible = false;
    }

    public void Update(TimeSpan elapsed)
    {
        _elapsed = elapsed;
        if (Visible) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        RoundedBackground(g, ClientRectangle, Theme.Shell);
        if (_blinkOn)
        {
            using var dot = new SolidBrush(Theme.Record);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(dot, 12, 11, 9, 9);
        }
        using var text = new SolidBrush(Theme.Text);
        g.DrawString(_elapsed.ToString(@"hh\:mm\:ss"), Theme.BodyBold, text, 28, 7);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _blink.Dispose();
        base.Dispose(disposing);
    }
}
