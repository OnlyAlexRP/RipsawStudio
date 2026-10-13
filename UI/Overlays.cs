using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RipsawStudio.UI;

/// <summary>
/// Base for the small things that float over the picture. Each one is its own layered window
/// rather than a child control: a child control over the D3D swap chain can only ever be an
/// opaque rectangle, while a layered window is composited with per-pixel alpha, so these can be
/// rounded and see-through like the rest of the HUD.
/// </summary>
internal abstract class Overlay : Form
{
    /// <summary>The HUD's glass: the navigation bar's near-black, mostly opaque.</summary>
    protected static readonly Color Glass = Color.FromArgb(218, Theme.Rail);
    protected static readonly Color GlassEdge = Color.FromArgb(38, 255, 255, 255);

    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private byte _alpha = 255;

    protected Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
    }

    /// <summary>Lets clicks fall through to the picture underneath. Off for an overlay you can click.</summary>
    protected virtual bool ClickThrough => true;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (ClickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>Whole-window opacity, for fades, on top of the per-pixel alpha.</summary>
    protected byte Alpha
    {
        get => _alpha;
        set
        {
            if (_alpha == value) return;
            _alpha = value;
            Render();
        }
    }

    protected abstract void Draw(Graphics g);

    /// <summary>Shows the overlay, hiding it again after <paramref name="milliseconds"/> (0 = stay).</summary>
    protected void Flash(int milliseconds)
    {
        _hideTimer.Stop();
        // Nothing to float over until the main window is up, or while it is minimised.
        if (Owner is not { Visible: true } owner || owner.WindowState == FormWindowState.Minimized) return;
        _ = Handle;
        Render();
        if (!Visible) Show();
        // Above the menu's backdrop too, without taking focus from whatever has it.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (milliseconds > 0)
        {
            _hideTimer.Interval = milliseconds;
            _hideTimer.Start();
        }
    }

    /// <summary>Redraws the window's pixels. Call after anything that changes what is shown.</summary>
    protected void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // GDI text (TextRenderer) knows nothing about alpha, so everything here goes through GDI+.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Draw(g);
        }

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr hbitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = SelectObject(memory, hbitmap);
        try
        {
            var size = new NativeSize { Width = Width, Height = Height };
            var origin = new NativePoint();
            var blend = new BlendFunction { SourceConstantAlpha = _alpha, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(hbitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Render();
    }

    // A layered window updated this way never paints through WM_PAINT.
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>The rounded glass every overlay sits on.</summary>
    protected void FillGlass(Graphics g, int radius)
    {
        using var path = FlatButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), radius);
        using (var fill = new SolidBrush(Glass)) g.FillPath(fill, path);
        using var edge = new Pen(GlassEdge);
        g.DrawPath(edge, path);
    }

    protected static SizeF Measure(string text, Font font)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g.MeasureString(text, font);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hideTimer.Dispose();
        base.Dispose(disposing);
    }

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int ULW_ALPHA = 2;
    private const byte AC_SRC_ALPHA = 1;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref NativeSize psize,
        IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}

/// <summary>Volume readout that appears on change and goes away by itself.</summary>
internal sealed class VolumeOverlay : Overlay
{
    private float _volume = 1f;
    private bool _muted;

    public VolumeOverlay() => Size = new Size(236, 44);

    public void Show(float volume, bool muted)
    {
        _volume = volume;
        _muted = muted;
        Flash(1500);
    }

