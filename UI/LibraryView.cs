using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.VisualBasic.FileIO;

namespace RipsawStudio.UI;

/// <summary>A page surface, taller than its viewport, slid by the wheel.</summary>
internal sealed class ContentPanel : Panel
{
    public ContentPanel()
    {
        BackColor = Theme.Shell;
        TabStop = false;
        DoubleBuffered = true;
    }
}

/// <summary>What a capture is, so its tile can badge it.</summary>
internal enum RecentKind { Recording, Replay, Screenshot }

/// <summary>Drawing shared by the Captures page and the card that announces a new capture.</summary>
internal static class CaptureTile
{
    public static Icon IconFor(RecentKind kind) => kind switch
    {
        RecentKind.Replay => Icon.Rewind,
        RecentKind.Screenshot => Icon.Camera,
        _ => Icon.Record,
    };

    /// <summary>
    /// Centre-crops to fill the tile, so a 4:3 capture is not letterboxed inside it.
    ///
    /// The ImageAttributes is not optional. Scaling with a high-quality interpolation mode
    /// makes GDI+ sample a pixel or two beyond the source rectangle along every edge, and
    /// off the edge of a bitmap it samples transparent - which blends the destination's
    /// background through and leaves a half-strength fringe around the drawn image. Over a
    /// dark card that is invisible; over live video it is the pale outline. TileFlipXY makes
    /// those out-of-bounds reads mirror the edge pixels back instead, so there is nothing
    /// transparent to blend in.
    /// </summary>
    public static void DrawCover(Graphics g, Image bmp, Rectangle dest)
    {
        if (dest.Width <= 0 || dest.Height <= 0) return;
        float srcAspect = (float)bmp.Width / bmp.Height;
        float dstAspect = (float)dest.Width / dest.Height;
        RectangleF src = srcAspect > dstAspect
            ? new RectangleF((bmp.Width - bmp.Height * dstAspect) / 2f, 0, bmp.Height * dstAspect, bmp.Height)
            : new RectangleF(0, (bmp.Height - bmp.Width / dstAspect) / 2f, bmp.Width, bmp.Width / dstAspect);

        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(bmp, dest, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attributes);
    }
}

/// <summary>
/// The Captures page: every recording, replay and screenshot in the three output folders,
/// newest first, as a scrolling grid of thumbnails. Click opens a file; the round check on a
/// tile, Ctrl+click or Shift+click select, and the actions above the grid then apply to the
/// selection. Clicking a name renames it in place. Deleting moves files to the Recycle Bin.
///
/// Each tile's picture is rendered once at tile size - cropped, rounded and shaded - and then
/// only copied, so the hover zoom redraws one small bitmap per frame rather than rescaling
/// every thumbnail on the page.
/// </summary>
internal sealed class LibraryView : Control
{
    private enum Filter { All, Recordings, Replays, Screenshots, Favourites }
    private enum Part { None, Tile, Check, Star, Name }

    private sealed record Item(string Path, string Name, DateTime Time, long Size, RecentKind Kind);

    private const int PadX = 36;
    private const int HeaderHeight = 108;
    private const int MinTileWidth = 220;
    private const int Gap = 12;
    private const int Corner = 8;
    private const int Knob = 24;
    private const float HoverScale = 1.04f;
    /// <summary>Time constant of the hover ease, in milliseconds: the same feel at any frame rate.</summary>
    private const double EaseMs = 45;
    /// <summary>Rendered tiles kept beyond this are dropped once they scroll out of view.</summary>
    private const int CacheLimit = 240;

    private readonly AppSettings _settings;
    private readonly Segmented _filter = new();
    private readonly FlatButton _refresh = new() { Glyph = Icon.Refresh };
    private readonly FlatButton _openFolder = new() { Glyph = Icon.Folder };
    private readonly FlatButton _selectAll = new() { Glyph = Icon.Check };
    private readonly FlatButton _favourite = new() { Glyph = Icon.Star };
    private readonly FlatButton _rename = new() { Glyph = Icon.Pencil };
    private readonly FlatButton _delete = new() { ButtonRole = FlatButton.Role.Danger, Glyph = Icon.Trash };
    private readonly FlatButton _clear = new() { ButtonRole = FlatButton.Role.Ghost, Glyph = Icon.Cross };
    private readonly NameEditor _editor = new();
    private readonly ToolTip _tip = new() { AutomaticDelay = 300, ReshowDelay = 100, InitialDelay = 400 };

    private List<Item> _all = new();
    private Item[] _items = Array.Empty<Item>();
    private HashSet<string> _favourites = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private int _anchor = -1;
    private string? _editing;

    private readonly Dictionary<string, Bitmap> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Size _tileSizeRendered;

