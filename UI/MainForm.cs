using NAudio.CoreAudioApi;
using RipsawStudio.Audio;
using RipsawStudio.Capture;
using RipsawStudio.Render;

namespace RipsawStudio.UI;

/// <summary>
/// Video-first shell: the picture owns the whole window and every control lives in a
/// panel that floats over it. Nothing but the picture is on screen while you play.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ShortcutMap _shortcuts;
    private readonly CaptureEngine _engine;

    private readonly VideoSurface _video = new();
    private readonly SettingsPanel _panel;
    private readonly VolumeOverlay _volumeOverlay = new();
    private readonly ToastOverlay _toast = new();
    private readonly StatsOverlay _stats = new();
    private readonly RecordOverlay _recordLight = new();

    private readonly System.Windows.Forms.Timer _cursorTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _relayout = new() { Interval = 160 };
    private readonly System.Windows.Forms.Timer _audioDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _slide = new() { Interval = 15 };
    /// <summary>How far below its resting place the panel sits when closed.</summary>
    private const int SlideDistance = 28;
    private Rectangle _panelRest;
    private double _slideOffset;
    private double _slideTarget;
    private int _builtWidth;

    private DateTime _lastMouseMove = DateTime.UtcNow;
    private bool _cursorHidden;
    private bool _fullscreen;
    private Rectangle _restoreBounds;
    private FormBorderStyle _restoreBorder;
    private FormWindowState _restoreState;

    private bool _loading = true;
    private bool _shown;
    private List<VideoFormat> _formats = new();
    /// <summary>Guards against a second save being started while one is still joining segments.</summary>
    private bool _replaySaving;
    /// <summary>Format the preview is actually running, so a dropdown change can restart it.</summary>
    private string? _activeFormatKey;

    private const int WS_CLIPCHILDREN = 0x02000000;

    /// <summary>Keeps the video window's presents inside its own rectangle.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_CLIPCHILDREN;
            return cp;
        }
    }

    public MainForm()
    {
        _settings = AppSettings.Load();
        _shortcuts = new ShortcutMap(_settings);
        _engine = new CaptureEngine(_settings.Adapter);
        _panel = new SettingsPanel(_settings, _shortcuts);

        Text = "Ripsaw Studio";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        MinimumSize = new Size(760, 460);
        ClientSize = new Size(_settings.WindowWidth, _settings.WindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        TopMost = _settings.AlwaysOnTop;
        ApplyWindowIcon();

        _video.Dock = DockStyle.Fill;
        _video.PlaceholderText = "Press Esc for settings";
        Controls.Add(_video);
        Controls.Add(_panel);
        Controls.Add(_volumeOverlay);
        Controls.Add(_toast);
        Controls.Add(_stats);
        Controls.Add(_recordLight);

        // Start where the panel belongs, so a restored-open panel does not slide on launch.
        _slideOffset = _slideTarget = _settings.PanelOpen ? 0 : SlideDistance;
        _panel.Visible = _settings.PanelOpen;

        WirePanel();
        WireEngine();
        ApplyAllSettings();

        _cursorTimer.Tick += CursorTick;
        _cursorTimer.Start();
        // Laying the cards out again is not free, so it waits until a drag-resize settles.
        _relayout.Tick += (_, _) => { _relayout.Stop(); RebuildPanelIfNeeded(); };
        _audioDebounce.Tick += (_, _) => { _audioDebounce.Stop(); RestartAudio(); };
        _slide.Tick += SlideTick;

        LayoutOverlays();

        _video.MouseMove += (_, _) => NoteMouseActivity();
        _video.MouseDoubleClick += (_, _) => ToggleFullscreen();
        _video.MouseDown += (_, _) => { if (_panel.Visible) SetPanelOpen(false); };
    }

    /// <summary>
    /// Uses the embedded appicon.ico for the title bar and Alt+Tab. Loading the .ico rather
    /// than extracting from the exe keeps every size in it, so it stays sharp on high-DPI
    /// taskbars. Silently does nothing when no icon has been added to the project.
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("RipsawStudio.appicon.ico");
            if (stream is not null) Icon = new System.Drawing.Icon(stream);
        }
        catch { /* a bad icon file must not stop the app opening */ }
    }

    // ---- wiring -------------------------------------------------------------------------

    private void WirePanel()
    {
        _panel.DeviceSelectionChanged += async (_, _) => await OnDeviceSelectionChanged();
        // Rescan used to only re-read the video devices, which made a microphone or a
        // capture endpoint plugged in after launch impossible to pick without a restart.
        _panel.RescanClicked += async (_, _) =>
        {
            await RefreshDevices();
            RefreshAudioDevices();
            RestartAudio();
            _toast.Show("Devices rescanned", false);
        };
        _panel.StartStopClicked += async (_, _) => await ToggleStreaming();
        _panel.RecordClicked += async (_, _) => await ToggleRecording();
        _panel.SnapshotClicked += async (_, _) => await TakeSnapshot();
        // Nudging the buffer spinner would otherwise restart WASAPI on every click.
        _panel.AudioSelectionChanged += (_, _) => { _audioDebounce.Stop(); _audioDebounce.Start(); };
        _panel.PictureChanged += (_, _) => _engine.SetPicture(_settings.ToPicture());
        _panel.RecordingSettingsChanged += (_, _) => ApplyRecordSettings();
        _panel.DiagnosticsClicked += async (_, _) => await SaveDiagnostics();
        _panel.AdapterChanged += async (_, _) => await SwitchAdapter();
        _panel.TraceClicked += async (_, _) => await RunTrace();
        _panel.ResetAllClicked += (_, _) =>
        {
            ApplyAllSettings();
            RestartAudio();
            _toast.Show("Settings reset to defaults", false);
        };
        _panel.DisplayChanged += (_, _) => ApplyDisplaySettings();
        _panel.VolumeChanged += (_, _) =>
        {
            _engine.Audio.Volume = _settings.Volume;
            _volumeOverlay.Show(_settings.Volume, _engine.Audio.Muted);
        };
        // Device, delay and monitoring need the graph rebuilt; gain and mute do not.
        _panel.MicSettingsChanged += (_, _) => { _audioDebounce.Stop(); _audioDebounce.Start(); };
        _panel.MicLevelChanged += (_, _) =>
        {
            _engine.Audio.MicVolume = _settings.MicVolume;
            _engine.Audio.MicMuted = _settings.MicMuted;
        };
        _panel.ReplaySettingsChanged += (_, _) => ApplyReplaySettings();
        _panel.SaveReplayClicked += async (_, _) => await SaveReplay();
        _panel.ProfileChanged += async (_, _) => await ApplyProfile();
        _panel.ShortcutsChanged += (_, note) =>
        {
            _shortcuts.SaveInto(_settings);
            if (note.Length > 0) _toast.Show(note, false);
        };
    }

    private void WireEngine()
    {
        _engine.Status += (_, message) => Ui(() => { _toast.Show(message, false); _panel.SetStatus(message); });
        _engine.Failed += (_, message) => Ui(() => { _toast.Show(message, true); _panel.SetStatus(message); });
        _engine.Stats += (_, stats) => Ui(() => UpdateStats(stats));
        _engine.SourceFormatChanged += (_, _) => Ui(() => _video.ShowPlaceholder = false);
    }

    private void ApplyAllSettings()
    {
        ApplyRecordSettings();
        ApplyDisplaySettings();
        ApplyReplaySettings();
        _engine.SetPicture(_settings.ToPicture());
        _engine.Audio.Volume = _settings.Volume;
        _engine.Audio.Muted = _settings.Muted;
        _engine.Audio.MicVolume = _settings.MicVolume;
        _engine.Audio.MicMuted = _settings.MicMuted;
        _engine.Audio.RestartIntervalMinutes = _settings.AudioRestartMinutes;
    }

    private void ApplyReplaySettings()
    {
        _engine.ReplayBufferSeconds = _settings.ReplayBufferSeconds;
        _engine.ReplayEnabled = _settings.ReplayEnabled;
    }

    private void ApplyRecordSettings()
    {
        _engine.RecordSettings.OutputFolder = _settings.OutputFolder;
        _engine.RecordSettings.VideoBitrateKbps = _settings.VideoBitrateKbps;
        _engine.RecordSettings.AudioBitrateKbps = _settings.AudioBitrateKbps;
        _engine.RecordSettings.AudioOffsetMs = _settings.AudioOffsetMs;
        _engine.RecordSettings.UseHardwareEncoder = _settings.HardwareEncoder;
    }

    private void ApplyDisplaySettings()
    {
        _engine.VSync = _settings.VSync;
        _engine.Scaling = _settings.Scaling;
        _engine.Aspect = _settings.Aspect;
        TopMost = _settings.AlwaysOnTop;
        _stats.SetVisible(_settings.ShowStatusOverlay);
        LayoutOverlays();
    }

    // ---- lifecycle ------------------------------------------------------------------------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_shown) return;
        _shown = true;
        LayoutOverlays();
        _engine.SetWindow(_video.Handle, _video.ClientSize.Width, _video.ClientSize.Height);
        // WinForms recreates handles for some property changes. Subscribing after the first
        // attach means every later event is a recreation, and the swap chain must follow it
        // rather than keep presenting into a destroyed window.
        _video.HandleCreated += (_, _) =>
            _engine.SetWindow(_video.Handle, _video.ClientSize.Width, _video.ClientSize.Height);

        try { _panel.SetAdapters(await _engine.ListAdaptersAsync(), _settings.Adapter); }
        catch (Exception ex) { _toast.Show("Could not list graphics adapters: " + ex.Message, true); }

        await RefreshDevices();
        RefreshAudioDevices();
        _loading = false;

        if (_settings.AutoStart && _panel.SelectedDevice is { SymbolicLink.Length: > 0 } && _panel.SelectedFormat is not null)
            await ToggleStreaming();
        else
            RestartAudio();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;
        LayoutOverlays();
        _engine.Resize(_video.ClientSize.Width, _video.ClientSize.Height);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;

        if (!_fullscreen)
        {
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
        }
        _settings.PanelOpen = _panel.Visible;
        _settings.Muted = _engine.Audio.Muted;
        _settings.MicMuted = _engine.Audio.MicMuted;
        _shortcuts.SaveInto(_settings);
        _settings.Save();
        _engine.Dispose();

        foreach (var timer in new[] { _cursorTimer, _relayout, _audioDebounce, _slide })
        {
            timer.Stop();
            timer.Dispose();
        }
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { /* handle went away between the check and the call */ }
    }

    // ---- panel + overlay placement ----------------------------------------------------------

    /// <summary>
    /// The panel floats over the picture, centred, sized to the window. It is rebuilt only
    /// when its width actually changes, because relaying out the cards is not free.
    /// </summary>
    private void LayoutOverlays()
    {
        int w = ClientSize.Width, h = ClientSize.Height;

        int panelWidth = Math.Clamp(w - 72, 720, 1180);
        int panelHeight = Math.Clamp(h - 72, 420, 940);
        _panelRest = new Rectangle((w - panelWidth) / 2, (h - panelHeight) / 2, panelWidth, panelHeight);
        PositionPanel();
        if (_builtWidth == 0) RebuildPanelIfNeeded();
        else if (_builtWidth != panelWidth) { _relayout.Stop(); _relayout.Start(); }
        _panel.BringToFront();

        _volumeOverlay.Location = new Point((w - _volumeOverlay.Width) / 2, h - _volumeOverlay.Height - 28);
        _toast.SetBounds(16, h - _toast.Height - 16, Math.Clamp(w - 32, 160, 560), _toast.Height);
        _stats.Location = new Point(w - _stats.Width - 16, 16);
        _recordLight.Location = new Point(w - _recordLight.Width - 16, _settings.ShowStatusOverlay ? 16 + _stats.Height + 8 : 16);

        foreach (Control overlay in new Control[] { _volumeOverlay, _toast, _stats, _recordLight })
            if (overlay.Visible) overlay.BringToFront();
    }

    private void RebuildPanelIfNeeded()
    {
        if (_panel.Width == _builtWidth) return;
        _builtWidth = _panel.Width;
        _panel.Build();
    }

    private void PositionPanel() =>
        _panel.SetBounds(_panelRest.X, _panelRest.Y + (int)Math.Round(_slideOffset),
                         _panelRest.Width, _panelRest.Height);

    /// <summary>
    /// Slides the panel in from below and back out. A cross-fade would read better, but a
    /// WinForms control has no opacity and a layered child window does not composite over
    /// the swap chain - so the animation is movement, which always works.
    /// Reversing mid-slide continues from wherever it had got to rather than snapping.
    /// </summary>
    private void SetPanelOpen(bool open)
    {
        _slideTarget = open ? 0 : SlideDistance;

        if (open)
        {
            if (!_panel.Visible) _slideOffset = SlideDistance;
            PositionPanel();
            _panel.Visible = true;
            _panel.BringToFront();
            ShowCursorIfHidden();
        }
        _slide.Start();
    }

    private void SlideTick(object? sender, EventArgs e)
    {
        // Ease out: most of the travel happens early, so it settles rather than stopping dead.
        _slideOffset += (_slideTarget - _slideOffset) * 0.34;
        if (Math.Abs(_slideTarget - _slideOffset) < 0.6) _slideOffset = _slideTarget;
        PositionPanel();

        if (Math.Abs(_slideTarget - _slideOffset) > 0.001) return;

        _slide.Stop();
        if (_slideTarget == 0) return;   // finished opening

        _panel.Visible = false;
        ActiveControl = null;            // a hidden panel must not keep swallowing hotkeys
    }

    // ---- devices --------------------------------------------------------------------------

    private async Task RefreshDevices()
    {
        List<VideoDeviceInfo> devices;
        try { devices = await _engine.EnumerateDevicesAsync(); }
        catch (Exception ex) { _toast.Show("Could not list capture devices: " + ex.Message, true); return; }

        VideoDeviceInfo? preferred =
            devices.FirstOrDefault(d => d.SymbolicLink == _settings.VideoDeviceLink) ??
            devices.FirstOrDefault(d => d.Name == _settings.VideoDeviceName) ??
            devices.FirstOrDefault(d => d.Name.Contains("Ripsaw", StringComparison.OrdinalIgnoreCase)) ??
            devices.FirstOrDefault();

        _panel.SetDevices(devices, preferred);
        if (devices.Count == 0)
        {
            _toast.Show("No capture device found. Check the cable, and that nothing else has the card open.", true);
            _panel.SetFormats(Array.Empty<VideoFormat>(), -1);
            return;
        }
        await OnDeviceSelectionChanged();
    }

    private async Task OnDeviceSelectionChanged()
    {
        var device = _panel.SelectedDevice;
        if (device is null || device.SymbolicLink.Length == 0)
        {
            if (_engine.IsStreaming) await StopStreaming();
            _panel.SetFormats(Array.Empty<VideoFormat>(), -1);
            return;
        }

        // Only re-read the format list when the card itself changed.
        if (device.SymbolicLink != _settings.VideoDeviceLink || _formats.Count == 0)
        {
            _settings.VideoDeviceName = device.Name;
            _settings.VideoDeviceLink = device.SymbolicLink;
            _panel.SetStatus("Reading the formats " + device.Name + " supports...");
            try
            {
                _formats = await _engine.QueryFormatsAsync(device);
            }
            catch (Exception ex)
            {
                _toast.Show("Could not open " + device.Name + ": " + ex.Message, true);
                return;
            }

            SelectFormatFromSettings();
            _panel.SetStatus($"{device.Name} - {_formats.Count} formats");
            if (!_loading && AudioAutoPick(device.Name)) RestartAudio();
        }

        if (_panel.SelectedFormat is not { } format) return;
        _settings.FormatKey = format.Key;

        // Picking a different format mid-preview used to be silently ignored until the next
        // manual stop and start, which looked like the dropdown doing nothing.
        if (_engine.IsStreaming && _activeFormatKey != format.Key)
        {
            await StopStreaming();
            await ToggleStreaming();
        }
    }

    /// <summary>
    /// Picks the format the settings ask for out of the list already read from the card,
    /// falling back to the best guess when that format is not on offer. Separate from
    /// reading the list, because a profile switch on the same card needs the second without
    /// the first - reopening the device to re-read formats would stop the preview.
    /// </summary>
    private void SelectFormatFromSettings()
    {
        if (_formats.Count == 0) return;
        int index = _formats.FindIndex(f => f.Key == _settings.FormatKey);
        if (index < 0) index = IndexOfPreferredFormat(_formats);
        _panel.SetFormats(_formats, index);
    }

    /// <summary>Prefers the highest frame rate at up to 1080p - what a game capture is normally for.</summary>
    private static int IndexOfPreferredFormat(List<VideoFormat> formats)
    {
        if (formats.Count == 0) return -1;
        int best = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < formats.Count; i++)
        {
            var f = formats[i];
            double score = Math.Min(f.Width, 1920) * (double)Math.Min(f.Height, 1080) / 1000.0 + f.Fps * 20;
            if (f.Width > 1920 || f.Height > 1080) score -= 500;   // rarely what you want over USB
            // Uncompressed arrives ready to display; MJPEG has to be decoded first, which costs.
            score += f.SubtypeName switch
            {
                "NV12" => 400,
                "YUY2" or "UYVY" => 380,
                "P010" => 300,
                "MJPG" => 0,
                _ => 200,
            };
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private async Task ToggleStreaming()
    {
        if (_engine.IsStreaming) { await StopStreaming(); return; }

        var device = _panel.SelectedDevice;
        var format = _panel.SelectedFormat;
        if (device is null || device.SymbolicLink.Length == 0 || format is null)
        {
            _toast.Show("Pick a capture card and a format first.", true);
            return;
        }

        _settings.FormatKey = format.Key;
        try
        {
            await _engine.StartAsync(device, format);
            _activeFormatKey = format.Key;
            _video.ShowPlaceholder = false;
            _panel.SetStreaming(true);
            RestartAudio();
        }
        catch (Exception ex)
        {
            _toast.Show(ex.Message, true);
        }
    }

    private async Task StopStreaming()
    {
        _activeFormatKey = null;
        await _engine.StopAsync();
        _engine.Audio.Stop();
        _panel.SetStreaming(false);
        _panel.SetRecording(false);
        _recordLight.Stop();
        _video.ShowPlaceholder = true;
        _video.Invalidate();
        _panel.SetStatus("Stopped");
    }

    // ---- audio -----------------------------------------------------------------------------

    private void RefreshAudioDevices()
    {
        List<AudioDeviceInfo> inputs;
        try
        {
            inputs = AudioMonitor.Enumerate(DataFlow.Capture);
            _panel.SetAudioDevices(inputs, AudioMonitor.Enumerate(DataFlow.Render),
                                    _settings.AudioInputId, _settings.AudioOutputId);
            // The microphone comes off the same list of capture endpoints as the card does.
            _panel.SetMicDevices(inputs, _settings.MicDeviceId);
        }
        catch (Exception ex)
        {
            _toast.Show("Could not list audio devices: " + ex.Message, true);
            return;
        }

        if (_panel.SelectedAudioIn is null && _panel.SelectedDevice is { } device)
            AudioAutoPick(device.Name, inputs);
        _panel.SelectAudioOutputIfNone();
    }

    /// <summary>
    /// Matches the card's own audio endpoint to the selected card by name. The candidate list
    /// is passed in where the caller already has one - enumerating endpoints opens every
    /// device in turn, and doing it three times for one refresh was pure waste.
    /// </summary>
    private bool AudioAutoPick(string videoDeviceName, List<AudioDeviceInfo>? candidates = null)
    {
        var current = _panel.SelectedAudioIn;
        if (current is not null && !string.IsNullOrEmpty(_settings.AudioInputId)) return false;

        candidates ??= AudioMonitor.Enumerate(DataFlow.Capture);
        var guess = AudioMonitor.GuessCaptureEndpoint(candidates, videoDeviceName);
        if (guess is null) return false;
        _panel.SelectAudioInput(candidates.First(d => d.Id == guess.Id));
        return true;
    }

    private void RestartAudio()
    {
        if (_loading) return;
        _engine.Audio.RestartIntervalMinutes = _settings.AudioRestartMinutes;

        var input = _panel.SelectedAudioIn;
        if (input is null) { _engine.Audio.Stop(); return; }

        var output = _panel.SelectedAudioOut;
        _settings.AudioInputId = input.Id;
        _settings.AudioOutputId = output?.Id;

        try
        {
            _engine.Audio.Start(input.Id, output?.Id, _settings.AudioLatencyMs,
                                _settings.AudioPassthrough, _settings.AudioExclusive, CurrentMicOptions());
        }
        catch (Exception ex)
        {
            _toast.Show("Audio: " + ex.Message, true);
        }

        // The replay buffer takes its stream layout from its first segment, so it is armed
        // once there is a sound format to put in it - otherwise a saved clip would be silent.
        _engine.RefreshReplay();
    }

    private MicOptions CurrentMicOptions() => new(_settings.MicDeviceId, _settings.MicEnabled,
        _settings.MicVolume, _settings.MicMuted, _settings.MicMonitor, _settings.MicOffsetMs);

    private void ToggleMicMute()
    {
        _settings.MicMuted = !_settings.MicMuted;
        _engine.Audio.MicMuted = _settings.MicMuted;
        _panel.ReloadFromSettings();
        _toast.Show(_settings.MicEnabled
            ? _settings.MicMuted ? "Microphone muted" : "Microphone live"
            : "No microphone is being mixed in - turn it on under Record.", !_settings.MicEnabled);
    }

    private void ToggleMute()
    {
        _engine.Audio.Muted = !_engine.Audio.Muted;
        _settings.Muted = _engine.Audio.Muted;
        _volumeOverlay.Show(_settings.Volume, _engine.Audio.Muted);
    }

    private void NudgeVolume(float delta)
    {
        _settings.Volume = Math.Clamp(_settings.Volume + delta, 0f, 2f);
        _engine.Audio.Volume = _settings.Volume;
        _panel.SetVolumeSilently(_settings.Volume);
        _volumeOverlay.Show(_settings.Volume, _engine.Audio.Muted);
    }

    private void Resync()
    {
        _engine.FlushPipeline();
        RestartAudio();
        _toast.Show("Pipeline flushed and audio restarted", false);
    }

    // ---- record / snapshot -------------------------------------------------------------------

    private async Task ToggleRecording()
    {
        try
        {
            if (_engine.IsRecording)
            {
                await _engine.StopRecordingAsync();
                _panel.SetRecording(false);
                _recordLight.Stop();
                return;
            }
            string name = $"Capture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
            await _engine.StartRecordingAsync(Path.Combine(_settings.OutputFolder, name));
            _panel.SetRecording(true);
            _recordLight.Start();
        }
        catch (Exception ex)
        {
            _toast.Show("Recording: " + ex.Message, true);
            _panel.SetRecording(false);
            _recordLight.Stop();
        }
    }

    /// <summary>
    /// Writes the tail of the replay buffer out. The join runs off the capture thread, so
    /// this can take a second or two on a long window - the button says so while it does.
    /// </summary>
    private async Task SaveReplay()
    {
        if (_replaySaving) return;
        _replaySaving = true;
        _panel.SetReplaySaving(true);
        try
        {
            string name = $"Replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
            _toast.Show($"Saving the last {_settings.ReplaySaveSeconds} seconds...", false);
            string path = await _engine.SaveReplayAsync(_settings.ReplaySaveSeconds,
                                                        Path.Combine(_settings.OutputFolder, name));
            _toast.Show("Saved " + Path.GetFileName(path), false);
            _panel.RefreshRecent();
        }
        catch (Exception ex)
        {
            _toast.Show("Instant replay: " + ex.Message, true);
        }
        finally
        {
            _replaySaving = false;
            _panel.SetReplaySaving(false);
        }
    }

    private void ToggleReplay()
    {
        _settings.ReplayEnabled = !_settings.ReplayEnabled;
        ApplyReplaySettings();
        _panel.ReloadFromSettings();
        _toast.Show(_settings.ReplayEnabled
            ? $"Instant replay armed - keeping the last {_settings.ReplayBufferSeconds} s"
            : "Instant replay off", false);
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            System.Diagnostics.Process.Start("explorer.exe", _settings.OutputFolder);
        }
        catch (Exception ex) { _toast.Show("Could not open the folder: " + ex.Message, true); }
    }

    // ---- profiles ---------------------------------------------------------------------------

    private async Task NextProfile()
    {
        if (_settings.Profiles.Count < 2) { _toast.Show("There is only one profile.", false); return; }
        int index = _settings.Profiles.FindIndex(p => p.Name == _settings.ActiveProfile);
        var next = _settings.Profiles[(index + 1) % _settings.Profiles.Count];
        _settings.SwitchProfile(next.Name);
        _panel.ReloadFromSettings();
        await ApplyProfile();
        _toast.Show("Profile: " + next.Name, false);
    }

    /// <summary>
    /// Puts a freshly loaded profile into effect.
    ///
    /// On the same card the format list is kept and the profile's format is just picked out
    /// of it: re-reading the list means reopening the device, which stops the preview. On a
    /// different card the list has to be read again, the preview does stop, and it is
    /// started back up at the end if it had been running.
    /// </summary>
    private async Task ApplyProfile()
    {
        ApplyAllSettings();

        bool sameCard = _panel.SelectedDevice is { SymbolicLink.Length: > 0 } current &&
                        current.SymbolicLink == _settings.VideoDeviceLink &&
                        _formats.Count > 0;
        if (sameCard) SelectFormatFromSettings();
        else _formats = new List<VideoFormat>();

        bool wasStreaming = _engine.IsStreaming;
        bool wasLoading = _loading;
        _loading = true;
        await RefreshDevices();
        RefreshAudioDevices();
        _loading = wasLoading;

        RestartAudio();
        if (wasStreaming && !_engine.IsStreaming) await ToggleStreaming();
    }

    private async Task TakeSnapshot()
    {
        try
        {
            string name = $"Shot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            await _engine.ScreenshotAsync(Path.Combine(_settings.OutputFolder, name));
        }
        catch (Exception ex) { _toast.Show("Snapshot: " + ex.Message, true); }
    }

    private async Task SwitchAdapter()
    {
        try
        {
            await _engine.SwitchAdapterAsync(_settings.Adapter);
            RestartAudio();
        }
        catch (Exception ex)
        {
            _toast.Show("Could not switch GPU: " + ex.Message, true);
        }
    }

    private async Task RunTrace()
    {
        try
        {
            _toast.Show("Measuring for 10 seconds - leave it running and play normally...", false);
            string report = await _engine.RunTraceAsync(10);
            string path = Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "trace.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, report);
            _toast.Show("Saved " + path, false);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast.Show("Trace failed: " + ex.Message, true);
        }
    }

    private async Task SaveDiagnostics()
    {
        try
        {
            _toast.Show("Collecting diagnostics...", false);
            string path = await Diagnostics.WriteAsync(_engine, _settings);
            _toast.Show("Saved " + path, false);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast.Show("Diagnostics failed: " + ex.Message, true);
        }
    }

    private void UpdateStats(EngineStats stats)
    {
        _panel.SetLevel(stats.AudioPeak);
        _panel.SetMicLevel(_engine.Audio.MicPeakLevel);
        if (_engine.IsRecording) _recordLight.Update(stats.RecordingElapsed);

        _panel.SetReplayState(stats.ReplayArmed
            ? $"Armed - {stats.ReplayBufferedSeconds:0} s of {_settings.ReplayBufferSeconds} s ready\n" +
              $"{ShortcutLabel(ShortcutAction.SaveReplay)} saves the last {_settings.ReplaySaveSeconds} s"
            : stats.ReplayError is { Length: > 0 } error
                ? "Stopped: " + error
                : _settings.ReplayEnabled
                    ? "Waiting for the preview to start"
                    : $"Off - {ShortcutLabel(ShortcutAction.ReplayToggle)} arms it");

        _panel.SetRailStatus(_engine.IsStreaming,
            _engine.IsStreaming ? $"{stats.PresentFps:0} FPS" : "Stopped",
            stats.Width > 0 ? $"{stats.Width}x{stats.Height}" : "no signal");
        _panel.SetRecordState(_engine.IsRecording
            ? $"Recording {stats.RecordingElapsed:hh\\:mm\\:ss}\n{Path.GetFileName(_engine.RecordingPath)}"
            : "Not recording");

        if (stats.Width == 0) return;
        string line1 = $"{stats.Width}x{stats.Height} {stats.Format}  " +
                       $"in {stats.CaptureFps:0.0} / shown {stats.PresentFps:0.0} fps" +
                       (stats.DisplayRefreshHz > 0 ? $"  display {stats.DisplayRefreshHz:0} Hz" : "");
        string line2 = $"lag {stats.PipelineMs:0.0} ms  draw {stats.PresentMs:0.0} ms" +
                       $"  wait {stats.VSyncWaitMs:0.0} ms  audio {stats.AudioBufferedMs:0} ms" +
                       $"  {(stats.Gpu ? "GPU" : "SW")}" +
                       (stats.FramesDropped > 0 ? $"  dropped {stats.FramesDropped}" : "");
        _stats.Update(line1, line2);
        _panel.SetStatus(line1 + "\n" + line2 + "\n" +
                         $"vsync {(stats.VSyncOn ? "on" : "off")}   queued {stats.QueuedFrames}");
    }

    // ---- fullscreen and keys ------------------------------------------------------------------

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreBounds = Bounds;
            _restoreBorder = FormBorderStyle;
            _restoreState = WindowState;
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _fullscreen = true;
        }
        else
        {
            _fullscreen = false;
            FormBorderStyle = _restoreBorder;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            ShowCursorIfHidden();
        }
        LayoutOverlays();
        _engine.Resize(_video.ClientSize.Width, _video.ClientSize.Height);
    }

    private void NoteMouseActivity()
    {
        _lastMouseMove = DateTime.UtcNow;
        ShowCursorIfHidden();
    }

    private void ShowCursorIfHidden()
    {
        if (!_cursorHidden) return;
        Cursor.Show();
        _cursorHidden = false;
    }

    private void CursorTick(object? sender, EventArgs e)
    {
        if (_cursorHidden || _panel.Visible) return;
        if ((DateTime.UtcNow - _lastMouseMove).TotalSeconds < 2.5) return;
        if (!_video.ClientRectangle.Contains(_video.PointToClient(Cursor.Position))) return;
        Cursor.Hide();
        _cursorHidden = true;
    }

    private string ShortcutLabel(ShortcutAction action) => _shortcuts.DescribeBoth(action);

    /// <summary>
    /// Every shortcut goes through the binding table rather than a fixed switch on keys, so
    /// rebinding one in the panel takes effect at once and nothing has to be kept in step.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // A field that is waiting for its new chord must have the keystroke, not the shell.
        if (KeyCaptureButton.AnyCapturing) return base.ProcessCmdKey(ref msg, keyData);

        if (_shortcuts.Lookup(keyData) is { } action && RunShortcut(action)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Runs an action, or returns false to let the keystroke go where it was going.</summary>
    private bool RunShortcut(ShortcutAction action)
    {
        // A letter or an arrow key belongs to whatever is being typed in or nudged.
        switch (action)
        {
            case ShortcutAction.Mute:
            case ShortcutAction.MicMute:
                if (ActiveControl is TextBoxBase or ComboBox) return false;
                break;
            case ShortcutAction.VolumeUp:
            case ShortcutAction.VolumeDown:
                if (ActiveControl is Slider or NumericField or ComboBox or TextBoxBase) return false;
                break;
        }

        switch (action)
        {
            case ShortcutAction.TogglePanel: SetPanelOpen(!_panel.Visible); return true;
            case ShortcutAction.Fullscreen: ToggleFullscreen(); return true;
            case ShortcutAction.Snapshot: _ = TakeSnapshot(); return true;
            case ShortcutAction.RecordToggle: _ = ToggleRecording(); return true;
            case ShortcutAction.SaveReplay: _ = SaveReplay(); return true;
            case ShortcutAction.ReplayToggle: ToggleReplay(); return true;
            case ShortcutAction.MicMute: ToggleMicMute(); return true;
            case ShortcutAction.OpenFolder: OpenOutputFolder(); return true;
            case ShortcutAction.Resync: Resync(); return true;
            case ShortcutAction.VolumeUp: NudgeVolume(0.05f); return true;
            case ShortcutAction.VolumeDown: NudgeVolume(-0.05f); return true;
            case ShortcutAction.Mute: ToggleMute(); return true;
            case ShortcutAction.NextProfile: _ = NextProfile(); return true;
            default: return false;
        }
    }
}
