using System.Drawing.Drawing2D;
using NAudio.CoreAudioApi;
using RipsawStudio.Audio;
using RipsawStudio.Capture;
using RipsawStudio.Record;
using RipsawStudio.Render;

namespace RipsawStudio.UI;

/// <summary>
/// The floating settings panel: a nav rail on the left, and pages built from cards laid
/// out in two columns. It sits over the picture rather than beside it, so the preview keeps
/// the whole window when the panel is closed.
/// </summary>
internal sealed class SettingsPanel : Panel
{
    private const int RailWidth = 116;
    private const int Gap = 14;
    private const int PagePad = 22;
    private const int ColumnGap = 16;

    private readonly AppSettings _settings;
    private readonly NavRail _rail = new();
    private readonly Panel _pageHost = new();
    private readonly Label _pageTitle = new();
    private readonly Dictionary<Page, ContentPanel> _pages = new();
    private Page _page = Page.Capture;
    private bool _binding = true;

    private readonly FlatCombo _device = new();
    private readonly FlatCombo _format = new();
    private readonly FlatCombo _aspect = new();
    private readonly FlatCombo _scaling = new();
    private readonly FlatCombo _range = new();
    private readonly FlatCombo _matrix = new();
    private readonly FlatCombo _audioIn = new();
    private readonly FlatCombo _audioOut = new();
    private readonly FlatCombo _adapter = new();
    private readonly FlatCombo _profile = new();
    private readonly FlatCombo _micDevice = new();

    private readonly Slider _brightness = new() { Bipolar = true };
    private readonly Slider _contrast = new();
    private readonly Slider _saturation = new();
    private readonly Slider _artifactSmoothing = new();
    private readonly Slider _volume = new();
    private readonly Slider _micVolume = new();
    private readonly Label _brightnessValue = new();
    private readonly Label _contrastValue = new();
    private readonly Label _saturationValue = new();
    private readonly Label _artifactSmoothingValue = new();
    private readonly Label _volumeValue = new();
    private readonly Label _micVolumeValue = new();

    private readonly NumericField _audioBuffer = new();
    private readonly NumericField _audioRestart = new();
    private readonly NumericField _videoBitrate = new();
    private readonly NumericField _audioBitrate = new();
    private readonly NumericField _audioOffset = new();
    private readonly NumericField _micOffset = new();
    private readonly NumericField _replayBuffer = new();
    private readonly NumericField _replaySave = new();
    private readonly TextBox _folder = new();

    private readonly FlatCheck _vsync = new();
    private readonly FlatCheck _passthrough = new();
    private readonly FlatCheck _exclusive = new();
    private readonly FlatCheck _hardware = new();
    private readonly FlatCheck _onTop = new();
    private readonly FlatCheck _autoStart = new();
    private readonly FlatCheck _showStats = new();
    private readonly FlatCheck _micEnabled = new();
    private readonly FlatCheck _micMuted = new();
    private readonly FlatCheck _micMonitor = new();
    private readonly FlatCheck _replayEnabled = new();

    private readonly FlatButton _startStop = new();
    private readonly FlatButton _rescan = new();
    private readonly FlatButton _record = new();
    private readonly FlatButton _snapshot = new();
    private readonly FlatButton _openFolder = new();
    private readonly FlatButton _saveReplay = new();
    private readonly LevelMeter _meter = new();
    private readonly LevelMeter _micMeter = new();
    private readonly Label _statusLine = new();
    private readonly Label _recordState = new();
    private readonly Label _replayState = new();
    private readonly RecentList _recent = new();

    /// <summary>One field per action and slot, created once and reused across rebuilds.</summary>
    private readonly Dictionary<(ShortcutAction, ShortcutSlot), KeyCaptureButton> _keyFields = new();
    private readonly ShortcutMap _shortcuts;
    /// <summary>The About page's read-only copy of the bindings, kept in step with the editor.</summary>
    private readonly List<(ShortcutAction Action, Label Value)> _aboutKeys = new();

    public static readonly VideoDeviceInfo NoDevice = new("No capture card", "");
    public static readonly AudioDeviceInfo NoMic = new("", "No microphone");
    private const string AutoAdapter = "Automatic (let Windows choose)";

    public event EventHandler? DeviceSelectionChanged;
    public event EventHandler? AudioSelectionChanged;
    public event EventHandler? PictureChanged;
    public event EventHandler? DisplayChanged;
    public event EventHandler? RecordingSettingsChanged;
    public event EventHandler? AdapterChanged;
    public event EventHandler? StartStopClicked;
    public event EventHandler? RescanClicked;
    public event EventHandler? RecordClicked;
    public event EventHandler? SnapshotClicked;
    public event EventHandler? VolumeChanged;
    public event EventHandler? DiagnosticsClicked;
    public event EventHandler? TraceClicked;
    public event EventHandler? ResetAllClicked;
    /// <summary>A different profile was picked, or one was added, renamed or removed.</summary>
    public event EventHandler? ProfileChanged;
    /// <summary>Something that needs the audio graph rebuilt - device, offset, monitoring.</summary>
    public event EventHandler? MicSettingsChanged;
    /// <summary>Gain or mute, which take effect without a restart.</summary>
    public event EventHandler? MicLevelChanged;
    public event EventHandler? ReplaySettingsChanged;
    public event EventHandler? SaveReplayClicked;
    /// <summary>A binding changed; the string is a note worth showing, or empty.</summary>
    public event EventHandler<string>? ShortcutsChanged;

    public VideoDeviceInfo? SelectedDevice => _device.SelectedItem as VideoDeviceInfo;
    public VideoFormat? SelectedFormat => _format.SelectedItem as VideoFormat;
    public AudioDeviceInfo? SelectedAudioIn => _audioIn.SelectedItem as AudioDeviceInfo;
    public AudioDeviceInfo? SelectedAudioOut => _audioOut.SelectedItem as AudioDeviceInfo;
    public string? SelectedAdapter => _adapter.SelectedIndex <= 0 ? null : _adapter.SelectedItem as string;
    public AudioDeviceInfo? SelectedMic =>
        _micDevice.SelectedItem is AudioDeviceInfo info && info.Id.Length > 0 ? info : null;

    public SettingsPanel(AppSettings settings, ShortcutMap shortcuts)
    {
        _settings = settings;
        _shortcuts = shortcuts;
        BackColor = Theme.Background;
        TabStop = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        _rail.PageSelected += (_, page) => ShowPage(page);
        Controls.Add(_rail);

        _pageTitle.Font = Theme.PageTitle;
        _pageTitle.ForeColor = Theme.TextDim;
        _pageTitle.AutoSize = false;
        _pageTitle.BackColor = Theme.Shell;
        Controls.Add(_pageTitle);

        _pageHost.BackColor = Theme.Shell;
        _pageHost.TabStop = false;
        Controls.Add(_pageHost);

        ConfigureControls();
        WireHandlers();
    }

