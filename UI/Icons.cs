using System.Drawing.Drawing2D;

namespace RipsawStudio.UI;

internal enum Icon
{
    None, Monitor, Record, Gear, Info, Image, Speaker, Sliders,
    Sun, Contrast, Droplet, Refresh, Download, Stopwatch, Folder,
    Camera, Stop, Play, Chip, Keyboard, Mic, Rewind, Profile,
}

/// <summary>
/// Icons drawn with primitives rather than taken from an icon font. Segoe's symbol fonts
/// differ between Windows builds, and a wrong codepoint shows up as a blank box. Geometry
/// renders the same everywhere.
/// </summary>
internal static class Icons
{
    public static void Draw(Graphics g, Icon icon, RectangleF r, Color colour, float weight = 1.6f)
    {
        if (icon == Icon.None) return;
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(colour, weight) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(colour);

        switch (icon)
        {
            case Icon.Monitor:
                g.DrawRectangle(pen, r.X, r.Y + r.Height * 0.12f, r.Width, r.Height * 0.62f);
                g.DrawLine(pen, r.X + r.Width * 0.32f, r.Bottom, r.X + r.Width * 0.68f, r.Bottom);
                g.DrawLine(pen, r.X + r.Width * 0.5f, r.Y + r.Height * 0.74f, r.X + r.Width * 0.5f, r.Bottom);
                break;

            case Icon.Record:
                g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
                g.FillEllipse(brush, r.X + r.Width * 0.28f, r.Y + r.Height * 0.28f, r.Width * 0.44f, r.Height * 0.44f);
                break;

            case Icon.Gear:
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                float outer = r.Width * 0.5f, inner = r.Width * 0.34f;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(pen,
                        cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner,
                        cx + (float)Math.Cos(a) * outer, cy + (float)Math.Sin(a) * outer);
                }
                g.DrawEllipse(pen, cx - inner, cy - inner, inner * 2, inner * 2);
                g.DrawEllipse(pen, cx - r.Width * 0.13f, cy - r.Width * 0.13f, r.Width * 0.26f, r.Width * 0.26f);
                break;
            }

            case Icon.Info:
                g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
                g.FillEllipse(brush, r.X + r.Width * 0.44f, r.Y + r.Height * 0.22f, r.Width * 0.13f, r.Height * 0.13f);
                g.DrawLine(pen, r.X + r.Width * 0.5f, r.Y + r.Height * 0.45f, r.X + r.Width * 0.5f, r.Y + r.Height * 0.75f);
                break;

            case Icon.Image:
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawEllipse(pen, r.X + r.Width * 0.18f, r.Y + r.Height * 0.18f, r.Width * 0.2f, r.Height * 0.2f);
                g.DrawLines(pen, new[]
                {
                    new PointF(r.X + r.Width * 0.08f, r.Bottom - r.Height * 0.18f),
                    new PointF(r.X + r.Width * 0.4f, r.Y + r.Height * 0.5f),
                    new PointF(r.Right - r.Width * 0.06f, r.Bottom - r.Height * 0.12f),
                });
                break;

            case Icon.Speaker:
                g.DrawLines(pen, new[]
                {
                    new PointF(r.X + r.Width * 0.06f, r.Y + r.Height * 0.35f),
                    new PointF(r.X + r.Width * 0.26f, r.Y + r.Height * 0.35f),
                    new PointF(r.X + r.Width * 0.5f, r.Y + r.Height * 0.1f),
                    new PointF(r.X + r.Width * 0.5f, r.Bottom - r.Height * 0.1f),
                    new PointF(r.X + r.Width * 0.26f, r.Y + r.Height * 0.65f),
                    new PointF(r.X + r.Width * 0.06f, r.Y + r.Height * 0.65f),
                });
                g.DrawArc(pen, r.X + r.Width * 0.44f, r.Y + r.Height * 0.2f, r.Width * 0.5f, r.Height * 0.6f, -60, 120);
                break;

            case Icon.Sliders:
                for (int i = 0; i < 3; i++)
                {
                    float y = r.Y + r.Height * (0.18f + i * 0.32f);
                    g.DrawLine(pen, r.X, y, r.Right, y);
                    float knob = r.X + r.Width * (i == 1 ? 0.68f : 0.34f);
                    g.FillEllipse(brush, knob - 2f, y - 2f, 4f, 4f);
                }
                break;

