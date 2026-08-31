using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// One rounded panel with a titled header and a vertical stack of rows. Each Add* call
/// advances an internal cursor, so a card's height falls out of its contents and the
/// column layout can just stack them.
/// </summary>
internal sealed class Card : Panel
{
    public const int Pad = 16;
    private const int HeaderHeight = 40;
    private const int LabelWidth = 78;
    private const int RowHeight = 30;

    private readonly string _title;
    private readonly Icon _icon;
    private int _y = HeaderHeight + 6;

    public int FieldLeft => Pad + LabelWidth + 8;
    public int FieldWidth => Width - FieldLeft - Pad;

    public Card(string title, Icon icon, int width)
    {
        _title = title.ToUpperInvariant();
        _icon = icon;
        Width = width;
        BackColor = Theme.Card;
        TabStop = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    /// <summary>Adds a "Reset" affordance on the right of the header.</summary>
    public void AddHeaderAction(string text, Action action)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            LinkColor = Theme.TextDim,
            ActiveLinkColor = Theme.Accent,
            VisitedLinkColor = Theme.TextDim,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Font = Theme.Small,
            BackColor = Theme.Card,
        };
        link.SetBounds(Width - Pad - 60, 13, 60, 18);
        link.LinkClicked += (_, _) => action();
        Controls.Add(link);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Shell);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = FlatButton.Rounded(rect, 10))
        {
            using var fill = new SolidBrush(Theme.Card);
            g.FillPath(fill, path);
            using var border = new Pen(Theme.CardBorder);
            g.DrawPath(border, path);
        }

        Icons.Draw(g, _icon, new RectangleF(Pad, 14, 16, 16), Theme.Accent);
        TextRenderer.DrawText(g, _title, Theme.Heading, new Rectangle(Pad + 26, 12, Width - Pad - 40, 20),
            Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    // ---- rows ---------------------------------------------------------------------------

    public T AddRow<T>(string label, T field) where T : Control
    {
        AddCaption(label, _y + 5);
        field.SetBounds(FieldLeft, _y, FieldWidth, 24);
        Controls.Add(field);
        _y += RowHeight;
        return field;
    }

    /// <summary>A row whose field is narrower, with a unit or note beside it.</summary>
    public T AddRow<T>(string label, T field, int fieldWidth, string suffix) where T : Control
    {
        AddCaption(label, _y + 5);
        field.SetBounds(FieldLeft, _y, fieldWidth, 24);
        Controls.Add(field);

        var unit = new Label
        {
            Text = suffix,
            ForeColor = Theme.TextDim,
            Font = Theme.Body,
            AutoSize = false,
            BackColor = Theme.Card,
        };
        unit.SetBounds(FieldLeft + fieldWidth + 8, _y + 5, FieldWidth - fieldWidth - 8, 18);
        Controls.Add(unit);
        _y += RowHeight;
        return field;
    }

    public void AddSlider(string label, Icon icon, Slider slider, Label value)
    {
        AddCaption(label, _y + 3);

        int iconLeft = FieldLeft;
        var glyph = new IconBox(icon) { BackColor = Theme.Card };
        glyph.SetBounds(iconLeft, _y + 3, 16, 16);
        Controls.Add(glyph);

        const int ValueWidth = 42;
        int sliderLeft = iconLeft + 24;
        slider.SetBounds(sliderLeft, _y, Width - Pad - ValueWidth - 8 - sliderLeft, 22);
        Controls.Add(slider);

        value.ForeColor = Theme.Text;
        value.Font = Theme.Body;
        value.AutoSize = false;
        value.TextAlign = ContentAlignment.MiddleRight;
        value.BackColor = Theme.Card;
        value.SetBounds(Width - Pad - ValueWidth, _y + 3, ValueWidth, 18);
        Controls.Add(value);
        _y += 28;
    }

    public void AddCheck(FlatCheck check)
    {
        check.SetBounds(Pad, _y, Width - Pad * 2, 22);
        check.BackColor = Theme.Card;
        Controls.Add(check);
        _y += 26;
    }

    public void AddHint(string text)
    {
        int lines = text.Count(c => c == '\n') + 1;
        var label = new Label
        {
            Text = text,
            ForeColor = Theme.TextFaint,
            Font = Theme.Hint,
            AutoSize = false,
            BackColor = Theme.Card,
        };
        label.SetBounds(Pad, _y - 4, Width - Pad * 2, 14 * lines);
        Controls.Add(label);
        _y += 14 * lines + 4;
    }

    public void AddText(string text, bool dim = false)
    {
        int lines = text.Count(c => c == '\n') + 1;
        var label = new Label
        {
            Text = text,
            ForeColor = dim ? Theme.TextDim : Theme.Text,
            Font = Theme.Body,
            AutoSize = false,
            BackColor = Theme.Card,
        };
        label.SetBounds(Pad, _y, Width - Pad * 2, 17 * lines);
        Controls.Add(label);
        _y += 17 * lines + 6;
    }

    public void AddMono(Label label, int lines = 2)
    {
        label.ForeColor = Theme.TextDim;
        label.Font = Theme.Mono;
        label.AutoSize = false;
        label.BackColor = Theme.Card;
        label.SetBounds(Pad, _y, Width - Pad * 2, 16 * lines);
        Controls.Add(label);
        _y += 16 * lines + 4;
    }


    /// <summary>Stretches the given buttons evenly across the card's width.</summary>
    public void AddButtonsFilled(params FlatButton[] buttons)
    {
        int available = Width - Pad * 2 - 8 * (buttons.Length - 1);
        int each = available / buttons.Length;
        int x = Pad;
        foreach (var button in buttons)
        {
            button.SetBounds(x, _y, each, 32);
            Controls.Add(button);
            x += each + 8;
        }
        _y += 40;
    }

    public void AddSpace(int pixels) => _y += pixels;

    /// <summary>
    /// A shortcut row: the action's name on the left, and its two chords on the right.
    /// The action names are sentences rather than words, so this cannot use AddRow's
    /// fixed label column - the label takes whatever the two key fields leave.
    /// </summary>
    public void AddShortcut(string label, Control primary, Control alternate)
    {
        const int KeyWidth = 122, KeyGap = 8;
        var caption = new Label
        {
            Text = label,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            AutoSize = false,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        caption.SetBounds(Pad, _y, Width - Pad * 2 - KeyWidth * 2 - KeyGap * 2, 24);
        Controls.Add(caption);

        primary.SetBounds(Width - Pad - KeyWidth * 2 - KeyGap, _y, KeyWidth, 24);
        alternate.SetBounds(Width - Pad - KeyWidth, _y, KeyWidth, 24);
        Controls.Add(primary);
        Controls.Add(alternate);
        _y += 30;
    }

    /// <summary>
    /// A reference line: what it does on the left, the keys that do it on the right.
    /// Returns the right-hand label, so a rebind can update it without rebuilding the page.
    /// </summary>
    public Label AddKeyLine(string action, string keys)
    {
        var left = new Label
        {
            Text = action,
            ForeColor = Theme.TextDim,
            Font = Theme.Body,
            AutoSize = false,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        left.SetBounds(Pad, _y, Width - Pad * 2 - 140, 20);
        Controls.Add(left);

        var right = new Label
        {
            Text = keys,
            ForeColor = Theme.Text,
            Font = Theme.BodyBold,
            AutoSize = false,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.MiddleRight,
        };
        right.SetBounds(Width - Pad - 136, _y, 136, 20);
        Controls.Add(right);
        _y += 23;
        return right;
    }

    /// <summary>A dim heading inside a card, for grouping rows that belong together.</summary>
    public void AddSubheading(string text)
    {
        var label = new Label
        {
            Text = text.ToUpperInvariant(),
            ForeColor = Theme.TextFaint,
            Font = Theme.Heading,
            AutoSize = false,
            BackColor = Theme.Card,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        label.SetBounds(Pad, _y, Width - Pad * 2, 18);
        Controls.Add(label);
        _y += 22;
    }

    public void AddMeter(string label, LevelMeter meter)
    {
        AddCaption(label, _y);
        meter.SetBounds(FieldLeft, _y + 5, FieldWidth, 8);
        Controls.Add(meter);
        _y += 24;
    }

    private void AddCaption(string text, int y)
    {
        var caption = new Label
        {
            Text = text,
            ForeColor = Theme.TextDim,
            Font = Theme.Body,
            AutoSize = false,
            BackColor = Theme.Card,
        };
        caption.SetBounds(Pad, y, LabelWidth, 18);
        Controls.Add(caption);
    }

    /// <summary>Call once everything is added, to size the card to its contents.</summary>
    public Card Finish()
    {
        Height = _y + Pad - 6;
        return this;
    }
}

/// <summary>A small icon rendered as a control, for placing next to a slider.</summary>
internal sealed class IconBox : Control
{
    private readonly Icon _icon;

    public IconBox(Icon icon)
    {
        _icon = icon;
        TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Icons.Draw(e.Graphics, _icon, new RectangleF(1, 1, Width - 2, Height - 2), Theme.TextDim, 1.4f);
    }
}
