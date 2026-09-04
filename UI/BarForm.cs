using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// The top-left icon bar: capture, record, keys, settings, about. A borderless owned
/// window rather than a child control, so its fade can use plain <see cref="Form.Opacity"/> -
/// a child control sitting over the D3D swap chain has no opacity that composites onto it,
/// which is why the rest of the shell animates by moving instead (see MainForm).
/// </summary>
internal sealed class BarForm : Form
{
    private static readonly (Page Page, Image Icon)[] Items =
    {
        (Page.Capture, Assets.IconCapture),
        (Page.Record, Assets.IconRecording),
        (Page.Shortcuts, Assets.IconKeyboard),
        (Page.Settings, Assets.IconGeneral),
        (Page.About, Assets.IconAbout),
    };

    // Ratios measured from the supplied artwork: a 599x104 bar holding five 90x90 icons on
    // a 120px pitch, 14px in from the left edge. Keeping them as ratios of one chosen height
    // means the whole bar can be resized later without the icons drifting off their spacing.
    private const int BarHeight = 60;
    private const int IconSize = 52;
    private const int Pitch = 69;
    private const int LeftInset = 8;
    private const int TopInset = 4;
    private static readonly int BarWidth = LeftInset * 2 + (Items.Length - 1) * Pitch + IconSize;

    /// <summary>The icons render a touch smaller than their slot (<see cref="IconSize"/>),
    /// centered within it, so the slot's position/hit area for clicks and hover is untouched.</summary>
    private const int IconDrawShrink = 3;

    /// <summary>How much larger a hovered or active icon draws, relative to its resting size.</summary>
    private const float HoverScale = 1.12f;
    private const float ActiveScale = 1.09f;

    private readonly float[] _scale;
    private int _hovered = -1;
    private Page? _active;
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 15 };

    public event EventHandler<Page>? PageSelected;

    public static Size DesiredSize => new(BarWidth, BarHeight);

    public BarForm()
    {
        _scale = new float[Items.Length];
        for (int i = 0; i < _scale.Length; i++) _scale[i] = 1f;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Theme.Shell;
        Size = DesiredSize;
        Cursor = Cursors.Default;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        _pulse.Tick += (_, _) => Animate();
    }

    /// <summary>The page whose window is currently open, so its icon stays enlarged.</summary>
    public Page? ActivePage
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            StartPulse();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || !IsHandleCreated) return;
        using var path = FlatButton.Rounded(new Rectangle(0, 0, Width, Height), Height / 2);
        Region?.Dispose();
        Region = new Region(path);
    }

    private Rectangle IconBounds(int index) => new(LeftInset + index * Pitch, TopInset, IconSize, IconSize);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Theme.Shell);

        for (int i = 0; i < Items.Length; i++)
        {
            var bounds = IconBounds(i);
            float s = _scale[i];
            int baseSize = IconSize - IconDrawShrink;
            int w = (int)Math.Round(baseSize * s);
            int h = (int)Math.Round(baseSize * s);
            var rect = new Rectangle(
                bounds.X + (bounds.Width - w) / 2,
                bounds.Y + (bounds.Height - h) / 2,
                w, h);
            g.DrawImage(Items[i].Icon, rect);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit == _hovered) return;
        _hovered = hit;
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        StartPulse();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered == -1) return;
        _hovered = -1;
        Cursor = Cursors.Default;
        StartPulse();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int hit = HitTest(e.Location);
        if (hit < 0) return;
        var page = Items[hit].Page;
        // Raising this straight from here means MainForm creates/shows/activates the panel
        // window while Windows is still mid-dispatch of this very click on the bar - and the
        // very first time that panel window is shown, that nesting is enough to leave it
        // internally "open" (the bar's icon already reflects it) but not actually on screen,
        // needing an unrelated second click to actually surface it. Posting this as a fresh
        // message instead lets the click finish its own round trip first, so the panel's
        // first-ever show always runs from a clean, non-nested dispatch.
        BeginInvoke(() => PageSelected?.Invoke(this, page));
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < Items.Length; i++)
            if (IconBounds(i).Contains(point)) return i;
        return -1;
    }

    private void StartPulse()
    {
        if (!_pulse.Enabled) _pulse.Start();
    }

    /// <summary>Eases every icon's scale toward its target, stopping itself once settled so a
    /// resting bar costs nothing.</summary>
    private void Animate()
    {
        bool moving = false;
        for (int i = 0; i < Items.Length; i++)
        {
            float target = i == _hovered ? HoverScale
                : Items[i].Page == _active ? ActiveScale
                : 1f;
            float current = _scale[i];
            float next = current + (target - current) * 0.35f;
            if (Math.Abs(target - next) < 0.002f) next = target;
            else moving = true;
            _scale[i] = next;
        }
        Invalidate();
        if (!moving) _pulse.Stop();
    }

    /// <summary>
    /// This bar is a separate top-level window, so keystrokes typed while it has focus would
    /// otherwise never reach MainForm's shortcut table. Forwarding them here keeps every
    /// hotkey working no matter which of the shell's windows the user last clicked into.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Owner is MainForm main && main.DispatchShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;
    private const int MA_ACTIVATEANDEAT = 2;

    /// <summary>
    /// The _bar.Activate() call in MainForm (see SetMenuOpen/OnLoad) exists so a click on an
    /// icon right after the menu opens lands as a real click rather than just an activation.
    /// That works reliably once this window's handle has been shown before, but the very
    /// first time it's ever created and shown in a run, the OS doesn't reliably finish
    /// activating it before the user's very next click arrives - and the default window proc
    /// answers that click's WM_MOUSEACTIVATE with MA_ACTIVATEANDEAT (activate, but discard the
    /// click), so it takes a second click to actually hit an icon. Forcing MA_ACTIVATE instead
    /// means the activating click is never discarded, regardless of whether Activate() already
    /// won that race - so the first icon click always registers, first launch included.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_MOUSEACTIVATE && m.Result == (IntPtr)MA_ACTIVATEANDEAT)
            m.Result = (IntPtr)MA_ACTIVATE;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pulse.Dispose();
        base.Dispose(disposing);
    }
}
