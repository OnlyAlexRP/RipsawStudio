using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

internal enum Page { Capture, Record, Shortcuts, Settings, About }

/// <summary>
/// The vertical page switcher down the left of the panel, plus a live status chip at the
/// bottom so the source and frame rate stay visible while you are in the settings.
/// </summary>
internal sealed class NavRail : Control
{
    private static readonly (Page Page, string Label, Icon Icon)[] Items =
    {
        (Page.Capture, "Capture", Icon.Monitor),
        (Page.Record, "Record", Icon.Record),
        (Page.Shortcuts, "Keys", Icon.Keyboard),
        (Page.Settings, "Settings", Icon.Gear),
        (Page.About, "About", Icon.Info),
    };

    private const int ItemHeight = 58;
    private const int ItemTop = 18;
    private const int ItemInset = 10;

    private Page _selected = Page.Capture;
    private int _hovered = -1;
    private string _statusTop = "";
    private string _statusBottom = "";
    private bool _live;

    public event EventHandler<Page>? PageSelected;

    public NavRail()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Shell;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public Page Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public void SetStatus(bool live, string top, string bottom)
    {
        if (_live == live && _statusTop == top && _statusBottom == bottom) return;
        _live = live;
        _statusTop = top;
        _statusBottom = bottom;
        Invalidate();
    }

    private Rectangle ItemBounds(int index) =>
        new(ItemInset, ItemTop + index * ItemHeight, Width - ItemInset * 2, ItemHeight - 8);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);

        using (var path = FlatButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 14))
        using (var fill = new SolidBrush(Theme.Shell))
            g.FillPath(fill, path);

        for (int i = 0; i < Items.Length; i++)
        {
            var (page, label, icon) = Items[i];
            var bounds = ItemBounds(i);
            bool active = page == _selected;

            if (active)
            {
                using var path = FlatButton.Rounded(bounds, 10);
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, path);
            }
            else if (i == _hovered)
            {
                using var path = FlatButton.Rounded(bounds, 10);
                using var fill = new SolidBrush(Theme.Hover);
                g.FillPath(fill, path);
            }

            Color ink = active ? Color.White : Theme.TextDim;
            Icons.Draw(g, icon, new RectangleF(bounds.X + bounds.Width / 2f - 11, bounds.Y + 10, 22, 22), ink, 1.7f);
            TextRenderer.DrawText(g, label, Theme.Small,
                new Rectangle(bounds.X, bounds.Bottom - 20, bounds.Width, 18), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        DrawStatusChip(g);
    }

    private void DrawStatusChip(Graphics g)
    {
        if (_statusTop.Length == 0) return;

        var chip = new Rectangle(ItemInset, Height - 72, Width - ItemInset * 2, 52);
        using (var path = FlatButton.Rounded(chip, 10))
        using (var fill = new SolidBrush(Theme.Card))
            g.FillPath(fill, path);

        using (var dot = new SolidBrush(_live ? Theme.Good : Theme.TextFaint))
            g.FillEllipse(dot, chip.X + 12, chip.Y + 14, 7, 7);

        TextRenderer.DrawText(g, _statusTop, Theme.Small,
            new Rectangle(chip.X + 24, chip.Y + 8, chip.Width - 28, 18), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _statusBottom, Theme.Small,
            new Rectangle(chip.X + 12, chip.Y + 28, chip.Width - 20, 18), Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit == _hovered) return;
        _hovered = hit;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit < 0) return;
        Selected = Items[hit].Page;
        PageSelected?.Invoke(this, Items[hit].Page);
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < Items.Length; i++)
            if (ItemBounds(i).Contains(point)) return i;
        return -1;
    }
}
