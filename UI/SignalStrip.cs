using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RipsawStudio.UI;

/// <summary>
/// The top edge of the control window: one icon per category on the left - the app's own
/// artwork - and the record, replay and screenshot actions on the right, so they are one
/// click away from any page.
/// </summary>
internal sealed class NavBar : Control
{
    private static readonly (Category Category, Image? Art, Icon Fallback)[] Items =
    {
        (Category.Capture, Assets.IconCapture, Icon.Monitor),
        (Category.Recording, Assets.IconRecording, Icon.Record),
        (Category.Keys, Assets.IconKeyboard, Icon.Keyboard),
        (Category.Settings, Assets.IconGeneral, Icon.Gear),
        (Category.About, Assets.IconAbout, Icon.Info),
    };

    // The artwork is 90x90 tiles drawn for a 599x104 bar on a 120px pitch; these keep the
    // same proportions at the bar's height.
    private const int IconSize = 52;
    private const int Pitch = 66;
    private const int LeftInset = 16;

    /// <summary>How much larger a hovered or active icon draws, relative to its resting size.</summary>
    private const float HoverScale = 1.12f;
    private const float ActiveScale = 1.08f;
    /// <summary>Icons other than the open category are dimmed, so the open one reads at a glance.</summary>
    private const float RestingAlpha = 0.5f;

