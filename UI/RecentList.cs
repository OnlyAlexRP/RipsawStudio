using System.Collections.Concurrent;
using System.Drawing.Drawing2D;

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

/// <summary>What a recent file is, so the tile can badge it - inferred from this app's own
/// naming (see <see cref="MainForm"/>'s Capture_/Replay_/Shot_ prefixes) rather than stored
/// anywhere, so it keeps working for files dropped in from an older version.</summary>
internal enum RecentKind { Recording, Replay, Screenshot }

internal readonly record struct RecentItem(string Path, string Name, string Detail, RecentKind Kind);

/// <summary>Drawing shared by the Captures grid and the card that announces a new capture.</summary>
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

        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(bmp, dest, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attributes);
    }
}

/// <summary>
/// Recent captures in the output folder, shown as a grid of thumbnails rather than a list of
/// filenames - click one to open it. Thumbnails come from the shell's own thumbnail cache
/// (see <see cref="ShellThumbnails"/>), fetched off the UI thread since a cold one can take a
/// moment, and cached here by path so a reload does not refetch what it already has.
/// </summary>
internal sealed class RecentList : Control
{
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 2;
    private const int Gap = 8;
    private const int Corner = 6;
    private const int BadgeSize = 18;
    /// <summary>How much a hovered tile grows - kept small on purpose; this is meant to read
    /// as "smooth", not as a pop.</summary>
    private const float HoverScale = 1.06f;