    /// <summary>Every control that survives a rebuild, so it can be detached before the old cards go.</summary>
    private IEnumerable<Control> Shared => new Control[]
    {
        _device, _format, _aspect, _scaling, _range, _matrix, _audioIn, _audioOut, _adapter,
        _profile, _micDevice,
        _brightness, _contrast, _saturation, _volume, _micVolume,
        _brightnessValue, _contrastValue, _saturationValue, _volumeValue, _micVolumeValue,
        _audioBuffer, _audioRestart, _videoBitrate, _audioBitrate, _audioOffset, _folder,
        _micOffset, _replayBuffer, _replaySave,
        _vsync, _passthrough, _exclusive, _hardware, _onTop, _autoStart, _showStats,
        _micEnabled, _micMuted, _micMonitor, _replayEnabled,
        _startStop, _rescan, _record, _snapshot, _openFolder, _saveReplay,
        _meter, _micMeter, _statusLine, _recordState, _replayState, _recent,
    }.Concat(_keyFields.Values);

    /// <summary>One-time setup. Kept out of Build so a relayout cannot duplicate any of it.</summary>
    private void ConfigureControls()
    {
        _aspect.Items.AddRange(new object[] { "Auto (from source)", "Force 16:9", "Force 4:3", "Force 16:10" });
        _scaling.Items.AddRange(new object[] { "Fit in window", "Stretch", "1:1 pixels" });
        _range.Items.AddRange(new object[] { "Auto", "Expand 16-235 to 0-255", "Compress 0-255 to 16-235" });
        _matrix.Items.AddRange(new object[] { "Auto", "BT.601 (SD)", "BT.709 (HD)" });

        _brightness.Minimum = -100; _brightness.Maximum = 100;
        _contrast.Minimum = 0; _contrast.Maximum = 200;
        _saturation.Minimum = 0; _saturation.Maximum = 200;
        _volume.Minimum = 0; _volume.Maximum = 200;
        _micVolume.Minimum = 0; _micVolume.Maximum = 200;

        Configure(_audioBuffer, 10, 300, 5);
        Configure(_audioRestart, 0, 600, 5);
        Configure(_videoBitrate, 1000, 200_000, 1000);
        Configure(_audioBitrate, 64, 320, 32);
        Configure(_audioOffset, -2000, 2000, 5);
        Configure(_micOffset, 0, 1000, 5);
        Configure(_replayBuffer, 15, 300, 15);
        Configure(_replaySave, 5, 300, 5);

        _folder.BackColor = Theme.Field;
        _folder.ForeColor = Theme.Text;
        _folder.BorderStyle = BorderStyle.None;
        _folder.ReadOnly = true;
        // A single-line TextBox sizes itself to the font and ignores Height, which left it
        // sitting short of the row and misaligned with the browse button beside it.
        _folder.Multiline = true;
        _folder.WordWrap = false;

        _vsync.Text = "Wait for vsync (no tearing)";
        _passthrough.Text = "Play the sound through the output";
        _exclusive.Text = "Exclusive output (lowest delay)";
        _hardware.Text = "Encode on the GPU";
        _onTop.Text = "Keep window on top";
        _autoStart.Text = "Start the preview on launch";
        _showStats.Text = "Show the stats overlay";
        _micEnabled.Text = "Mix a microphone into recordings";
        _micMuted.Text = "Mute the microphone";
        _micMonitor.Text = "Hear yourself in the monitor too";
        _replayEnabled.Text = "Keep the recent past ready to save";

        _startStop.ButtonRole = FlatButton.Role.Accent;
        _startStop.Glyph = Icon.Play;
        _startStop.Text = "Start";
        _rescan.Glyph = Icon.Refresh;
        _rescan.Text = "Rescan";
        _record.ButtonRole = FlatButton.Role.Danger;
        _record.Glyph = Icon.Record;
        _record.Text = "Record";
        _snapshot.Glyph = Icon.Camera;
        _snapshot.Text = "Snapshot";
        _openFolder.Glyph = Icon.Folder;
        _openFolder.Text = "Open";
        _saveReplay.ButtonRole = FlatButton.Role.Accent;
        _saveReplay.Glyph = Icon.Rewind;
        _saveReplay.Text = "Save replay";

        foreach (var definition in ShortcutCatalog.All)
        {
            _keyFields[(definition.Action, ShortcutSlot.Primary)] = NewKeyField(definition.Action, ShortcutSlot.Primary);
            _keyFields[(definition.Action, ShortcutSlot.Alternate)] = NewKeyField(definition.Action, ShortcutSlot.Alternate);
        }
    }

    private KeyCaptureButton NewKeyField(ShortcutAction action, ShortcutSlot slot)
    {
        var field = new KeyCaptureButton
        {
            EmptyText = slot == ShortcutSlot.Primary ? "not bound" : "add a key",
        };
        field.Rebound += (_, keys) =>
        {
            var displaced = _shortcuts.Set(action, slot, keys);
            LoadShortcuts();
            ShortcutsChanged?.Invoke(this, displaced is null
                ? ""
                : $"{ShortcutCatalog.Describe(keys)} was on \"{ShortcutCatalog.Definition(displaced.Value).Label}\" - it has been cleared there.");
        };
        return field;
    }

