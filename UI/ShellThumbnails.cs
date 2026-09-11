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
    /// <summary>Loads a thumbnail no smaller than <paramref name="size"/> pixels on its long
    /// edge. Meant to be called off the UI thread - a cold shell thumbnail can take a
    /// noticeable moment on a slow disk.</summary>
    public static Bitmap? Load(string path, int size)
    {
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
                try { return Image.FromHbitmap(hbmp); }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx, cy;
        public NativeSize(int cx, int cy) { this.cx = cx; this.cy = cy; }
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
}