    private float[] _scale = Array.Empty<float>();
    private int _hovered = -1;
    private Part _hoveredPart;
    private int _scroll;
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 10 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTick;

    private static readonly StringFormat OneLine = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Center,
    };

    /// <summary>Measures with GDI+, the engine the captions are drawn with, so the name box fits the drawn name.</summary>
    private static readonly Graphics Measurer = NewMeasurer();

    private static Graphics NewMeasurer()
    {
        var g = Graphics.FromImage(new Bitmap(1, 1));
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        return g;
    }

    /// <summary>Favourites changed, so settings.json has something new to save.</summary>
    public event EventHandler? SettingsTouched;
    public event EventHandler? OpenFolderClicked;
    public event EventHandler? ScrollChanged;

    public LibraryView(AppSettings settings)
    {
        _settings = settings;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Shell;
        TabStop = true;

        Controls.AddRange(new Control[] { _filter, _refresh, _openFolder, _selectAll, _favourite, _rename, _delete, _clear, _editor });
        _filter.SelectedIndexChanged += (_, _) => { ApplyFilter(); _scroll = 0; LayoutGrid(); };
        _refresh.Click += (_, _) => Reload();
        _openFolder.Click += (_, _) => OpenFolderClicked?.Invoke(this, EventArgs.Empty);
        _selectAll.Click += (_, _) => SelectAll();
        _favourite.Click += (_, _) => ToggleFavourite(SelectedItems());
        _rename.Click += (_, _) => { if (SelectedItems() is [var only]) StartRename(only); };
        _delete.Click += (_, _) => Delete(SelectedItems());
        _clear.Click += (_, _) => ClearSelection();
        _editor.Committed += (_, _) => CommitRename();
        _editor.Cancelled += (_, _) => EndRename();
        _pulse.Tick += (_, _) => Animate();
        ApplyTexts();
    }

    /// <summary>Puts the labels in the current language.</summary>
    public void ApplyTexts()
    {
        int filter = Math.Max(0, _filter.SelectedIndex);
        _filter.Items = new[] { Tr("All"), Tr("Recordings"), Tr("Replays"), Tr("Screenshots"), Tr("Favourites") };
        _filter.SetSelectedSilently(filter);
        _refresh.Text = Tr("Refresh");
        _openFolder.Text = Tr("Open folder");
        _selectAll.Text = Tr("Select all");
        _rename.Text = Tr("Rename");
        _delete.Text = Tr("Delete");
        _tip.SetToolTip(_clear, Tr("Cancel the selection (Esc)"));
        UpdateActions();
    }

    // ---- data ---------------------------------------------------------------------------------

    /// <summary>Re-reads the three output folders, keeping the selection and the scroll position where they still apply.</summary>
    public void Reload()
    {
        if (_editing is not null) CommitRename();

        var folders = new[] { _settings.RecordsFolder, _settings.ReplayFolder, _settings.ScreenshotsFolder }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        string replayFolder = FullPath(_settings.ReplayFolder);
        bool replayHasOwnFolder = !string.Equals(replayFolder, FullPath(_settings.RecordsFolder), StringComparison.OrdinalIgnoreCase);

        _all = folders
            .SelectMany(FilesIn)
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new Item(f.FullName, f.Name, f.LastWriteTime, f.Length,
                                  KindOf(f, replayHasOwnFolder ? replayFolder : null)))
            .ToList();
        var present = new HashSet<string>(_all.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        // A favourite deleted outside the app would stay in settings.json forever. One on a drive
        // that is unplugged right now is kept: its folder is missing, not the file.
        if (_settings.FavoriteCaptures.RemoveAll(p => !present.Contains(p) && !File.Exists(p) &&
                                                      Directory.Exists(Path.GetDirectoryName(p))) > 0)
            SettingsTouched?.Invoke(this, EventArgs.Empty);
        _favourites = new HashSet<string>(_settings.FavoriteCaptures, StringComparer.OrdinalIgnoreCase);

        _selected.RemoveWhere(p => !present.Contains(p));
        foreach (var stale in _tiles.Keys.Where(k => !present.Contains(k)).ToArray()) DropTile(stale);
        ApplyFilter();
        LayoutGrid();
    }

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path; }
    }

    private static IEnumerable<FileInfo> FilesIn(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            return directory.Exists
                ? directory.EnumerateFiles().Where(f => f.Extension.ToLowerInvariant() is ".mp4" or ".png").ToList()
                : Enumerable.Empty<FileInfo>();
        }
        catch
        {
            return Enumerable.Empty<FileInfo>();
        }
    }

    /// <summary>
    /// The app's own name prefixes say what a file is. A file renamed here loses its prefix,
    /// so the folder it sits in decides instead.
    /// </summary>
    private static RecentKind KindOf(FileInfo file, string? replayFolder)
    {
        if (file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return RecentKind.Screenshot;
        if (file.Name.StartsWith("Replay_", StringComparison.OrdinalIgnoreCase)) return RecentKind.Replay;
        if (file.Name.StartsWith("Capture_", StringComparison.OrdinalIgnoreCase)) return RecentKind.Recording;
        if (replayFolder is not null && string.Equals(FullPath(file.DirectoryName ?? ""), replayFolder, StringComparison.OrdinalIgnoreCase))
            return RecentKind.Replay;
        return RecentKind.Recording;
    }

    private void ApplyFilter()
    {
        var filter = (Filter)Math.Max(0, _filter.SelectedIndex);
        _items = _all.Where(i => filter switch
        {
            Filter.Recordings => i.Kind == RecentKind.Recording,
            Filter.Replays => i.Kind == RecentKind.Replay,
            Filter.Screenshots => i.Kind == RecentKind.Screenshot,
            Filter.Favourites => _favourites.Contains(i.Path),
            _ => true,
        }).ToArray();
        _scale = new float[_items.Length];
        Array.Fill(_scale, 1f);
        _hovered = -1;
        _anchor = -1;
        UpdateActions();
        Invalidate();
    }

    private List<Item> SelectedItems() => _items.Where(i => _selected.Contains(i.Path)).ToList();

    // ---- layout -------------------------------------------------------------------------------

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutGrid();
    }

    private int GridLeft => PadX;
    private int GridWidth => Math.Max(200, Width - PadX * 2);
    private int GridTop => HeaderHeight;
    private int GridHeight => Math.Max(1, Height - HeaderHeight);
    private int Columns => Math.Max(2, (GridWidth + Gap) / (MinTileWidth + Gap));
    private Size TileSize
    {
        get
        {
            int w = (GridWidth - (Columns - 1) * Gap) / Columns;
            return new Size(w, w * 9 / 16);
        }
    }
    private int ContentHeight => _items.Length == 0 ? 0
        : ((_items.Length + Columns - 1) / Columns) * (TileSize.Height + Gap) + Gap;
    private int MaxScroll => Math.Max(0, ContentHeight - GridHeight);

    private Rectangle TileRect(int index)
    {
        var size = TileSize;
        int col = index % Columns, row = index / Columns;
        return new Rectangle(GridLeft + col * (size.Width + Gap), GridTop + 6 + row * (size.Height + Gap) - _scroll,
                             size.Width, size.Height);
    }

    private static Rectangle CheckRect(Rectangle tile) => new(tile.X + 8, tile.Y + 8, Knob, Knob);
    private static Rectangle StarRect(Rectangle tile) => new(tile.Right - 8 - Knob, tile.Y + 8, Knob, Knob);

    /// <summary>The file name and the pencil after it - the area that renames on click.</summary>
    private Rectangle NameRect(int index)
    {
        var tile = TileRect(index);
        int text = (int)Math.Ceiling(Measurer.MeasureString(DisplayName(_items[index]), Theme.BodyBold, PointF.Empty, OneLine).Width);
        int width = Math.Min(text + 36, tile.Width - 32);
        return new Rectangle(tile.X + 24, tile.Bottom - 45, width, 24);
    }

    private static string DisplayName(Item item) => Path.GetFileNameWithoutExtension(item.Name);

    private void LayoutGrid()
    {
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        if (_tileSizeRendered != TileSize)
        {
            // Tiles are pre-rendered at one size; a new size means fresh ones.
            foreach (var path in _tiles.Keys.ToArray()) DropTile(path);
            _tileSizeRendered = TileSize;
        }
        LayoutHeader();
        PlaceEditor();
        Invalidate();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LayoutHeader()
    {
        const int h = 34;
        int right = Width - PadX;
        _filter.SetBounds(PadX, 62, Math.Min(GridWidth / 2 + 120, 560), 32);

        // One row of actions at a time: the selection's when there is one, the page's otherwise.
        bool selecting = _selected.Count > 0;
        foreach (var b in new[] { _refresh, _openFolder }) b.Visible = !selecting;
        foreach (var b in new[] { _selectAll, _favourite, _rename, _delete, _clear }) b.Visible = selecting;
        var row = selecting
            ? new[] { _selectAll, _favourite, _rename, _delete, _clear }
            : new[] { _refresh, _openFolder };
        for (int i = row.Length - 1; i >= 0; i--)
        {
            var b = row[i];
            int w = string.IsNullOrEmpty(b.Text) ? 34 : TextRenderer.MeasureText(b.Text, b.Font).Width + 52;
            b.SetBounds(right - w, 14, w, h);
            right -= w + 8;
        }
    }

    private void UpdateActions()
    {
        var selection = SelectedItems();
        bool allFavourite = selection.Count > 0 && selection.All(i => _favourites.Contains(i.Path));
        _favourite.Text = allFavourite ? Tr("Unfavourite") : Tr("Favourite");
        _favourite.Glyph = allFavourite ? Icon.StarFilled : Icon.Star;
        _rename.Enabled = selection.Count == 1;
        LayoutHeader();
        Invalidate(new Rectangle(0, 0, Width, GridTop));
    }

    // ---- scrolling, used by the panel's wheel routing and its scroll thumb ---------------------

    public float ScrollRatio => MaxScroll == 0 ? 0f : _scroll / (float)MaxScroll;
    public float ViewRatio => ContentHeight <= GridHeight ? 1f : GridHeight / (float)ContentHeight;

    public void ScrollBy(int delta) => SetScroll(_scroll - delta);
    public void ScrollToRatio(float ratio) => SetScroll((int)Math.Round(MaxScroll * Math.Clamp(ratio, 0f, 1f)));

    private void SetScroll(int value)
    {
        value = Math.Clamp(value, 0, MaxScroll);
        if (value == _scroll) return;
        if (_editing is not null) CommitRename();
        _scroll = value;
        _hovered = -1;
        Invalidate();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- painting -----------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Shell);

        if (e.ClipRectangle.Top < GridTop)
        {
            // Where the page title used to be: how much is here, or how much is selected.
            string summary = _selected.Count > 0
                ? Tr("Selected: {0}", _selected.Count)
                : Tr("Files: {0}", _items.Length) + "     " + FormatBytes(_items.Sum(i => i.Size));
            int room = (_selected.Count > 0 ? _selectAll.Left : _refresh.Left) - PadX - 16;
            TextRenderer.DrawText(g, summary, _selected.Count > 0 ? Theme.Heading : Theme.Body,
                new Rectangle(PadX, 14, Math.Max(0, room), 34),
                _selected.Count > 0 ? Theme.Accent : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        g.SetClip(new Rectangle(0, GridTop, Width, GridHeight), CombineMode.Intersect);
        if (_items.Length == 0)
        {
            bool favourites = (Filter)Math.Max(0, _filter.SelectedIndex) == Filter.Favourites;
            string empty = favourites
                ? Tr("No favourites yet. Click the star on a capture to keep it here.")
                : Tr("Nothing here yet. Recordings, replays and screenshots show up here.");
            TextRenderer.DrawText(g, empty, Theme.Body, new Rectangle(PadX, GridTop + 40, GridWidth, 40), Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Plain anti-aliasing: grid-fitted text would hop between pixel sizes during the zoom.
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        var (first, last) = VisibleRange();
        // The hovered tile last, so its zoom is never covered by a neighbour.
        for (int i = first; i <= last; i++)
            if (i != _hovered && Rectangle.Inflate(TileRect(i), 8, 8).IntersectsWith(e.ClipRectangle)) DrawTile(g, i);
        if (_hovered >= first && _hovered <= last) DrawTile(g, _hovered);
        TrimCache(first, last);
    }

    private (int First, int Last) VisibleRange()
    {
        int pitch = TileSize.Height + Gap;
        int firstRow = Math.Max(0, (_scroll - 6) / pitch);
        int lastRow = (_scroll + GridHeight) / pitch;
        return (Math.Min(_items.Length - 1, firstRow * Columns), Math.Min(_items.Length - 1, (lastRow + 1) * Columns - 1));
    }

    private void DrawTile(Graphics g, int index)
    {
        var item = _items[index];
        var rect = TileRect(index);
        float scale = index < _scale.Length ? _scale[index] : 1f;
        bool selected = _selected.Contains(item.Path);
        bool hovered = index == _hovered;
        bool favourite = _favourites.Contains(item.Path);

        var state = g.Save();
        if (scale != 1f)
        {
            // Everything on the tile - picture, name, badges - zooms together about its centre.
            float cx = rect.X + rect.Width / 2f, cy = rect.Y + rect.Height / 2f;
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-cx, -cy);
            g.InterpolationMode = InterpolationMode.Bilinear;
        }
        else g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        if (_tiles.TryGetValue(item.Path, out var tile)) g.DrawImage(tile, rect);
        else
        {
            using var shape = FlatButton.Rounded(rect, Corner);
            using var back = new SolidBrush(Theme.Field);
            g.FillPath(back, shape);
            RequestThumbnail(item.Path);
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;

        float hoverT = Math.Clamp((scale - 1f) / (HoverScale - 1f), 0f, 1f);
        if (hoverT > 0.01f || selected)
            using (var shape = FlatButton.Rounded(rect, Corner))
            {
                if (hoverT > 0.01f)
                    using (var wash = new SolidBrush(Color.FromArgb((int)(30 * hoverT), Color.White)))
                        g.FillPath(wash, shape);
                if (selected)
                    using (var ring = new Pen(Theme.Accent, 3f))
                        g.DrawPath(ring, shape);
            }

        DrawCaption(g, index, item, rect);

        // The check shows on hover, and on every tile once something is selected, so the way
        // into selecting is visible without cluttering the grid at rest.
        if (selected || hovered || _selected.Count > 0)
        {
            var check = CheckRect(rect);
            bool hot = hovered && _hoveredPart == Part.Check;
            using (var fill = new SolidBrush(selected ? Theme.Accent : Color.FromArgb(hot ? 200 : 140, 0, 0, 0)))
                g.FillEllipse(fill, check);
            using (var edge = new Pen(selected ? Theme.Accent : Color.FromArgb(230, 255, 255, 255), 1.6f))
                g.DrawEllipse(edge, check);
            if (selected || hot)
                Icons.Draw(g, Icon.Check, RectangleF.Inflate(check, -6.5f, -6.5f), Color.White, 2f);
        }

        if (favourite || hovered)
        {
            var star = StarRect(rect);
            bool hot = hovered && _hoveredPart == Part.Star;
            using (var fill = new SolidBrush(Color.FromArgb(hot ? 200 : 140, 0, 0, 0))) g.FillEllipse(fill, star);
            Icons.Draw(g, favourite ? Icon.StarFilled : Icon.Star, RectangleF.Inflate(star, -5.5f, -5.5f),
                favourite ? Theme.Accent : Color.White, 1.5f);
        }

        g.Restore(state);
    }

    /// <summary>
    /// Name and details, drawn with GDI+ rather than TextRenderer so they follow the tile's zoom.
    /// Over the name, a frame and a pencil say it can be clicked to rename.
    /// </summary>
    private void DrawCaption(Graphics g, int index, Item item, Rectangle rect)
    {
        bool editing = item.Path == _editing;
        bool nameHot = index == _hovered && _hoveredPart == Part.Name;
        var nameRect = NameRect(index);
        if (!editing)
        {
            if (nameHot)
            {
                using var frame = FlatButton.Rounded(nameRect, 5);
                using var fill = new SolidBrush(Color.FromArgb(150, 20, 22, 25));
                using var edge = new Pen(Color.FromArgb(150, 255, 255, 255));
                g.FillPath(fill, frame);
                g.DrawPath(edge, frame);
                Icons.Draw(g, Icon.Pencil, new RectangleF(nameRect.Right - 20, nameRect.Y + 5, 13, 13), Theme.Accent, 1.5f);
            }
            Icons.Draw(g, CaptureTile.IconFor(item.Kind), new RectangleF(rect.X + 9, rect.Bottom - 39, 13, 13), Color.White, 1.4f);
            using var white = new SolidBrush(Color.White);
            g.DrawString(DisplayName(item), Theme.BodyBold, white,
                new RectangleF(nameRect.X + 5, nameRect.Y, nameRect.Width - (nameHot ? 29 : 6), nameRect.Height), OneLine);
        }
        using (var dim = new SolidBrush(Color.FromArgb(205, 210, 216)))
            g.DrawString($"{item.Time:g}   {FormatBytes(item.Size)}", Theme.Small, dim,
                new RectangleF(rect.X + 29, rect.Bottom - 22, rect.Width - 38, 16), OneLine);
    }

    private static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : $"{bytes / (double)(1L << 20):0.0} MB";

    // ---- tiles --------------------------------------------------------------------------------

    private void RequestThumbnail(string path)
    {
        if (_tiles.ContainsKey(path) || !_pending.TryAdd(path, 0)) return;
        var size = TileSize;
        Task.Run(() =>
        {
            Bitmap? tile = null;
            using (var thumb = ShellThumbnails.Load(path, Math.Max(64, Math.Max(size.Width, size.Height) * 3 / 2)))
                if (thumb is not null) tile = RenderTile(thumb, size);
            try
            {
                BeginInvoke(() =>
                {
                    _pending.TryRemove(path, out _);
                    bool wanted = tile is not null && !IsDisposed && size == TileSize &&
                                  _all.Exists(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (!wanted) { tile?.Dispose(); return; }
                    DropTile(path);
                    _tiles[path] = tile!;
                    InvalidateTile(Array.FindIndex(_items, i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)));
                });
            }
            catch
            {
                // No handle to marshal onto (not shown yet, or gone): free the slot so a later paint retries.
                _pending.TryRemove(path, out _);
                tile?.Dispose();
            }
        });
    }

    /// <summary>The thumbnail cropped to the tile, with rounded corners and the shade the caption sits on, baked in.</summary>
    private static Bitmap RenderTile(Bitmap thumb, Size size)
    {
        using var cover = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(cover))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            CaptureTile.DrawCover(g, thumb, new Rectangle(Point.Empty, size));
            var shade = new Rectangle(0, size.Height - 58, size.Width, 58);
            using var brush = new LinearGradientBrush(new Point(0, shade.Y - 1), new Point(0, shade.Bottom),
                                                      Color.FromArgb(0, 0, 0, 0), Color.FromArgb(215, 0, 0, 0));
            g.FillRectangle(brush, shade);
        }

        var tile = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(tile))
        using (var texture = new TextureBrush(cover))
        using (var shape = FlatButton.Rounded(new Rectangle(0, 0, size.Width - 1, size.Height - 1), Corner))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(texture, shape);
        }
        return tile;
    }

    private void DropTile(string path)
    {
        if (_tiles.Remove(path, out var bmp)) bmp.Dispose();
    }

    private void TrimCache(int first, int last)
    {
        if (_tiles.Count <= CacheLimit) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = Math.Max(0, first - Columns * 2); i <= Math.Min(_items.Length - 1, last + Columns * 2); i++)
            keep.Add(_items[i].Path);
        foreach (var path in _tiles.Keys.Where(p => !keep.Contains(p)).ToArray()) DropTile(path);
    }

    private void InvalidateTile(int index)
    {
        if (index < 0 || index >= _items.Length) return;
        var r = Rectangle.Inflate(TileRect(index), 10, 10);
        r.Intersect(new Rectangle(0, GridTop, Width, GridHeight));
        if (!r.IsEmpty) Invalidate(r);
    }

    // ---- mouse --------------------------------------------------------------------------------

    private (int Index, Part Part) HitTest(Point point)
    {
        if (point.Y < GridTop || _items.Length == 0) return (-1, Part.None);
        var (first, last) = VisibleRange();
        for (int i = first; i <= last; i++)
        {
            var rect = TileRect(i);
            if (!rect.Contains(point)) continue;
            if (Inflated(CheckRect(rect)).Contains(point)) return (i, Part.Check);
            if (Inflated(StarRect(rect)).Contains(point)) return (i, Part.Star);
            if (NameRect(i).Contains(point)) return (i, Part.Name);
            return (i, Part.Tile);
        }
        return (-1, Part.None);
    }

    private static Rectangle Inflated(Rectangle r) => Rectangle.Inflate(r, 4, 4);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (hit, part) = HitTest(e.Location);
        if (hit == _hovered && part == _hoveredPart) return;
        int previous = _hovered;
        bool tileChanged = hit != _hovered;
        _hovered = hit;
        _hoveredPart = part;
        Cursor = part == Part.Name ? Cursors.IBeam : hit >= 0 ? Cursors.Hand : Cursors.Default;
        if (part == Part.Name) _tip.SetToolTip(this, Tr("Click to rename"));
        else if (tileChanged)
            _tip.SetToolTip(this, hit >= 0 ? $"{_items[hit].Name}\n{_items[hit].Time:g}   {FormatBytes(_items[hit].Size)}" : null);
        InvalidateTile(previous);
        InvalidateTile(hit);
        StartPulse();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered == -1) return;
        InvalidateTile(_hovered);
        _hovered = -1;
        _tip.SetToolTip(this, null);
        StartPulse();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(e.Delta > 0 ? 90 : -90);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var (hit, part) = HitTest(e.Location);
        if (hit < 0)
        {
            if (e.Button == MouseButtons.Left && e.Y >= GridTop) ClearSelection();
            return;
        }
        var item = _items[hit];

        if (e.Button == MouseButtons.Right)
        {
            ShowMenu(_selected.Contains(item.Path) ? SelectedItems() : new List<Item> { item }, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        bool modified = ModifierKeys.HasFlag(Keys.Shift) || ModifierKeys.HasFlag(Keys.Control);
        if (part == Part.Name && !modified) { StartRename(item); return; }
        if (part == Part.Star) { ToggleFavourite(new List<Item> { item }); return; }
        if (ModifierKeys.HasFlag(Keys.Shift) && _anchor >= 0) { SelectRange(_anchor, hit); return; }
        if (part == Part.Check || ModifierKeys.HasFlag(Keys.Control) || _selected.Count > 0) { ToggleSelected(hit); return; }
        Open(item);
    }

    // ---- selection ----------------------------------------------------------------------------

    private void ToggleSelected(int index)
    {
        string path = _items[index].Path;
        if (!_selected.Remove(path)) _selected.Add(path);
        _anchor = index;
        SelectionChanged();
    }

    private void SelectRange(int from, int to)
    {
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _selected.Add(_items[i].Path);
        SelectionChanged();
    }

    public void SelectAll()
    {
        foreach (var item in _items) _selected.Add(item.Path);
        SelectionChanged();
    }

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        _anchor = -1;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        UpdateActions();
        Invalidate();
    }

    /// <summary>Keys for the page, offered by the panel before its shortcuts. True when used.</summary>
    public bool HandleKey(Keys keyData)
    {
        // While a name is being typed, every key belongs to the text box.
        if (_editing is not null) return false;
        switch (keyData)
        {
            case Keys.Control | Keys.A: SelectAll(); return true;
            case Keys.Delete: if (_selected.Count == 0) return false; Delete(SelectedItems()); return true;
            case Keys.F2: if (SelectedItems() is not [var only]) return false; StartRename(only); return true;
            case Keys.Escape: if (_selected.Count == 0) return false; ClearSelection(); return true;
            default: return false;
        }
    }

    // ---- renaming in place --------------------------------------------------------------------

    private void StartRename(Item item)
    {
        if (_editing is not null) CommitRename();
        int index = Array.FindIndex(_items, i => i.Path == item.Path);
        if (index < 0) return;

        // Bring the tile fully into view, then park it at its resting size under the editor.
        var rect = TileRect(index);
        if (rect.Top < GridTop) SetScroll(_scroll - (GridTop - rect.Top) - 6);
        else if (rect.Bottom > Height) SetScroll(_scroll + rect.Bottom - Height + 6);
        _scale[index] = 1f;
        _hovered = -1;

        _editing = item.Path;
        _editor.Text = DisplayName(item);
        PlaceEditor();
        _editor.Visible = true;
        _editor.Focus();
        _editor.SelectAll();
        InvalidateTile(index);
    }

    private void PlaceEditor()
    {
        if (_editing is null) return;
        int index = Array.FindIndex(_items, i => i.Path == _editing);
        if (index < 0) return;
        var tile = TileRect(index);
        _editor.SetBounds(tile.X + 24, tile.Bottom - 46, tile.Width - 32, 26);
    }

    private void EndRename()
    {
        if (_editing is null) return;
        int index = Array.FindIndex(_items, i => i.Path == _editing);
        _editing = null;
        _editor.Visible = false;
        Focus();
        InvalidateTile(index);
    }

    private void CommitRename()
    {
        string? path = _editing;
        if (path is null) return;
        string name = _editor.Text.Trim();
        EndRename();
        var item = _all.FirstOrDefault(i => i.Path == path);
        if (item is not null && name.Length > 0 && name != DisplayName(item)) Rename(item, name);
    }

    private void Rename(Item item, string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(FindForm(), Tr("A file name can't contain any of these characters:") + "  \\ / : * ? \" < > |",
                Tr("Rename"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string target = Path.Combine(Path.GetDirectoryName(item.Path)!, name + Path.GetExtension(item.Path));
        if (File.Exists(target) && !string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(FindForm(), Tr("There is already a file with that name."), Tr("Rename"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try { File.Move(item.Path, target); }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), Tr("Couldn't rename this file: {0}", ex.Message), Tr("Rename"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_favourites.Remove(item.Path)) { _favourites.Add(target); SaveFavourites(); }
        if (_selected.Remove(item.Path)) _selected.Add(target);
        if (_tiles.Remove(item.Path, out var bmp)) _tiles[target] = bmp;
        Reload();
    }

    /// <summary>
    /// The text box laid over a name while it is renamed. Enter and Esc are taken here, before
    /// the panel's own shortcuts see them - Esc would otherwise close the whole panel.
    /// </summary>
    private sealed class NameEditor : TextBox
    {
        public event EventHandler? Committed;
        public event EventHandler? Cancelled;

        public NameEditor()
        {
            Visible = false;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Theme.Field;
            ForeColor = Theme.Text;
            Font = Theme.BodyBold;
            MaxLength = 120;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) { Committed?.Invoke(this, EventArgs.Empty); return true; }
            if (keyData == Keys.Escape) { Cancelled?.Invoke(this, EventArgs.Empty); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (Visible) Committed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- other actions ------------------------------------------------------------------------

    private void Open(Item item)
    {
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
        catch { /* moved or deleted since the grid was built */ }
    }

    private static void ShowInFolder(Item item)
    {
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); }
        catch { }
    }

    private void ToggleFavourite(List<Item> items)
    {
        if (items.Count == 0) return;
        bool remove = items.All(i => _favourites.Contains(i.Path));
        foreach (var item in items)
        {
            if (remove) _favourites.Remove(item.Path);
            else _favourites.Add(item.Path);
        }
        SaveFavourites();
        if ((Filter)Math.Max(0, _filter.SelectedIndex) == Filter.Favourites) ApplyFilter();
        UpdateActions();
        Invalidate();
    }

    private void SaveFavourites()
    {
        _settings.FavoriteCaptures = _favourites.ToList();
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>To the Recycle Bin, so a wrong click can still be taken back.</summary>
    private void Delete(List<Item> items)
    {
        if (items.Count == 0) return;
        string question = items.Count == 1
            ? Tr("Move \"{0}\" to the Recycle Bin?", items[0].Name)
            : Tr("Move the selected captures to the Recycle Bin?") + "\n\n" + Tr("Selected: {0}", items.Count);
        if (MessageBox.Show(FindForm(), question, Tr("Delete"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        int failed = 0;
        foreach (var item in items)
        {
            try
            {
                FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                _favourites.Remove(item.Path);
                _selected.Remove(item.Path);
            }
            catch { failed++; }
        }
        SaveFavourites();
        Reload();
        if (failed > 0)
            MessageBox.Show(FindForm(), Tr("Some files couldn't be deleted. They may be open in another program."),
                Tr("Delete"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowMenu(List<Item> items, Point at)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.Body };
        if (items.Count == 1)
        {
            var only = items[0];
            menu.Items.Add(Tr("Open"), null, (_, _) => Open(only));
            menu.Items.Add(Tr("Show in folder"), null, (_, _) => ShowInFolder(only));
            menu.Items.Add(new ToolStripSeparator());
        }
        bool allFavourite = items.All(i => _favourites.Contains(i.Path));
        menu.Items.Add(allFavourite ? Tr("Unfavourite") : Tr("Favourite"), null, (_, _) => ToggleFavourite(items));
        if (items.Count == 1)
            menu.Items.Add(new ToolStripMenuItem(Tr("Rename"), null, (_, _) => StartRename(items[0])) { ShortcutKeyDisplayString = "F2" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Tr("Delete"), null, (_, _) => Delete(items)) { ShortcutKeyDisplayString = Tr("Del") });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, at);
    }

    // ---- animation ----------------------------------------------------------------------------

    private void StartPulse()
    {
        if (_pulse.Enabled) return;
        _lastTick = _clock.ElapsedMilliseconds;
        _pulse.Start();
    }

    /// <summary>
    /// Eases each tile toward its hover size by elapsed time rather than by tick, so a late
    /// timer tick makes a bigger step instead of a visible stall, and repaints only the tiles
    /// that moved.
    /// </summary>
    private void Animate()
    {
        long now = _clock.ElapsedMilliseconds;
        float k = (float)(1 - Math.Exp(-(now - _lastTick) / EaseMs));
        _lastTick = now;

        bool moving = false;
        for (int i = 0; i < _scale.Length; i++)
        {
            float target = i == _hovered && _items[i].Path != _editing ? HoverScale : 1f;
            if (_scale[i] == target) continue;
            float next = _scale[i] + (target - _scale[i]) * k;
            if (Math.Abs(target - next) < 0.0005f) next = target;
            else moving = true;
            _scale[i] = next;
            InvalidateTile(i);
        }
        if (!moving) _pulse.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulse.Dispose();
            _tip.Dispose();
            foreach (var bmp in _tiles.Values) bmp.Dispose();
            _tiles.Clear();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The right-click menu in the panel's colours rather than the light system ones.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new Colours()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
        base.OnRenderItemText(e);
    }

    private sealed class Colours : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Field;
        public override Color ImageMarginGradientBegin => Theme.Field;
        public override Color ImageMarginGradientMiddle => Theme.Field;
        public override Color ImageMarginGradientEnd => Theme.Field;
        public override Color MenuBorder => Theme.FieldBorder;
        public override Color MenuItemBorder => Theme.Hover;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color MenuItemSelectedGradientBegin => Theme.Hover;
        public override Color MenuItemSelectedGradientEnd => Theme.Hover;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Field;
    }
}

/// <summary>Thin peak meter - the fastest way to tell whether audio is arriving at all.</summary>
internal sealed class LevelMeter : Control
{
    private float _level;

    public LevelMeter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        TabStop = false;
    }

    public void SetLevel(float level)
    {
        if (Math.Abs(level - _level) < 0.01f) return;
        _level = level;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        using (var back = new SolidBrush(Theme.Track))
            g.FillRectangle(back, 0, 0, Width, Height);

        int filled = (int)(Math.Clamp(_level, 0f, 1f) * Width);
        if (filled <= 0) return;
        var colour = _level > 0.95f ? Theme.Record : _level > 0.7f ? Color.FromArgb(226, 186, 92) : Theme.Good;
        using var brush = new SolidBrush(colour);
        g.FillRectangle(brush, 0, 0, filled, Height);
    }
}
