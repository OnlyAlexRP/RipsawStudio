using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// Owner-drawn slider. Replaces WinForms' TrackBar, which is a Win32 common control that
/// ignores the dark palette, refuses to lay out below its preferred height, and paints
/// outside its own bounds when forced smaller.
/// </summary>
internal sealed class Slider : Control
{
    private int _value;
    private bool _dragging;
    private bool _hovered;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    /// <summary>Fills outward from the centre rather than from the left, for +/- values.</summary>
    public bool Bipolar { get; set; }
    public int SmallStep { get; set; } = 1;

    public event EventHandler? ValueChanged;

    public Slider()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Height = 22;
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the value without raising ValueChanged, for binding from settings.</summary>
    public void SetValueSilently(int value)
    {
        _value = Math.Clamp(value, Minimum, Maximum);
        Invalidate();
    }

    private const int ThumbRadius = 6;
    private Rectangle TrackRect => new(ThumbRadius, Height / 2 - 2, Math.Max(1, Width - ThumbRadius * 2), 4);

    private float Fraction => Maximum <= Minimum ? 0f : (float)(_value - Minimum) / (Maximum - Minimum);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = TrackRect;
        using (var back = new SolidBrush(Theme.Track))
            g.FillRectangle(back, track);

        int thumbX = track.X + (int)Math.Round(track.Width * Fraction);
        using (var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.FieldBorder))
        {
            if (Bipolar)
            {
                int centre = track.X + track.Width / 2;
                var span = Rectangle.FromLTRB(Math.Min(centre, thumbX), track.Y, Math.Max(centre, thumbX), track.Bottom);
                if (span.Width > 0) g.FillRectangle(fill, span);
                using var tick = new SolidBrush(Theme.FieldBorder);
                g.FillRectangle(tick, centre - 1, track.Y - 3, 2, track.Height + 6);
            }
            else
            {
                g.FillRectangle(fill, track.X, track.Y, thumbX - track.X, track.Height);
            }
        }

        int r = _hovered || _dragging ? ThumbRadius + 1 : ThumbRadius;
        var thumb = new Rectangle(thumbX - r, Height / 2 - r, r * 2, r * 2);
        using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            g.FillEllipse(shadow, thumb.X, thumb.Y + 1, thumb.Width, thumb.Height);
        using (var brush = new SolidBrush(Enabled ? Color.White : Theme.TextDim))
            g.FillEllipse(brush, thumb);
        if (Focused)
        {
            using var pen = new Pen(Theme.Accent, 2f);
            g.DrawEllipse(pen, thumb);
        }
    }

    private void SetFromMouse(int x)
    {
        var track = TrackRect;
        float fraction = Math.Clamp((x - track.X) / (float)track.Width, 0f, 1f);
        Value = Minimum + (int)Math.Round(fraction * (Maximum - Minimum));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        SetFromMouse(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromMouse(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) => Invalidate();
    protected override void OnLostFocus(EventArgs e) => Invalidate();

    protected override void OnMouseWheel(MouseEventArgs e) =>
        Value += Math.Sign(e.Delta) * SmallStep;

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left: Value -= SmallStep; e.Handled = true; break;
            case Keys.Right: Value += SmallStep; e.Handled = true; break;
            case Keys.Home: Value = Minimum; e.Handled = true; break;
            case Keys.End: Value = Maximum; e.Handled = true; break;
        }
    }
}

/// <summary>
/// A ComboBox that actually matches the rest of the window. WinForms leaves the drop-down
/// button to the system even in flat mode, which is the pale square that looks out of place
/// on a dark panel, so it is painted over after the control draws itself.
/// </summary>
internal sealed class FlatCombo : ComboBox
{
    private const int WM_PAINT = 0x000F;
    private bool _hovered;

