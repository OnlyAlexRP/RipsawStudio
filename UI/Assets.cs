namespace RipsawStudio.UI;

/// <summary>
/// The five navigation-bar icons, embedded so the exe stays a single file. Loaded once and kept
/// for the life of the process - the same reasoning as the shared fonts in <see cref="Theme"/>,
/// a fresh decode on every repaint would be wasteful and would leak GDI+ bitmap handles.
/// A missing one comes back null and the bar draws a vector glyph in its place.
/// </summary>
internal static class Assets
{
    public static readonly Image? IconCapture = Load("icon_capture.png");
    public static readonly Image? IconRecording = Load("icon_recording.png");
    public static readonly Image? IconKeyboard = Load("icon_keyboard.png");
    public static readonly Image? IconGeneral = Load("icon_general.png");
    public static readonly Image? IconAbout = Load("icon_about.png");

    private static Image? Load(string name)
    {
        var assembly = typeof(Assets).Assembly;
        using var stream = assembly.GetManifestResourceStream("RipsawStudio.Assets." + name);
        if (stream is null) return null;
        // Image.FromStream keeps reading its stream for as long as the image is alive, so the
        // decoded image is copied into a bitmap that owns its pixels before the stream closes.
        using var decoded = Image.FromStream(stream);
        return new Bitmap(decoded);
    }
}
