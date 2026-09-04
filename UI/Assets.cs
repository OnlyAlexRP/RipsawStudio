namespace RipsawStudio.UI;

/// <summary>
/// The five menu-bar icons, embedded so the exe stays a single file. Loaded once and kept
/// for the life of the process - the same reasoning as the shared fonts in <see cref="Theme"/>,
/// a fresh decode on every repaint would be wasteful and would leak GDI+ bitmap handles.
/// </summary>
internal static class Assets
{
    public static readonly Image IconCapture = Load("icon_capture.png");
    public static readonly Image IconRecording = Load("icon_recording.png");
    public static readonly Image IconKeyboard = Load("icon_keyboard.png");
    public static readonly Image IconGeneral = Load("icon_general.png");
    public static readonly Image IconAbout = Load("icon_about.png");

    private static Image Load(string name)
    {
        var assembly = typeof(Assets).Assembly;
        using var stream = assembly.GetManifestResourceStream("RipsawStudio.Assets." + name)
            ?? throw new InvalidOperationException("Missing embedded asset: " + name);
        // Image.FromStream keeps reading from its stream for as long as the image is alive
        // unless the stream is copied out first - that would otherwise pin an assembly
        // resource stream open for the whole run.
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Image.FromStream(buffer);
    }
}
