using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// A thin, discreet scroll indicator for <see cref="SettingsPanel"/>. The wheel already
/// scrolls a page (see SettingsPanel's message filter); this just shows where you are, and
/// can be dragged directly for pages long enough to need it. Hides itself entirely when a
/// page already fits the window.
/// </summary>
internal sealed class ScrollThumb : Control
{
    private const int MinThumbHeight = 24;

    private float _topRatio;
    private float _sizeRatio = 1f;
    private bool _dragging;
    private int _dragStartY;
    private float _dragStartRatio;

    /// <summary>Requests scrolling to the given ratio (0 = top, 1 = bottom) while dragging.</summary>
    public event Action<float>? DragToRatio;

    public ScrollThumb()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        TabStop = false;
        Cursor = Cursors.Default;
        Visible = false;
    }

    /// <summary>topRatio: 0..1 position of the thumb's top within the track.
    /// sizeRatio: 0..1 how much of the track the thumb covers (1 = whole page fits, so hide).</summary>
    public void SetMetrics(float topRatio, float sizeRatio)
    {
        topRatio = Math.Clamp(topRatio, 0f, 1f);
        sizeRatio = Math.Clamp(sizeRatio, 0f, 1f);
        bool shouldShow = sizeRatio < 0.999f;
        if (Visible != shouldShow) Visible = shouldShow;
        if (!shouldShow) return;

        if (Math.Abs(_topRatio - topRatio) < 0.0005f && Math.Abs(_sizeRatio - sizeRatio) < 0.0005f) return;
        _topRatio = topRatio;
        _sizeRatio = sizeRatio;
        Invalidate();
    }

    private int ThumbHeight => Math.Min(Height, Math.Max(MinThumbHeight, (int)(Height * _sizeRatio)));
    private int MaxTravel => Math.Max(1, Height - ThumbHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Height <= 0 || Width <= 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Shell);

        int thumbHeight = ThumbHeight;
        int top = (int)Math.Round(_topRatio * MaxTravel);
        var rect = new Rectangle(0, top, Width, thumbHeight);
        using var path = FlatButton.Rounded(rect, Width / 2);
        using var brush = new SolidBrush(_dragging ? Theme.FieldBorder : Theme.CardBorder);
        g.FillPath(brush, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragging = true;
        _dragStartY = e.Y;
        _dragStartRatio = _topRatio;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        float delta = (e.Y - _dragStartY) / (float)MaxTravel;
        DragToRatio?.Invoke(Math.Clamp(_dragStartRatio + delta, 0f, 1f));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        Invalidate();
    }
}