    protected override void Draw(Graphics g)
    {
        FillGlass(g, Height / 2);
        using (var text = new SolidBrush(Theme.Text))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center })
            g.DrawString(_muted ? "Muted" : $"{_volume * 100:0}%", Theme.BodyBold, text, new RectangleF(20, 0, 64, Height), format);

        var track = new Rectangle(86, Height / 2 - 2, Width - 86 - 22, 4);
        using (var path = FlatButton.Rounded(track, 2))
        using (var back = new SolidBrush(Theme.Track))
            g.FillPath(back, path);
        int filled = _muted ? 0 : (int)(track.Width * Math.Clamp(_volume / 2f, 0f, 1f));
        if (filled < 4) return;
        using (var path = FlatButton.Rounded(new Rectangle(track.X, track.Y, filled, track.Height), 2))
        using (var fill = new SolidBrush(Theme.Accent))
            g.FillPath(fill, path);
    }
}

/// <summary>Transient message pill for status and errors, sized to its text.</summary>
internal sealed class ToastOverlay : Overlay
{
    private const int PillHeight = 38;
    private string _message = "";
    private bool _isError;

    /// <summary>Set by the shell's layout, so a long message never runs past the window.</summary>
    public int MaxWidth { get; set; } = 560;

    public ToastOverlay() => Size = new Size(200, PillHeight);

    public void Show(string message, bool isError)
    {
        _message = message;
        _isError = isError;
        // Text box is inset 33 left and 18 right in Draw, plus slack for MeasureString's rounding.
        int width = (int)Math.Ceiling(Measure(message, Theme.Body).Width) + 33 + 18 + 10;
        Size = new Size(Math.Clamp(width, 140, Math.Max(140, MaxWidth)), PillHeight);
        Flash(isError ? 8000 : 3500);
    }

    protected override void Draw(Graphics g)
    {
        FillGlass(g, Height / 2);
        using (var dot = new SolidBrush(_isError ? Theme.Record : Theme.Accent))
            g.FillEllipse(dot, 17, Height / 2f - 4, 8, 8);
        using var brush = new SolidBrush(_isError ? Color.FromArgb(255, 176, 168) : Theme.Text);
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(_message, Theme.Body, brush, new RectangleF(33, 0, Width - 33 - 18, Height), format);
    }
}

/// <summary>Optional always-on readout of what the pipeline is doing.</summary>
internal sealed class StatsOverlay : Overlay
{
    private string _line1 = "";
    private string _line2 = "";

    public StatsOverlay() => Size = new Size(250, 52);

    public void Update(string line1, string line2)
    {
        _line1 = line1;
        _line2 = line2;
        // Grows to fit, keeping its right edge where the layout put it.
        int width = (int)Math.Ceiling(Math.Max(Measure(line1, Theme.Mono).Width, Measure(line2, Theme.Mono).Width)) + 28;
        if (width > Width)
        {
            int right = Right;
            Width = width;
            Left = right - width;
        }
        if (Visible) Render();
    }

    public void SetVisible(bool visible)
    {
        if (visible) Flash(0);
        else Hide();
    }

    protected override void Draw(Graphics g)
    {
        FillGlass(g, 12);
        using var bright = new SolidBrush(Theme.Text);
        using var dim = new SolidBrush(Theme.TextDim);
        g.DrawString(_line1, Theme.Mono, bright, 14, 9);
        g.DrawString(_line2, Theme.Mono, dim, 14, 27);
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
        Size = new Size(128, 36);
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; Render(); };
    }

    public void Start()
    {
        _blinkOn = true;
        _blink.Start();
        Flash(0);
    }

    public void Stop()
    {
        _blink.Stop();
        Hide();
    }

    public void Update(TimeSpan elapsed)
    {
        _elapsed = elapsed;
        if (Visible) Render();
    }

    protected override void Draw(Graphics g)
    {
        FillGlass(g, Height / 2);
        if (_blinkOn)
            using (var dot = new SolidBrush(Theme.Record))
                g.FillEllipse(dot, 16, Height / 2f - 5, 10, 10);
        using var text = new SolidBrush(Theme.Text);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString(_elapsed.ToString(@"hh\:mm\:ss"), Theme.Readout, text, new RectangleF(33, 0, Width - 33, Height), format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _blink.Dispose();
        base.Dispose(disposing);
    }
}
