using UiIcon = RipsawStudio.UI.Icon;
using RipsawStudio.Audio;
using RipsawStudio.Capture;
using RipsawStudio.Record;
using RipsawStudio.Render;

namespace RipsawStudio.UI;

/// <summary>
/// The control window that floats over the picture: the navigation bar of category icons along
/// the top, the category's pages as tabs under it, and the selected page's settings as one
/// aligned column of rows.
///
/// A borderless owned top-level window rather than a child control, so it can fade with plain
/// <see cref="Form.Opacity"/> - a child control sitting over the D3D swap chain has no opacity
/// that composites onto it.
/// </summary>
internal sealed class ControlPanel : Form
{
    private const int NavHeight = 76;
    private const int TabsHeight = 46;
    private const int HeaderHeight = NavHeight + TabsHeight;
    private const int PadX = 36;
    /// <summary>The settings column never grows past this, and is centred in wider windows.</summary>
    private const int ColumnMaxWidth = 780;
    private const int PadTop = 26;
    private const int LabelWidth = 168;
    private const int RowHeight = 30;
    private const int RowGap = 10;
    private const int ScrollbarGutter = 14;

    private readonly AppSettings _settings;
    private readonly ShortcutMap _shortcuts;
    private readonly SignalStrip _strip = new();
    private readonly NavBar _nav = new();
    private readonly Panel _pageHost = new();
    private readonly ScrollThumb _scrollThumb = new();
    private readonly Dictionary<Page, ContentPanel> _pages = new();
    private Page _page = Page.Source;
    /// <summary>The page last open in each category, so clicking its icon goes back there.</summary>
    private readonly Dictionary<Category, Page> _lastPage = new();
    private bool _binding = true;

    // ---- controls that outlive a rebuild ----------------------------------------------------
    private readonly FlatCombo _profile = new();
    private readonly FlatCombo _device = new();
    private readonly FlatCombo _format = new();
    private readonly FlatCombo _adapter = new();
    private readonly FlatCombo _audioIn = new();
    private readonly FlatCombo _audioOut = new();
    private readonly FlatCombo _micDevice = new();
    private readonly FlatCombo _encoder = new();
    private readonly FlatCombo _language = new();

    private readonly Segmented _aspect = new();
    private readonly Segmented _scaling = new();
    private readonly Segmented _range = new();
    private readonly Segmented _matrix = new();
    private readonly Segmented _codec = new() { Items = new[] { "H.264", "HEVC" } };
    private readonly Segmented _container = new();
    private readonly Segmented _rate = new();

