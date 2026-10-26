using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RipsawStudio.Record;
using RipsawStudio.Render;

namespace RipsawStudio;

/// <summary>
/// The half of the settings that belongs to one input rather than to the app: which card,
/// which format, how its colour should be handled, which audio endpoints it uses and what
/// quality it records at. A Switch wants none of the same answers as a PC input, which is
/// the whole reason profiles exist.
///
/// Every property here must also exist on <see cref="AppSettings"/> with the same name and
/// type - that pairing is what <see cref="AppSettings.CaptureInto"/> copies across.
/// </summary>
public sealed class ProfileData
{
    public string Name { get; set; } = "Default";

    public string? VideoDeviceName { get; set; }
    public string? VideoDeviceLink { get; set; }
    public string? FormatKey { get; set; }

    public string? AudioInputId { get; set; }
    public string? AudioOutputId { get; set; }
    public bool AudioPassthrough { get; set; } = true;
    public int AudioLatencyMs { get; set; } = 40;
    public bool AudioExclusive { get; set; }
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public int AudioRestartMinutes { get; set; } = 30;

    public bool VSync { get; set; }
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public AspectMode Aspect { get; set; } = AspectMode.Auto;

    public float Brightness { get; set; }
    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public RangeMode Range { get; set; } = RangeMode.Auto;
    public ColorMatrix Matrix { get; set; } = ColorMatrix.Auto;
    public float ArtifactSmoothing { get; set; }

    public int VideoBitrateKbps { get; set; } = 25_000;
    public int AudioBitrateKbps { get; set; } = 192;
    public int AudioOffsetMs { get; set; }

    public override string ToString() => Name;
}

/// <summary>One key chord, plus the second chord kept for the VideoGameCapture equivalents.</summary>
public sealed class ShortcutBinding
{
    public string? Primary { get; set; }
    public string? Alternate { get; set; }
}

public sealed class AppSettings
{
    public string? VideoDeviceName { get; set; }
    public string? VideoDeviceLink { get; set; }
    public string? FormatKey { get; set; }

    public string? AudioInputId { get; set; }
    public string? AudioOutputId { get; set; }
    public bool AudioPassthrough { get; set; } = true;
    public int AudioLatencyMs { get; set; } = 40;
    public bool AudioExclusive { get; set; }
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    /// <summary>Minutes between scheduled audio restarts; 0 disables them.</summary>
    public int AudioRestartMinutes { get; set; } = 30;
    public bool RecordDesktopAudio { get; set; }

    // ---- microphone (yours, not the console's - so it is not part of a profile) --------
    public string? MicDeviceId { get; set; }
    /// <summary>Whether the mic is captured and mixed into recordings at all.</summary>
    public bool MicEnabled { get; set; }
    public float MicVolume { get; set; } = 1f;
    public bool MicMuted { get; set; }
    /// <summary>Off by default: hearing yourself through any delay is unpleasant.</summary>
    public bool MicMonitor { get; set; }
    /// <summary>Positive values delay the mic relative to the game sound.</summary>
    public int MicOffsetMs { get; set; }
    /// <summary>Duck the game audio while the mic hears you talking, like Discord's voice ducking.</summary>
    public bool MicDucking { get; set; }

    // ---- instant replay ---------------------------------------------------------------
    /// <summary>Whether the rolling buffer runs while the preview is live.</summary>
    public bool ReplayEnabled { get; set; }
    /// <summary>How many seconds are kept ready to save.</summary>
    public int ReplayBufferSeconds { get; set; } = 60;
    /// <summary>How many of those seconds a save actually writes out.</summary>
    public int ReplaySaveSeconds { get; set; } = 30;

    /// <summary>GPU description to render on; null means let Windows choose.</summary>
    public string? Adapter { get; set; }

    // ---- output folders -----------------------------------------------------------------
    /// <summary>
    /// Kept only so a settings file written before Records/Replay/Screenshots had separate
    /// folders still deserialises - <see cref="Load"/> migrates it into
    /// <see cref="RecordsFolder"/> once and never writes it again on purpose. Nothing else
    /// should read this; use <see cref="RecordsFolder"/> instead.
    /// </summary>
    public string OutputFolder { get; set; } = DefaultRoot;

