using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public int VideoBitrateKbps { get; set; } = 25_000;
    public int AudioBitrateKbps { get; set; } = 192;
    public int AudioOffsetMs { get; set; }
    public bool HardwareEncoder { get; set; } = true;

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

    // ---- instant replay ---------------------------------------------------------------
    /// <summary>Whether the rolling buffer runs while the preview is live.</summary>
    public bool ReplayEnabled { get; set; }
    /// <summary>How many seconds are kept ready to save.</summary>
    public int ReplayBufferSeconds { get; set; } = 60;
    /// <summary>How many of those seconds a save actually writes out.</summary>
    public int ReplaySaveSeconds { get; set; } = 30;

    /// <summary>GPU description to render on; null means let Windows choose.</summary>
    public string? Adapter { get; set; }

    public bool VSync { get; set; }
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public AspectMode Aspect { get; set; } = AspectMode.Auto;

    public float Brightness { get; set; }
    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public RangeMode Range { get; set; } = RangeMode.Auto;
    public ColorMatrix Matrix { get; set; } = ColorMatrix.Auto;
    public bool AlwaysOnTop { get; set; }
    public bool AutoStart { get; set; } = true;

    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RipsawStudio");
    public int VideoBitrateKbps { get; set; } = 25_000;
    public int AudioBitrateKbps { get; set; } = 192;
    public int AudioOffsetMs { get; set; }
    public bool HardwareEncoder { get; set; } = true;

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

    public static string SettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RipsawStudio", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Puts every tunable back to its default. The chosen card, format, audio endpoints and
    /// output folder are kept, since re-picking those is the tedious part. Shortcuts are
    /// left alone too - they have their own reset, next to where they are edited.
    /// </summary>
    public void ResetToDefaults()
    {
        var d = new AppSettings();
        AudioPassthrough = d.AudioPassthrough;
        AudioLatencyMs = d.AudioLatencyMs;
        AudioExclusive = d.AudioExclusive;
        AudioRestartMinutes = d.AudioRestartMinutes;
        Volume = d.Volume;
        Muted = d.Muted;

        MicEnabled = d.MicEnabled;
        MicVolume = d.MicVolume;
        MicMuted = d.MicMuted;
        MicMonitor = d.MicMonitor;
        MicOffsetMs = d.MicOffsetMs;

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

        VideoBitrateKbps = d.VideoBitrateKbps;
        AudioBitrateKbps = d.AudioBitrateKbps;
        AudioOffsetMs = d.AudioOffsetMs;
        HardwareEncoder = d.HardwareEncoder;

        AlwaysOnTop = d.AlwaysOnTop;
        AutoStart = d.AutoStart;
        ShowStatusOverlay = d.ShowStatusOverlay;
    }

    public PictureSettings ToPicture() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Range = Range,
        Matrix = Matrix,
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
        catch { settings = new AppSettings(); /* a corrupt settings file must never stop the app starting */ }

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

    public void Save()
    {
        try
        {
            SyncActiveProfile();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}