    public FlatCombo()
    {
        FlatStyle = FlatStyle.Flat;
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        ItemHeight = 19;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;

        using (var back = new SolidBrush(selected && inList ? Theme.Accent : Theme.Field))
            e.Graphics.FillRectangle(back, e.Bounds);

        using var text = new SolidBrush(selected && inList ? Color.White : Theme.Text);
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var bounds = new RectangleF(e.Bounds.X + 5, e.Bounds.Y, e.Bounds.Width - (inList ? 10 : 26), e.Bounds.Height);
        e.Graphics.DrawString(Items[e.Index]?.ToString() ?? "", e.Font ?? Font, text, bounds, format);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnSelectedIndexChanged(EventArgs e) { Invalidate(); base.OnSelectedIndexChanged(e); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT) return;

        using var g = Graphics.FromHwnd(Handle);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Cover the system drop-down button, then draw our own chevron.
        var button = new Rectangle(Width - 20, 1, 19, Height - 2);
        using (var back = new SolidBrush(Theme.Field))
            g.FillRectangle(back, button);

        using (var pen = new Pen(Enabled ? Theme.TextDim : Theme.FieldBorder, 1.6f))
        {
            int cx = button.X + button.Width / 2;
            int cy = Height / 2 - 1;
            g.DrawLines(pen, new[] { new Point(cx - 4, cy - 1), new Point(cx, cy + 3), new Point(cx + 4, cy - 1) });
        }

        using (var border = new Pen(_hovered && Enabled ? Theme.FieldBorder : Theme.FieldBorder))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// Numeric field drawn from scratch. WinForms' NumericUpDown hosts a Win32 up-down control
/// whose buttons cannot be themed and are laid over the text box rather than beside it,
/// which is what clipped the digits and left a pale spinner on a dark card.
/// </summary>
internal sealed class NumericField : Control
{
    private const int ButtonWidth = 20;

    private readonly TextBox _text = new();
    private readonly System.Windows.Forms.Timer _repeat = new();
    private int _value;
    private int _repeatDirection;
    private int _hoverButton;      // -1 none, 0 up, 1 down
    private int _pressedButton = -1;
    private bool _syncing;

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int Increment { get; set; } = 1;

    public event EventHandler? ValueChanged;

    public NumericField()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Height = 24;
        BackColor = Theme.Field;
        _hoverButton = -1;

        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = Theme.Field;
        _text.ForeColor = Theme.Text;
        _text.TextAlign = HorizontalAlignment.Right;
        _text.Font = Theme.Body;
        _text.Leave += (_, _) => Commit();
        _text.KeyDown += OnTextKeyDown;
        Controls.Add(_text);

        _repeat.Tick += (_, _) =>
        {
            _repeat.Interval = 55;      // slow first step, then accelerate, like a real spinner
            Step(_repeatDirection);
        };
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value) { SyncText(); return; }
            _value = clamped;
            SyncText();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the value without raising ValueChanged, for binding from settings.</summary>
    public void SetValueSilently(int value)
    {
        _value = Math.Clamp(value, Minimum, Maximum);
        SyncText();
        Invalidate();
    }

    private void SyncText()
    {
        _syncing = true;
        _text.Text = _value.ToString();
        _text.SelectionStart = _text.TextLength;
        _syncing = false;
    }

    private void Commit()
    {
        if (_syncing) return;
        Value = int.TryParse(_text.Text, out int parsed) ? parsed : _value;
        SyncText();
    }

    private void Step(int direction) => Value = _value + direction * Increment;

    private void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up: Step(1); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Down: Step(-1); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Enter: Commit(); e.Handled = e.SuppressKeyPress = true; break;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int textHeight = Math.Min(Height - 6, _text.PreferredHeight);
        _text.SetBounds(7, (Height - textHeight) / 2, Math.Max(10, Width - ButtonWidth - 14), textHeight);
    }

    protected override void OnMouseWheel(MouseEventArgs e) => Step(Math.Sign(e.Delta));

    private int ButtonAt(Point point)
    {
        if (point.X < Width - ButtonWidth) return -1;
        return point.Y < Height / 2 ? 0 : 1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = ButtonAt(e.Location);
        if (hit == _hoverButton) return;
        _hoverButton = hit;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverButton = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int hit = ButtonAt(e.Location);
        if (hit < 0) { _text.Focus(); return; }

        _pressedButton = hit;
        _repeatDirection = hit == 0 ? 1 : -1;
        Step(_repeatDirection);
        _repeat.Interval = 380;
        _repeat.Start();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _repeat.Stop();
        _pressedButton = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = FlatButton.Rounded(rect, 5))
        {
            using var fill = new SolidBrush(Theme.Field);
            g.FillPath(fill, path);
            using var border = new Pen(_hoverButton >= 0 || _text.Focused ? Theme.Accent : Theme.FieldBorder);
            g.DrawPath(border, path);
        }

        int columnLeft = Width - ButtonWidth;
        using (var divider = new Pen(Theme.FieldBorder))
            g.DrawLine(divider, columnLeft, 3, columnLeft, Height - 4);

