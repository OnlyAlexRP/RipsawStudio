using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// The little card that appears top-left the moment a recording, screenshot or instant
/// replay lands: the file's own thumbnail, with a badge saying which of the three it was.
///
/// A child control, like the other overlays, rather than an owned window like
/// <see cref="ControlPanel"/>. A child control over the D3D swap chain has no opacity that
/// composites onto it, so this animates
/// by moving instead of fading: in from above, then back out through the top edge, where the
/// parent's clipping hides it.
/// </summary>
internal sealed class CaptureToast : Control
{
    private const int ThumbWidth = 192;
    private const int ThumbHeight = 108;   // 16:9
    private const int BadgeSize = 22;

    /// <summary>The card slides in over this many ms.</summary>
    private const int SlideInMs = 160;
    /// <summary>How long it sits still before leaving.</summary>
    private const int HoldMs = 1600;
    /// <summary>The slide out. Deliberately quick - it is an acknowledgement, not a dialog.</summary>
    private const int SlideOutMs = 260;

    /// <summary>The shell can need a moment before it will hand out a thumbnail for a file
    /// that was being written a heartbeat ago, so a miss is retried rather than given up on.</summary>
    private const int ThumbnailAttempts = 6;
    private const int ThumbnailRetryMs = 250;

    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    private readonly System.Diagnostics.Stopwatch _clock = new();

    private Bitmap? _thumbnail;
    private RecentKind _kind;
    private string _path = "";
    /// <summary>Bumped on every Show so a thumbnail that arrives late for a previous capture is dropped.</summary>
    private int _generation;

    /// <summary>Where the card rests. Set by MainForm's layout, in parent coordinates.</summary>
    private Point _anchor;

    private enum Phase { Idle, SlideIn, Hold, SlideOut }
    private Phase _phase = Phase.Idle;

    public CaptureToast()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // What the tile is filled with before a thumbnail arrives; OnPaint covers the rest.
        BackColor = Theme.Field;
        Size = new Size(ThumbWidth, ThumbHeight);
        Cursor = Cursors.Hand;
        Visible = false;
        TabStop = false;
        _animation.Tick += (_, _) => Step();
    }

    /// <summary>Resting position in the parent. Safe to call while one is on screen.</summary>
    public void SetAnchor(Point parentTopLeft)
    {
        _anchor = parentTopLeft;
        if (_phase is Phase.SlideIn or Phase.Hold) Location = _anchor;
    }

    /// <summary>
    /// Announces a freshly written file. The card goes up at once with a placeholder tile and
    /// swaps in the real thumbnail as soon as the shell produces one.
    /// </summary>
    public void Show(string path, RecentKind kind)
    {
        _generation++;
        _path = path;
        _kind = kind;

        _thumbnail?.Dispose();
        _thumbnail = null;

        _phase = Phase.SlideIn;
        _clock.Restart();
        // Starts fully above its resting place, outside the parent, and is clipped until it
        // has travelled far enough to show.
        Location = new Point(_anchor.X, -Height);
        Visible = true;
        BringToFront();
        Invalidate();
        if (!_animation.Enabled) _animation.Start();

        // After the control is live, so the load always has a handle to marshal its result
        // back to - the very first capture of a session would otherwise race handle creation
        // and silently keep the placeholder tile.
        LoadThumbnail(path, _generation);
    }

    /// <summary>
    /// Off the UI thread: a cold shell thumbnail for an .mp4 is a full decode and can take a
    /// noticeable moment. Failures are silent - the placeholder tile is a fine fallback.
    /// </summary>
    private void LoadThumbnail(string path, int generation)
    {
        int pixelSize = Math.Max(Width, Height) * 2;
        Task.Run(async () =>
        {
            Bitmap? bmp = null;
            for (int attempt = 0; attempt < ThumbnailAttempts && bmp is null; attempt++)
            {
                if (attempt > 0) await Task.Delay(ThumbnailRetryMs).ConfigureAwait(false);
                bmp = ShellThumbnails.Load(path, pixelSize);
            }
            if (bmp is null) return;

            try
            {
                BeginInvoke(() =>
                {
                    // A newer capture took the card over, this one has already slid away, or
                    // the control is gone - in every case the bitmap has nowhere to be drawn.
                    if (generation != _generation || _phase == Phase.Idle || IsDisposed) { bmp.Dispose(); return; }
                    _thumbnail?.Dispose();
                    _thumbnail = bmp;
                    Invalidate();
                });
            }
            catch
            {
                // No handle yet, or it has been destroyed - nothing marshalled, so free it here.
                bmp.Dispose();
            }
        });
    }

    /// <summary>One tick of the slide-in, hold and slide-out sequence.</summary>
    private void Step()
    {
        double elapsed = _clock.Elapsed.TotalMilliseconds;

        switch (_phase)
        {
            case Phase.SlideIn:
            {
                double t = Math.Clamp(elapsed / SlideInMs, 0, 1);
                double eased = 1 - Math.Pow(1 - t, 3);          // ease-out: quick, then settles
                Top = (int)Math.Round(-Height + (_anchor.Y + Height) * eased);
                if (t >= 1) { Location = _anchor; _phase = Phase.Hold; _clock.Restart(); }
                break;
            }

            case Phase.Hold:
                if (elapsed >= HoldMs) { _phase = Phase.SlideOut; _clock.Restart(); }
                break;

            case Phase.SlideOut:
            {
                double t = Math.Clamp(elapsed / SlideOutMs, 0, 1);
                double eased = t * t;                            // ease-in: accelerates away
                Top = (int)Math.Round(_anchor.Y - (_anchor.Y + Height) * eased);
                if (t >= 1) Finish();
                break;
            }
        }
    }

    private void Finish()
    {
        _animation.Stop();
        _clock.Stop();
        _phase = Phase.Idle;
        Visible = false;
        _thumbnail?.Dispose();
        _thumbnail = null;
    }

    /// <summary>Opens what was just captured. Dismisses either way, so a click never leaves it hanging.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        string path = _path;
        Finish();
        if (path.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Edge to edge: the thumbnail is the whole card, and Theme.Field only shows in the
        // moment before the thumbnail arrives.
        var thumb = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        using (var back = new SolidBrush(Theme.Field)) g.FillRectangle(back, thumb);
        if (_thumbnail is not null) CaptureTile.DrawCover(g, _thumbnail, thumb);

        var badge = new RectangleF(8, ClientSize.Height - 8 - BadgeSize, BadgeSize, BadgeSize);
        using (var badgeBack = new SolidBrush(Color.FromArgb(190, 0, 0, 0))) g.FillEllipse(badgeBack, badge);
        Icons.Draw(g, CaptureTile.IconFor(_kind), RectangleF.Inflate(badge, -BadgeSize * 0.22f, -BadgeSize * 0.22f), Color.White, 1.5f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
            _thumbnail?.Dispose();
        }
        base.Dispose(disposing);
    }
}
