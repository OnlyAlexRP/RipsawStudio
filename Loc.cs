global using static RipsawStudio.Loc;

using System.Globalization;

namespace RipsawStudio;

/// <summary>
/// Interface language. Texts are written in English in the code and looked up here by that
/// English text, so a string with no translation simply stays in English.
/// </summary>
internal static partial class Loc
{
    /// <summary>Codes in the order of each <see cref="Table"/> row, after English.</summary>
    private static readonly string[] Translated = { "fr", "es", "de", "ru" };

    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("fr", "Français"), ("es", "Español"), ("de", "Deutsch"), ("ru", "Русский"),
    };

    public const string Auto = "auto";

    public static string Current { get; private set; } = "en";
    private static int _column = -1;

    /// <summary>"auto" follows Windows' display language, and anything not translated falls back to English.</summary>
    public static void Apply(string? setting)
    {
        string code = string.IsNullOrEmpty(setting) || setting == Auto
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : setting;
        _column = Array.IndexOf(Translated, code);
        Current = _column < 0 ? "en" : code;
    }

    public static string Tr(string english) =>
        _column >= 0 && Table.TryGetValue(english, out var row) ? row[_column] : english;

    public static string Tr(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Tr(english), args);
}