        DrawChevron(g, 0, columnLeft, up: true);
        DrawChevron(g, 1, columnLeft, up: false);
    }

    private void DrawChevron(Graphics g, int index, int columnLeft, bool up)
    {
        bool enabled = up ? _value < Maximum : _value > Minimum;
        Color ink = !enabled ? Theme.TextFaint
            : _pressedButton == index ? Theme.Accent
            : _hoverButton == index ? Theme.Text
            : Theme.TextDim;

        float cx = columnLeft + ButtonWidth / 2f;
        float cy = up ? Height * 0.32f : Height * 0.68f;
        using var pen = new Pen(ink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, up
            ? new[] { new PointF(cx - 3.5f, cy + 1.6f), new PointF(cx, cy - 1.8f), new PointF(cx + 3.5f, cy + 1.6f) }
            : new[] { new PointF(cx - 3.5f, cy - 1.6f), new PointF(cx, cy + 1.8f), new PointF(cx + 3.5f, cy - 1.6f) });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _repeat.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Flat button with a hover state and an optional accent or danger role.</summary>
internal sealed class FlatButton : Button
{
    public enum Role { Normal, Accent, Danger }

    private bool _hovered;
    private Role _role = Role.Normal;

    public Role ButtonRole
    {
        get => _role;
        set { _role = value; Invalidate(); }
    }

    public Icon Glyph { get; set; } = Icon.None;

    public FlatButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Height = 28;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        Color fill = _role switch
        {
            Role.Accent => _hovered ? Theme.AccentHover : Theme.Accent,
            Role.Danger => _hovered ? Theme.AccentHover : Theme.Accent,
            _ => _hovered ? Theme.Hover : Theme.Field,
        };
        Color text = _role switch
        {
            Role.Accent => Color.White,
            Role.Danger => Color.White,
            _ => Theme.Text,
        };
        if (!Enabled) { fill = Theme.Field; text = Theme.TextDim; }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rect, 5))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        if (_role != Role.Accent)
        {
            using var path = Rounded(rect, 5);
            using var pen = new Pen(Theme.FieldBorder);
            g.DrawPath(pen, path);
        }

        if (Glyph == Icon.None)
        {
            TextRenderer.DrawText(g, Text, Font, rect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        const int GlyphSize = 15, Gap = 8;
        if (string.IsNullOrEmpty(Text))
        {
            Icons.Draw(g, Glyph, new RectangleF(rect.X + (rect.Width - GlyphSize) / 2f,
                rect.Y + (rect.Height - GlyphSize) / 2f, GlyphSize, GlyphSize), text, 1.5f);
            return;
        }

        // Icon and label are centred together, so the pair stays balanced in the button.
        var size = TextRenderer.MeasureText(g, Text, Font);
        int totalWidth = GlyphSize + Gap + size.Width;
        int left = rect.X + (rect.Width - totalWidth) / 2;
        Icons.Draw(g, Glyph, new RectangleF(left, rect.Y + (rect.Height - GlyphSize) / 2f, GlyphSize, GlyphSize), text, 1.5f);
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(left + GlyphSize + Gap, rect.Y, size.Width + 2, rect.Height), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Checkbox drawn to match, since the system one does not follow a custom palette.</summary>
internal sealed class FlatCheck : CheckBox
{
    private bool _hovered;

    public FlatCheck()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        Height = 22;
        ForeColor = Theme.Text;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var box = new Rectangle(0, Height / 2 - 8, 16, 16);
        using (var path = FlatButton.Rounded(box, 4))
        {
            using var fill = new SolidBrush(Checked ? Theme.Accent : Theme.Field);
            g.FillPath(fill, path);
            using var pen = new Pen(Checked ? Theme.Accent : (_hovered ? Theme.FieldBorder : Theme.FieldBorder));
            g.DrawPath(pen, path);
        }

        if (Checked)
        {
            using var tick = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(tick, new[]
            {
                new Point(box.X + 4, box.Y + 8),
                new Point(box.X + 7, box.Y + 11),
                new Point(box.X + 12, box.Y + 5),
            });
        }

        var text = new Rectangle(box.Right + 8, 0, Width - box.Right - 8, Height);
        TextRenderer.DrawText(g, Text, Font, text, Enabled ? ForeColor : Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// A key chord shown as a field, rebound by clicking it and pressing the new chord.
/// It grabs the keystroke in <see cref="ProcessCmdKey"/> - which the focused control sees
/// before the form does - so the very keys being rebound cannot trigger the actions they
/// are bound to while they are being typed. Esc cancels, Backspace and Delete clear it.
/// </summary>
internal sealed class KeyCaptureButton : Control
{
    private bool _capturing;
    private bool _hovered;
    private Keys _keys = Keys.None;

    /// <summary>
    /// The field currently listening, if any. Held as a reference rather than a bare flag so
    /// the state cannot get stuck on: a control that is disposed or hidden mid-capture -
    /// a window resize rebuilds the panel - stops counting on its own, and a stuck flag would
    /// silently disable every shortcut in the app.
    /// </summary>
    private static KeyCaptureButton? _listening;

    /// <summary>True while one of these controls is listening, so the shell can stand back.</summary>
    public static bool AnyCapturing =>
        _listening is { _capturing: true, IsDisposed: false, Visible: true };

    /// <summary>Raised with the new chord; Keys.None means the binding was cleared.</summary>
    public event EventHandler<Keys>? Rebound;

    public KeyCaptureButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Height = 24;
        BackColor = Theme.Field;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public Keys Keys
    {
        get => _keys;
        set { _keys = value; Invalidate(); }
    }

    /// <summary>Shown dimmed when nothing is bound, e.g. "add a second key".</summary>
    public string EmptyText { get; set; } = "not bound";

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnLostFocus(EventArgs e) { StopCapturing(); base.OnLostFocus(e); }
    protected override void OnVisibleChanged(EventArgs e) { if (!Visible) StopCapturing(); base.OnVisibleChanged(e); }

    protected override void Dispose(bool disposing)
    {
        StopCapturing();
        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Right) { Commit(System.Windows.Forms.Keys.None); return; }
        StartCapturing();
    }

    private void StartCapturing()
    {
        if (_capturing) return;
        _listening?.StopCapturing();
        _capturing = true;
        _listening = this;
        Invalidate();
    }

    private void StopCapturing()
    {
        if (ReferenceEquals(_listening, this)) _listening = null;
        if (!_capturing) return;
        _capturing = false;
        if (!IsDisposed) Invalidate();
    }

    private void Commit(Keys keys)
    {
        StopCapturing();
        if (keys == _keys) return;
        _keys = keys;
        Invalidate();
        Rebound?.Invoke(this, keys);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData & System.Windows.Forms.Keys.KeyCode)
        {
            case System.Windows.Forms.Keys.Escape:
                StopCapturing();
                return true;
            case System.Windows.Forms.Keys.Back:
            case System.Windows.Forms.Keys.Delete:
                Commit(System.Windows.Forms.Keys.None);
                return true;
        }

        // A lone Ctrl or Shift is the first half of a chord, not the chord itself.
        if (!ShortcutCatalog.IsBindable(keyData)) return true;
        Commit(keyData);
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = FlatButton.Rounded(rect, 5))
        {
            using var fill = new SolidBrush(_capturing ? Theme.Hover : Theme.Field);
            g.FillPath(fill, path);
            using var border = new Pen(_capturing ? Theme.Accent : _hovered || Focused ? Theme.FieldBorder : Theme.FieldBorder);
            g.DrawPath(border, path);
        }

        bool empty = _keys == System.Windows.Forms.Keys.None;
        string text = _capturing ? "Press a key..." : empty ? EmptyText : ShortcutCatalog.Describe(_keys);
        Color ink = _capturing ? Theme.Accent : empty ? Theme.TextFaint : Theme.Text;
        TextRenderer.DrawText(g, text, _capturing ? Theme.Body : Theme.BodyBold, rect, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// A one-field modal, for naming a profile. WinForms has no input box, and a MessageBox
/// cannot take text - so this is the smallest thing that matches the rest of the window
/// rather than the system dialog palette.
/// </summary>
internal static class Prompt
{
    /// <summary>Returns the typed name, or null if it was cancelled or left blank.</summary>
    public static string? Ask(IWin32Window owner, string title, string caption, string initial)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 132),
            BackColor = Theme.Shell,
            ForeColor = Theme.Text,
            Font = Theme.Body,
        };

        var label = new Label
        {
            Text = caption,
            ForeColor = Theme.TextDim,
            BackColor = Theme.Shell,
            AutoSize = false,
            Bounds = new Rectangle(18, 16, 324, 20),
        };
        var box = new TextBox
        {
            Text = initial,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(18, 42, 324, 24),
            MaxLength = 40,
        };
        var ok = new FlatButton { Text = "OK", ButtonRole = FlatButton.Role.Accent, Bounds = new Rectangle(178, 84, 78, 30) };
        var cancel = new FlatButton { Text = "Cancel", Bounds = new Rectangle(264, 84, 78, 30) };
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        box.SelectAll();

        if (form.ShowDialog(owner) != DialogResult.OK) return null;
        string name = box.Text.Trim();
        return name.Length == 0 ? null : name;
    }
}
