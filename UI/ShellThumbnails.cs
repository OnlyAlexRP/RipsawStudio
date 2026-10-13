using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RipsawStudio.UI;

/// <summary>
/// Asks Windows itself for a file's thumbnail - the same bitmap Explorer's icon view would
/// show - via <c>IShellItemImageFactory</c>. This is what gives a real decoded video frame
/// for an .mp4 without this app carrying its own Media Foundation frame-grab path: the shell's
/// registered thumbnail provider for video (built into Windows 10/11) does the decoding, and
/// a .png just gets read and scaled the same way.
///
/// Every call is COM and can legitimately fail - a network drive that dropped, a file that is
/// still being written, a stripped-down Windows image with no video thumbnail handler - so
/// this only ever returns null on failure. Callers are expected to fall back to a plain tile.
/// </summary>
internal static class ShellThumbnails
{
    /// <summary>Loads a thumbnail around <paramref name="size"/> pixels on its long edge,
    /// cropped to its actual picture. Meant to be called off the UI thread - a cold shell
    /// thumbnail can take a noticeable moment on a slow disk.</summary>
    public static Bitmap? Load(string path, int size)
    {
        // A picture this app wrote itself needs no thumbnail provider: decoding it directly
        // gives the exact pixels, with no square box to pad out and no alpha to recover.
        if (IsImageFile(path))
        {
            var direct = LoadImageFile(path, size);
            if (direct is not null) return direct;
        }

        try
        {
            var riid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref riid, out var factory);
            if (hr != 0 || factory is null) return null;
            try
            {
                factory.GetImage(new NativeSize(size, size),
                    SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out IntPtr hbmp);
                if (hbmp == IntPtr.Zero) return null;
                try { return Convert(hbmp); }
                finally { DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch
        {
            // COMException (no handler registered, file locked, ...) and anything else that
            // can come out of a shell call - a missing thumbnail is not worth surfacing.
            return null;
        }
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp";

    /// <summary>
    /// Decodes a picture file straight off disk, scaled down to roughly the size asked for.
    ///
    /// The copy is not optional: <c>Image.FromStream</c> keeps the stream alive for the
    /// lifetime of the image, and this one is closed on the way out. Scaling is not optional
    /// either - a screenshot is a full-resolution frame, and callers cache what they get, so
    /// handing back the original would hold tens of megabytes for a tile a few hundred pixels
    /// across. Returns null so the caller can fall back to the shell; a file that is still
    /// being written will not decode.
    /// </summary>
    private static Bitmap? LoadImageFile(string path, int size)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            if (image.Width <= 0 || image.Height <= 0) return null;

            // Never upscale: a small source stays its own size rather than being blown up.
            double scale = Math.Min(1.0, (double)size / Math.Max(image.Width, image.Height));
            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));

            var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(copy);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // Same out-of-bounds sampling as the draw in CaptureToast, and the same fix.
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(image, new Rectangle(0, 0, width, height),
                            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Turns the shell's HBITMAP into a bitmap of the picture alone.
    ///
    /// Two things have to be undone here. The shell is asked for a square box - the interface
    /// has no way to say "whatever shape the file is" - and most providers answer by centring
    /// the real thumbnail in that square and leaving the rest fully transparent. And
    /// <c>Image.FromHbitmap</c> throws the alpha channel away, so that padding survives as
    /// whatever raw colour the provider happened to leave in those bytes, and gets scaled up
    /// along the edges of the picture as if it were part of it.
    ///
    /// So the DIB is wrapped with its alpha intact, the opaque region is measured, and only
    /// that region is copied out.
    /// </summary>
    private static Bitmap? Convert(IntPtr hbmp)
    {
        var info = new BITMAP();
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref info) == 0) return null;

        // Anything that is not a 32-bit DIB section has no alpha to misread in the first place.
        if (info.bmBitsPixel != 32 || info.bmBits == IntPtr.Zero || info.bmWidth <= 0 || info.bmHeight <= 0)
            return Image.FromHbitmap(hbmp);

        // The shell hands back a top-down DIB, so bmBits is the first row and the stride is
        // positive. PArgb because the provider writes premultiplied pixels.
        using var raw = new Bitmap(info.bmWidth, info.bmHeight, info.bmWidthBytes,
                                   PixelFormat.Format32bppPArgb, info.bmBits);

        var content = OpaqueBounds(raw);
        if (content.Width <= 0 || content.Height <= 0) return Image.FromHbitmap(hbmp);

        // Copied rather than returned directly: raw points at memory owned by the HBITMAP,
        // which the caller deletes the moment this returns.
        var cropped = new Bitmap(content.Width, content.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(raw, new Rectangle(0, 0, content.Width, content.Height), content, GraphicsUnit.Pixel);
        }
        return cropped;
    }

    /// <summary>
    /// The smallest rectangle holding every pixel that is not fully transparent. Returns the
    /// whole bitmap when nothing is transparent, which is the common case for a video frame.
    /// </summary>
    private static Rectangle OpaqueBounds(Bitmap bmp)
    {
        var all = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData? data = null;
        try
        {
            data = bmp.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            int left = bmp.Width, top = bmp.Height, right = -1, bottom = -1;

            unsafe
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        // BGRA in memory, so alpha is the fourth byte of each pixel. Some
                        // providers feather the edge of the padding rather than cutting it
                        // dead, and a row that is 3% opaque is still padding - left in, it
                        // survives the crop as the faint line this is here to remove.
                        if (row[x * 4 + 3] <= 8) continue;
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        bottom = y;
                    }
                }
            }

            if (right < left || bottom < top) return all;   // fully transparent: nothing to measure
            return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        catch
        {
            return all;
        }
        finally
        {
            if (data is not null) bmp.UnlockBits(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx, cy;
        public NativeSize(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        ThumbnailOnly = 0x08,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int size, ref BITMAP bitmap);
}