    public string RecordsFolder { get; set; } = Path.Combine(DefaultRoot, "Records");
    public string ReplayFolder { get; set; } = Path.Combine(DefaultRoot, "Replay");
    public string ScreenshotsFolder { get; set; } = Path.Combine(DefaultRoot, "Screenshots");

    private static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RipsawStudio");

    public bool VSync { get; set; }
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public AspectMode Aspect { get; set; } = AspectMode.Auto;

    public float Brightness { get; set; }
    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public RangeMode Range { get; set; } = RangeMode.Auto;
    public ColorMatrix Matrix { get; set; } = ColorMatrix.Auto;
    public float ArtifactSmoothing { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AutoStart { get; set; } = true;

    public int VideoBitrateKbps { get; set; } = 25_000;
    public int AudioBitrateKbps { get; set; } = 192;
    public int AudioOffsetMs { get; set; }

    // ---- encoder (the machine's, not the console's - so not part of a profile) -----------
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    /// <summary>"auto", "sw", or "hw:" + a GPU name - see <see cref="EncoderInfo.Id"/>.</summary>
    public string EncoderId { get; set; } = "auto";
    public ContainerFormat Container { get; set; } = ContainerFormat.FragmentedMp4;
    public RateControl RateControl { get; set; } = RateControl.Cbr;
    public int Quality { get; set; } = 70;
    public int KeyframeSeconds { get; set; } = 2;

    /// <summary>
    /// Read only to migrate files from before the encoder could be chosen: false there meant
    /// "software". Nulled once migrated, so it is never written back out.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HardwareEncoder { get; set; }

    /// <summary>The newest version the user has already been told about, so it is said once.</summary>
    public string? NotifiedUpdate { get; set; }

    /// <summary>Whether the settings panel was open when the app last closed.</summary>
    public bool PanelOpen { get; set; } = true;
    public bool ShowStatusOverlay { get; set; }

    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 800;

    // ---- profiles ----------------------------------------------------------------------
    public List<ProfileData> Profiles { get; set; } = new();
    /// <summary>Name of the profile the live settings above belong to.</summary>
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Keyed by <c>ShortcutAction</c> name. Anything missing falls back to its default.</summary>
    public Dictionary<string, ShortcutBinding> Shortcuts { get; set; } = new();

    /// <summary>
    /// Registers the capture shortcuts system-wide, so they fire while a game has focus
    /// rather than only when Ripsaw Studio's own window does. Off by default: it takes the
    /// chords away from every other program on the machine for as long as the app runs.
    /// </summary>
    public bool GlobalShortcuts { get; set; }

    /// <summary>"auto" follows Windows, otherwise a code from <see cref="Loc.Languages"/>.</summary>
    public string Language { get; set; } = Loc.Auto;

    /// <summary>Full paths of the captures starred on the Captures page.</summary>
    public List<string> FavoriteCaptures { get; set; } = new();

    public static string SettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RipsawStudio", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Puts every tunable back to its default. The chosen card, format, audio endpoints and
    /// output folders are kept, since re-picking those is the tedious part. Shortcuts are
    /// left alone too - they have their own reset, next to where they are edited.
    /// </summary>
    public void ResetToDefaults()
    {
        var d = new AppSettings();
        AudioPassthrough = d.AudioPassthrough;
        AudioLatencyMs = d.AudioLatencyMs;
        AudioExclusive = d.AudioExclusive;
        AudioRestartMinutes = d.AudioRestartMinutes;
        RecordDesktopAudio = d.RecordDesktopAudio;
        Volume = d.Volume;
        Muted = d.Muted;

        MicEnabled = d.MicEnabled;
        MicVolume = d.MicVolume;
        MicMuted = d.MicMuted;
        MicMonitor = d.MicMonitor;
        MicOffsetMs = d.MicOffsetMs;
        MicDucking = d.MicDucking;

        ReplayEnabled = d.ReplayEnabled;
        ReplayBufferSeconds = d.ReplayBufferSeconds;
        ReplaySaveSeconds = d.ReplaySaveSeconds;

        Adapter = d.Adapter;
        VSync = d.VSync;
        Scaling = d.Scaling;
        Aspect = d.Aspect;
        Brightness = d.Brightness;
        Contrast = d.Contrast;
        Saturation = d.Saturation;
        Range = d.Range;
        Matrix = d.Matrix;
        ArtifactSmoothing = d.ArtifactSmoothing;

        VideoBitrateKbps = d.VideoBitrateKbps;
        AudioBitrateKbps = d.AudioBitrateKbps;
        AudioOffsetMs = d.AudioOffsetMs;
        Codec = d.Codec;
        EncoderId = d.EncoderId;
        Container = d.Container;
        RateControl = d.RateControl;
        Quality = d.Quality;
        KeyframeSeconds = d.KeyframeSeconds;

        AlwaysOnTop = d.AlwaysOnTop;
        AutoStart = d.AutoStart;
        ShowStatusOverlay = d.ShowStatusOverlay;
        GlobalShortcuts = d.GlobalShortcuts;
    }

    public RecordSettings ToRecordSettings() => new()
    {
        OutputFolder = RecordsFolder,
        VideoBitrateKbps = VideoBitrateKbps,
        AudioBitrateKbps = AudioBitrateKbps,
        AudioOffsetMs = AudioOffsetMs,
        Codec = Codec,
        EncoderId = EncoderId,
        Container = Container,
        RateControl = RateControl,
        Quality = Quality,
        KeyframeSeconds = KeyframeSeconds,
        FullRangeSource = Range == RangeMode.Compress,
    };

    public PictureSettings ToPicture() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Range = Range,
        Matrix = Matrix,
        ArtifactSmoothing = ArtifactSmoothing,
    };