    private void WireHandlers()
    {
        _device.SelectedIndexChanged += (_, _) => Raise(DeviceSelectionChanged);
        _format.SelectedIndexChanged += (_, _) => Raise(DeviceSelectionChanged);
        _audioIn.SelectedIndexChanged += (_, _) => Raise(AudioSelectionChanged);
        _audioOut.SelectedIndexChanged += (_, _) => Raise(AudioSelectionChanged);

        _aspect.SelectedIndexChanged += (_, _) => { _settings.Aspect = (AspectMode)_aspect.SelectedIndex; Raise(DisplayChanged); };
        _scaling.SelectedIndexChanged += (_, _) => { _settings.Scaling = (ScalingMode)_scaling.SelectedIndex; Raise(DisplayChanged); };
        _range.SelectedIndexChanged += (_, _) => { _settings.Range = (RangeMode)_range.SelectedIndex; Raise(PictureChanged); };
        _matrix.SelectedIndexChanged += (_, _) => { _settings.Matrix = (ColorMatrix)_matrix.SelectedIndex; Raise(PictureChanged); };
        _adapter.SelectedIndexChanged += (_, _) => { _settings.Adapter = SelectedAdapter; Raise(AdapterChanged); };
        _profile.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _profile.SelectedItem is not ProfileData profile) return;
            _settings.SwitchProfile(profile.Name);
            LoadFromSettings();
            ProfileChanged?.Invoke(this, EventArgs.Empty);
        };
        _micDevice.SelectedIndexChanged += (_, _) =>
        {
            _settings.MicDeviceId = SelectedMic?.Id;
            Raise(MicSettingsChanged);
        };

        _brightness.ValueChanged += (_, _) =>
        {
            _settings.Brightness = _brightness.Value / 100f;
            _brightnessValue.Text = _settings.Brightness.ToString("0.00");
            Raise(PictureChanged);
        };
        _contrast.ValueChanged += (_, _) =>
        {
            _settings.Contrast = _contrast.Value / 100f;
            _contrastValue.Text = _settings.Contrast.ToString("0.00");
            Raise(PictureChanged);
        };
        _saturation.ValueChanged += (_, _) =>
        {
            _settings.Saturation = _saturation.Value / 100f;
            _saturationValue.Text = _settings.Saturation.ToString("0.00");
            Raise(PictureChanged);
        };
        _artifactSmoothing.ValueChanged += (_, _) =>
        {
            _settings.ArtifactSmoothing = _artifactSmoothing.Value / 100f;
            _artifactSmoothingValue.Text = _settings.ArtifactSmoothing.ToString("0.00");
            Raise(PictureChanged);
        };
        _volume.ValueChanged += (_, _) =>
        {
            _settings.Volume = _volume.Value / 100f;
            _volumeValue.Text = $"{_volume.Value}%";
            Raise(VolumeChanged);
        };

        _micVolume.ValueChanged += (_, _) =>
        {
            _settings.MicVolume = _micVolume.Value / 100f;
            _micVolumeValue.Text = $"{_micVolume.Value}%";
            Raise(MicLevelChanged);
        };

        _audioBuffer.ValueChanged += (_, _) => { _settings.AudioLatencyMs = _audioBuffer.Value; Raise(AudioSelectionChanged); };
        _audioRestart.ValueChanged += (_, _) => { _settings.AudioRestartMinutes = _audioRestart.Value; Raise(AudioSelectionChanged); };
        _videoBitrate.ValueChanged += (_, _) => { _settings.VideoBitrateKbps = _videoBitrate.Value; Raise(RecordingSettingsChanged); };
        _audioBitrate.ValueChanged += (_, _) => { _settings.AudioBitrateKbps = _audioBitrate.Value; Raise(RecordingSettingsChanged); };
        _audioOffset.ValueChanged += (_, _) => { _settings.AudioOffsetMs = _audioOffset.Value; Raise(RecordingSettingsChanged); };
        _micOffset.ValueChanged += (_, _) => { _settings.MicOffsetMs = _micOffset.Value; Raise(MicSettingsChanged); };
        _replayBuffer.ValueChanged += (_, _) =>
        {
            _settings.ReplayBufferSeconds = _replayBuffer.Value;
            // Saving more than is kept is not a thing that can be honoured, so the save
            // length follows the buffer down rather than silently overshooting.
            if (_settings.ReplaySaveSeconds > _replayBuffer.Value)
            {
                _settings.ReplaySaveSeconds = _replayBuffer.Value;
                _replaySave.SetValueSilently(_replayBuffer.Value);
            }
            _replaySave.Maximum = _replayBuffer.Value;
            Raise(ReplaySettingsChanged);
        };
        _replaySave.ValueChanged += (_, _) => { _settings.ReplaySaveSeconds = _replaySave.Value; Raise(ReplaySettingsChanged); };

        _vsync.CheckedChanged += (_, _) => { _settings.VSync = _vsync.Checked; Raise(DisplayChanged); };
        _passthrough.CheckedChanged += (_, _) => { _settings.AudioPassthrough = _passthrough.Checked; Raise(AudioSelectionChanged); };
        _exclusive.CheckedChanged += (_, _) => { _settings.AudioExclusive = _exclusive.Checked; Raise(AudioSelectionChanged); };
        _hardware.CheckedChanged += (_, _) => { _settings.HardwareEncoder = _hardware.Checked; Raise(RecordingSettingsChanged); };
        _onTop.CheckedChanged += (_, _) => { _settings.AlwaysOnTop = _onTop.Checked; Raise(DisplayChanged); };
        _autoStart.CheckedChanged += (_, _) => { if (!_binding) _settings.AutoStart = _autoStart.Checked; };
        _showStats.CheckedChanged += (_, _) => { _settings.ShowStatusOverlay = _showStats.Checked; Raise(DisplayChanged); };
        _micEnabled.CheckedChanged += (_, _) => { _settings.MicEnabled = _micEnabled.Checked; Raise(MicSettingsChanged); };
        _micMonitor.CheckedChanged += (_, _) => { _settings.MicMonitor = _micMonitor.Checked; Raise(MicSettingsChanged); };
        _micMuted.CheckedChanged += (_, _) => { _settings.MicMuted = _micMuted.Checked; Raise(MicLevelChanged); };
        _replayEnabled.CheckedChanged += (_, _) => { _settings.ReplayEnabled = _replayEnabled.Checked; Raise(ReplaySettingsChanged); };

        _startStop.Click += (_, _) => StartStopClicked?.Invoke(this, EventArgs.Empty);
        _rescan.Click += (_, _) => RescanClicked?.Invoke(this, EventArgs.Empty);
        _record.Click += (_, _) => RecordClicked?.Invoke(this, EventArgs.Empty);
        _snapshot.Click += (_, _) => SnapshotClicked?.Invoke(this, EventArgs.Empty);
        _openFolder.Click += (_, _) => OpenOutputFolder();
        _saveReplay.Click += (_, _) => SaveReplayClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cards are laid out for the panel's size, so they are built once it is known.</summary>
    public void Build()
    {
        // Shared controls outlive a relayout, so they must leave the old cards before those
        // are disposed - otherwise disposing a card would dispose the controls with it.
        foreach (var control in Shared) control.Parent?.Controls.Remove(control);
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear();
        _pageHost.Controls.Clear();
        _binding = true;

        int columnWidth = (PageWidth - PagePad * 2 - ColumnGap) / 2;
        BuildCapture(columnWidth);
        BuildRecord(columnWidth);
        BuildShortcuts();
        BuildSettings(columnWidth);
        BuildAbout(columnWidth);

        LoadProfiles();
        LoadFromSettings();
        LoadShortcuts();
        _binding = false;
        ShowPage(_page);
    }

    private int PageWidth => Math.Max(560, Width - RailWidth - Gap);

    // ---- pages ----------------------------------------------------------------------------

    private ContentPanel NewPage(Page page)
    {
        var content = new ContentPanel { BackColor = Theme.Shell, Width = PageWidth };
        _pages[page] = content;
        _pageHost.Controls.Add(content);
        content.Visible = false;
        return content;
    }

    private void BuildCapture(int columnWidth)
    {
        var page = NewPage(Page.Capture);
        var left = new List<Card>();
        var right = new List<Card>();

        // --- PROFILE
        var profiles = new Card("Profile", Icon.Profile, columnWidth);
        profiles.AddRow("Profile", _profile);
        profiles.AddHint("A profile remembers its own card, format, picture, audio devices\nand recording quality. Switching one in applies it straight away.");
        profiles.AddSpace(4);
        var newProfile = new FlatButton { Text = "New", Glyph = Icon.Profile };
        var renameProfile = new FlatButton { Text = "Rename" };
        var deleteProfile = new FlatButton { Text = "Delete" };
        newProfile.Click += (_, _) => NewProfile();
        renameProfile.Click += (_, _) => RenameProfile();
        deleteProfile.Click += (_, _) => DeleteProfile();
        profiles.AddButtonsFilled(newProfile, renameProfile, deleteProfile);
        left.Add(profiles.Finish());

        // --- SOURCE
        var source = new Card("Source", Icon.Monitor, columnWidth);
        source.AddRow("Card", _device);
        source.AddRow("Format", _format);
        source.AddHint("NV12 and YUY2 arrive ready to display. MJPG must be decoded first,\nwhich costs a few ms - prefer uncompressed if the card offers it.");
        source.AddSpace(4);
        source.AddButtonsFilled(_startStop, _rescan);
        left.Add(source.Finish());

        // --- PICTURE
        var picture = new Card("Picture", Icon.Image, columnWidth);
        picture.AddHeaderAction("Reset", ResetPicture);
        picture.AddRow("Aspect", _aspect);
        picture.AddRow("Scaling", _scaling);
        picture.AddRow("Range", _range);
        picture.AddRow("Matrix", _matrix);
        picture.AddSlider("Brightness", Icon.Sun, _brightness, _brightnessValue);
        picture.AddSlider("Contrast", Icon.Contrast, _contrast, _contrastValue);
        picture.AddSlider("Saturation", Icon.Droplet, _saturation, _saturationValue);
        picture.AddSlider("Smoothing Pass", Icon.Sliders, _artifactSmoothing, _artifactSmoothingValue);
        picture.AddHint("Not needed on a decent card, but if you're using a low-end capture card\nthat produces blocky/smeared pixels, try this between 0.25 and 0.50 at most.\nPush it any further and you will ruin fine detail in every game you play.");
        left.Add(picture.Finish());

        // --- GRAPHICS
        var graphics = new Card("Graphics", Icon.Chip, columnWidth);
        graphics.AddRow("GPU", _adapter);
        graphics.AddHint("Changing this restarts the preview.");
        graphics.AddSpace(6);
        graphics.AddCheck(_vsync);
        graphics.AddHint("Off is the lowest latency. On, the wait happens before each frame\nis fetched, so it costs as little delay as vsync allows.");
        right.Add(graphics.Finish());

        // --- AUDIO
        var audio = new Card("Audio", Icon.Speaker, columnWidth);
        audio.AddHeaderAction("Reset", ResetAudio);
        audio.AddRow("Input", _audioIn);
        audio.AddRow("Output", _audioOut);
        audio.AddCheck(_passthrough);
        audio.AddSlider("Volume", Icon.Speaker, _volume, _volumeValue);
        audio.AddRow("Buffer", _audioBuffer, 88, "ms");
        audio.AddRow("Restart", _audioRestart, 88, "min  (0 = off)");
        audio.AddCheck(_exclusive);
        audio.AddMeter("Level", _meter);
        right.Add(audio.Finish());

        LayoutColumns(page, left, right);
    }

    private void BuildRecord(int columnWidth)
    {
        var page = NewPage(Page.Record);
        var left = new List<Card>();
        var right = new List<Card>();

        var recording = new Card("Recording", Icon.Record, columnWidth);
        recording.AddHeaderAction("Reset", ResetRecording);

        recording.AddRow("Folder", _folder, recording.FieldWidth - 40, "");
        var browse = new FlatButton { Text = "", Glyph = Icon.Folder };
        browse.SetBounds(recording.FieldLeft + recording.FieldWidth - 34, _folder.Top - 1, 34, 26);
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _folder.Text = dialog.SelectedPath;
            _settings.OutputFolder = dialog.SelectedPath;
            RecordingSettingsChanged?.Invoke(this, EventArgs.Empty);
            _recent.Reload(_settings.OutputFolder);
        };
        recording.Controls.Add(browse);

        recording.AddRow("Video", _videoBitrate, 88, "kbps");
        recording.AddRow("Audio", _audioBitrate, 88, "kbps");
        recording.AddRow("A/V offset", _audioOffset, 88, "ms");
        recording.AddCheck(_hardware);
        recording.AddSpace(4);

        recording.AddButtonsFilled(_record, _snapshot, _openFolder);
        left.Add(recording.Finish());

        var microphone = new Card("Microphone", Icon.Mic, columnWidth);
        microphone.AddHeaderAction("Reset", ResetMic);
        microphone.AddCheck(_micEnabled);
        microphone.AddRow("Mic", _micDevice);
        microphone.AddSlider("Level", Icon.Mic, _micVolume, _micVolumeValue);
        microphone.AddMeter("Signal", _micMeter);
        microphone.AddSpace(4);
        microphone.AddCheck(_micMuted);
        microphone.AddCheck(_micMonitor);
        microphone.AddRow("Delay", _micOffset, 88, "ms");
        microphone.AddHint("Your voice is mixed in ahead of the encoder, so it lands in recordings\nand in saved replays. It is kept out of the monitor by default - hearing\nyourself through any delay is unpleasant.");
        left.Add(microphone.Finish());

        var replay = new Card("Instant replay", Icon.Rewind, columnWidth);
        replay.AddCheck(_replayEnabled);
        replay.AddRow("Keep", _replayBuffer, 88, "seconds");
        replay.AddRow("Save", _replaySave, 88, "seconds of it");
        replay.AddHint($"The buffer runs while the preview is live, writing {ReplayBuffer.SegmentSeconds}-second pieces\nto a temporary folder and dropping the oldest. Saving joins the last few\nback together without re-encoding, so it takes a moment, not a minute.");
        replay.AddSpace(4);
        replay.AddMono(_replayState, 2);
        replay.AddButtonsFilled(_saveReplay);
        right.Add(replay.Finish());

        var status = new Card("Status", Icon.Info, columnWidth);
        status.AddMono(_recordState, 2);
        right.Add(status.Finish());

        var recent = new Card("Recent files", Icon.Folder, columnWidth);
        recent.AddHeaderAction("Refresh", () => _recent.Reload(_settings.OutputFolder));
        _recent.SetBounds(Card.Pad, 46, columnWidth - Card.Pad * 2, 232);
        recent.Controls.Add(_recent);
        recent.AddSpace(238);
        right.Add(recent.Finish());

        LayoutColumns(page, left, right);
    }

    /// <summary>
    /// One full-width card rather than two columns: the action names are sentences, and
    /// two chord fields have to sit beside each one.
    /// </summary>
    private void BuildShortcuts()
    {
        var page = NewPage(Page.Shortcuts);
        var card = new Card("Shortcuts", Icon.Keyboard, PageWidth - PagePad * 2);
        card.AddHeaderAction("Reset", () =>
        {
            _shortcuts.ResetToDefaults();
            LoadShortcuts();
            ShortcutsChanged?.Invoke(this, "Shortcuts put back to their defaults");
        });
        card.AddHint("Click a key and press the one you want. Esc cancels, Backspace clears it,\nand so does a right-click. The second column is an alternative for the same action.");
        card.AddSpace(8);

        card.AddSubheading("Recording");
        foreach (var definition in ShortcutCatalog.All.Where(d => d.IsRecording))
            AddShortcutRow(card, definition);

        card.AddSpace(10);
        card.AddSubheading("Everything else");
        foreach (var definition in ShortcutCatalog.All.Where(d => !d.IsRecording))
            AddShortcutRow(card, definition);

        card.Finish();
        card.Location = new Point(PagePad, 0);
        page.Controls.Add(card);
        page.Height = card.Height + 8;
    }

    private void AddShortcutRow(Card card, ShortcutDefinition definition) =>
        card.AddShortcut(definition.Label,
            _keyFields[(definition.Action, ShortcutSlot.Primary)],
            _keyFields[(definition.Action, ShortcutSlot.Alternate)]);

    private void LoadShortcuts()
    {
        foreach (var ((action, slot), field) in _keyFields)
            field.Keys = _shortcuts.Get(action, slot);
        foreach (var (action, label) in _aboutKeys)
            label.Text = _shortcuts.DescribeBoth(action);
    }

    private void BuildSettings(int columnWidth)
    {
        var page = NewPage(Page.Settings);
        var left = new List<Card>();
        var right = new List<Card>();

        var general = new Card("General", Icon.Sliders, columnWidth);
        general.AddCheck(_onTop);
        general.AddCheck(_autoStart);
        general.AddCheck(_showStats);
        left.Add(general.Finish());

        var maintenance = new Card("Diagnostics", Icon.Stopwatch, columnWidth);
        maintenance.AddHint("A trace records every frame for ten seconds and reports the\ndistribution, which shows up stutters that an average hides.");
        maintenance.AddSpace(6);
        var diagnostics = new FlatButton { Text = "Save diagnostics", Glyph = Icon.Download };
        var trace = new FlatButton { Text = "Trace 10 s", Glyph = Icon.Stopwatch };
        diagnostics.Click += (_, _) => DiagnosticsClicked?.Invoke(this, EventArgs.Empty);
        trace.Click += (_, _) => TraceClicked?.Invoke(this, EventArgs.Empty);
        maintenance.AddButtonsFilled(diagnostics, trace);

        var resetAll = new FlatButton { Text = "Reset everything", Glyph = Icon.Refresh };
        resetAll.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Put every setting back to its default?\n\n" +
                    "Your capture card, format and output folder are kept.",
                    "Reset everything", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            _settings.ResetToDefaults();
            LoadFromSettings();
            ResetAllClicked?.Invoke(this, EventArgs.Empty);
        };
        maintenance.AddButtonsFilled(resetAll);
        right.Add(maintenance.Finish());

        var live = new Card("Live pipeline", Icon.Monitor, columnWidth);
        live.AddMono(_statusLine, 3);
        left.Add(live.Finish());

        LayoutColumns(page, left, right);
    }

    /// <summary>
    /// .NET always stores an assembly version as four components, so a csproj
    /// &lt;Version&gt;1.1&lt;/Version&gt; still reports as "1.1.0.0" through Version.ToString().
    /// This keeps Major.Minor always, and only appends Build/Revision when they're non-zero,
    /// so "1.1" stays "1.1" but "1.1.3" or "1.1.3.2" still show in full.
    /// </summary>
    private static string FormatVersion(Version? version)
    {
        if (version is null) return "unknown";
        if (version.Revision > 0) return version.ToString(4);
        if (version.Build > 0) return version.ToString(3);
        return version.ToString(2);
    }

    private void BuildAbout(int columnWidth)
    {
        var page = NewPage(Page.About);
        var left = new List<Card>();
        var right = new List<Card>();

        var about = new Card("Ripsaw Studio", Icon.Info, columnWidth);
        about.AddText("A low-latency capture viewer and recorder for Windows.");
        about.AddText($"Version {FormatVersion(typeof(SettingsPanel).Assembly.GetName().Version)}", dim: true);
        about.AddSpace(6);
        about.AddText("Software for viewing and recording video from generic game capture cards.\n" +
                      "If your capture card's hardware causes frame drops or occasional stuttering,\n" +
                      "enable vsync at the cost of a slight increase in latency.", dim: true);
        about.AddSpace(6);
        about.AddText("Settings and logs:", dim: true);
        about.AddText(Path.GetDirectoryName(AppSettings.SettingsPath) ?? "", dim: true);
        left.Add(about.Finish());

        var keys = new Card("Shortcuts", Icon.Keyboard, columnWidth);
        keys.AddHeaderAction("Edit", () => ShowPage(Page.Shortcuts));
        _aboutKeys.Clear();
        foreach (var definition in ShortcutCatalog.All)
            _aboutKeys.Add((definition.Action,
                keys.AddKeyLine(definition.Label, _shortcuts.DescribeBoth(definition.Action))));
        right.Add(keys.Finish());

        LayoutColumns(page, left, right);
    }

    /// <summary>Stacks cards into two columns and sizes the page to the taller one.</summary>
    private void LayoutColumns(ContentPanel page, List<Card> left, List<Card> right)
    {
        int columnWidth = (PageWidth - PagePad * 2 - ColumnGap) / 2;
        int leftY = 0, rightY = 0;

        foreach (var card in left)
        {
            card.Location = new Point(PagePad, leftY);
            page.Controls.Add(card);
            leftY += card.Height + ColumnGap;
        }
        foreach (var card in right)
        {
            card.Location = new Point(PagePad + columnWidth + ColumnGap, rightY);
            page.Controls.Add(card);
            rightY += card.Height + ColumnGap;
        }
        page.Height = Math.Max(leftY, rightY) + 8;
    }

    private void ShowPage(Page page)
    {
        _page = page;
        _rail.Selected = page;
        _pageTitle.Text = page switch
        {
            Page.Capture => "CAPTURE SETTINGS",
            Page.Record => "RECORDING",
            Page.Shortcuts => "KEYBOARD SHORTCUTS",
            Page.Settings => "SETTINGS",
            _ => "ABOUT",
        };
        foreach (var (key, content) in _pages)
        {
            content.Visible = key == page;
            if (key == page) content.Top = 0;
        }
        if (page == Page.Record) _recent.Reload(_settings.OutputFolder);
    }

    // ---- layout of the panel itself ----------------------------------------------------

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _rail.SetBounds(0, 0, RailWidth, Height);
        _pageHost.SetBounds(RailWidth + Gap, ContentTop, Math.Max(1, Width - RailWidth - Gap),
                            Math.Max(1, Height - ContentTop - 12));
        _pageTitle.SetBounds(RailWidth + Gap + PagePad, 22, 400, 24);
        foreach (var content in _pages.Values)
        {
            content.Width = _pageHost.Width;
            content.Top = Math.Clamp(content.Top, Math.Min(0, ContentViewHeight - content.Height), 0);
        }
        UpdateRegion();
    }

    private int ContentTop => 58;
    private int ContentViewHeight => _pageHost.Height;

    /// <summary>Rounded corners on both the rail and the page area, so the video shows through.</summary>
    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = new GraphicsPath();
        using (var rail = FlatButton.Rounded(new Rectangle(0, 0, RailWidth, Height), 14))
            path.AddPath(rail, false);
        using (var page = FlatButton.Rounded(new Rectangle(RailWidth + Gap, 0, Width - RailWidth - Gap, Height), 14))
            path.AddPath(page, false);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Background);
        using var fill = new SolidBrush(Theme.Shell);
        using var page = FlatButton.Rounded(new Rectangle(RailWidth + Gap, 0, Width - RailWidth - Gap - 1, Height - 1), 14);
        g.FillPath(fill, page);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_filterInstalled) return;
        Application.AddMessageFilter(_wheelFilter);
        _filterInstalled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _filterInstalled)
        {
            Application.RemoveMessageFilter(_wheelFilter);
            _filterInstalled = false;
        }
        base.Dispose(disposing);
    }

    // ---- wheel scrolling ----------------------------------------------------------------

    private bool _filterInstalled;
    private MessageFilter? _wheelFilterBacking;
    private MessageFilter _wheelFilter => _wheelFilterBacking ??= new MessageFilter(this);

    public void ScrollBy(int delta)
    {
        if (!_pages.TryGetValue(_page, out var content)) return;
        int lowest = Math.Min(0, ContentViewHeight - content.Height);
        content.Top = Math.Clamp(content.Top + delta, lowest, 0);
    }

    /// <summary>Routes the wheel by cursor position rather than focus.</summary>
    private sealed class MessageFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly SettingsPanel _panel;

        public MessageFilter(SettingsPanel panel) => _panel = panel;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || !_panel.Visible || _panel.IsDisposed) return false;
            if (!_panel.RectangleToScreen(_panel.ClientRectangle).Contains(Control.MousePosition)) return false;

            // Let a control that uses the wheel itself have it; only scroll the page otherwise.
            if (WheelTarget() is Slider or NumericField) return false;

            _panel.ScrollBy((short)((long)m.WParam >> 16) > 0 ? 72 : -72);
            return true;
        }

        private static Control? WheelTarget()
        {
            var point = Control.MousePosition;
            var control = Control.FromHandle(WindowFromPoint(new POINT { X = point.X, Y = point.Y }));
            // A numeric field's editor is a child window, so step up to the field itself.
            return control?.Parent is NumericField field ? field : control;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);
    }

    // ---- resets --------------------------------------------------------------------------

    private void ResetPicture()
    {
        _settings.Brightness = 0f;
        _settings.Contrast = 1f;
        _settings.Saturation = 1f;
        _settings.ArtifactSmoothing = 0f;
        _settings.Range = RangeMode.Auto;
        _settings.Matrix = ColorMatrix.Auto;
        _settings.Aspect = AspectMode.Auto;
        _settings.Scaling = ScalingMode.Fit;
        LoadFromSettings();
        PictureChanged?.Invoke(this, EventArgs.Empty);
        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetAudio()
    {
        var defaults = new AppSettings();
        _settings.Volume = defaults.Volume;
        _settings.AudioLatencyMs = defaults.AudioLatencyMs;
        _settings.AudioRestartMinutes = defaults.AudioRestartMinutes;
        _settings.AudioExclusive = defaults.AudioExclusive;
        _settings.AudioPassthrough = defaults.AudioPassthrough;
        LoadFromSettings();
        AudioSelectionChanged?.Invoke(this, EventArgs.Empty);
        VolumeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetMic()
    {
        var defaults = new AppSettings();
        _settings.MicEnabled = defaults.MicEnabled;
        _settings.MicVolume = defaults.MicVolume;
        _settings.MicMuted = defaults.MicMuted;
        _settings.MicMonitor = defaults.MicMonitor;
        _settings.MicOffsetMs = defaults.MicOffsetMs;
        LoadFromSettings();
        MicSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetRecording()
    {
        var defaults = new AppSettings();
        _settings.VideoBitrateKbps = defaults.VideoBitrateKbps;
        _settings.AudioBitrateKbps = defaults.AudioBitrateKbps;
        _settings.AudioOffsetMs = defaults.AudioOffsetMs;
        _settings.HardwareEncoder = defaults.HardwareEncoder;
        LoadFromSettings();
        RecordingSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NewProfile()
    {
        string? name = Prompt.Ask(this, "New profile", "Name it after the console it is for:",
                                  "Console " + (_settings.Profiles.Count + 1));
        if (name is null) return;
        // A new profile is a copy of what is already running, and a rename touches nothing
        // but the label - neither needs the shell to re-apply anything, so neither raises
        // ProfileChanged. Only an actual switch does.
        _settings.AddProfile(name);
        LoadProfiles();
    }

    private void RenameProfile()
    {
        string? name = Prompt.Ask(this, "Rename profile", "New name:", _settings.ActiveProfile);
        if (name is null) return;
        _settings.RenameActiveProfile(name);
        LoadProfiles();
    }

    private void DeleteProfile()
    {
        if (_settings.Profiles.Count <= 1)
        {
            MessageBox.Show(this, "There has to be at least one profile.", "Delete profile",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Delete the profile \"{_settings.ActiveProfile}\"?", "Delete profile",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        _settings.DeleteActiveProfile();
        LoadProfiles();
        LoadFromSettings();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds the profile dropdown. Only for when the list itself changed - refilling a
    /// ComboBox's items from inside its own SelectedIndexChanged is reentrant, so an ordinary
    /// switch uses <see cref="SelectActiveProfile"/> instead.
    /// </summary>
    private void LoadProfiles()
    {
        bool wasBinding = _binding;
        _binding = true;
        _profile.Items.Clear();
        foreach (var profile in _settings.Profiles) _profile.Items.Add(profile);
        _binding = wasBinding;
        SelectActiveProfile();
    }

    private void SelectActiveProfile()
    {
        var active = _settings.Profiles.FirstOrDefault(p => p.Name == _settings.ActiveProfile);
        if (ReferenceEquals(_profile.SelectedItem, active)) return;
        bool wasBinding = _binding;
        _binding = true;
        _profile.SelectedItem = active;
        _binding = wasBinding;
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            System.Diagnostics.Process.Start("explorer.exe", _settings.OutputFolder);
        }
        catch { /* nothing useful to do if Explorer will not open */ }
    }

    private static void Configure(NumericField control, int min, int max, int step)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Increment = step;
    }

    private void Raise(EventHandler? handler)
    {
        if (_binding) return;
        handler?.Invoke(this, EventArgs.Empty);
    }

    // ---- binding -------------------------------------------------------------------------

    private void LoadFromSettings()
    {
        bool wasBinding = _binding;
        _binding = true;

        _aspect.SelectedIndex = (int)_settings.Aspect;
        _scaling.SelectedIndex = (int)_settings.Scaling;
        _range.SelectedIndex = (int)_settings.Range;
        _matrix.SelectedIndex = (int)_settings.Matrix;

        _brightness.SetValueSilently((int)Math.Clamp(_settings.Brightness * 100, -100, 100));
        _contrast.SetValueSilently((int)Math.Clamp(_settings.Contrast * 100, 0, 200));
        _saturation.SetValueSilently((int)Math.Clamp(_settings.Saturation * 100, 0, 200));
        _artifactSmoothing.SetValueSilently((int)Math.Clamp(_settings.ArtifactSmoothing * 100, 0, 100));
        _volume.SetValueSilently((int)Math.Clamp(_settings.Volume * 100, 0, 200));
        _micVolume.SetValueSilently((int)Math.Clamp(_settings.MicVolume * 100, 0, 200));
        _brightnessValue.Text = _settings.Brightness.ToString("0.00");
        _contrastValue.Text = _settings.Contrast.ToString("0.00");
        _saturationValue.Text = _settings.Saturation.ToString("0.00");
        _artifactSmoothingValue.Text = _settings.ArtifactSmoothing.ToString("0.00");
        _volumeValue.Text = $"{_volume.Value}%";
        _micVolumeValue.Text = $"{_micVolume.Value}%";

        _audioBuffer.SetValueSilently(_settings.AudioLatencyMs);
        _audioRestart.SetValueSilently(_settings.AudioRestartMinutes);
        _videoBitrate.SetValueSilently(_settings.VideoBitrateKbps);
        _audioBitrate.SetValueSilently(_settings.AudioBitrateKbps);
        _audioOffset.SetValueSilently(_settings.AudioOffsetMs);
        _micOffset.SetValueSilently(_settings.MicOffsetMs);
        _replayBuffer.SetValueSilently(_settings.ReplayBufferSeconds);
        _replaySave.Maximum = _settings.ReplayBufferSeconds;
        _replaySave.SetValueSilently(Math.Min(_settings.ReplaySaveSeconds, _settings.ReplayBufferSeconds));
        _folder.Text = _settings.OutputFolder;

        _vsync.Checked = _settings.VSync;
        _passthrough.Checked = _settings.AudioPassthrough;
        _exclusive.Checked = _settings.AudioExclusive;
        _hardware.Checked = _settings.HardwareEncoder;
        _onTop.Checked = _settings.AlwaysOnTop;
        _autoStart.Checked = _settings.AutoStart;
        _showStats.Checked = _settings.ShowStatusOverlay;
        _micEnabled.Checked = _settings.MicEnabled;
        _micMuted.Checked = _settings.MicMuted;
        _micMonitor.Checked = _settings.MicMonitor;
        _replayEnabled.Checked = _settings.ReplayEnabled;

        SelectById(_micDevice, _settings.MicDeviceId);
        SelectActiveProfile();

        _binding = wasBinding;
    }

    // ---- calls from the shell -------------------------------------------------------------

    public void SetAdapters(IEnumerable<string> adapters, string? selected)
    {
        _binding = true;
        _adapter.Items.Clear();
        _adapter.Items.Add(AutoAdapter);
        foreach (var name in adapters) _adapter.Items.Add(name);
        int index = selected is null ? 0 : _adapter.Items.IndexOf(selected);
        _adapter.SelectedIndex = index < 0 ? 0 : index;
        _binding = false;
    }

    public void SetDevices(IEnumerable<VideoDeviceInfo> devices, VideoDeviceInfo? select)
    {
        _binding = true;
        _device.Items.Clear();
        _device.Items.Add(NoDevice);
        foreach (var device in devices) _device.Items.Add(device);
        _device.SelectedItem = select is null
            ? NoDevice
            : _device.Items.OfType<VideoDeviceInfo>().FirstOrDefault(d => d.SymbolicLink == select.SymbolicLink) ?? NoDevice;
        _binding = false;
    }

    public void SetFormats(IEnumerable<VideoFormat> formats, int selectIndex)
    {
        _binding = true;
        _format.Items.Clear();
        foreach (var format in formats) _format.Items.Add(format);
        if (selectIndex >= 0 && selectIndex < _format.Items.Count) _format.SelectedIndex = selectIndex;
        _format.Enabled = _format.Items.Count > 0;
        _binding = false;
    }

    public void SetAudioDevices(IEnumerable<AudioDeviceInfo> inputs, IEnumerable<AudioDeviceInfo> outputs,
                                string? selectInput, string? selectOutput)
    {
        _binding = true;
        _audioIn.Items.Clear();
        _audioOut.Items.Clear();
        foreach (var device in inputs) _audioIn.Items.Add(device);
        foreach (var device in outputs) _audioOut.Items.Add(device);
        SelectById(_audioIn, selectInput);
        SelectById(_audioOut, selectOutput);
        _binding = false;
    }

    /// <summary>
    /// Fills the microphone list. The same capture endpoints as the card's audio input, so
    /// nothing new is enumerated - it is only a different question asked of the same list.
    /// </summary>
    public void SetMicDevices(IEnumerable<AudioDeviceInfo> inputs, string? selected)
    {
        _binding = true;
        _micDevice.Items.Clear();
        _micDevice.Items.Add(NoMic);
        foreach (var device in inputs) _micDevice.Items.Add(device);
        _micDevice.SelectedItem = _micDevice.Items.OfType<AudioDeviceInfo>()
            .FirstOrDefault(d => d.Id.Length > 0 && d.Id == selected) ?? NoMic;
        _binding = false;
    }

    public void SelectAudioInput(AudioDeviceInfo device)
    {
        _binding = true;
        _audioIn.SelectedItem = device;
        _binding = false;
    }

    public void SelectAudioOutputIfNone()
    {
        if (_audioOut.SelectedIndex >= 0 || _audioOut.Items.Count == 0) return;
        _binding = true;
        _audioOut.SelectedIndex = 0;
        _binding = false;
    }

    private static void SelectById(ComboBox combo, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is AudioDeviceInfo info && info.Id == id) { combo.SelectedIndex = i; return; }
    }

    public void SetVolumeSilently(float volume)
    {
        _volume.SetValueSilently((int)Math.Clamp(volume * 100, 0, 200));
        _volumeValue.Text = $"{_volume.Value}%";
    }

    public void SetStreaming(bool streaming)
    {
        _startStop.Text = streaming ? "Stop" : "Start";
        _startStop.Glyph = streaming ? Icon.Stop : Icon.Play;
    }

    public void SetRecording(bool recording)
    {
        _record.Text = recording ? "Stop" : "Record";
        _record.Glyph = recording ? Icon.Stop : Icon.Record;
        if (!recording) _recent.Reload(_settings.OutputFolder);
    }

    public void SetStatus(string text) => _statusLine.Text = text;

    public void SetRecordState(string text) => _recordState.Text = text;

    public void SetLevel(float peak) => _meter.SetLevel(peak);

    public void SetMicLevel(float peak) => _micMeter.SetLevel(peak);

    /// <summary>Re-reads every control from the settings, after something outside changed them.</summary>
    public void ReloadFromSettings() => LoadFromSettings();

    public void RefreshRecent() => _recent.Reload(_settings.OutputFolder);

    public void SetReplayState(string text) => _replayState.Text = text;

    public void SetReplaySaving(bool saving)
    {
        _saveReplay.Enabled = !saving;
        _saveReplay.Text = saving ? "Saving..." : "Save replay";
    }


    public void SetRailStatus(bool live, string top, string bottom) => _rail.SetStatus(live, top, bottom);
}

/// <summary>A page surface, taller than its viewport, slid by the wheel.</summary>
internal sealed class ContentPanel : Panel
{
    public ContentPanel()
    {
        BackColor = Theme.Shell;
        TabStop = false;
        DoubleBuffered = true;
    }
}

/// <summary>Recent captures in the output folder, click to open.</summary>
internal sealed class RecentList : Control
{
    private (string Path, string Name, string Detail)[] _items = Array.Empty<(string, string, string)>();
    private int _hovered = -1;
    private const int RowHeight = 28;

    public RecentList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public void Reload(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            _items = !directory.Exists
                ? Array.Empty<(string, string, string)>()
                : directory.EnumerateFiles()
                    .Where(f => f.Extension is ".mp4" or ".png")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(8)
                    .Select(f => (f.FullName, f.Name,
                        $"{f.LastWriteTime:dd MMM HH:mm}   {f.Length / 1024.0 / 1024.0:0.0} MB"))
                    .ToArray();
        }
        catch
        {
            _items = Array.Empty<(string, string, string)>();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);

        if (_items.Length == 0)
        {
            TextRenderer.DrawText(g, "Nothing recorded yet", Theme.Body,
                new Rectangle(0, 8, Width, 20), Theme.TextFaint, TextFormatFlags.Left);
            return;
        }

        for (int i = 0; i < _items.Length; i++)
        {
            var row = new Rectangle(0, i * RowHeight, Width, RowHeight - 2);
            if (i == _hovered)
            {
                using var back = new SolidBrush(Theme.Hover);
                using var path = FlatButton.Rounded(row, 5);
                g.FillPath(back, path);
            }
            TextRenderer.DrawText(g, _items[i].Name, Theme.Body,
                new Rectangle(row.X + 8, row.Y, row.Width - 150, row.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);
            TextRenderer.DrawText(g, _items[i].Detail, Theme.Small,
                new Rectangle(row.Right - 148, row.Y, 140, row.Height), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = e.Y / RowHeight;
        if (hit >= _items.Length) hit = -1;
        if (hit == _hovered) return;
        _hovered = hit;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { _hovered = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int hit = e.Y / RowHeight;
        if (hit < 0 || hit >= _items.Length) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_items[hit].Path) { UseShellExecute = true });
        }
        catch { /* the file may have been moved since the list was built */ }
    }
}

/// <summary>Thin peak meter - the fastest way to tell whether audio is arriving at all.</summary>
internal sealed class LevelMeter : Control
{
    private float _level;

    public LevelMeter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        TabStop = false;
    }

    public void SetLevel(float level)
    {
        if (Math.Abs(level - _level) < 0.01f) return;
        _level = level;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        using (var back = new SolidBrush(Theme.Track))
            g.FillRectangle(back, 0, 0, Width, Height);

        int filled = (int)(Math.Clamp(_level, 0f, 1f) * Width);
        if (filled <= 0) return;
        var colour = _level > 0.95f ? Theme.Record : _level > 0.7f ? Color.FromArgb(226, 186, 92) : Theme.Good;
        using var brush = new SolidBrush(colour);
        g.FillRectangle(brush, 0, 0, filled, Height);
    }
}
