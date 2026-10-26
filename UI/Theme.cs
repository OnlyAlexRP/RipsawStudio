namespace RipsawStudio.UI;

/// <summary>
/// The palette is the one in the app's icon artwork: near-black surfaces and a single
/// orange-red accent, so the navigation icons and every control around them read as one
/// skin. Numbers and titles are set in Bahnschrift, Windows' own DIN face.
/// </summary>
internal static class Theme
{
    /// <summary>Letterbox area around the picture.</summary>
    public static readonly Color Background = Color.FromArgb(10, 10, 12);

    /// <summary>The control window's page surface - the artwork's card grey.</summary>
    public static readonly Color Shell = Color.FromArgb(22, 24, 27);
    /// <summary>The navigation bar and the tab strip: the near-black the icon tiles were drawn for.</summary>
    public static readonly Color Rail = Color.FromArgb(11, 13, 15);
    /// <summary>What a section sits on. The same as the shell: sections are not boxed.</summary>
    public static readonly Color Card = Shell;
    public static readonly Color CardBorder = Color.FromArgb(38, 40, 45);
    /// <summary>Hairlines between groups.</summary>
    public static readonly Color Line = Color.FromArgb(38, 40, 45);
    /// <summary>Inputs: dropdowns, number fields, buttons.</summary>
    public static readonly Color Field = Color.FromArgb(31, 33, 37);
    public static readonly Color FieldBorder = Color.FromArgb(48, 50, 55);
    public static readonly Color Hover = Color.FromArgb(43, 45, 50);
    /// <summary>The chosen segment of a segmented control.</summary>
    public static readonly Color Selected = Color.FromArgb(56, 58, 64);
    public static readonly Color Track = Color.FromArgb(49, 51, 57);

    public static readonly Color Text = Color.FromArgb(235, 237, 240);
    public static readonly Color TextDim = Color.FromArgb(158, 161, 168);
    public static readonly Color TextFaint = Color.FromArgb(108, 112, 120);

    /// <summary>The orange from the icon artwork - the app's one accent colour.</summary>
    public static readonly Color Accent = Color.FromArgb(235, 71, 42);
    public static readonly Color AccentHover = Color.FromArgb(247, 110, 84);
    /// <summary>Text drawn on top of the accent.</summary>
    public static readonly Color AccentInk = Color.White;
    public static readonly Color Good = Color.FromArgb(95, 208, 138);
    /// <summary>Recording.</summary>
    public static readonly Color Record = Color.FromArgb(226, 58, 50);
    /// <summary>No signal: amber, so it never reads as the accent or as recording.</summary>
    public static readonly Color Warning = Color.FromArgb(240, 184, 80);

    private static readonly string TextFace = Pick("Segoe UI Variable Text", "Segoe UI");
    private static readonly string DisplayFace = Pick("Bahnschrift", "Segoe UI");

    // Shared for the life of the process. These are handed to controls and to paint code,
    // so they must not be disposed by either - a per-access `new Font` leaked one GDI
    // handle on every repaint of the rail and the file list.
    public static readonly Font Body = new(TextFace, 9.5f);
    public static readonly Font Small = new(TextFace, 8.5f);
    public static readonly Font Hint = new(TextFace, 8.5f);
    public static readonly Font Heading = new(TextFace, 10f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 8.5f);
    public static readonly Font BodyBold = new(TextFace, 9.5f, FontStyle.Bold);
    public static readonly Font Large = new(TextFace, 11f);
    /// <summary>Live readouts in the signal strip: resolution, frame rate, timecode.</summary>
    public static readonly Font Readout = new(DisplayFace, 13f, FontStyle.Bold);
    public static readonly Font ReadoutSmall = new(DisplayFace, 9f);
    /// <summary>The navigation rail.</summary>
    public static readonly Font Nav = new(TextFace, 10f);

    private static string Pick(params string[] families)
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        foreach (var name in families)
            if (installed.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        return "Segoe UI";
    }
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