    // ---- profile plumbing ----------------------------------------------------------------

    /// <summary>
    /// The properties a profile owns, paired with their counterparts here and resolved once.
    /// Copying by name rather than listing the same twenty-odd assignments in both directions
    /// is what stops the two lists drifting apart the first time a setting is added.
    ///
    /// A property with no matching counterpart throws rather than being skipped: silently
    /// dropping it would mean a setting that looks per-profile in the UI and quietly is not,
    /// which is the kind of bug nobody finds by looking.
    /// </summary>
    private static readonly (PropertyInfo Profile, PropertyInfo Live)[] ProfileFields = BuildProfileFields();

    private static (PropertyInfo Profile, PropertyInfo Live)[] BuildProfileFields()
    {
        var pairs = new List<(PropertyInfo, PropertyInfo)>();
        foreach (var profileProperty in typeof(ProfileData).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!profileProperty.CanRead || !profileProperty.CanWrite) continue;
            if (profileProperty.Name == nameof(ProfileData.Name)) continue;

            var live = typeof(AppSettings).GetProperty(profileProperty.Name);
            if (live is null || live.PropertyType != profileProperty.PropertyType || !live.CanRead || !live.CanWrite)
                throw new InvalidOperationException(
                    $"ProfileData.{profileProperty.Name} has no matching {profileProperty.PropertyType.Name} " +
                    $"property on AppSettings. Every profile setting needs both halves.");
            pairs.Add((profileProperty, live));
        }
        return pairs.ToArray();
    }

    /// <summary>Copies the live settings into a profile.</summary>
    public void CaptureInto(ProfileData profile)
    {
        foreach (var (p, live) in ProfileFields) p.SetValue(profile, live.GetValue(this));
    }

    /// <summary>Copies a profile over the live settings.</summary>
    public void ApplyFrom(ProfileData profile)
    {
        foreach (var (p, live) in ProfileFields) live.SetValue(this, p.GetValue(profile));
        ActiveProfile = profile.Name;
    }

    public ProfileData ActiveProfileData
    {
        get
        {
            var found = Profiles.FirstOrDefault(p => p.Name == ActiveProfile);
            if (found is not null) return found;
            found = new ProfileData { Name = ActiveProfile };
            CaptureInto(found);
            Profiles.Add(found);
            return found;
        }
    }

    /// <summary>Writes the live settings back into whichever profile they belong to.</summary>
    public void SyncActiveProfile() => CaptureInto(ActiveProfileData);

    /// <summary>Stores the current settings, then loads the named profile over them.</summary>
    public void SwitchProfile(string name)
    {
        if (name == ActiveProfile) return;
        var target = Profiles.FirstOrDefault(p => p.Name == name);
        if (target is null) return;
        SyncActiveProfile();
        ApplyFrom(target);
    }

    /// <summary>Adds a profile holding a copy of what is set up right now.</summary>
    public ProfileData AddProfile(string name)
    {
        SyncActiveProfile();
        name = UniqueName(name);
        var profile = new ProfileData { Name = name };
        CaptureInto(profile);
        Profiles.Add(profile);
        ActiveProfile = name;
        return profile;
    }

    public void RenameActiveProfile(string name)
    {
        var profile = ActiveProfileData;
        name = UniqueName(name, profile);
        profile.Name = name;
        ActiveProfile = name;
    }

    /// <summary>Removes the active profile and moves to another. The last one cannot be removed.</summary>
    public bool DeleteActiveProfile()
    {
        if (Profiles.Count <= 1) return false;
        var profile = ActiveProfileData;
        Profiles.Remove(profile);
        ApplyFrom(Profiles[0]);
        return true;
    }

    private string UniqueName(string name, ProfileData? ignore = null)
    {
        name = name.Trim();
        if (name.Length == 0) name = "Profile";
        if (!Profiles.Any(p => p != ignore && p.Name == name)) return name;
        for (int i = 2; ; i++)
        {
            string candidate = $"{name} {i}";
            if (!Profiles.Any(p => p != ignore && p.Name == candidate)) return candidate;
        }
    }

    // ---- persistence -----------------------------------------------------------------------

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            // A corrupt settings file must never stop the app starting - but it is kept aside
            // rather than silently overwritten by the defaults at the next save.
            settings = new AppSettings();
            try { File.Copy(SettingsPath, SettingsPath + ".corrupt", overwrite: true); } catch { }
        }

        if (settings.HardwareEncoder == false && settings.EncoderId == "auto") settings.EncoderId = "sw";
        settings.HardwareEncoder = null;

        // A file written before Records/Replay/Screenshots had their own folders carried one
        // OutputFolder that everything landed in. Migrate it once: if it had been pointed
        // somewhere other than the stock default, that becomes the Records folder - the
        // closest match to what it meant before - rather than silently reverting on upgrade.
        // Replay and Screenshots start at their own new defaults either way, since neither
        // folder existed as a separate concept for there to be a custom value to preserve.
        // This only ever fires once: after it runs, RecordsFolder no longer matches its own
        // default, so the condition below stays false on every later load.
        if (!string.IsNullOrWhiteSpace(settings.OutputFolder) &&
            !string.Equals(settings.OutputFolder, DefaultRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(settings.RecordsFolder, Path.Combine(DefaultRoot, "Records"), StringComparison.OrdinalIgnoreCase))
        {
            settings.RecordsFolder = settings.OutputFolder;
        }
        // Consumed: left in place, it would fire again the day Records is set back to its default.
        settings.OutputFolder = DefaultRoot;

        // A file written before profiles existed becomes the "Default" profile, so nobody
        // loses what they had set up.
        if (settings.Profiles.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(settings.ActiveProfile)) settings.ActiveProfile = "Default";
            settings.SyncActiveProfile();
        }
        else if (settings.Profiles.All(p => p.Name != settings.ActiveProfile))
        {
            settings.ApplyFrom(settings.Profiles[0]);
        }
        return settings;
    }

    /// <summary>
    /// Written to a temporary file and swapped in, so a crash or power cut mid-write leaves
    /// the previous settings rather than half a file.
    /// </summary>
    public void Save()
    {
        try
        {
            SyncActiveProfile();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch { }
    }
}
