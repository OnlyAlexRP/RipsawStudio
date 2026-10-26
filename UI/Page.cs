namespace RipsawStudio.UI;

/// <summary>The pages of the control window.</summary>
internal enum Page { Source, Picture, Sound, Video, RecordAudio, Replay, Library, Shortcuts, General, About }

/// <summary>The icons of the navigation bar. Each one holds one or more pages.</summary>
internal enum Category { Capture, Recording, Library, Keys, Settings, About }

internal static class Pages
{
    /// <summary>Navigation-bar order, with the pages each category shows as tabs. Labels are English keys for <see cref="Loc"/>.</summary>
    public static readonly (Category Category, string Label, (Page Page, string Label)[] Pages)[] Categories =
    {
        (Category.Capture, "Capture", new[] { (Page.Source, "Source"), (Page.Picture, "Picture"), (Page.Sound, "Sound") }),
        (Category.Recording, "Recording", new[] { (Page.Video, "Video"), (Page.RecordAudio, "Audio"), (Page.Replay, "Instant replay") }),
        (Category.Library, "Captures", new[] { (Page.Library, "Captures") }),
        (Category.Keys, "Shortcuts", new[] { (Page.Shortcuts, "Shortcuts") }),
        (Category.Settings, "Settings", new[] { (Page.General, "General") }),
        (Category.About, "About", new[] { (Page.About, "About") }),
    };

    public static Category CategoryOf(Page page) =>
        Categories.First(c => c.Pages.Any(p => p.Page == page)).Category;

    public static (Page Page, string Label)[] PagesOf(Category category) =>
        Categories.First(c => c.Category == category).Pages;
}