            case Icon.Sun:
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawEllipse(pen, cx - r.Width * 0.22f, cy - r.Width * 0.22f, r.Width * 0.44f, r.Width * 0.44f);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(pen,
                        cx + (float)Math.Cos(a) * r.Width * 0.34f, cy + (float)Math.Sin(a) * r.Width * 0.34f,
                        cx + (float)Math.Cos(a) * r.Width * 0.48f, cy + (float)Math.Sin(a) * r.Width * 0.48f);
                }
                break;
            }

            case Icon.Contrast:
                g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
                using (var path = new GraphicsPath())
                {
                    path.AddArc(r.X, r.Y, r.Width, r.Height, -90, 180);
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }
                break;

            case Icon.Droplet:
                using (var path = new GraphicsPath())
                {
                    path.AddLine(r.X + r.Width * 0.5f, r.Y, r.Right, r.Y + r.Height * 0.62f);
                    path.AddArc(r.X, r.Y + r.Height * 0.24f, r.Width, r.Height * 0.76f, 0, 180);
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
                break;

            case Icon.Refresh:
                g.DrawArc(pen, r.X, r.Y, r.Width, r.Height, 40, 280);
                g.DrawLines(pen, new[]
                {
                    new PointF(r.Right - r.Width * 0.12f, r.Y),
                    new PointF(r.Right, r.Y + r.Height * 0.28f),
                    new PointF(r.Right - r.Width * 0.32f, r.Y + r.Height * 0.3f),
                });
                break;

            case Icon.Download:
                g.DrawLine(pen, r.X + r.Width * 0.5f, r.Y, r.X + r.Width * 0.5f, r.Y + r.Height * 0.62f);
                g.DrawLines(pen, new[]
                {
                    new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.38f),
                    new PointF(r.X + r.Width * 0.5f, r.Y + r.Height * 0.64f),
                    new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.38f),
                });
                g.DrawLine(pen, r.X, r.Bottom, r.Right, r.Bottom);
                break;

            case Icon.Stopwatch:
                g.DrawEllipse(pen, r.X, r.Y + r.Height * 0.16f, r.Width, r.Height * 0.84f);
                g.DrawLine(pen, r.X + r.Width * 0.34f, r.Y, r.X + r.Width * 0.66f, r.Y);
                g.DrawLine(pen, r.X + r.Width * 0.5f, r.Y + r.Height * 0.36f,
                                r.X + r.Width * 0.5f, r.Y + r.Height * 0.58f);
                g.DrawLine(pen, r.X + r.Width * 0.5f, r.Y + r.Height * 0.58f,
                                r.X + r.Width * 0.7f, r.Y + r.Height * 0.58f);
                break;

            case Icon.Folder:
                g.DrawLines(pen, new[]
                {
                    new PointF(r.X, r.Bottom),
                    new PointF(r.X, r.Y + r.Height * 0.16f),
                    new PointF(r.X + r.Width * 0.4f, r.Y + r.Height * 0.16f),
                    new PointF(r.X + r.Width * 0.52f, r.Y + r.Height * 0.36f),
                    new PointF(r.Right, r.Y + r.Height * 0.36f),
                    new PointF(r.Right, r.Bottom),
                    new PointF(r.X, r.Bottom),
                });
                break;

            case Icon.Camera:
                g.DrawRectangle(pen, r.X, r.Y + r.Height * 0.22f, r.Width, r.Height * 0.66f);
                g.DrawLine(pen, r.X + r.Width * 0.3f, r.Y + r.Height * 0.22f,
                                r.X + r.Width * 0.42f, r.Y + r.Height * 0.06f);
                g.DrawLine(pen, r.X + r.Width * 0.42f, r.Y + r.Height * 0.06f,
                                r.X + r.Width * 0.62f, r.Y + r.Height * 0.06f);
                g.DrawEllipse(pen, r.X + r.Width * 0.32f, r.Y + r.Height * 0.38f, r.Width * 0.36f, r.Height * 0.36f);
                break;

            case Icon.Stop:
                using (var path = FlatButton.Rounded(Rectangle.Round(r), 2))
                    g.FillPath(brush, path);
                break;

            case Icon.Play:
                g.FillPolygon(brush, new[]
                {
                    new PointF(r.X + r.Width * 0.16f, r.Y),
                    new PointF(r.Right, r.Y + r.Height * 0.5f),
                    new PointF(r.X + r.Width * 0.16f, r.Bottom),
                });
                break;

            case Icon.Chip:
                g.DrawRectangle(pen, r.X + r.Width * 0.18f, r.Y + r.Height * 0.18f, r.Width * 0.64f, r.Height * 0.64f);
                for (int i = 0; i < 3; i++)
                {
                    float t = r.X + r.Width * (0.3f + i * 0.2f);
                    g.DrawLine(pen, t, r.Y, t, r.Y + r.Height * 0.18f);
                    g.DrawLine(pen, t, r.Y + r.Height * 0.82f, t, r.Bottom);
                    float s = r.Y + r.Height * (0.3f + i * 0.2f);
                    g.DrawLine(pen, r.X, s, r.X + r.Width * 0.18f, s);
                    g.DrawLine(pen, r.X + r.Width * 0.82f, s, r.Right, s);
                }
                break;

            case Icon.Keyboard:
                g.DrawRectangle(pen, r.X, r.Y + r.Height * 0.2f, r.Width, r.Height * 0.6f);
                for (int i = 0; i < 4; i++)
                {
                    float x = r.X + r.Width * (0.16f + i * 0.22f);
                    g.DrawLine(pen, x, r.Y + r.Height * 0.38f, x, r.Y + r.Height * 0.42f);
                }
                g.DrawLine(pen, r.X + r.Width * 0.3f, r.Y + r.Height * 0.62f,
                                r.X + r.Width * 0.7f, r.Y + r.Height * 0.62f);
                break;

            case Icon.Mic:
                using (var path = new GraphicsPath())
                {
                    var capsule = new RectangleF(r.X + r.Width * 0.3f, r.Y, r.Width * 0.4f, r.Height * 0.58f);
                    path.AddArc(capsule.X, capsule.Y, capsule.Width, capsule.Width, 180, 180);
                    path.AddArc(capsule.X, capsule.Bottom - capsule.Width, capsule.Width, capsule.Width, 0, 180);
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
                g.DrawArc(pen, r.X + r.Width * 0.14f, r.Y + r.Height * 0.3f, r.Width * 0.72f, r.Height * 0.6f, 20, 140);
                g.DrawLine(pen, r.X + r.Width * 0.5f, r.Y + r.Height * 0.82f, r.X + r.Width * 0.5f, r.Bottom);
                break;

            case Icon.Rewind:
                // Two arrowheads pointing back, over a bar: rewind, not just "play backwards".
                g.FillPolygon(brush, new[]
                {
                    new PointF(r.X + r.Width * 0.54f, r.Y + r.Height * 0.14f),
                    new PointF(r.X + r.Width * 0.54f, r.Bottom - r.Height * 0.14f),
                    new PointF(r.X + r.Width * 0.16f, r.Y + r.Height * 0.5f),
                });
                g.FillPolygon(brush, new[]
                {
                    new PointF(r.Right, r.Y + r.Height * 0.14f),
                    new PointF(r.Right, r.Bottom - r.Height * 0.14f),
                    new PointF(r.X + r.Width * 0.62f, r.Y + r.Height * 0.5f),
                });
                g.DrawLine(pen, r.X + r.Width * 0.08f, r.Y + r.Height * 0.16f,
                                r.X + r.Width * 0.08f, r.Bottom - r.Height * 0.16f);
                break;

            case Icon.Profile:
                g.DrawEllipse(pen, r.X + r.Width * 0.28f, r.Y + r.Height * 0.06f, r.Width * 0.44f, r.Height * 0.44f);
                g.DrawArc(pen, r.X + r.Width * 0.08f, r.Y + r.Height * 0.56f, r.Width * 0.84f, r.Height * 0.8f, 180, 180);
                break;
        }

        g.SmoothingMode = mode;
    }
}
