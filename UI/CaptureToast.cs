using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

/// <summary>
/// The little card that appears top-left the moment a recording, screenshot or instant
/// replay lands: the file's own thumbnail, with a badge saying which of the three it was.
/// A layered window like the other overlays, so it is rounded and fades in and out; unlike
/// them it takes clicks, which open the file.
/// </summary>
internal sealed class CaptureToast : Overlay
{
    private const int ThumbWidth = 192;
    private const int ThumbHeight = 108;   // 16:9
    private const int BadgeSize = 22;
    private const int Radius = 10;
    /// <summary>How far it drops into place while fading in.</summary>
    private const int Drop = 10;

    private const int FadeInMs = 180;
    /// <summary>How long it sits still before leaving.</summary>
    private const int HoldMs = 1600;
    /// <summary>Deliberately quick: it is an acknowledgement, not a dialog.</summary>
    private const int FadeOutMs = 260;

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

    /// <summary>Where the card rests, in screen coordinates. Set by MainForm's layout.</summary>
    private Point _anchor;

    private enum Phase { Idle, FadeIn, Hold, FadeOut }
    private Phase _phase = Phase.Idle;

    public CaptureToast()
    {
        Size = new Size(ThumbWidth, ThumbHeight);
        Cursor = Cursors.Hand;
        _animation.Tick += (_, _) => Step();
    }

    protected override bool ClickThrough => false;

    /// <summary>Resting position on screen. Safe to call while one is showing.</summary>
    public void SetAnchor(Point screenTopLeft)
    {
        _anchor = screenTopLeft;
        if (_phase is Phase.Hold or Phase.FadeOut) Location = _anchor;
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

        _phase = Phase.FadeIn;
        _clock.Restart();
        Location = new Point(_anchor.X, _anchor.Y - Drop);
        Alpha = 0;
        Flash(0);
        if (!Visible) { _phase = Phase.Idle; return; }
        if (!_animation.Enabled) _animation.Start();

        // After the window exists, so the load always has a handle to marshal its result back to.
        LoadThumbnail(path, _generation);
    }

    /// <summary>
    /// Off the UI thread: a cold shell thumbnail for an .mp4 is a full decode and can take a
    /// noticeable moment. Failures are silent, the placeholder tile is a fine fallback.
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
                    // A newer capture took the card over, this one has already gone, or the
                    // window is gone: in every case the bitmap has nowhere to be drawn.
                    if (generation != _generation || _phase == Phase.Idle || IsDisposed) { bmp.Dispose(); return; }
                    _thumbnail?.Dispose();
                    _thumbnail = bmp;
                    Render();
                });
            }
            catch
            {
                // No handle, or it has been destroyed: nothing marshalled, so free it here.
                bmp.Dispose();
            }
        });
    }

    /// <summary>One tick of the fade-in, hold and fade-out sequence.</summary>
    private void Step()
    {
        double elapsed = _clock.Elapsed.TotalMilliseconds;
        switch (_phase)
        {
            case Phase.FadeIn:
            {
                double t = Math.Clamp(elapsed / FadeInMs, 0, 1);
                double eased = 1 - Math.Pow(1 - t, 3);   // ease-out: quick, then settles
                Top = _anchor.Y - Drop + (int)Math.Round(Drop * eased);
                Alpha = (byte)(255 * eased);
                if (t >= 1) { Location = _anchor; _phase = Phase.Hold; _clock.Restart(); }
                break;
            }
            case Phase.Hold:
                if (elapsed >= HoldMs) { _phase = Phase.FadeOut; _clock.Restart(); }
                break;
            case Phase.FadeOut:
            {
                double t = Math.Clamp(elapsed / FadeOutMs, 0, 1);
                Alpha = (byte)(255 * (1 - t * t));        // ease-in: accelerates away
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
        Hide();
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

    protected override void Draw(Graphics g)
    {
        using var shape = FlatButton.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        if (_thumbnail is null)
        {
            using var back = new SolidBrush(Glass);
            g.FillPath(back, shape);
        }
        else
        {
            // Filled through a texture rather than clipped, so the rounded edge is anti-aliased.
            using var cover = new Bitmap(Width, Height);
            using (var cg = Graphics.FromImage(cover))
            {
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                CaptureTile.DrawCover(cg, _thumbnail, new Rectangle(0, 0, Width, Height));
            }
            using var texture = new TextureBrush(cover);
            g.FillPath(texture, shape);
        }
        using (var edge = new Pen(GlassEdge)) g.DrawPath(edge, shape);

        var badge = new RectangleF(8, Height - 8 - BadgeSize, BadgeSize, BadgeSize);
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
