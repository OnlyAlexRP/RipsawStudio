using NAudio.CoreAudioApi;
using RipsawStudio.Audio;
using RipsawStudio.Capture;

namespace RipsawStudio.UI;

/// <summary>
/// Video-first shell: the picture owns the whole window, and the controls live in one panel
/// that floats over it. Nothing but the picture is on screen while you play.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ShortcutMap _shortcuts;
    private readonly CaptureEngine _engine;

    private readonly VideoSurface _video = new();
    private readonly ControlPanel _panel;
    private readonly ScrimForm _scrim = new();
    private readonly VolumeOverlay _volumeOverlay = new();
    private readonly ToastOverlay _toast = new();
    private readonly StatsOverlay _stats = new();
    private readonly RecordOverlay _recordLight = new();
    private readonly CaptureToast _captureToast = new();
    private GlobalHotkeys? _globalHotkeys;

    private readonly System.Windows.Forms.Timer _cursorTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _relayout = new() { Interval = 160 };
    private readonly System.Windows.Forms.Timer _audioDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    /// <summary>Settings are written shortly after they change, not only on exit - a crash used to lose them all.</summary>
    private readonly System.Windows.Forms.Timer _autosave = new() { Interval = 1500 };
    /// <summary>Retries the card after it was unplugged or stopped sending, until it is back.</summary>
    private readonly System.Windows.Forms.Timer _reconnect = new() { Interval = 2000 };

    private bool _menuOpen;
    private double _opacity, _opacityTarget;
    private int _builtWidth;

    private DateTime _lastMouseMove = DateTime.UtcNow;
    private bool _cursorHidden;
    private bool _fullscreen;
    private Rectangle _restoreBounds;
    private FormBorderStyle _restoreBorder;
    private FormWindowState _restoreState;

    private bool _loading = true;
    private bool _shown;
    private bool _reconnecting;
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
        _panel = new ControlPanel(_settings, _shortcuts);

        Text = "Ripsaw Studio";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        MinimumSize = new Size(900, 600);
        ClientSize = new Size(_settings.WindowWidth, _settings.WindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        TopMost = _settings.AlwaysOnTop;
        ApplyWindowIcon();

        _video.Dock = DockStyle.Fill;
        _video.PlaceholderText = "Press Esc for the controls";
        Controls.Add(_video);

        // The backdrop, the panel and the overlays are borderless owned windows rather than
        // child controls: over the swap chain a child control can only be an opaque rectangle.
        _panel.Owner = this;
        _scrim.Owner = this;
        foreach (var overlay in Overlays) overlay.Owner = this;
        _scrim.Clicked += (_, _) => SetMenuOpen(false);

        _menuOpen = _settings.PanelOpen;
        _opacity = _opacityTarget = _menuOpen ? 1 : 0;

        WirePanel();
        WireEngine();
        ApplyAllSettings();

        _cursorTimer.Tick += CursorTick;
        _cursorTimer.Start();
        // Laying the pages out again is not free, so it waits until a drag-resize settles.
        _relayout.Tick += (_, _) => { _relayout.Stop(); RebuildPanelIfNeeded(); };
        _audioDebounce.Tick += (_, _) => { _audioDebounce.Stop(); RestartAudio(); };
        _fade.Tick += FadeTick;
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveSettings(); };
        _reconnect.Tick += async (_, _) => await TryReconnect();

        LayoutOverlays();
        if (_menuOpen)
        {
            _scrim.Opacity = ScrimForm.MaxOpacity;
            _panel.Opacity = 1;
            _scrim.Show();
            // Show() has to run before ShowPage(): ShowPage can reload the captures list, which
            // fetches thumbnails on a background thread and marshals each one back via
            // BeginInvoke - that fails while the panel's window handle does not exist yet.
            _panel.Show();
            _panel.ShowPage(Page.Source);
            _panel.BringToFront();
            _panel.Activate();
        }

        _video.MouseMove += (_, _) => NoteMouseActivity();
        _video.MouseDoubleClick += (_, _) => ToggleFullscreen();
        _video.MouseDown += (_, _) => { if (_menuOpen) SetMenuOpen(false); };
    }

    private Overlay[] Overlays => new Overlay[] { _volumeOverlay, _toast, _stats, _recordLight, _captureToast };

    /// <summary>
    /// Lets the panel and the backdrop - each its own top-level window - forward keystrokes
    /// back into the same shortcut table MainForm itself uses.
    /// </summary>
    internal bool DispatchShortcut(Keys keyData)
    {
        if (KeyCaptureButton.AnyCapturing) return false;
        return _shortcuts.Lookup(keyData) is { } action && RunShortcut(action);
    }

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
        _panel.RescanClicked += async (_, _) =>
        {
            await RefreshDevices();
            RefreshAudioDevices();
            RestartAudio();
            _toast.Show("Devices rescanned", false);
        };
        _panel.StartStopClicked += async (_, _) => await ToggleStreaming(userAction: true);
        _panel.RecordClicked += async (_, _) => await ToggleRecording();
        _panel.SnapshotClicked += async (_, _) => await TakeSnapshot();
        // Nudging the buffer spinner would otherwise restart WASAPI on every click.
        _panel.AudioSelectionChanged += (_, _) => { _audioDebounce.Stop(); _audioDebounce.Start(); };
        _panel.PictureChanged += (_, _) => _engine.SetPicture(_settings.ToPicture());
        _panel.RecordingSettingsChanged += (_, _) => ApplyRecordSettings();
        _panel.DiagnosticsClicked += async (_, _) => await SaveDiagnostics();
        _panel.AdapterChanged += async (_, _) => await SwitchAdapter();
        _panel.TraceClicked += async (_, _) => await RunTrace();
        _panel.EncoderTestClicked += async (_, _) => await RunEncoderTest();
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
            _engine.Audio.MicDucking = _settings.MicDucking;
        };
        _panel.ReplaySettingsChanged += (_, _) => ApplyReplaySettings();
        _panel.SaveReplayClicked += async (_, _) => await SaveReplay();
        _panel.ProfileChanged += async (_, _) => await ApplyProfile();
        _panel.CloseClicked += (_, _) => SetMenuOpen(false);
        _panel.OpenFolderClicked += (_, _) => OpenOutputFolder();
        _panel.SettingsTouched += (_, _) => { _autosave.Stop(); _autosave.Start(); };
        _panel.ShortcutsChanged += (_, note) =>
        {
            _shortcuts.SaveInto(_settings);
            if (note.Length > 0) _toast.Show(note, false);
            ApplyGlobalShortcuts();
        };
        // Suspended while a key is being rebound, and taken back as soon as it is not.
        KeyCaptureButton.CapturingChanged += OnKeyCaptureChanged;
    }

    private void WireEngine()
    {
        _engine.Status += (_, message) => Ui(() =>
        {
            // "Saved <path>" is what the engine raises when a recording, replay or screenshot
            // lands, and each of those is announced by the thumbnail card instead.
            if (!message.StartsWith("Saved ", StringComparison.Ordinal)) _toast.Show(message, false);
        });
        _engine.Failed += (_, message) => Ui(() => _toast.Show(message, true));
        _engine.Stats += (_, stats) => Ui(() => UpdateStats(stats));
        _engine.SourceFormatChanged += (_, _) => Ui(() => _video.ShowPlaceholder = false);
        _engine.RecordingChanged += (_, e) => Ui(() => OnRecordingChanged(e));
        _engine.StreamLost += (_, _) => Ui(OnStreamLost);
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
        _engine.Audio.MicDucking = _settings.MicDucking;
        _engine.Audio.RestartIntervalMinutes = _settings.AudioRestartMinutes;
    }

    private void ApplyReplaySettings()
    {
        _engine.ReplayBufferSeconds = _settings.ReplayBufferSeconds;
        _engine.ReplayEnabled = _settings.ReplayEnabled;
    }

    private void ApplyRecordSettings() => _engine.SetRecordSettings(_settings.ToRecordSettings());

    private void ApplyDisplaySettings()
    {
        _engine.VSync = _settings.VSync;
        _engine.Scaling = _settings.Scaling;
        _engine.Aspect = _settings.Aspect;
        // Re-assigning TopMost - even to the same value - makes Windows reissue
        // SetWindowPos(HWND_TOPMOST), which reorders the topmost windows and can put the
        // backdrop above the panel. So it is only touched when the value really changes.
        if (_panel.TopMost != _settings.AlwaysOnTop)
        {
            TopMost = _settings.AlwaysOnTop;
            _scrim.TopMost = _panel.TopMost = _settings.AlwaysOnTop;
            foreach (var overlay in Overlays) overlay.TopMost = _settings.AlwaysOnTop;
            _panel.BringToFront();
        }
        _stats.SetVisible(_settings.ShowStatusOverlay);
        LayoutOverlays();
    }

    private void SaveSettings()
    {
        _shortcuts.SaveInto(_settings);
        _settings.Save();
    }

    // ---- lifecycle ------------------------------------------------------------------------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_shown) return;
        _shown = true;
        LayoutOverlays();
        // Asked for in the constructor, before there was a window to float over.
        _stats.SetVisible(_settings.ShowStatusOverlay);
        _engine.SetWindow(_video.Handle, _video.ClientSize.Width, _video.ClientSize.Height);
        // WinForms recreates handles for some property changes; the swap chain must follow.
        _video.HandleCreated += (_, _) =>
            _engine.SetWindow(_video.Handle, _video.ClientSize.Width, _video.ClientSize.Height);

        try { _panel.SetAdapters(await _engine.ListAdaptersAsync(), _settings.Adapter); }
        catch (Exception ex) { _toast.Show("Could not list graphics cards: " + ex.Message, true); }
        await RefreshEncoders();

        await RefreshDevices();
        RefreshAudioDevices();
        _loading = false;
        // Deferred to here rather than the constructor: RegisterHotKey needs a real window.
        ApplyGlobalShortcuts();
        _ = CheckForUpdates();

        if (_settings.AutoStart && _panel.SelectedDevice is { SymbolicLink.Length: > 0 } && _panel.SelectedFormat is not null)
            await ToggleStreaming(userAction: false);
        else
            RestartAudio();
    }

    private async Task RefreshEncoders()
    {
        try { _panel.SetEncoders(await _engine.ListEncodersAsync(), await _engine.PreviewAdapterAsync()); }
        catch (Exception ex) { _toast.Show("Could not list encoders: " + ex.Message, true); }
    }

    /// <summary>
    /// Asks GitHub for the newest release and tells the General page what it found. A newer
    /// version gets one toast, once per version - not a dialog in the way of every launch.
    /// </summary>
    private async Task CheckForUpdates()
    {
        var result = await UpdateChecker.CheckAsync();
        if (IsDisposed) return;
        _panel.ShowUpdateStatus(result);
        if (!result.UpdateAvailable || result.Latest is null) return;
        string latest = UpdateChecker.Format(result.Latest);
        if (_settings.NotifiedUpdate == latest) return;
        _settings.NotifiedUpdate = latest;
        _autosave.Start();
        _toast.Show($"Ripsaw Studio {latest} is available. Download it from About.", false);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;
        LayoutOverlays();
        _engine.Resize(_video.ClientSize.Width, _video.ClientSize.Height);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (IsHandleCreated) LayoutOverlays();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;

        if (!_fullscreen && WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
        }
        _settings.PanelOpen = _menuOpen;
        _settings.Muted = _engine.Audio.Muted;
        _settings.MicMuted = _engine.Audio.MicMuted;
        SaveSettings();
        // Released before anything else: these are OS-wide registrations.
        KeyCaptureButton.CapturingChanged -= OnKeyCaptureChanged;
        _globalHotkeys?.Dispose();
        _globalHotkeys = null;
        _engine.Dispose();

        foreach (var timer in new[] { _cursorTimer, _relayout, _audioDebounce, _fade, _autosave, _reconnect })
        {
            timer.Stop();
            timer.Dispose();
        }
        _panel.Dispose();
        _scrim.Dispose();
        foreach (var overlay in Overlays) overlay.Dispose();
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { /* handle went away between the check and the call */ }
    }

    // ---- panel + overlay placement -------------------------------------------------------

    /// <summary>
    /// Centres the panel over the picture and stretches the backdrop across it, in screen
    /// coordinates since both are top-level windows. The pages are rebuilt only when the
    /// panel's width actually changes, because laying them out is not free.
    /// </summary>
    private void LayoutOverlays()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        _scrim.Bounds = RectangleToScreen(ClientRectangle);

        int panelWidth = Math.Clamp(w - 80, 820, 1160);
        int panelHeight = Math.Clamp(h - 80, 520, 860);
        var topLeft = PointToScreen(new Point(Math.Max(0, (w - panelWidth) / 2), Math.Max(0, (h - panelHeight) / 2)));
        _panel.Bounds = new Rectangle(topLeft, new Size(panelWidth, panelHeight));
        if (_builtWidth == 0) RebuildPanelIfNeeded();
        else if (_builtWidth != panelWidth) { _relayout.Stop(); _relayout.Start(); }

        _captureToast.SetAnchor(PointToScreen(new Point(16, 16)));
        _volumeOverlay.Location = PointToScreen(new Point((w - _volumeOverlay.Width) / 2, h - _volumeOverlay.Height - 28));
        _toast.MaxWidth = Math.Clamp(w - 32, 160, 620);
        _toast.Location = PointToScreen(new Point(16, h - _toast.Height - 16));
        _stats.Location = PointToScreen(new Point(w - _stats.Width - 16, 16));
        _recordLight.Location = PointToScreen(new Point(w - _recordLight.Width - 16, _settings.ShowStatusOverlay ? 16 + _stats.Height + 8 : 16));
    }

    private void RebuildPanelIfNeeded()
    {
        if (_panel.Width == _builtWidth) return;
        _builtWidth = _panel.Width;
        _panel.Build();
    }

    private void SetMenuOpen(bool open, Page? page = null)
    {
        if (_menuOpen == open)
        {
            if (open && page is { } p) _panel.ShowPage(p);
            return;
        }
        _menuOpen = open;

        if (open)
        {
            LayoutOverlays();
            if (!_scrim.Visible) { _scrim.Opacity = 0; _scrim.Show(); }
            if (!_panel.Visible) { _panel.Opacity = 0; _panel.Show(); }
            _panel.ShowPage(page ?? _panel.CurrentPage);
            _panel.BringToFront();
            // Show() alone doesn't reliably hand an owned window focus; without this the
            // first click on the panel is eaten as an activation click.
            _panel.Activate();
            ShowCursorIfHidden();
        }
        _opacityTarget = open ? 1 : 0;
        _fade.Start();
        _autosave.Start();
    }

    /// <summary>Fades the backdrop and the panel together, hiding both once fully out.</summary>
    private void FadeTick(object? sender, EventArgs e)
    {
        double next = _opacity + (_opacityTarget - _opacity) * 0.35;
        bool settled = Math.Abs(_opacityTarget - next) < 0.01;
        _opacity = settled ? _opacityTarget : next;

        _scrim.Opacity = _opacity * ScrimForm.MaxOpacity;
        _panel.Opacity = Math.Max(_opacity, 0.001);
        if (!settled) return;

        _fade.Stop();
        if (_opacity <= 0)
        {
            // Take focus back before hiding, so Windows never has to guess where it goes -
            // a guess that can land on another app entirely.
            Activate();
            _scrim.Hide();
            _panel.Hide();
        }
    }

    // ---- devices --------------------------------------------------------------------------

    private async Task RefreshDevices()
    {
        List<VideoDeviceInfo> devices;
        try { devices = await _engine.EnumerateDevicesAsync(); }
        catch (Exception ex) { _toast.Show("Could not list capture cards: " + ex.Message, true); return; }

        VideoDeviceInfo? preferred =
            devices.FirstOrDefault(d => d.SymbolicLink == _settings.VideoDeviceLink) ??
            devices.FirstOrDefault(d => d.Name == _settings.VideoDeviceName) ??
            devices.FirstOrDefault(d => d.Name.Contains("Ripsaw", StringComparison.OrdinalIgnoreCase)) ??
            devices.FirstOrDefault();

        _panel.SetDevices(devices, preferred);
        if (devices.Count == 0)
        {
            _toast.Show("No capture card found. Check the cable, and that no other app has the card open.", true);
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
            if (!_loading && AudioAutoPick(device.Name)) RestartAudio();
        }

        if (_panel.SelectedFormat is not { } format) return;
        _settings.FormatKey = format.Key;
        _autosave.Start();

        // Picking a different format mid-preview restarts the preview on it.
        if (_engine.IsStreaming && _activeFormatKey != format.Key)
        {
            await StopStreaming();
            await ToggleStreaming(userAction: false);
        }
    }

    /// <summary>
    /// Picks the format the settings ask for out of the list already read from the card,
    /// falling back to the best guess when that format is not on offer.
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

    private async Task ToggleStreaming(bool userAction)
    {
        if (_engine.IsStreaming)
        {
            await StopStreaming();
            return;
        }
        if (userAction) _reconnect.Stop();

        var device = _panel.SelectedDevice;
        var format = _panel.SelectedFormat;
        if (device is null || device.SymbolicLink.Length == 0 || format is null)
        {
            _toast.Show("Choose a capture card and a format first.", true);
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
        _reconnect.Stop();
        _activeFormatKey = null;
        await _engine.StopAsync();
        _engine.Audio.Stop();
        ShowStopped();
    }

    private void ShowStopped()
    {
        _panel.SetStreaming(false);
        _panel.SetRecording(false);
        _recordLight.Stop();
        _video.ShowPlaceholder = true;
        _video.Invalidate();
    }

    /// <summary>The card went away by itself - unplugged, or claimed by another app.</summary>
    private void OnStreamLost()
    {
        _activeFormatKey = null;
        ShowStopped();
        _video.PlaceholderText = "Waiting for the capture card...";
        _reconnect.Start();
    }

    /// <summary>
    /// Every couple of seconds after the card is lost: once the same card shows up again,
    /// the preview starts back up on its own, the way plugging a cable back in should work.
    /// </summary>
    private async Task TryReconnect()
    {
        if (_engine.IsStreaming) { _reconnect.Stop(); return; }
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            var devices = await _engine.EnumerateDevicesAsync();
            var match = devices.FirstOrDefault(d => d.SymbolicLink == _settings.VideoDeviceLink);
            if (match is null) return;
            _panel.SetDevices(devices, match);
            _formats = await _engine.QueryFormatsAsync(match);
            SelectFormatFromSettings();
            await ToggleStreaming(userAction: false);
            if (_engine.IsStreaming)
            {
                _reconnect.Stop();
                _video.PlaceholderText = "Press Esc for the controls";
                _toast.Show(match.Name + " is back", false);
            }
        }
        catch { /* not back yet - try again on the next tick */ }
        finally { _reconnecting = false; }
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

    /// <summary>Matches the card's own audio endpoint to the selected card by name.</summary>
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

        // The replay buffer is armed once there is a sound format to put in it.
        _engine.RefreshReplay();
    }

    private MicOptions CurrentMicOptions() => new(_settings.MicDeviceId, _settings.MicEnabled,
        _settings.MicVolume, _settings.MicMuted, _settings.MicMonitor, _settings.MicOffsetMs, _settings.MicDucking);

    private void ToggleMicMute()
    {
        _settings.MicMuted = !_settings.MicMuted;
        _engine.Audio.MicMuted = _settings.MicMuted;
        _panel.ReloadFromSettings();
        _autosave.Start();
        _toast.Show(_settings.MicEnabled
            ? _settings.MicMuted ? "Microphone muted" : "Microphone live"
            : "No microphone is mixed in. Turn it on under Audio.", !_settings.MicEnabled);
    }

    private void ToggleMute()
    {
        _engine.Audio.Muted = !_engine.Audio.Muted;
        _settings.Muted = _engine.Audio.Muted;
        _autosave.Start();
        _volumeOverlay.Show(_settings.Volume, _engine.Audio.Muted);
    }

    private void NudgeVolume(float delta)
    {
        _settings.Volume = Math.Clamp(_settings.Volume + delta, 0f, 2f);
        _engine.Audio.Volume = _settings.Volume;
        _panel.SetVolumeSilently(_settings.Volume);
        _autosave.Start();
        _volumeOverlay.Show(_settings.Volume, _engine.Audio.Muted);
    }

    private void Resync()
    {
        _engine.FlushPipeline();
        RestartAudio();
        _toast.Show("Video flushed and audio restarted", false);
    }

    // ---- record / snapshot -------------------------------------------------------------------

    private async Task ToggleRecording()
    {
        try
        {
            if (_engine.IsRecording)
            {
                _panel.SetRecording(false);
                _recordLight.Stop();
                string? saved = await _engine.StopRecordingAsync();
                _panel.RefreshRecent();
                if (saved is not null) ShowCapture(saved, RecentKind.Recording);
                return;
            }
            string name = $"Capture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
            await _engine.StartRecordingAsync(Path.Combine(_settings.RecordsFolder, name));
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

    /// <summary>The engine moved a recording on to a new file, or ended it, without being asked.</summary>
    private void OnRecordingChanged(RecordingChangedEventArgs e)
    {
        if (e.Message is { } message) _toast.Show(message, e.IsError);
        if (e.Recording) return;
        _panel.SetRecording(false);
        _recordLight.Stop();
        _panel.RefreshRecent();
    }

    /// <summary>Writes the tail of the replay buffer out. The join runs off the capture thread.</summary>
    private async Task SaveReplay()
    {
        if (_replaySaving) return;
        _replaySaving = true;
        _panel.SetReplayState(_engine.IsReplayArmed, saving: true);
        try
        {
            string name = $"Replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
            string path = await _engine.SaveReplayAsync(_settings.ReplaySaveSeconds,
                                                        Path.Combine(_settings.ReplayFolder, name));
            _panel.RefreshRecent();
            ShowCapture(path, RecentKind.Replay);
        }
        catch (Exception ex)
        {
            _toast.Show("Instant replay: " + ex.Message, true);
        }
        finally
        {
            _replaySaving = false;
            _panel.SetReplayState(_engine.IsReplayArmed, saving: false);
        }
    }

    private void ToggleReplay()
    {
        _settings.ReplayEnabled = !_settings.ReplayEnabled;
        ApplyReplaySettings();
        _panel.ReloadFromSettings();
        _autosave.Start();
        _toast.Show(_settings.ReplayEnabled
            ? $"Instant replay on, keeping the last {_settings.ReplayBufferSeconds} s"
            : "Instant replay off", false);
    }

    /// <summary>
    /// Opens the Recordings folder, or - when Recordings, Replay and Screenshots are still the
    /// three stock subfolders of one RipsawStudio folder - that shared parent instead.
    /// </summary>
    private void OpenOutputFolder()
    {
        string target = CommonOutputRoot();
        try
        {
            Directory.CreateDirectory(target);
            System.Diagnostics.Process.Start("explorer.exe", target);
        }
        catch (Exception ex) { _toast.Show("Could not open the folder: " + ex.Message, true); }
    }

    private string CommonOutputRoot()
    {
        static string? ParentOf(string path)
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(path)); }
            catch { return null; }
        }

        string? root = ParentOf(_settings.RecordsFolder);
        if (root is not null &&
            string.Equals(root, ParentOf(_settings.ReplayFolder), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(root, ParentOf(_settings.ScreenshotsFolder), StringComparison.OrdinalIgnoreCase))
            return root;
        return _settings.RecordsFolder;
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
        _autosave.Start();
        _toast.Show("Profile: " + next.Name, false);
    }

    /// <summary>
    /// Puts a freshly loaded profile into effect. On the same card the format list is kept and
    /// the profile's format is just picked out of it; on a different card the list is read
    /// again, and the preview is started back up at the end if it had been running.
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
        if (wasStreaming && !_engine.IsStreaming) await ToggleStreaming(userAction: false);
    }

    private async Task TakeSnapshot()
    {
        try
        {
            // Milliseconds in the name: two snapshots in the same second used to overwrite each other.
            string name = $"Shot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png";
            string path = await _engine.ScreenshotAsync(Path.Combine(_settings.ScreenshotsFolder, name));
            _panel.RefreshRecent();
            ShowCapture(path, RecentKind.Screenshot);
        }
        catch (Exception ex) { _toast.Show("Screenshot: " + ex.Message, true); }
    }

    private async Task SwitchAdapter()
    {
        try
        {
            await _engine.SwitchAdapterAsync(_settings.Adapter);
            RestartAudio();
            await RefreshEncoders();
        }
        catch (Exception ex)
        {
            _toast.Show("Could not switch graphics card: " + ex.Message, true);
        }
    }

    private async Task RunTrace()
    {
        try
        {
            _toast.Show("Measuring for 10 seconds, keep playing normally...", false);
            string report = await _engine.RunTraceAsync(10);
            string path = Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "trace.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, report);
            _toast.Show("Saved " + path, false);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast.Show("Measuring failed: " + ex.Message, true);
        }
    }

    private async Task RunEncoderTest()
    {
        if (_engine.IsRecording)
        {
            _toast.Show("Stop the recording before testing encoders.", true);
            return;
        }
        try
        {
            _toast.Show("Testing encoders, this takes about half a minute...", false);
            string folder = Path.Combine(Path.GetTempPath(), "RipsawStudio", "encoder-test");
            string report = await Task.Run(() => EncoderSelfTest.Run(folder));
            _toast.Show("Encoder test finished", false);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(report) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast.Show("Encoder test failed: " + ex.Message, true);
        }
    }

    private async Task SaveDiagnostics()
    {
        try
        {
            _toast.Show("Collecting a report...", false);
            string path = await Diagnostics.WriteAsync(_engine, _settings);
            _toast.Show("Saved " + path, false);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast.Show("Report failed: " + ex.Message, true);
        }
    }

    private void UpdateStats(EngineStats stats)
    {
        _panel.SetLevel(stats.AudioPeak);
        _panel.SetMicLevel(_engine.Audio.MicPeakLevel);

        bool streaming = _engine.IsStreaming;
        bool recording = _engine.IsRecording;
        if (recording) _recordLight.Update(stats.RecordingElapsed);
        _panel.SetRecording(recording, stats.RecordingElapsed);

        var state = !streaming ? SignalStrip.State.Stopped
            : stats.SecondsSinceFrame > 1.5 ? SignalStrip.State.NoSignal
            : SignalStrip.State.Live;
        string detail = recording
            ? $"{stats.RecordingEncoder}   {FormatBytes(stats.RecordingBytes)}" + (stats.FreeDiskBytes >= 0 ? $" written, {FormatBytes(stats.FreeDiskBytes)} free" : "")
            : _engine.IsReplayArmed ? $"Replay ready: {stats.ReplayBufferedSeconds:0} s" : "";
        _panel.SetSignal(state,
            stats.Width > 0 ? $"{stats.Width}×{stats.Height}" : "",
            streaming ? $"{stats.PresentFps:0}" : "",
            streaming && stats.PipelineMs > 0 ? $"{stats.PipelineMs:0.0}" : "",
            detail);

        _panel.SetReplayState(stats.ReplayArmed, _replaySaving);
        _panel.SetReplayState(stats.ReplayArmed
            ? $"Ready: {stats.ReplayBufferedSeconds:0} of {_settings.ReplayBufferSeconds} s kept.\n" +
              $"{ShortcutLabel(ShortcutAction.SaveReplay)} saves the last {_settings.ReplaySaveSeconds} s."
            : stats.ReplayError is { Length: > 0 } error
                ? "Stopped: " + error
                : _settings.ReplayEnabled
                    ? "Waiting for the preview to start."
                    : $"Off. {ShortcutLabel(ShortcutAction.ReplayToggle)} turns it on.");

        _panel.SetLiveStatus(streaming, streaming
            ? stats.Width > 0 ? $"On: {stats.Width}×{stats.Height}, {stats.CaptureFps:0} fps in, {stats.PresentFps:0} on screen" : "On"
            : "Off");
        _panel.SetRecordState(recording
            ? $"Recording {Path.GetFileName(_engine.RecordingPath)} with {stats.RecordingEncoder}"
            : $"Not recording. {ShortcutLabel(ShortcutAction.RecordToggle)} starts and stops a recording.");

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

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 0 => "?",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        _ => $"{bytes / (double)(1L << 20):0} MB",
    };

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
        if (_cursorHidden || _menuOpen) return;
        if ((DateTime.UtcNow - _lastMouseMove).TotalSeconds < 2.5) return;
        if (!_video.ClientRectangle.Contains(_video.PointToClient(Cursor.Position))) return;
        Cursor.Hide();
        _cursorHidden = true;
    }

    /// <summary>Puts the thumbnail card on screen for something that was just written.</summary>
    private void ShowCapture(string path, RecentKind kind) => _captureToast.Show(path, kind);

    /// <summary>
    /// Registers or releases the system-wide capture keys to match the setting, and says which
    /// chords Windows refused - usually because another overlay already holds them.
    /// </summary>
    private void ApplyGlobalShortcuts(bool quiet = false)
    {
        _globalHotkeys ??= new GlobalHotkeys(this, action => Ui(() => RunShortcut(action)));

        bool wanted = _settings.GlobalShortcuts && !KeyCaptureButton.AnyCapturing;
        _globalHotkeys.Apply(wanted, _shortcuts);

        if (quiet || !wanted || _globalHotkeys.Rejected.Count == 0) return;
        _toast.Show("Another program already uses " + string.Join(", ", _globalHotkeys.Rejected) +
                    ". Pick a different key for it.", true);
    }

    private void OnKeyCaptureChanged(object? sender, EventArgs e) => ApplyGlobalShortcuts(quiet: true);

    private string ShortcutLabel(ShortcutAction action) => _shortcuts.DescribeBoth(action);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (DispatchShortcut(keyData)) return true;
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
                if (ActiveControl is TextBoxBase or FlatCombo) return false;
                if (_panel.ActiveControl is TextBoxBase or FlatCombo) return false;
                break;
            case ShortcutAction.VolumeUp:
            case ShortcutAction.VolumeDown:
                if (ActiveControl is Slider or NumericField or FlatCombo or TextBoxBase) return false;
                if (_panel.ActiveControl is Slider or NumericField or FlatCombo or TextBoxBase or Segmented) return false;
                break;
        }

        switch (action)
        {
            case ShortcutAction.TogglePanel: SetMenuOpen(!_menuOpen); return true;
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