    private RecentItem[] _items = Array.Empty<RecentItem>();
    private readonly Dictionary<string, Bitmap> _thumbnails = new();
    /// <summary>Paths with a fetch in flight. A ConcurrentDictionary (used as a set via its
    /// keys) because, unlike the success path below, the cleanup on a failed marshal has to
    /// run from the background thread itself - see <see cref="RequestThumbnail"/>.</summary>
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private float[] _scale = Array.Empty<float>();
    private int _hovered = -1;
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 15 };
    private readonly ToolTip _tip = new() { AutomaticDelay = 300, ReshowDelay = 100, InitialDelay = 300 };

    public RecentList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        TabStop = false;
        _pulse.Tick += (_, _) => Animate();
    }

    /// <summary>Merges the newest files across all of the given folders (Records, Replay, Screenshots).</summary>
    public void Reload(params string[] folders)
    {
        try
        {
            _items = folders
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .SelectMany(FilesIn)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(Columns * Rows)
                .Select(f => new RecentItem(f.FullName, f.Name,
                    $"{f.LastWriteTime:dd MMM HH:mm}   {f.Length / 1024.0 / 1024.0:0.0} MB", KindOf(f.Name)))
                .ToArray();
        }
        catch
        {
            _items = Array.Empty<RecentItem>();
        }

        _scale = new float[_items.Length];
        Array.Fill(_scale, 1f);
        _hovered = -1;
        _tip.SetToolTip(this, null);

        // Drop cached thumbnails for files that fell out of the grid, so a long session
        // cannot pile up GDI bitmaps for clips nobody is looking at any more.
        var keep = new HashSet<string>(_items.Select(i => i.Path));
        foreach (var stale in _thumbnails.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            _thumbnails[stale].Dispose();
            _thumbnails.Remove(stale);
        }

        foreach (var item in _items) RequestThumbnail(item.Path);
        Invalidate();
    }

    /// <summary>Recording and replay are both .mp4 and cannot be told apart by extension - the
    /// filename prefix this app itself writes them with is what distinguishes them.</summary>
    private static RecentKind KindOf(string fileName)
    {
        if (fileName.StartsWith("Replay_", StringComparison.OrdinalIgnoreCase)) return RecentKind.Replay;
        if (fileName.StartsWith("Shot_", StringComparison.OrdinalIgnoreCase)) return RecentKind.Screenshot;
        if (string.Equals(Path.GetExtension(fileName), ".png", StringComparison.OrdinalIgnoreCase)) return RecentKind.Screenshot;
        return RecentKind.Recording;
    }

    private static IEnumerable<FileInfo> FilesIn(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            return !directory.Exists
                ? Enumerable.Empty<FileInfo>()
                : directory.EnumerateFiles().Where(f => f.Extension is ".mp4" or ".png");
        }
        catch
        {
            return Enumerable.Empty<FileInfo>();
        }
    }

    private void RequestThumbnail(string path)
    {
        if (_thumbnails.ContainsKey(path) || !_pending.TryAdd(path, 0)) return;

        var tile = TileSize();
        int pixelSize = Math.Max(64, Math.Max(tile.Width, tile.Height) * 2);
        Task.Run(() =>
        {
            var bmp = ShellThumbnails.Load(path, pixelSize);
            try
            {
                BeginInvoke(() =>
                {
                    _pending.TryRemove(path, out _);
                    if (bmp is not null && Array.Exists(_items, i => i.Path == path))
                    {
                        _thumbnails[path] = bmp;
                        Invalidate();
                    }
                    else bmp?.Dispose();
                });
            }
            catch
            {
                // BeginInvoke throws when the handle does not exist yet - notably, the very
                // first Reload() of a session, which ControlPanel's ShowPage can trigger
                // before the panel window has ever been shown - or when it has since
                // been destroyed. Either way the marshal above never ran, so path has to be
                // freed here instead: left in _pending, it would silently block every future
                // Reload() from ever retrying this file's thumbnail for the rest of the
                // session, even once the handle exists again.
                _pending.TryRemove(path, out _);
                bmp?.Dispose();
            }
        });
    }

    private Size TileSize()
    {
        int w = (Width - (Columns - 1) * Gap) / Columns;
        int h = (Height - (Rows - 1) * Gap) / Rows;
        return new Size(Math.Max(1, w), Math.Max(1, h));
    }

    private Rectangle TileRect(int index)
    {
        var size = TileSize();
        int col = index % Columns;
        int row = index / Columns;
        return new Rectangle(col * (size.Width + Gap), row * (size.Height + Gap), size.Width, size.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Theme.Card);

        if (_items.Length == 0)
        {
            TextRenderer.DrawText(g, "Nothing recorded yet", Theme.Body,
                new Rectangle(0, 8, Width, 20), Theme.TextFaint, TextFormatFlags.Left);
            return;
        }

        for (int i = 0; i < _items.Length; i++) DrawTile(g, i);
    }

    private void DrawTile(Graphics g, int index)
    {
        var rect = TileRect(index);
        float scale = index < _scale.Length ? _scale[index] : 1f;

        // Each tile zooms about its own centre rather than the grid's, so neighbours do not
        // visibly shift when one of them grows - the "fade zoom" is meant to feel local.
        var state = g.Save();
        float cx = rect.X + rect.Width / 2f, cy = rect.Y + rect.Height / 2f;
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-cx, -cy);

        using (var shape = FlatButton.Rounded(rect, Corner))
        {
            var oldClip = g.Clip;
            try
            {
                g.SetClip(shape, CombineMode.Intersect);
                using (var back = new SolidBrush(Theme.Field)) g.FillRectangle(back, rect);
                if (_thumbnails.TryGetValue(_items[index].Path, out var bmp)) CaptureTile.DrawCover(g, bmp, rect);

                // The zoom alone barely reads as an effect at 6% - a soft wash brightening in
                // step with it is what actually makes the hover look intentional, not jittery.
                float hoverT = Math.Clamp((scale - 1f) / (HoverScale - 1f), 0f, 1f);
                if (hoverT > 0.01f)
                    using (var wash = new SolidBrush(Color.FromArgb((int)(50 * hoverT), Color.White)))
                        g.FillRectangle(wash, rect);
            }
            finally { g.Clip = oldClip; oldClip.Dispose(); }
        }

        var badge = new RectangleF(rect.X + 6, rect.Bottom - 6 - BadgeSize, BadgeSize, BadgeSize);
        using (var badgeBack = new SolidBrush(Color.FromArgb(190, 0, 0, 0))) g.FillEllipse(badgeBack, badge);
        Icons.Draw(g, CaptureTile.IconFor(_items[index].Kind),
            RectangleF.Inflate(badge, -BadgeSize * 0.22f, -BadgeSize * 0.22f), Color.White, 1.5f);

        // Kept off unless the tile is hovered, so the grid is nothing but thumbnails at rest -
        // this is strictly a "clean up one I don't need" affordance, not a default fixture.
        if (index == _hovered)
        {
            var trash = TrashRect(index);
            using (var trashBack = new SolidBrush(Color.FromArgb(200, 130, 32, 32))) g.FillEllipse(trashBack, trash);
            Icons.Draw(g, Icon.Trash,
                RectangleF.Inflate(trash, -BadgeSize * 0.24f, -BadgeSize * 0.24f), Color.White, 1.5f);
        }

        g.Restore(state);
    }

    private RectangleF TrashRect(int index)
    {
        var rect = TileRect(index);
        return new RectangleF(rect.Right - 6 - BadgeSize, rect.Y + 6, BadgeSize, BadgeSize);
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < _items.Length; i++)
            if (TileRect(i).Contains(point)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit == _hovered) return;
        _hovered = hit;
        _tip.SetToolTip(this, hit >= 0 ? $"{_items[hit].Name}\n{_items[hit].Detail}" : null);
        StartPulse();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hovered == -1) return;
        _hovered = -1;
        _tip.SetToolTip(this, null);
        StartPulse();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit < 0) return;

        if (TrashRect(hit).Contains(e.Location)) { DeleteItem(hit); return; }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_items[hit].Path) { UseShellExecute = true });
        }
        catch { /* the file may have been moved since the list was built */ }
    }

    /// <summary>
    /// Removes one file after the user confirms - the per-tile equivalent of the "delete all"
    /// button above the grid, for tidying up a single clip rather than emptying everything.
    /// Updates the grid in place rather than re-scanning the folders, so the remaining tiles
    /// simply close the gap.
    /// </summary>
    private void DeleteItem(int index)
    {
        if (index < 0 || index >= _items.Length) return;
        var item = _items[index];

        if (MessageBox.Show(this, $"Delete \"{item.Name}\"? This can't be undone.",
                "Delete file", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        try { File.Delete(item.Path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't delete this file: " + ex.Message,
                            "Delete file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_thumbnails.TryGetValue(item.Path, out var bmp)) { bmp.Dispose(); _thumbnails.Remove(item.Path); }
        _pending.TryRemove(item.Path, out _);
        _items = _items.Where((_, i) => i != index).ToArray();
        _scale = _scale.Where((_, i) => i != index).ToArray();
        _hovered = -1;
        _tip.SetToolTip(this, null);
        Invalidate();
    }

    private void StartPulse() { if (!_pulse.Enabled) _pulse.Start(); }

    /// <summary>Eases every tile's scale toward its target, stopping itself once settled - same
    /// approach as <see cref="NavBar"/>'s icon hover, so the two animations in this app feel
    /// like the same hand drew them.</summary>
    private void Animate()
    {
        bool moving = false;
        for (int i = 0; i < _scale.Length; i++)
        {
            float target = i == _hovered ? HoverScale : 1f;
            float current = _scale[i];
            float next = current + (target - current) * 0.35f;
            if (Math.Abs(target - next) < 0.002f) next = target;
            else moving = true;
            _scale[i] = next;
        }
        Invalidate();
        if (!moving) _pulse.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulse.Dispose();
            _tip.Dispose();
            foreach (var bmp in _thumbnails.Values) bmp.Dispose();
            _thumbnails.Clear();
        }
        base.Dispose(disposing);
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