    private readonly float[] _scale = new float[Items.Length];
    private readonly float[] _alpha = new float[Items.Length];
    private int _hovered = -1;
    private Category _active = Category.Capture;
    private string _profile = "";
    private bool _recording;
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 15 };
    private readonly ToolTip _tip = new();

    public FlatButton Record { get; } = new() { ButtonRole = FlatButton.Role.Normal, Glyph = Icon.Record, Text = "Record" };
    public FlatButton SaveReplay { get; } = new() { Glyph = Icon.Rewind, Text = "Save replay" };
    public FlatButton Snapshot { get; } = new() { Glyph = Icon.Camera, Text = "" };
    public FlatButton Close { get; } = new() { ButtonRole = FlatButton.Role.Ghost, Text = "✕" };

    public event EventHandler<Category>? CategorySelected;

    public NavBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;
        for (int i = 0; i < Items.Length; i++)
        {
            _scale[i] = Items[i].Category == _active ? ActiveScale : 1f;
            _alpha[i] = Items[i].Category == _active ? 1f : RestingAlpha;
        }
        Controls.AddRange(new Control[] { Record, SaveReplay, Snapshot, Close });
        _tip.SetToolTip(Snapshot, "Screenshot");
        _tip.SetToolTip(Close, "Close (Esc)");
        _pulse.Tick += (_, _) => Animate();
    }

    public Category Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            StartPulse();
        }
    }

    /// <summary>The active profile, in small print beside the actions.</summary>
    public string Profile
    {
        get => _profile;
        set { _profile = value; Invalidate(); }
    }

    public void SetRecording(bool recording, TimeSpan elapsed)
    {
        if (recording != _recording)
        {
            _recording = recording;
            Record.ButtonRole = recording ? FlatButton.Role.Danger : FlatButton.Role.Normal;
            Record.Glyph = recording ? Icon.Stop : Icon.Record;
            LayoutButtons();
        }
        Record.Text = recording ? $"Stop  {elapsed:hh\\:mm\\:ss}" : "Record";
        Record.Invalidate();
    }

    public void SetReplayAvailable(bool armed, bool saving)
    {
        SaveReplay.Enabled = armed && !saving;
        SaveReplay.Text = saving ? "Saving..." : "Save replay";
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutButtons();
    }

    private void LayoutButtons()
    {
        int h = 34, y = (Height - h) / 2, right = Width - 16;
        Close.SetBounds(right - 34, y, 34, h);
        right -= 34 + 14;
        Snapshot.SetBounds(right - 40, y, 40, h);
        right -= 40 + 8;
        SaveReplay.SetBounds(right - 124, y, 124, h);
        right -= 124 + 8;
        int recordWidth = _recording ? 136 : 104;
        Record.SetBounds(right - recordWidth, y, recordWidth, h);
        Invalidate();
    }

    private Rectangle IconBounds(int index) =>
        new(LeftInset + index * Pitch, (Height - IconSize) / 2 - 2, IconSize, IconSize);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Theme.Rail);
        using (var line = new Pen(Theme.Line))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        for (int i = 0; i < Items.Length; i++)
        {
            var slot = IconBounds(i);
            int size = (int)Math.Round((IconSize - 3) * _scale[i]);
            var rect = new Rectangle(slot.X + (slot.Width - size) / 2, slot.Y + (slot.Height - size) / 2, size, size);
            DrawIcon(g, Items[i], rect, _alpha[i]);

            if (Items[i].Category == _active)
            {
                var mark = new Rectangle(slot.X + slot.Width / 2 - 10, Height - 7, 20, 3);
                using var path = FlatButton.Rounded(mark, 1);
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, path);
            }
        }

        if (_profile.Length > 0)
        {
            int left = IconBounds(Items.Length - 1).Right + 24;
            int room = Record.Left - 16 - left;
            if (room > 80)
                TextRenderer.DrawText(g, _profile, Theme.Small, new Rectangle(left, 0, room, Height), Theme.TextFaint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private static void DrawIcon(Graphics g, (Category Category, Image? Art, Icon Fallback) item, Rectangle rect, float alpha)
    {
        if (item.Art is { } art)
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
            g.DrawImage(art, rect, 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, attributes);
            return;
        }
        // No artwork embedded: a tile in the same spirit, so the bar still works.
        using (var path = FlatButton.Rounded(Rectangle.Inflate(rect, -3, -3), 12))
        using (var fill = new SolidBrush(Theme.Field))
            g.FillPath(fill, path);
        var glyph = Color.FromArgb((int)(255 * alpha), Theme.Accent);
        Icons.Draw(g, item.Fallback, RectangleF.Inflate(rect, -rect.Width * 0.27f, -rect.Height * 0.27f), glyph, 2.2f);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit == _hovered) return;
        _hovered = hit;
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        _tip.SetToolTip(this, hit >= 0 ? Pages.Categories[hit].Label : null);
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
        Active = Items[hit].Category;
        CategorySelected?.Invoke(this, Items[hit].Category);
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

    /// <summary>Eases every icon toward its target size and brightness, stopping itself once
    /// settled so a resting bar costs nothing.</summary>
    private void Animate()
    {
        bool moving = false;
        for (int i = 0; i < Items.Length; i++)
        {
            bool active = Items[i].Category == _active;
            float scale = i == _hovered ? HoverScale : active ? ActiveScale : 1f;
            float alpha = active || i == _hovered ? 1f : RestingAlpha;
            moving |= Ease(ref _scale[i], scale);
            moving |= Ease(ref _alpha[i], alpha);
        }
        Invalidate();
        if (!moving) _pulse.Stop();
    }

    private static bool Ease(ref float value, float target)
    {
        float next = value + (target - value) * 0.35f;
        if (Math.Abs(target - next) < 0.002f) { value = target; return false; }
        value = next;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulse.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Under the navigation bar: the open category's pages as tabs on the left, and on the right
/// the signal read like a monitor's status line - what the card is sending, how fast, how late,
/// and whether anything is being recorded.
/// </summary>
internal sealed class SignalStrip : Control
{
    public enum State { Stopped, Live, NoSignal }

    private const int TabPad = 14;
    private const int TabLeft = 22;

    private State _state = State.Stopped;
    private string _resolution = "";
    private string _fps = "";
    private string _lag = "";
    private string _detail = "";
    private bool _recording;
    private bool _blink = true;
    private readonly System.Windows.Forms.Timer _blinkTimer = new() { Interval = 600 };

    private (Page Page, string Label)[] _tabs = Array.Empty<(Page, string)>();
    private Page _selected;
    private int _hovered = -1;

    public event EventHandler<Page>? TabSelected;

    public SignalStrip()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;
        _blinkTimer.Tick += (_, _) => { _blink = !_blink; Invalidate(); };
    }

    public void SetTabs((Page Page, string Label)[] tabs, Page selected)
    {
        _tabs = tabs;
        _selected = selected;
        _hovered = -1;
        Invalidate();
    }

    public void SetSignal(State state, string resolution, string fps, string lag, string detail)
    {
        if (_state == state && _resolution == resolution && _fps == fps && _lag == lag && _detail == detail) return;
        _state = state;
        _resolution = resolution;
        _fps = fps;
        _lag = lag;
        _detail = detail;
        Invalidate();
    }

    public void SetRecording(bool recording)
    {
        if (recording == _recording) return;
        _recording = recording;
        if (recording) _blinkTimer.Start(); else { _blinkTimer.Stop(); _blink = true; }
        Invalidate();
    }

    private Rectangle TabBounds(int index)
    {
        int x = TabLeft;
        for (int i = 0; i < _tabs.Length; i++)
        {
            int w = TextRenderer.MeasureText(_tabs[i].Label, Theme.Nav, Size.Empty, TextFormatFlags.NoPadding).Width + TabPad * 2;
            if (i == index) return new Rectangle(x, 0, w, Height);
            x += w;
        }
        return Rectangle.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Rail);
        using (var line = new Pen(Theme.Line))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        int tabsRight = TabLeft;
        for (int i = 0; i < _tabs.Length; i++)
        {
            var r = TabBounds(i);
            bool selected = _tabs[i].Page == _selected;
            TextRenderer.DrawText(g, _tabs[i].Label, Theme.Nav, r,
                selected || i == _hovered ? Theme.Text : Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (selected)
                using (var bar = new SolidBrush(Theme.Accent))
                    g.FillRectangle(bar, r.X + TabPad, Height - 3, r.Width - TabPad * 2, 3);
            tabsRight = r.Right;
        }

        DrawSignal(g, tabsRight + 24);
    }

    /// <summary>Lays the readouts out from the right edge in, then gives the detail whatever is left.</summary>
    private void DrawSignal(Graphics g, int leftLimit)
    {
        var parts = new List<(string Text, Font Font, Color Colour, int Gap, int Lift)>();
        Color lamp = _recording ? Theme.Record : _state switch
        {
            State.Live => Theme.Good,
            State.NoSignal => Theme.Warning,
            _ => Theme.TextFaint,
        };
        string word = _recording ? "REC" : _state switch
        {
            State.Live => "ON",
            State.NoSignal => "NO SIGNAL",
            _ => "OFF",
        };
        parts.Add((word, Theme.ReadoutSmall, lamp, 22, 0));
        if (_state != State.Stopped && _resolution.Length > 0)
        {
            parts.Add((_resolution, Theme.Readout, Theme.Text, 18, 0));
            if (_fps.Length > 0)
            {
                parts.Add((_fps, Theme.Readout, Theme.Text, 4, 0));
                parts.Add(("fps", Theme.ReadoutSmall, Theme.TextDim, 18, 2));
            }
            if (_lag.Length > 0)
            {
                parts.Add((_lag, Theme.Readout, Theme.Text, 4, 0));
                parts.Add(("ms lag", Theme.ReadoutSmall, Theme.TextDim, 18, 2));
            }
        }

        var widths = parts.Select(p => TextRenderer.MeasureText(g, p.Text, p.Font, Size.Empty, TextFormatFlags.NoPadding).Width).ToArray();
        int total = 18;   // the lamp
        for (int i = 0; i < parts.Count; i++) total += widths[i] + (i < parts.Count - 1 ? parts[i].Gap : 0);
        int x = Width - 22 - total;
        int cy = Height / 2;

        if (_detail.Length > 0 && x - 20 - leftLimit > 60)
            TextRenderer.DrawText(g, _detail, Theme.Small, new Rectangle(leftLimit, 0, x - 20 - leftLimit, Height), Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (!_recording || _blink)
            using (var dot = new SolidBrush(lamp))
                g.FillEllipse(dot, x, cy - 5, 10, 10);
        x += 18;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var size = TextRenderer.MeasureText(g, p.Text, p.Font, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, p.Text, p.Font, new Point(x, cy - size.Height / 2 + p.Lift), p.Colour, TextFormatFlags.NoPadding);
            x += widths[i] + p.Gap;
        }
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _tabs.Length; i++)
            if (TabBounds(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit == _hovered) return;
        _hovered = hit;
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { _hovered = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit < 0) return;
        _selected = _tabs[hit].Page;
        Invalidate();
        TabSelected?.Invoke(this, _selected);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _blinkTimer.Dispose();
        base.Dispose(disposing);
    }
}
