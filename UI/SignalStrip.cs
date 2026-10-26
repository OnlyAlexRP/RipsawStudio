using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// The top edge of the control window: one line icon per category with its name under it on
/// the left, and the record, replay and screenshot actions on the right, so they are one click
/// away from any page. Icons are drawn rather than loaded, so they stay sharp at any scale and
/// take the same grey, white and orange as the rest of the panel.
/// </summary>
internal sealed class NavBar : Control
{
    private static readonly Category[] Items = Pages.Categories.Select(c => c.Category).ToArray();

    private const int LeftInset = 12;
    private const int MinItemWidth = 74;
    private const int GlyphSize = 24;
    private const int GlyphTop = 14;
    private const int LabelTop = 45;
    private const float HoverScale = 1.1f;

    /// <summary>0 at rest, 1 when lit (hovered or open), eased so the colours fade rather than snap.</summary>
    private readonly float[] _light = new float[Items.Length];
    private readonly float[] _scale = new float[Items.Length];
    private int[] _x = new int[Items.Length + 1];
    private int _hovered = -1;
    private Category _active = Category.Capture;
    private string _profile = "";
    private bool _recording;
    private bool _saving;
    /// <summary>Icons only, when the names would run into the buttons on the right.</summary>
    private bool _compact;
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 15 };
    private readonly ToolTip _tip = new();

    public FlatButton Record { get; } = new() { ButtonRole = FlatButton.Role.Normal, Glyph = Icon.Record };
    public FlatButton SaveReplay { get; } = new() { Glyph = Icon.Rewind };
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
            _light[i] = Items[i] == _active ? 1f : 0f;
            _scale[i] = 1f;
        }
        Controls.AddRange(new Control[] { Record, SaveReplay, Snapshot, Close });
        _pulse.Tick += (_, _) => Animate();
        ApplyTexts();
    }

    public void ApplyTexts()
    {
        _tip.SetToolTip(Snapshot, Tr("Screenshot"));
        _tip.SetToolTip(Close, Tr("Close (Esc)"));
        SaveReplay.Text = _saving ? Tr("Saving...") : Tr("Save replay");
        if (!_recording) Record.Text = Tr("Record");
        LayoutButtons();
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
        Record.Text = recording ? Tr("Stop") + $"  {elapsed:hh\\:mm\\:ss}" : Tr("Record");
        Record.Invalidate();
    }

    public void SetReplayAvailable(bool armed, bool saving)
    {
        SaveReplay.Enabled = armed && !saving;
        if (_saving == saving) return;
        _saving = saving;
        SaveReplay.Text = saving ? Tr("Saving...") : Tr("Save replay");
        LayoutButtons();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutButtons();
    }

    /// <summary>Each item is as wide as its name needs, so longer languages never clip.</summary>
    private void LayoutItems()
    {
        _x = new int[Items.Length + 1];
        _x[0] = LeftInset;
        for (int i = 0; i < Items.Length; i++)
        {
            int label = TextRenderer.MeasureText(Label(i), Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
            _x[i + 1] = _x[i] + Math.Max(MinItemWidth, label + 22);
        }
        _compact = _x[Items.Length] > Record.Left - 24;
        if (_compact)
            for (int i = 0; i < Items.Length; i++) _x[i + 1] = _x[i] + 56;
        Invalidate();
    }

    private int GlyphY => _compact ? (Height - GlyphSize) / 2 : GlyphTop;

    private static string Label(int index) => Tr(Pages.Categories[index].Label);

    private void LayoutButtons()
    {
        int h = 34, y = (Height - h) / 2, right = Width - 16;
        Close.SetBounds(right - 34, y, 34, h);
        right -= 34 + 14;
        Snapshot.SetBounds(right - 40, y, 40, h);
        right -= 40 + 8;
        // Sized to the words, which are longer in some languages; the recording timer is
        // measured at its widest so the button does not twitch every second.
        int replayWidth = Math.Max(124, ButtonWidth(SaveReplay, SaveReplay.Text));
        SaveReplay.SetBounds(right - replayWidth, y, replayWidth, h);
        right -= replayWidth + 8;
        int recordWidth = Math.Max(_recording ? 136 : 104,
            ButtonWidth(Record, _recording ? Tr("Stop") + "  00:00:00" : Tr("Record")));
        Record.SetBounds(right - recordWidth, y, recordWidth, h);
        LayoutItems();
    }

    private static int ButtonWidth(FlatButton button, string text) =>
        TextRenderer.MeasureText(text, button.Font).Width + 52;

    private Rectangle ItemBounds(int index) => new(_x[index], 0, _x[index + 1] - _x[index], Height - 1);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Rail);
        using (var line = new Pen(Theme.Line))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        for (int i = 0; i < Items.Length; i++)
        {
            var slot = ItemBounds(i);
            bool active = Items[i] == _active;
            Color ink = Blend(Theme.TextDim, Theme.Text, _light[i]);
            Color glyph = active ? Theme.Accent : ink;

            // A soft plate under the open category, in the accent at low strength.
            if (active)
            {
                var plate = new Rectangle(slot.X + slot.Width / 2 - 20, GlyphY - 7, 40, GlyphSize + 14);
                using var path = FlatButton.Rounded(plate, 9);
                using var fill = new SolidBrush(Color.FromArgb(38, Theme.Accent));
                g.FillPath(fill, path);
            }

            float size = GlyphSize * _scale[i];
            var box = new RectangleF(slot.X + (slot.Width - size) / 2f, GlyphY + (GlyphSize - size) / 2f, size, size);
            DrawGlyph(g, Items[i], box, glyph);

            if (!_compact)
                TextRenderer.DrawText(g, Label(i), Theme.Small, new Rectangle(slot.X, LabelTop, slot.Width, 18),
                active ? Theme.Text : ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            if (active)
            {
                var mark = new Rectangle(slot.X + slot.Width / 2 - 12, Height - 4, 24, 3);
                using var path = FlatButton.Rounded(mark, 1);
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, path);
            }
        }

        if (_profile.Length > 0)
        {
            int left = _x[Items.Length] + 20;
            int room = Record.Left - 16 - left;
            if (room > 80)
                TextRenderer.DrawText(g, _profile, Theme.Small, new Rectangle(left, 0, room, Height), Theme.TextFaint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>The category icons, as 2 px line drawings on a 24 px grid.</summary>
    private static void DrawGlyph(Graphics g, Category category, RectangleF r, Color colour)
    {
        float u = r.Width / 24f;
        PointF P(float x, float y) => new(r.X + x * u, r.Y + y * u);
        RectangleF R(float x, float y, float w, float h) => new(r.X + x * u, r.Y + y * u, w * u, h * u);

        using var pen = new Pen(colour, 2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(colour);

        switch (category)
        {
            case Category.Capture:
                // A screen with a live signal on it.
                using (var screen = RoundedF(R(2, 3.5f, 20, 13.5f), 2.5f * u)) g.DrawPath(pen, screen);
                g.DrawLine(pen, P(8, 21), P(16, 21));
                g.DrawLine(pen, P(12, 17), P(12, 21));
                g.DrawLines(pen, new[] { P(6, 12.5f), P(9, 9.5f), P(11.5f, 11.5f), P(14.5f, 7.5f), P(18, 11) });
                break;

            case Category.Recording:
                g.DrawEllipse(pen, R(2.5f, 2.5f, 19, 19));
                g.FillEllipse(brush, R(7.5f, 7.5f, 9, 9));
                break;

            case Category.Library:
                // A frame behind a frame, with a play mark: a stack of clips.
                using (var back = new GraphicsPath())
                {
                    back.AddLines(new[] { P(7, 5.5f), P(7, 4.5f) });
                    back.AddArc(R(7, 2.5f, 4, 4), 180, 90);
                    back.AddLine(P(9, 2.5f), P(20, 2.5f));
                    back.AddArc(R(18, 2.5f, 4, 4), 270, 90);
                    back.AddLine(P(22, 4.5f), P(22, 14));
                    back.AddArc(R(18, 12, 4, 4), 0, 90);
                    back.AddLine(P(20, 16), P(19, 16));
                    g.DrawPath(pen, back);
                }
                using (var front = RoundedF(R(2, 8, 15, 13.5f), 2.5f * u)) g.DrawPath(pen, front);
                g.FillPolygon(brush, new[] { P(7.8f, 11.6f), P(7.8f, 17.9f), P(12.6f, 14.75f) });
                break;

            case Category.Keys:
                using (var board = RoundedF(R(1.5f, 5.5f, 21, 13), 2.5f * u)) g.DrawPath(pen, board);
                foreach (float x in new[] { 6f, 10f, 14f, 18f })
                    g.FillEllipse(brush, R(x - 1.1f, 9 - 1.1f, 2.2f, 2.2f));
                g.DrawLine(pen, P(8, 14.5f), P(16, 14.5f));
                break;

            case Category.Settings:
            {
                // Eight teeth around a ring, as one outline.
                var points = new List<PointF>();
                for (int t = 0; t < 8; t++)
                {
                    double a = t * Math.PI / 4;
                    foreach (var (da, radius) in new[] { (-0.42, 7.2), (-0.2, 10.2), (0.2, 10.2), (0.42, 7.2) })
                        points.Add(P(12 + (float)(Math.Cos(a + da) * radius), 12 + (float)(Math.Sin(a + da) * radius)));
                }
                g.DrawPolygon(pen, points.ToArray());
                g.DrawEllipse(pen, R(8.75f, 8.75f, 6.5f, 6.5f));
                break;
            }

            case Category.About:
                g.DrawEllipse(pen, R(2.5f, 2.5f, 19, 19));
                g.FillEllipse(brush, R(10.75f, 6.4f, 2.5f, 2.5f));
                g.DrawLine(pen, P(12, 11), P(12, 17));
                break;
        }
    }

    private static GraphicsPath RoundedF(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit == _hovered) return;
        _hovered = hit;
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        _tip.SetToolTip(this, _compact && hit >= 0 ? Label(hit) : null);
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
        Active = Items[hit];
        CategorySelected?.Invoke(this, Items[hit]);
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < Items.Length; i++)
            if (ItemBounds(i).Contains(point)) return i;
        return -1;
    }

    private void StartPulse()
    {
        if (!_pulse.Enabled) _pulse.Start();
    }

    /// <summary>Eases every item toward its target, stopping once settled so a resting bar costs nothing.</summary>
    private void Animate()
    {
        bool moving = false;
        for (int i = 0; i < Items.Length; i++)
        {
            bool lit = Items[i] == _active || i == _hovered;
            moving |= Ease(ref _light[i], lit ? 1f : 0f);
            moving |= Ease(ref _scale[i], i == _hovered ? HoverScale : 1f);
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
            State.Live => Tr("ON"),
            State.NoSignal => Tr("NO SIGNAL"),
            _ => Tr("OFF"),
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
                parts.Add((Tr("ms lag"), Theme.ReadoutSmall, Theme.TextDim, 18, 2));
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
