namespace RipsawStudio.UI;

internal static class Theme
{
    // Palette lifted straight from the supplied artwork (bar, icons and window mockups all
    // sample to the same near-black with one orange accent), so every surface in the app
    // reads as one consistent skin rather than a rail-and-shell theme reskinned in place.

    /// <summary>Letterbox area around the picture.</summary>
    public static readonly Color Background = Color.FromArgb(10, 10, 12);

    /// <summary>The floating settings window and the menu bar.</summary>
    public static readonly Color Shell = Color.FromArgb(11, 13, 15);
    /// <summary>Cards sitting on the shell.</summary>
    public static readonly Color Card = Color.FromArgb(22, 24, 27);
    public static readonly Color CardBorder = Color.FromArgb(38, 40, 45);
    /// <summary>Inputs sitting on a card.</summary>
    public static readonly Color Field = Color.FromArgb(31, 33, 37);
    public static readonly Color FieldBorder = Color.FromArgb(48, 50, 55);
    public static readonly Color Hover = Color.FromArgb(43, 45, 50);
    public static readonly Color Track = Color.FromArgb(49, 51, 57);

    public static readonly Color Text = Color.FromArgb(235, 237, 240);
    public static readonly Color TextDim = Color.FromArgb(158, 161, 168);
    public static readonly Color TextFaint = Color.FromArgb(108, 112, 120);

    /// <summary>The orange from the icon artwork - the app's one accent colour.</summary>
    public static readonly Color Accent = Color.FromArgb(235, 71, 42);
    public static readonly Color AccentHover = Color.FromArgb(247, 110, 84);
    public static readonly Color Good = Color.FromArgb(95, 208, 138);
    public static readonly Color Record = Color.FromArgb(226, 58, 50);

    // Shared for the life of the process. These are handed to controls and to paint code,
    // so they must not be disposed by either - a per-access `new Font` leaked one GDI
    // handle on every repaint of the rail and the file list.
    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Hint = new("Segoe UI", 7.5f);
    public static readonly Font Heading = new("Segoe UI", 8.5f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 8.5f);
    public static readonly Font BodyBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Large = new("Segoe UI", 11f);
    /// <summary>Bold and a little larger, standing in for the poster-style condensed
    /// headline in the mockups - Windows has no guaranteed condensed display face.</summary>
    public static readonly Font PageTitle = new("Segoe UI", 14f, FontStyle.Bold);
}

/// <summary>
/// The surface D3D presents into. It never paints itself, otherwise WinForms would
/// flicker over the swap chain between frames.
/// </summary>
internal sealed class VideoSurface : Control
{
    private const int WS_CLIPSIBLINGS = 0x04000000;

    public VideoSurface()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, false);
        BackColor = Theme.Background;
        TabStop = false;
    }

    public bool ShowPlaceholder { get; set; } = true;
    public string PlaceholderText { get; set; } = "No signal";

    /// <summary>
    /// Without WS_CLIPSIBLINGS the swap chain would present over the settings panel and the
    /// overlays, because DXGI draws to this window's whole rectangle.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_CLIPSIBLINGS;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!ShowPlaceholder) return;   // live frames own the surface
        e.Graphics.Clear(Theme.Background);
        using var brush = new SolidBrush(Theme.TextDim);
        var size = e.Graphics.MeasureString(PlaceholderText, Theme.Large);
        e.Graphics.DrawString(PlaceholderText, Theme.Large, brush,
            (Width - size.Width) / 2, (Height - size.Height) / 2);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ShowPlaceholder) base.OnPaintBackground(e);
    }
}