    private readonly Slider _brightness = new() { Bipolar = true, Minimum = -100, Maximum = 100 };
    private readonly Slider _contrast = new() { Minimum = 0, Maximum = 200 };
    private readonly Slider _saturation = new() { Minimum = 0, Maximum = 200 };
    private readonly Slider _smoothing = new() { Minimum = 0, Maximum = 100 };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 200 };
    private readonly Slider _micVolume = new() { Minimum = 0, Maximum = 200 };
    private readonly Slider _quality = new() { Minimum = 1, Maximum = 100 };
    private readonly Label _brightnessValue = new();
    private readonly Label _contrastValue = new();
    private readonly Label _saturationValue = new();
    private readonly Label _smoothingValue = new();
    private readonly Label _volumeValue = new();
    private readonly Label _micVolumeValue = new();
    private readonly Label _qualityValue = new();

    private readonly NumericField _audioBuffer = new() { Minimum = 10, Maximum = 300, Increment = 5 };
    private readonly NumericField _audioRestart = new() { Minimum = 0, Maximum = 600, Increment = 5 };
    private readonly NumericField _videoBitrate = new() { Minimum = 1000, Maximum = 200_000, Increment = 1000 };
    private readonly NumericField _audioBitrate = new() { Minimum = 64, Maximum = 320, Increment = 32 };
    private readonly NumericField _audioOffset = new() { Minimum = -2000, Maximum = 2000, Increment = 5 };
    private readonly NumericField _micOffset = new() { Minimum = 0, Maximum = 1000, Increment = 5 };
    private readonly NumericField _keyframe = new() { Minimum = 1, Maximum = 10, Increment = 1 };
    private readonly NumericField _replayBuffer = new() { Minimum = 15, Maximum = 300, Increment = 15 };
    private readonly NumericField _replaySave = new() { Minimum = 5, Maximum = 300, Increment = 5 };
    private readonly PathField _recordsFolder = new();
    private readonly PathField _replayFolder = new();
    private readonly PathField _screenshotsFolder = new();

    private readonly FlatCheck _vsync = new();
    private readonly FlatCheck _passthrough = new();
    private readonly FlatCheck _desktopAudio = new();
    private readonly FlatCheck _exclusive = new();
    private readonly FlatCheck _onTop = new();
    private readonly FlatCheck _autoStart = new();
    private readonly FlatCheck _showStats = new();
    private readonly FlatCheck _micEnabled = new();
    private readonly FlatCheck _micMuted = new();
    private readonly FlatCheck _micMonitor = new();
    private readonly FlatCheck _micDucking = new();
    private readonly FlatCheck _replayEnabled = new();
    private readonly FlatCheck _globalKeys = new();

    private readonly FlatButton _startStop = new() { ButtonRole = FlatButton.Role.Accent, Glyph = UiIcon.Play };
    private readonly FlatButton _rescan = new() { Glyph = UiIcon.Refresh };
    private readonly LevelMeter _meter = new();
    private readonly LevelMeter _micMeter = new();
    private readonly Label _liveStatus = new();
    private readonly Label _statusLine = new();
    private readonly Label _recordState = new();
    private readonly Label _replayState = new();
    private readonly Label _encoderNote = new();
    private readonly Label _containerNote = new();
    private readonly Label _updateStatus = new();
    private readonly FlatButton _download = new() { ButtonRole = FlatButton.Role.Accent, Visible = false };
    private readonly LibraryView _library;
    private UpdateCheckResult? _lastUpdate;
    private bool _streaming;
    private bool _replaySaving;
    private bool _replayArmed;

    private readonly Dictionary<(ShortcutAction, ShortcutSlot), KeyCaptureButton> _keyFields = new();

    private List<EncoderInfo> _encoders = new();
    private string? _previewAdapter;

    // Records, so a fresh copy still equals the one in the list.
    private static VideoDeviceInfo NoDevice => new(Tr("No capture card"), "");
    private static AudioDeviceInfo NoMic => new("", Tr("No microphone"));

    private sealed record LanguageChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }

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
    public event EventHandler? EncoderTestClicked;
    public event EventHandler? ResetAllClicked;
    public event EventHandler? ProfileChanged;
    public event EventHandler? MicSettingsChanged;
    public event EventHandler? MicLevelChanged;
    public event EventHandler? ReplaySettingsChanged;
    public event EventHandler? SaveReplayClicked;
    public event EventHandler? CloseClicked;
    public event EventHandler? OpenFolderClicked;
    public event EventHandler? LanguageChanged;
    /// <summary>A binding changed; the string is a note worth showing, or empty.</summary>
    public event EventHandler<string>? ShortcutsChanged;
    /// <summary>Anything at all that ends up in settings.json changed - the shell saves soon after.</summary>
    public event EventHandler? SettingsTouched;

    public VideoDeviceInfo? SelectedDevice => _device.SelectedItem as VideoDeviceInfo;
    public VideoFormat? SelectedFormat => _format.SelectedItem as VideoFormat;
    public AudioDeviceInfo? SelectedAudioIn => _audioIn.SelectedItem as AudioDeviceInfo;
    public AudioDeviceInfo? SelectedAudioOut => _audioOut.SelectedItem as AudioDeviceInfo;
    public string? SelectedAdapter => _adapter.SelectedIndex <= 0 ? null : _adapter.SelectedItem as string;
    public AudioDeviceInfo? SelectedMic =>
        _micDevice.SelectedItem is AudioDeviceInfo info && info.Id.Length > 0 ? info : null;
    public Page CurrentPage => _page;

    public ControlPanel(AppSettings settings, ShortcutMap shortcuts)
    {
        _settings = settings;
        _shortcuts = shortcuts;
        _library = new LibraryView(settings);
        _library.SettingsTouched += (_, _) => SettingsTouched?.Invoke(this, EventArgs.Empty);
        _library.OpenFolderClicked += (_, _) => OpenFolderClicked?.Invoke(this, EventArgs.Empty);
        _library.ScrollChanged += (_, _) => { if (_page == Page.Library) UpdateScrollbar(); };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Theme.Shell;
        Font = Theme.Body;
        DoubleBuffered = true;

        Controls.Add(_nav);
        Controls.Add(_strip);
        _pageHost.BackColor = Theme.Shell;
        Controls.Add(_pageHost);
        _scrollThumb.DragToRatio += ScrollToRatio;
        Controls.Add(_scrollThumb);
        _scrollThumb.BringToFront();

        _nav.CategorySelected += (_, category) =>
            ShowPage(_lastPage.TryGetValue(category, out var last) ? last : Pages.PagesOf(category)[0].Page);
        _strip.TabSelected += (_, page) => ShowPage(page);
        _nav.Record.Click += (_, _) => RecordClicked?.Invoke(this, EventArgs.Empty);
        _nav.SaveReplay.Click += (_, _) => SaveReplayClicked?.Invoke(this, EventArgs.Empty);
        _nav.Snapshot.Click += (_, _) => SnapshotClicked?.Invoke(this, EventArgs.Empty);
        _nav.Close.Click += (_, _) => CloseClicked?.Invoke(this, EventArgs.Empty);
        _nav.SetReplayAvailable(false, false);

        foreach (var definition in ShortcutCatalog.All)
        {
            _keyFields[(definition.Action, ShortcutSlot.Primary)] = NewKeyField(definition.Action, ShortcutSlot.Primary);
            _keyFields[(definition.Action, ShortcutSlot.Alternate)] = NewKeyField(definition.Action, ShortcutSlot.Alternate);
        }
        WireHandlers();
    }

    /// <summary>Every control that survives a rebuild, so it can be detached before the old pages go.</summary>
    private IEnumerable<Control> Shared => new Control[]
    {
        _profile, _device, _format, _adapter, _audioIn, _audioOut, _micDevice, _encoder, _language,
        _aspect, _scaling, _range, _matrix, _codec, _container, _rate,
        _brightness, _contrast, _saturation, _smoothing, _volume, _micVolume, _quality,
        _brightnessValue, _contrastValue, _saturationValue, _smoothingValue, _volumeValue, _micVolumeValue, _qualityValue,
        _audioBuffer, _audioRestart, _videoBitrate, _audioBitrate, _audioOffset, _micOffset, _keyframe,
        _replayBuffer, _replaySave, _recordsFolder, _replayFolder, _screenshotsFolder,
        _vsync, _passthrough, _desktopAudio, _exclusive, _onTop, _autoStart, _showStats, _micEnabled, _micMuted,
        _micMonitor, _micDucking, _replayEnabled, _globalKeys,
        _startStop, _rescan, _meter, _micMeter,
        _liveStatus, _statusLine, _recordState, _replayState, _encoderNote, _containerNote, _updateStatus, _download, _library,
    }.Concat(_keyFields.Values);

    private KeyCaptureButton NewKeyField(ShortcutAction action, ShortcutSlot slot)
    {
        var field = new KeyCaptureButton();
        field.Rebound += (_, keys) =>
        {
            var displaced = _shortcuts.Set(action, slot, keys);
            LoadShortcuts();
            ShortcutsChanged?.Invoke(this, displaced is null
                ? ""
                : Tr("{0} was on \"{1}\", it has been cleared there.",
                     ShortcutCatalog.Describe(keys), Tr(ShortcutCatalog.Definition(displaced.Value).Label)));
            SettingsTouched?.Invoke(this, EventArgs.Empty);
        };
        return field;
    }

    /// <summary>Labels of the controls that outlive a rebuild, in the current language.</summary>
    private void ApplyTexts()
    {
        _aspect.Items = new[] { Tr("Auto"), "16:9", "4:3", "16:10" };
        _scaling.Items = new[] { Tr("Fit"), Tr("Stretch"), Tr("1:1 pixels") };
        _range.Items = new[] { Tr("Auto"), Tr("Expand"), Tr("Compress") };
        _matrix.Items = new[] { Tr("Auto"), "BT.601", "BT.709" };
        _container.Items = new[] { Tr("Fragmented MP4"), "MP4" };
        _rate.Items = new[] { Tr("Constant"), Tr("Variable"), Tr("Quality") };

        _vsync.Text = Tr("Wait for vsync");
        _passthrough.Text = Tr("Play the game sound through the output");
        _desktopAudio.Text = Tr("Record the PC sound instead of the game sound");
        _exclusive.Text = Tr("Exclusive output, for the lowest delay");
        _onTop.Text = Tr("Keep the window on top");
        _autoStart.Text = Tr("Start the preview when the app opens");
        _showStats.Text = Tr("Show live stats over the picture");
        _micEnabled.Text = Tr("Mix a microphone into recordings and replays");
        _micMuted.Text = Tr("Mute the microphone");
        _micMonitor.Text = Tr("Hear yourself in the monitor");
        _micDucking.Text = Tr("Lower the game while you talk");
        _replayEnabled.Text = Tr("Keep the last moments ready to save");
        _globalKeys.Text = Tr("Capture keys work while a game has focus");

        _rescan.Text = Tr("Rescan devices");
        _download.Text = Tr("Download update");
        if (UpdateChecker.IsStoreInstall) _updateStatus.Text = Tr("Updates are installed by the Microsoft Store.");
        else if (_lastUpdate is null) _updateStatus.Text = Tr("Checking for updates...");
        foreach (var ((_, slot), field) in _keyFields)
            field.EmptyText = slot == ShortcutSlot.Primary ? Tr("Not set") : Tr("Add a key");
        SetStreaming(_streaming);
        SetReplayState(_replayArmed, _replaySaving);
        _nav.ApplyTexts();
        _library.ApplyTexts();

        _language.Items.Clear();
        _language.Items.Add(new LanguageChoice(Loc.Auto, Tr("Automatic (Windows)")));
        foreach (var (code, name) in Loc.Languages) _language.Items.Add(new LanguageChoice(code, name));
        _language.SelectedItem = _language.Items.OfType<LanguageChoice>().FirstOrDefault(l => l.Code == _settings.Language)
                                 ?? _language.Items[0];
    }

    private void WireHandlers()
    {
        _device.SelectedIndexChanged += (_, _) => Raise(DeviceSelectionChanged);
        _format.SelectedIndexChanged += (_, _) => Raise(DeviceSelectionChanged);
        _audioIn.SelectedIndexChanged += (_, _) => Raise(AudioSelectionChanged);
        _audioOut.SelectedIndexChanged += (_, _) => Raise(AudioSelectionChanged);
        _adapter.SelectedIndexChanged += (_, _) => { if (_binding) return; _settings.Adapter = SelectedAdapter; Raise(AdapterChanged); };
        _profile.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _profile.SelectedItem is not ProfileData profile) return;
            _settings.SwitchProfile(profile.Name);
            LoadFromSettings();
            ProfileChanged?.Invoke(this, EventArgs.Empty);
            SettingsTouched?.Invoke(this, EventArgs.Empty);
        };
        _micDevice.SelectedIndexChanged += (_, _) => { if (_binding) return; _settings.MicDeviceId = SelectedMic?.Id; Raise(MicSettingsChanged); };
        _language.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _language.SelectedItem is not LanguageChoice choice || choice.Code == _settings.Language) return;
            _settings.Language = choice.Code;
            SettingsTouched?.Invoke(this, EventArgs.Empty);
            // After the combo's own event has finished: the panel is rebuilt around it.
            BeginInvoke(() => LanguageChanged?.Invoke(this, EventArgs.Empty));
        };

        _aspect.SelectedIndexChanged += (_, _) => { _settings.Aspect = (AspectMode)_aspect.SelectedIndex; Raise(DisplayChanged); };
        _scaling.SelectedIndexChanged += (_, _) => { _settings.Scaling = (ScalingMode)_scaling.SelectedIndex; Raise(DisplayChanged); };
        _range.SelectedIndexChanged += (_, _) =>
        {
            _settings.Range = (RangeMode)_range.SelectedIndex;
            Raise(PictureChanged);
            // The range is also tagged into recordings, so the encoder settings follow it.
            Raise(RecordingSettingsChanged);
        };
        _matrix.SelectedIndexChanged += (_, _) => { _settings.Matrix = (ColorMatrix)_matrix.SelectedIndex; Raise(PictureChanged); };

        BindSlider(_brightness, _brightnessValue, v => _settings.Brightness = v / 100f, v => (v / 100f).ToString("0.00"), () => Raise(PictureChanged));
        BindSlider(_contrast, _contrastValue, v => _settings.Contrast = v / 100f, v => (v / 100f).ToString("0.00"), () => Raise(PictureChanged));
        BindSlider(_saturation, _saturationValue, v => _settings.Saturation = v / 100f, v => (v / 100f).ToString("0.00"), () => Raise(PictureChanged));
        BindSlider(_smoothing, _smoothingValue, v => _settings.ArtifactSmoothing = v / 100f, v => (v / 100f).ToString("0.00"), () => Raise(PictureChanged));
        BindSlider(_volume, _volumeValue, v => _settings.Volume = v / 100f, v => $"{v}%", () => Raise(VolumeChanged));
        BindSlider(_micVolume, _micVolumeValue, v => _settings.MicVolume = v / 100f, v => $"{v}%", () => Raise(MicLevelChanged));
        BindSlider(_quality, _qualityValue, v => _settings.Quality = v, v => v.ToString(), () => Raise(RecordingSettingsChanged));

        _audioBuffer.ValueChanged += (_, _) => { _settings.AudioLatencyMs = _audioBuffer.Value; Raise(AudioSelectionChanged); };
        _audioRestart.ValueChanged += (_, _) => { _settings.AudioRestartMinutes = _audioRestart.Value; Raise(AudioSelectionChanged); };
        _videoBitrate.ValueChanged += (_, _) => { _settings.VideoBitrateKbps = _videoBitrate.Value; Raise(RecordingSettingsChanged); };
        _audioBitrate.ValueChanged += (_, _) => { _settings.AudioBitrateKbps = _audioBitrate.Value; Raise(RecordingSettingsChanged); };
        _audioOffset.ValueChanged += (_, _) => { _settings.AudioOffsetMs = _audioOffset.Value; Raise(RecordingSettingsChanged); };
        _keyframe.ValueChanged += (_, _) => { _settings.KeyframeSeconds = _keyframe.Value; Raise(RecordingSettingsChanged); };
        _micOffset.ValueChanged += (_, _) => { _settings.MicOffsetMs = _micOffset.Value; Raise(MicSettingsChanged); };
        _replayBuffer.ValueChanged += (_, _) =>
        {
            _settings.ReplayBufferSeconds = _replayBuffer.Value;
            // Saving more than is kept cannot be honoured, so the save length follows it down.
            if (_settings.ReplaySaveSeconds > _replayBuffer.Value)
            {
                _settings.ReplaySaveSeconds = _replayBuffer.Value;
                _replaySave.SetValueSilently(_replayBuffer.Value);
            }
            _replaySave.Maximum = _replayBuffer.Value;
            Raise(ReplaySettingsChanged);
        };
        _replaySave.ValueChanged += (_, _) => { _settings.ReplaySaveSeconds = _replaySave.Value; Raise(ReplaySettingsChanged); };

        _codec.SelectedIndexChanged += (_, _) =>
        {
            if (_binding) return;
            _settings.Codec = (VideoCodec)_codec.SelectedIndex;
            FillEncoders();
            UpdateEncoderNotes();
            Raise(RecordingSettingsChanged);
        };
        _encoder.SelectedIndexChanged += (_, _) =>
        {
            if (_binding) return;
            _settings.EncoderId = _encoder.SelectedItem is EncoderChoice choice ? choice.Id : "auto";
            UpdateEncoderNotes();
            Raise(RecordingSettingsChanged);
        };
        _container.SelectedIndexChanged += (_, _) => { _settings.Container = (ContainerFormat)_container.SelectedIndex; UpdateEncoderNotes(); Raise(RecordingSettingsChanged); };
        _rate.SelectedIndexChanged += (_, _) => { _settings.RateControl = (RateControl)_rate.SelectedIndex; UpdateEncoderNotes(); Raise(RecordingSettingsChanged); };

        _recordsFolder.PathChanged += (_, path) => { _settings.RecordsFolder = path; Raise(RecordingSettingsChanged); ReloadRecent(); };
        _screenshotsFolder.PathChanged += (_, path) => { _settings.ScreenshotsFolder = path; Raise(RecordingSettingsChanged); ReloadRecent(); };
        _replayFolder.PathChanged += (_, path) => { _settings.ReplayFolder = path; Raise(ReplaySettingsChanged); ReloadRecent(); };

        _vsync.CheckedChanged += (_, _) => { _settings.VSync = _vsync.Checked; Raise(DisplayChanged); };
        _passthrough.CheckedChanged += (_, _) => { _settings.AudioPassthrough = _passthrough.Checked; Raise(AudioSelectionChanged); };
        _desktopAudio.CheckedChanged += (_, _) => { _settings.RecordDesktopAudio = _desktopAudio.Checked; Raise(AudioSelectionChanged); };
        _exclusive.CheckedChanged += (_, _) => { _settings.AudioExclusive = _exclusive.Checked; Raise(AudioSelectionChanged); };
        _onTop.CheckedChanged += (_, _) => { _settings.AlwaysOnTop = _onTop.Checked; Raise(DisplayChanged); };
        _autoStart.CheckedChanged += (_, _) => { if (_binding) return; _settings.AutoStart = _autoStart.Checked; SettingsTouched?.Invoke(this, EventArgs.Empty); };
        _showStats.CheckedChanged += (_, _) => { _settings.ShowStatusOverlay = _showStats.Checked; Raise(DisplayChanged); };
        _globalKeys.CheckedChanged += (_, _) =>
        {
            if (_binding) return;
            _settings.GlobalShortcuts = _globalKeys.Checked;
            // Empty note: MainForm reports which chords it actually managed to take.
            ShortcutsChanged?.Invoke(this, "");
            SettingsTouched?.Invoke(this, EventArgs.Empty);
        };
        _micEnabled.CheckedChanged += (_, _) => { _settings.MicEnabled = _micEnabled.Checked; Raise(MicSettingsChanged); };
        _micMonitor.CheckedChanged += (_, _) => { _settings.MicMonitor = _micMonitor.Checked; Raise(MicSettingsChanged); };
        _micMuted.CheckedChanged += (_, _) => { _settings.MicMuted = _micMuted.Checked; Raise(MicLevelChanged); };
        _micDucking.CheckedChanged += (_, _) => { _settings.MicDucking = _micDucking.Checked; Raise(MicLevelChanged); };
        _replayEnabled.CheckedChanged += (_, _) => { _settings.ReplayEnabled = _replayEnabled.Checked; Raise(ReplaySettingsChanged); };

        _startStop.Click += (_, _) => StartStopClicked?.Invoke(this, EventArgs.Empty);
        _rescan.Click += (_, _) => RescanClicked?.Invoke(this, EventArgs.Empty);
        _download.Click += (_, _) => UpdateChecker.OpenReleasesPage();
    }

    private void BindSlider(Slider slider, Label value, Action<int> apply, Func<int, string> format, Action notify)
    {
        slider.ValueChanged += (_, _) =>
        {
            apply(slider.Value);
            value.Text = format(slider.Value);
            notify();
        };
        _sliderFormats[slider] = (value, format);
    }

    private readonly Dictionary<Slider, (Label Value, Func<int, string> Format)> _sliderFormats = new();

    private void Raise(EventHandler? handler)
    {
        if (_binding) return;
        handler?.Invoke(this, EventArgs.Empty);
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    // ---- building ---------------------------------------------------------------------------

    /// <summary>Pages are laid out for the window's size, so they are built once it is known.</summary>
    public void Build()
    {
        foreach (var control in Shared) control.Parent?.Controls.Remove(control);
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear();
        _pageHost.Controls.Clear();
        _binding = true;
        ApplyTexts();

        BuildSource();
        BuildPicture();
        BuildSound();
        BuildVideo();
        BuildRecordAudio();
        BuildReplay();
        BuildLibrary();
        BuildShortcuts();
        BuildGeneral();
        BuildAbout();

        LoadProfiles();
        LoadFromSettings();
        LoadShortcuts();
        _binding = false;
        if (_lastUpdate is { } last) ShowUpdateStatus(last);
        ShowPage(_page);
    }

    private int PageWidth => Math.Max(480, Width - ScrollbarGutter);

    private Sheet NewSheet(Page page)
    {
        var content = new ContentPanel { BackColor = Theme.Shell, Width = PageWidth, Visible = false };
        _pages[page] = content;
        _pageHost.Controls.Add(content);
        return new Sheet(content, PageWidth, Math.Max(PadX, (PageWidth - ColumnMaxWidth) / 2));
    }

    private void BuildSource()
    {
        var s = NewSheet(Page.Source);
        s.Group("Capture card");
        s.Row("Card", _device);
        s.Row("Format", _format);
        s.Mono(_liveStatus);
        s.Buttons(_startStop, _rescan);
        s.Note("Uncompressed formats (NV12, YUY2) reach the screen fastest. MJPG has to be decoded first.");

        s.Group("Profile");
        s.Row("Active profile", _profile);
        var add = new FlatButton { Text = Tr("New"), ButtonRole = FlatButton.Role.Normal };
        var rename = new FlatButton { Text = Tr("Rename"), ButtonRole = FlatButton.Role.Ghost };
        var delete = new FlatButton { Text = Tr("Delete"), ButtonRole = FlatButton.Role.Ghost };
        add.Click += (_, _) => NewProfile();
        rename.Click += (_, _) => RenameProfile();
        delete.Click += (_, _) => DeleteProfile();
        s.Buttons(add, rename, delete);
        s.Note("A profile keeps its own card, format, picture, audio devices and bitrate. Make one per console.");

        s.Group("Display");
        s.Row("Graphics card", _adapter);
        s.Note("Changing it restarts the preview.");
        s.Toggle(_vsync);
        s.Note("Removes tearing at the cost of up to one refresh of delay. Turn it on if the picture stutters or freezes.");
        s.Finish();
    }

    private void BuildPicture()
    {
        var s = NewSheet(Page.Picture);
        s.Group("Shape", ("Reset picture", ResetPicture));
        s.Row("Aspect ratio", _aspect);
        s.Row("Scaling", _scaling);

        s.Group("Colour");
        s.Row("Range", _range);
        s.Note("Expand if blacks look grey and washed out. Compress if dark detail is crushed to black.");
        s.Row("Matrix", _matrix);
        s.Slider("Brightness", _brightness, _brightnessValue);
        s.Slider("Contrast", _contrast, _contrastValue);
        s.Slider("Saturation", _saturation, _saturationValue);

        s.Group("Cleanup");
        s.Slider("Smoothing", _smoothing, _smoothingValue);
        s.Note("For cheap cards that produce blocky pixels. Stay between 0.25 and 0.50; more blurs fine detail.");
        s.Finish();
    }

    /// <summary>What you hear while playing: where the card's sound comes from and where it goes.</summary>
    private void BuildSound()
    {
        var s = NewSheet(Page.Sound);
        s.Group("Game sound", ("Reset sound", ResetSound));
        s.Row("Input", _audioIn);
        s.Row("Output", _audioOut);
        s.Toggle(_passthrough);
        s.Slider("Volume", _volume, _volumeValue);
        s.Meter("Level", _meter);

        s.Group("Delay");
        s.Row("Buffer", _audioBuffer, 96, "ms");
        s.Toggle(_exclusive);
        s.Row("Restart every", _audioRestart, 96, "minutes (0 = never)");
        s.Note("Some cards drift out of sync over hours. A scheduled restart pulls them back.");
        s.Note("What ends up in recordings is set under Recording > Audio.");
        s.Finish();
    }

    private void BuildVideo()
    {
        var s = NewSheet(Page.Video);
        s.Mono(_recordState);

        s.Group("Encoder", ("Reset video", ResetVideo));
        s.Row("Codec", _codec);
        s.Row("Encoder", _encoder);
        s.Text(_encoderNote);
        s.Row("Rate control", _rate);
        s.Row("Video bitrate", _videoBitrate, 120, "kbps");
        s.Slider("Quality", _quality, _qualityValue);
        s.Row("Keyframe every", _keyframe, 96, "seconds");

        s.Group("File");
        s.Row("Container", _container);
        s.Text(_containerNote);
        s.Row("Recordings", _recordsFolder);
        s.Row("Screenshots", _screenshotsFolder);
        s.Finish();
    }

    /// <summary>Everything about the sound that ends up in recordings and replays.</summary>
    private void BuildRecordAudio()
    {
        var s = NewSheet(Page.RecordAudio);
        s.Group("Recorded sound", ("Reset audio", ResetRecordAudio));
        s.Toggle(_desktopAudio);
        s.Note("Records everything the output plays, voice chat included. The game is only in it while its sound plays through the output.");
        s.Row("Audio bitrate", _audioBitrate, 96, "kbps");
        s.Row("A/V offset", _audioOffset, 96, "ms, positive delays the sound");

        s.Group("Microphone", ("Reset microphone", ResetMic));
        s.Toggle(_micEnabled);
        s.Row("Microphone", _micDevice);
        s.Slider("Level", _micVolume, _micVolumeValue);
        s.Meter("Signal", _micMeter);
        s.Toggle(_micMuted);
        s.Toggle(_micMonitor);
        s.Toggle(_micDucking);
        s.Row("Delay", _micOffset, 96, "ms");
        s.Note("Delays your voice to line up with the game when the card adds latency.");
        s.Finish();
    }

    private void BuildReplay()
    {
        var s = NewSheet(Page.Replay);
        s.Toggle(_replayEnabled);
        s.Mono(_replayState, 2);

        s.Group("Length");
        s.Row("Keep", _replayBuffer, 96, "seconds");
        s.Row("Save", _replaySave, 96, "seconds of it");
        s.Note("While the preview runs, short pieces are written to a temporary folder and the oldest are dropped. " +
               "Saving joins the last ones without re-encoding, so it takes a moment.");

        s.Group("Where");
        s.Row("Folder", _replayFolder);
        s.Finish();
    }

    /// <summary>The library scrolls itself, so its page is exactly the viewport's height.</summary>
    private void BuildLibrary()
    {
        var content = new ContentPanel { BackColor = Theme.Shell, Width = PageWidth, Height = _pageHost.Height, Visible = false };
        _pages[Page.Library] = content;
        _pageHost.Controls.Add(content);
        _library.Dock = DockStyle.Fill;
        content.Controls.Add(_library);
    }

    private void BuildShortcuts()
    {
        var s = NewSheet(Page.Shortcuts);
        s.Note("Click a key, then press the new one. Esc cancels; Backspace or a right-click clears it.");
        s.Group("Capture", ("Reset shortcuts", () =>
        {
            _shortcuts.ResetToDefaults();
            LoadShortcuts();
            ShortcutsChanged?.Invoke(this, Tr("Shortcuts put back to their defaults"));
            SettingsTouched?.Invoke(this, EventArgs.Empty);
        }));
        foreach (var d in ShortcutCatalog.All.Where(d => d.IsRecording))
            s.KeyRow(d.Label, _keyFields[(d.Action, ShortcutSlot.Primary)], _keyFields[(d.Action, ShortcutSlot.Alternate)]);
        s.Toggle(_globalKeys);
        s.Note("Windows reserves these keys for Ripsaw Studio while it runs, so they also fire from inside a game.");

        s.Group("Window and sound");
        foreach (var d in ShortcutCatalog.All.Where(d => !d.IsRecording))
            s.KeyRow(d.Label, _keyFields[(d.Action, ShortcutSlot.Primary)], _keyFields[(d.Action, ShortcutSlot.Alternate)]);
        s.Finish();
    }

    private void BuildGeneral()
    {
        var s = NewSheet(Page.General);
        s.Group("Language");
        s.Row("Interface", _language);
        s.Note("Automatic follows the language of Windows, and uses English when that language is not in the list.");

        s.Group("Window");
        s.Toggle(_onTop);
        s.Toggle(_autoStart);
        s.Toggle(_showStats);

        s.Group("Troubleshooting");
        var diagnostics = new FlatButton { Glyph = UiIcon.Download, Text = Tr("Save a report") };
        var trace = new FlatButton { Glyph = UiIcon.Stopwatch, Text = Tr("Measure 10 s") };
        var encoders = new FlatButton { Glyph = UiIcon.Chip, Text = Tr("Test encoders") };
        diagnostics.Click += (_, _) => DiagnosticsClicked?.Invoke(this, EventArgs.Empty);
        trace.Click += (_, _) => TraceClicked?.Invoke(this, EventArgs.Empty);
        encoders.Click += (_, _) => EncoderTestClicked?.Invoke(this, EventArgs.Empty);
        s.Buttons(diagnostics, trace, encoders);
        s.Note("Measure records every frame for ten seconds and shows the stutters an average hides. " +
               "Test encoders writes a short clip with each encoder and reports which ones work.");
        s.Mono(_statusLine, 3);

        var resetAll = new FlatButton { ButtonRole = FlatButton.Role.Ghost, Glyph = UiIcon.Refresh, Text = Tr("Reset all settings") };
        resetAll.Click += (_, _) =>
        {
            if (MessageBox.Show(this, Tr("Put every setting back to its default?\n\nYour capture card, format and folders are kept."),
                    Tr("Reset all settings"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            _settings.ResetToDefaults();
            LoadFromSettings();
            ResetAllClicked?.Invoke(this, EventArgs.Empty);
            SettingsTouched?.Invoke(this, EventArgs.Empty);
        };
        s.Buttons(resetAll);
        s.Finish();
    }

    private void BuildAbout()
    {
        var s = NewSheet(Page.About);
        s.Group("Version");
        s.Text(new Label { Text = $"Ripsaw Studio {UpdateChecker.Format(UpdateChecker.Current)}" }, Theme.Heading, Theme.Text, minLines: 1);
        s.Text(_updateStatus, minLines: 1);

        s.Group("Ripsaw Studio");
        s.Text(new Label
        {
            Text = Tr("A lightweight capture app for any capture card. The feed reaches the screen the moment it " +
                      "arrives, and recording and instant replay are built in, with nothing sitting in between."),
        }, Theme.Body, Theme.TextDim);
        var website = new FlatButton { Glyph = UiIcon.Info, Text = Tr("Website") };
        website.Click += (_, _) => OpenLink("https://onlyalexrp.itch.io/ripsaw-studio");
        if (UpdateChecker.IsStoreInstall)
        {
            // The Store updates the app itself, and its rules forbid pointing at another download.
            s.Buttons(website);
        }
        else
        {
            var source = new FlatButton { Glyph = UiIcon.Download, Text = Tr("Releases on GitHub") };
            source.Click += (_, _) => UpdateChecker.OpenReleasesPage();
            // Last in the row: it is hidden while there is no update, and must not leave a gap.
            s.Buttons(website, source, _download);
        }
        s.Note(Tr("Settings and logs:") + " " + (Path.GetDirectoryName(AppSettings.SettingsPath) ?? ""));
        s.Finish();
    }

    private static void OpenLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser registered - nothing useful to do about it here */ }
    }

    // ---- page switching and layout ------------------------------------------------------------

    public void ShowPage(Page page)
    {
        _page = page;
        var category = Pages.CategoryOf(page);
        _lastPage[category] = page;
        _nav.Active = category;
        // A lone tab would only repeat the name under the icon.
        var tabs = Pages.PagesOf(category);
        _strip.SetTabs(tabs.Length > 1 ? tabs.Select(p => (p.Page, Tr(p.Label))).ToArray() : Array.Empty<(Page, string)>(), page);
        foreach (var (key, content) in _pages)
        {
            content.Visible = key == page;
            if (key == page) content.Top = 0;
        }
        if (page == Page.Library) _library.Reload();
        else _library.ClearSelection();
        UpdateScrollbar();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _nav.SetBounds(0, 0, Width, NavHeight);
        _strip.SetBounds(0, NavHeight, Width, TabsHeight);
        _pageHost.SetBounds(0, HeaderHeight, Math.Max(1, Width - ScrollbarGutter), Math.Max(1, Height - HeaderHeight));
        _scrollThumb.SetBounds(Width - ScrollbarGutter + 4, HeaderHeight + 8, 4, Math.Max(1, _pageHost.Height - 16));
        foreach (var content in _pages.Values)
        {
            content.Width = _pageHost.Width;
            content.Top = Math.Clamp(content.Top, Math.Min(0, _pageHost.Height - content.Height), 0);
        }
        if (_pages.TryGetValue(Page.Library, out var library)) library.Height = _pageHost.Height;
        UpdateRegion();
        UpdateScrollbar();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || !IsHandleCreated) return;
        using var path = FlatButton.Rounded(new Rectangle(0, 0, Width, Height), 10);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateRegion();
        if (_filterInstalled) return;
        Application.AddMessageFilter(_wheelFilter);
        _filterInstalled = true;
    }

    /// <summary>
    /// This window is a separate top-level form, so keystrokes typed while it has focus would
    /// otherwise never reach MainForm's shortcut table.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // The library's own keys first, so Esc clears a selection before it closes the panel.
        if (_page == Page.Library && !KeyCaptureButton.AnyCapturing && _library.HandleKey(keyData)) return true;
        if (Owner is MainForm main && main.DispatchShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;
    private const int MA_ACTIVATEANDEAT = 2;

    /// <summary>
    /// The first click on a freshly shown owned window is otherwise eaten as activation-only,
    /// so the first click on any control after opening the panel would do nothing.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_MOUSEACTIVATE && m.Result == (IntPtr)MA_ACTIVATEANDEAT)
            m.Result = (IntPtr)MA_ACTIVATE;
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

    // ---- scrolling ------------------------------------------------------------------------------

    private bool _filterInstalled;
    private WheelFilter? _wheelFilterBacking;
    private WheelFilter _wheelFilter => _wheelFilterBacking ??= new WheelFilter(this);

    private void ScrollBy(int delta)
    {
        if (_page == Page.Library) { _library.ScrollBy(delta); return; }
        if (!_pages.TryGetValue(_page, out var content)) return;
        int lowest = Math.Min(0, _pageHost.Height - content.Height);
        content.Top = Math.Clamp(content.Top + delta, lowest, 0);
        UpdateScrollbar();
    }

    private void ScrollToRatio(float ratio)
    {
        if (_page == Page.Library) { _library.ScrollToRatio(ratio); return; }
        if (!_pages.TryGetValue(_page, out var content)) return;
        int lowest = Math.Min(0, _pageHost.Height - content.Height);
        content.Top = (int)Math.Round(lowest * Math.Clamp(ratio, 0f, 1f));
        UpdateScrollbar();
    }

    private void UpdateScrollbar()
    {
        if (_page == Page.Library) { _scrollThumb.SetMetrics(_library.ScrollRatio, _library.ViewRatio); return; }
        if (!_pages.TryGetValue(_page, out var content) || _pageHost.Height <= 0)
        {
            _scrollThumb.SetMetrics(0f, 1f);
            return;
        }
        float sizeRatio = content.Height <= _pageHost.Height ? 1f : (float)_pageHost.Height / content.Height;
        int lowest = Math.Min(0, _pageHost.Height - content.Height);
        float topRatio = lowest == 0 ? 0f : content.Top / (float)lowest;
        _scrollThumb.SetMetrics(topRatio, sizeRatio);
    }

    /// <summary>Routes the wheel by cursor position rather than focus.</summary>
    private sealed class WheelFilter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private readonly ControlPanel _panel;

        public WheelFilter(ControlPanel panel) => _panel = panel;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || !_panel.Visible || _panel.IsDisposed) return false;
            if (!_panel._pageHost.RectangleToScreen(_panel._pageHost.ClientRectangle).Contains(Control.MousePosition)) return false;
            if (WheelTarget() is Slider or NumericField) return false;
            _panel.ScrollBy((short)((long)m.WParam >> 16) > 0 ? 80 : -80);
            return true;
        }

        private static Control? WheelTarget()
        {
            var point = Control.MousePosition;
            var control = Control.FromHandle(WindowFromPoint(new POINT { X = point.X, Y = point.Y }));
            return control?.Parent is NumericField field ? field : control;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);
    }

    // ---- resets, profiles, files ---------------------------------------------------------------

    private void ResetPicture()
    {
        var d = new AppSettings();
        _settings.Brightness = d.Brightness;
        _settings.Contrast = d.Contrast;
        _settings.Saturation = d.Saturation;
        _settings.ArtifactSmoothing = d.ArtifactSmoothing;
        _settings.Range = d.Range;
        _settings.Matrix = d.Matrix;
        _settings.Aspect = d.Aspect;
        _settings.Scaling = d.Scaling;
        LoadFromSettings();
        PictureChanged?.Invoke(this, EventArgs.Empty);
        DisplayChanged?.Invoke(this, EventArgs.Empty);
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    private void ResetSound()
    {
        var d = new AppSettings();
        _settings.Volume = d.Volume;
        _settings.AudioLatencyMs = d.AudioLatencyMs;
        _settings.AudioRestartMinutes = d.AudioRestartMinutes;
        _settings.AudioExclusive = d.AudioExclusive;
        _settings.AudioPassthrough = d.AudioPassthrough;
        LoadFromSettings();
        AudioSelectionChanged?.Invoke(this, EventArgs.Empty);
        VolumeChanged?.Invoke(this, EventArgs.Empty);
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    private void ResetRecordAudio()
    {
        var d = new AppSettings();
        _settings.RecordDesktopAudio = d.RecordDesktopAudio;
        _settings.AudioBitrateKbps = d.AudioBitrateKbps;
        _settings.AudioOffsetMs = d.AudioOffsetMs;
        LoadFromSettings();
        AudioSelectionChanged?.Invoke(this, EventArgs.Empty);
        RecordingSettingsChanged?.Invoke(this, EventArgs.Empty);
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    private void ResetMic()
    {
        var d = new AppSettings();
        _settings.MicEnabled = d.MicEnabled;
        _settings.MicVolume = d.MicVolume;
        _settings.MicMuted = d.MicMuted;
        _settings.MicMonitor = d.MicMonitor;
        _settings.MicOffsetMs = d.MicOffsetMs;
        _settings.MicDucking = d.MicDucking;
        LoadFromSettings();
        MicSettingsChanged?.Invoke(this, EventArgs.Empty);
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    private void ResetVideo()
    {
        var d = new AppSettings();
        _settings.VideoBitrateKbps = d.VideoBitrateKbps;
        _settings.Codec = d.Codec;
        _settings.EncoderId = d.EncoderId;
        _settings.Container = d.Container;
        _settings.RateControl = d.RateControl;
        _settings.Quality = d.Quality;
        _settings.KeyframeSeconds = d.KeyframeSeconds;
        LoadFromSettings();
        RecordingSettingsChanged?.Invoke(this, EventArgs.Empty);
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    private void NewProfile()
    {
        string? name = Prompt.Ask(this, Tr("New profile"), Tr("Name it after the console it is for:"),
                                  Tr("Console {0}", _settings.Profiles.Count + 1));
        if (name is null) return;
        _settings.AddProfile(name);
        LoadProfiles();
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    private void RenameProfile()
    {
        string? name = Prompt.Ask(this, Tr("Rename profile"), Tr("New name:"), _settings.ActiveProfile);
        if (name is null) return;
        _settings.RenameActiveProfile(name);
        LoadProfiles();
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteProfile()
    {
        if (_settings.Profiles.Count <= 1)
        {
            MessageBox.Show(this, Tr("There has to be at least one profile."), Tr("Delete profile"),
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, Tr("Delete the profile \"{0}\"?", _settings.ActiveProfile), Tr("Delete profile"),
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _settings.DeleteActiveProfile();
        LoadProfiles();
        LoadFromSettings();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
        SettingsTouched?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds the profile dropdown. Only for when the list itself changed - refilling a
    /// FlatCombo's items from inside its own SelectedIndexChanged is reentrant.
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
        _nav.Profile = Tr("Profile: {0}", _settings.ActiveProfile);
        if (ReferenceEquals(_profile.SelectedItem, active)) return;
        bool wasBinding = _binding;
        _binding = true;
        _profile.SelectedItem = active;
        _binding = wasBinding;
    }

    private void ReloadRecent()
    {
        if (_page == Page.Library) _library.Reload();
    }

    // ---- binding ----------------------------------------------------------------------------------

    private void LoadShortcuts()
    {
        foreach (var ((action, slot), field) in _keyFields)
            field.Keys = _shortcuts.Get(action, slot);
    }

    private void LoadFromSettings()
    {
        bool wasBinding = _binding;
        _binding = true;

        _aspect.SetSelectedSilently((int)_settings.Aspect);
        _scaling.SetSelectedSilently((int)_settings.Scaling);
        _range.SetSelectedSilently((int)_settings.Range);
        _matrix.SetSelectedSilently((int)_settings.Matrix);
        _codec.SetSelectedSilently((int)_settings.Codec);
        _container.SetSelectedSilently((int)_settings.Container);
        _rate.SetSelectedSilently((int)_settings.RateControl);

        SetSlider(_brightness, (int)Math.Round(_settings.Brightness * 100));
        SetSlider(_contrast, (int)Math.Round(_settings.Contrast * 100));
        SetSlider(_saturation, (int)Math.Round(_settings.Saturation * 100));
        SetSlider(_smoothing, (int)Math.Round(_settings.ArtifactSmoothing * 100));
        SetSlider(_volume, (int)Math.Round(_settings.Volume * 100));
        SetSlider(_micVolume, (int)Math.Round(_settings.MicVolume * 100));
        SetSlider(_quality, _settings.Quality);

        _audioBuffer.SetValueSilently(_settings.AudioLatencyMs);
        _audioRestart.SetValueSilently(_settings.AudioRestartMinutes);
        _videoBitrate.SetValueSilently(_settings.VideoBitrateKbps);
        _audioBitrate.SetValueSilently(_settings.AudioBitrateKbps);
        _audioOffset.SetValueSilently(_settings.AudioOffsetMs);
        _keyframe.SetValueSilently(_settings.KeyframeSeconds);
        _micOffset.SetValueSilently(_settings.MicOffsetMs);
        _replayBuffer.SetValueSilently(_settings.ReplayBufferSeconds);
        _replaySave.Maximum = _settings.ReplayBufferSeconds;
        _replaySave.SetValueSilently(Math.Min(_settings.ReplaySaveSeconds, _settings.ReplayBufferSeconds));
        _recordsFolder.Path = _settings.RecordsFolder;
        _replayFolder.Path = _settings.ReplayFolder;
        _screenshotsFolder.Path = _settings.ScreenshotsFolder;

        _vsync.Checked = _settings.VSync;
        _passthrough.Checked = _settings.AudioPassthrough;
        _desktopAudio.Checked = _settings.RecordDesktopAudio;
        _exclusive.Checked = _settings.AudioExclusive;
        _onTop.Checked = _settings.AlwaysOnTop;
        _autoStart.Checked = _settings.AutoStart;
        _showStats.Checked = _settings.ShowStatusOverlay;
        _micEnabled.Checked = _settings.MicEnabled;
        _micMuted.Checked = _settings.MicMuted;
        _micMonitor.Checked = _settings.MicMonitor;
        _micDucking.Checked = _settings.MicDucking;
        _replayEnabled.Checked = _settings.ReplayEnabled;
        _globalKeys.Checked = _settings.GlobalShortcuts;

        SelectById(_micDevice, _settings.MicDeviceId);
        SelectActiveProfile();
        FillEncoders();
        UpdateEncoderNotes();

        _binding = wasBinding;
    }

    private void SetSlider(Slider slider, int value)
    {
        slider.SetValueSilently(value);
        if (_sliderFormats.TryGetValue(slider, out var f)) f.Value.Text = f.Format(slider.Value);
    }

    // ---- encoders --------------------------------------------------------------------------------

    private sealed record EncoderChoice(string Id, string Label, EncoderInfo? Info)
    {
        public override string ToString() => Label;
    }

    /// <summary>Called once the engine has listed this machine's encoders.</summary>
    public void SetEncoders(List<EncoderInfo> encoders, string? previewAdapter)
    {
        _encoders = encoders;
        _previewAdapter = previewAdapter;
        bool wasBinding = _binding;
        _binding = true;
        bool hevc = encoders.Any(e => e.Codec == VideoCodec.Hevc);
        _codec.SetEnabled(1, hevc);
        FillEncoders();
        UpdateEncoderNotes();
        _binding = wasBinding;
    }

    private void FillEncoders()
    {
        bool wasBinding = _binding;
        _binding = true;
        _encoder.Items.Clear();
        var codec = _settings.Codec;
        var auto = EncoderCatalog.Resolve(_encoders, codec, "auto", _previewAdapter);
        _encoder.Items.Add(new EncoderChoice("auto", auto is null ? Tr("Automatic") : Tr("Automatic ({0})", auto.Label), auto));
        foreach (var e in _encoders.Where(e => e.Codec == codec))
            _encoder.Items.Add(new EncoderChoice(e.Id, e.Label, e));
        var chosen = _encoder.Items.OfType<EncoderChoice>().FirstOrDefault(c => c.Id == _settings.EncoderId);
        _encoder.SelectedItem = chosen ?? _encoder.Items[0];
        _binding = wasBinding;
    }

    private void UpdateEncoderNotes()
    {
        var info = (_encoder.SelectedItem as EncoderChoice)?.Info
                   ?? EncoderCatalog.Resolve(_encoders, _settings.Codec, _settings.EncoderId, _previewAdapter);
        _encoderNote.Text = info switch
        {
            null when _encoders.Count == 0 => Tr("Looking for encoders..."),
            null => Tr("No encoder for this codec on this PC."),
            { Hardware: false } => Tr("Runs on the processor. Fine at 1080p30; at 1080p60 it can drop frames."),
            _ when EncoderCatalog.Same(info.AdapterName, _previewAdapter) =>
                Tr("Runs on the same graphics card as the preview, straight from the captured frames."),
            _ => Tr("Frames are copied over to the {0} first, which costs some processor time.", info.AdapterName),
        };

        bool quality = _settings.RateControl == RateControl.Quality;
        _quality.Enabled = quality;
        _qualityValue.ForeColor = quality ? Theme.Text : Theme.TextFaint;
        _videoBitrate.Enabled = !quality;

        bool hevc = _settings.Codec == VideoCodec.Hevc;
        _container.SetEnabled(0, !hevc);
        if (hevc && _container.SelectedIndex == 0) _container.SetSelectedSilently(1);
        if (!hevc && _container.SelectedIndex != (int)_settings.Container) _container.SetSelectedSilently((int)_settings.Container);
        _containerNote.Text = hevc
            ? Tr("HEVC is always written as plain MP4: Windows cannot fragment it.")
            : _settings.Container == ContainerFormat.FragmentedMp4
                ? Tr("Stays playable if the app or the PC crashes mid-recording. Some older editors can't open it.")
                : Tr("Opens everywhere, but a crash mid-recording leaves a file that won't play.");
    }

    // ---- calls from the shell ----------------------------------------------------------------------

    public void SetAdapters(IEnumerable<string> adapters, string? selected)
    {
        _binding = true;
        _adapter.Items.Clear();
        _adapter.Items.Add(Tr("Automatic"));
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

    private static void SelectById(FlatCombo combo, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is AudioDeviceInfo info && info.Id == id) { combo.SelectedIndex = i; return; }
    }

    public void SetVolumeSilently(float volume) => SetSlider(_volume, (int)Math.Round(volume * 100));

    public void SetStreaming(bool streaming)
    {
        _streaming = streaming;
        _startStop.Text = streaming ? Tr("Stop preview") : Tr("Start preview");
        _startStop.Glyph = streaming ? UiIcon.Stop : UiIcon.Play;
        _startStop.ButtonRole = streaming ? FlatButton.Role.Normal : FlatButton.Role.Accent;
    }

    public void SetRecording(bool recording, TimeSpan elapsed = default)
    {
        _nav.SetRecording(recording, elapsed);
        _strip.SetRecording(recording);
    }

    public void SetSignal(SignalStrip.State state, string resolution, string fps, string lag, string detail) =>
        _strip.SetSignal(state, resolution, fps, lag, detail);

    public void SetStatus(string text) => _statusLine.Text = text;
    public void SetRecordState(string text) => _recordState.Text = text;
    public void SetLevel(float peak) => _meter.SetLevel(peak);
    public void SetMicLevel(float peak) => _micMeter.SetLevel(peak);
    public void ReloadFromSettings() => LoadFromSettings();
    public void RefreshRecent() => ReloadRecent();
    public void SetReplayState(string text) => _replayState.Text = text;

    public void SetReplayState(bool armed, bool saving)
    {
        _replayArmed = armed;
        _replaySaving = saving;
        _nav.SetReplayAvailable(armed, saving);
    }

    public void SetLiveStatus(bool live, string text)
    {
        _liveStatus.Text = text;
        _liveStatus.ForeColor = live ? Theme.Good : Theme.TextFaint;
    }

    public void ShowUpdateStatus(UpdateCheckResult result)
    {
        _lastUpdate = result;
        if (!result.Checked)
        {
            _updateStatus.Text = Tr("Could not check for updates.");
            _updateStatus.ForeColor = Theme.TextFaint;
            _download.Visible = false;
        }
        else if (result.UpdateAvailable)
        {
            _updateStatus.Text = Tr("Version {0} is available.", UpdateChecker.Format(result.Latest));
            _updateStatus.ForeColor = Theme.Accent;
            _download.Visible = true;
        }
        else
        {
            _updateStatus.Text = Tr("This is the latest version.");
            _updateStatus.ForeColor = Theme.TextDim;
            _download.Visible = false;
        }
    }

    // ---- the row layout ---------------------------------------------------------------------------

    /// <summary>
    /// Lays one page out top to bottom: a label column on the left, the control column to its
    /// right, every row on the same grid. Groups are separated by a hairline and a little air
    /// rather than boxed into cards.
    /// </summary>
    private sealed class Sheet
    {
        private readonly ContentPanel _page;
        private readonly int _width;
        /// <summary>Left margin of the column - wider than the minimum when the column is centred.</summary>
        private readonly int _padX;
        private int _y = PadTop;
        private bool _firstGroup = true;

        public Sheet(ContentPanel page, int width, int padX)
        {
            _page = page;
            _width = width;
            _padX = padX;
        }

        private int ControlLeft => _padX + LabelWidth;
        private int ControlWidth => Math.Min(440, _width - ControlLeft - _padX);
        /// <summary>Before the first group there are no row labels to line up with, so free-standing items start at the margin.</summary>
        private int FreeLeft => _firstGroup ? _padX : ControlLeft;
        public int ContentWidth => Math.Min(_width - _padX * 2, ColumnMaxWidth);

        public void Group(string title, (string Text, Action Run)? action = null)
        {
            if (!_firstGroup)
            {
                _y += 14;
                var rule = new Panel { BackColor = Theme.Line };
                rule.SetBounds(_padX, _y, ContentWidth, 1);
                _page.Controls.Add(rule);
                _y += 20;
            }
            else _y += 4;
            _firstGroup = false;

            var label = MakeLabel(Tr(title), Theme.Heading, Theme.Text);
            label.SetBounds(_padX, _y, ContentWidth - 160, 22);
            _page.Controls.Add(label);
            if (action is { } a)
            {
                var link = new FlatButton { ButtonRole = FlatButton.Role.Ghost, Text = Tr(a.Text), Font = Theme.Small };
                int w = TextRenderer.MeasureText(link.Text, Theme.Small).Width + 22;
                link.SetBounds(_padX + ContentWidth - w, _y - 4, w, 28);
                link.Click += (_, _) => a.Run();
                _page.Controls.Add(link);
            }
            _y += 34;
        }

        public void Row(string label, Control control, int? width = null, string? suffix = null)
        {
            AddCaption(label, _y, RowHeight);
            int w = width ?? ControlWidth;
            control.SetBounds(ControlLeft, _y, w, RowHeight);
            _page.Controls.Add(control);
            if (suffix is not null)
            {
                var unit = MakeLabel(Tr(suffix), Theme.Body, Theme.TextDim);
                unit.SetBounds(ControlLeft + w + 10, _y, Math.Max(10, ControlWidth - w - 10 + 160), RowHeight);
                unit.TextAlign = ContentAlignment.MiddleLeft;
                _page.Controls.Add(unit);
            }
            _y += RowHeight + RowGap;
        }

        public void Slider(string label, Slider slider, Label value)
        {
            AddCaption(label, _y, 24);
            const int ValueWidth = 48;
            slider.SetBounds(ControlLeft, _y, ControlWidth - ValueWidth - 12, 24);
            slider.BackColor = Theme.Shell;
            _page.Controls.Add(slider);
            value.Font = Theme.ReadoutSmall;
            value.ForeColor = Theme.Text;
            value.BackColor = Theme.Shell;
            value.AutoSize = false;
            value.TextAlign = ContentAlignment.MiddleRight;
            value.SetBounds(ControlLeft + ControlWidth - ValueWidth, _y, ValueWidth, 24);
            _page.Controls.Add(value);
            _y += 24 + RowGap + 2;
        }

        public void Toggle(FlatCheck check)
        {
            check.BackColor = Theme.Shell;
            check.SetBounds(FreeLeft, _y, ControlWidth + 120, 28);
            _page.Controls.Add(check);
            _y += 28 + RowGap - 2;
        }

        public void Meter(string label, LevelMeter meter)
        {
            AddCaption(label, _y, 20);
            meter.SetBounds(ControlLeft, _y + 8, ControlWidth, 5);
            _page.Controls.Add(meter);
            _y += 20 + RowGap;
        }

        /// <summary>Small print under a row, in the control column so it reads as belonging to it.</summary>
        public void Note(string text)
        {
            var label = MakeLabel(Tr(text), Theme.Hint, Theme.TextFaint);
            Place(label, Theme.Hint);
        }

        /// <summary>Like <see cref="Note"/>, for a label whose text is changed later.</summary>
        public void Text(Label label, Font? font = null, Color? colour = null, int minLines = 2)
        {
            label.Font = font ?? Theme.Hint;
            label.ForeColor = colour ?? Theme.TextDim;
            label.BackColor = Theme.Shell;
            label.AutoSize = false;
            label.UseMnemonic = false;
            Place(label, label.Font, minLines);
        }

        private void Place(Label label, Font font, int minLines = 1)
        {
            int width = ControlWidth + 80;
            int lineHeight = TextRenderer.MeasureText("Ag", font).Height;
            var size = TextRenderer.MeasureText(label.Text.Length == 0 ? "Ag" : label.Text, font,
                new Size(width, 0), TextFormatFlags.WordBreak);
            int height = Math.Max(size.Height, lineHeight * minLines);
            label.SetBounds(FreeLeft, _y - 4, width, height + 2);
            _page.Controls.Add(label);
            _y += height + RowGap - 2;
        }

        /// <summary>A live status line, a few lines tall, in the control column.</summary>
        public void Mono(Label label, int lines = 1)
        {
            label.Font = Theme.Body;
            if (label.ForeColor == Control.DefaultForeColor) label.ForeColor = Theme.TextDim;
            label.BackColor = Theme.Shell;
            label.AutoSize = false;
            label.UseMnemonic = false;
            int lineHeight = TextRenderer.MeasureText("Ag", Theme.Body).Height;
            label.SetBounds(FreeLeft, _y, ControlWidth + 120, lineHeight * lines + 4);
            _page.Controls.Add(label);
            _y += lineHeight * lines + RowGap;
        }

        public void Buttons(params FlatButton[] buttons)
        {
            int x = FreeLeft;
            foreach (var b in buttons)
            {
                int w = TextRenderer.MeasureText(b.Text, b.Font).Width + (b.Glyph == UiIcon.None ? 32 : 56);
                b.SetBounds(x, _y, Math.Max(w, 84), 34);
                _page.Controls.Add(b);
                x += b.Width + 8;
            }
            _y += 34 + RowGap + 4;
        }

        public void KeyRow(string label, Control primary, Control alternate)
        {
            AddCaption(label, _y, RowHeight, wide: true);
            const int KeyWidth = 136;
            int left = _padX + 280;
            primary.SetBounds(left, _y, KeyWidth, RowHeight);
            alternate.SetBounds(left + KeyWidth + 8, _y, KeyWidth, RowHeight);
            _page.Controls.Add(primary);
            _page.Controls.Add(alternate);
            _y += RowHeight + 6;
        }

        public void Finish() => _page.Height = _y + 30;

        private void AddCaption(string text, int y, int height, bool wide = false)
        {
            var caption = MakeLabel(Tr(text), Theme.Body, Theme.TextDim);
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.SetBounds(_padX, y, wide ? 270 : LabelWidth - 12, height);
            _page.Controls.Add(caption);
        }

        private static Label MakeLabel(string text, Font font, Color colour) => new()
        {
            Text = text,
            Font = font,
            ForeColor = colour,
            BackColor = Theme.Shell,
            AutoSize = false,
            UseMnemonic = false,
        };
    }
}

/// <summary>A folder, shown as its path; clicking it opens the folder picker.</summary>
internal sealed class PathField : Control
{
    private string _path = "";
    private bool _hovered;

    public event EventHandler<string>? PathChanged;

    public PathField()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public string Path
    {
        get => _path;
        set { _path = value ?? ""; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Shell);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = FlatButton.Rounded(rect, 5))
        {
            using var fill = new SolidBrush(_hovered ? Theme.Hover : Theme.Field);
            g.FillPath(fill, path);
            if (Focused && ShowFocusCues)
            {
                using var ring = new Pen(Theme.Accent);
                g.DrawPath(ring, path);
            }
        }
        TextRenderer.DrawText(g, _path, Font, new Rectangle(10, 0, Width - 44, Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
        Icons.Draw(g, UiIcon.Folder, new RectangleF(Width - 28, (Height - 15) / 2f, 15, 15), Theme.TextDim, 1.4f);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Browse(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { Browse(); e.Handled = true; }
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _path, UseDescriptionForTitle = true, Description = Tr("Choose a folder") };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        Path = dialog.SelectedPath;
        PathChanged?.Invoke(this, _path);
    }
}
