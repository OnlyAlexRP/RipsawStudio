using System.Net.Http;
using System.Text.Json;

namespace RipsawStudio;

/// <summary>
/// Outcome of one update check. <see cref="Checked"/> is false when the lookup itself
/// failed (offline, GitHub down, rate-limited, unparseable tag), in which case nothing
/// else in here means anything.
/// </summary>
public readonly record struct UpdateCheckResult(bool Checked, bool UpdateAvailable, Version? Latest)
{
    public static readonly UpdateCheckResult Unknown = new(false, false, null);
}

/// <summary>
/// Looks up the newest GitHub release and compares it to the running build. Best effort:
/// every failure collapses into <see cref="UpdateCheckResult.Unknown"/> rather than an
/// exception, because this is a convenience and must never get in the way of capture.
/// </summary>
public static class UpdateChecker
{
    /// <summary>Human-facing page, opened in the browser when the user wants the download.</summary>
    public const string ReleasesPage = "https://github.com/OnlyAlexRP/RipsawStudio/releases/latest";

    private const string LatestApi = "https://api.github.com/repos/OnlyAlexRP/RipsawStudio/releases/latest";

    /// <summary>The build that is running, trimmed the same way the About page shows it.</summary>
    public static Version Current => typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            // GitHub rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RipsawStudio/" + Format(Current));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json = await http.GetStringAsync(LatestApi).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            // Tags on the repo are "v1.2", "v1.1", ... - strip the prefix before parsing.
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return UpdateCheckResult.Unknown;

            return new UpdateCheckResult(true, Compare(latest, Current) > 0, latest);
        }
        catch
        {
            return UpdateCheckResult.Unknown;
        }
    }

    /// <summary>
    /// "1.2" from a tag and "1.2.0.0" from the assembly must compare as equal, so the
    /// missing Build/Revision parts (-1 in System.Version) are treated as zero.
    /// </summary>
    private static int Compare(Version a, Version b)
    {
        int c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        c = Math.Max(a.Build, 0).CompareTo(Math.Max(b.Build, 0));
        if (c != 0) return c;
        return Math.Max(a.Revision, 0).CompareTo(Math.Max(b.Revision, 0));
    }

    /// <summary>"1.4" rather than "1.4.0.0": only shows the parts that carry a value.</summary>
    public static string Format(Version? version)
    {
        if (version is null) return "unknown";
        if (version.Revision > 0) return version.ToString(4);
        if (version.Build > 0) return version.ToString(3);
        return version.ToString(2);
    }

    public static void OpenReleasesPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        catch { }
    }
}
