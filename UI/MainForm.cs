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
    private readonly BarForm _bar = new();
    private readonly ScrimForm _scrim = new();
    private readonly VolumeOverlay _volumeOverlay = new();
    private readonly ToastOverlay _toast = new();
    private readonly StatsOverlay _stats = new();
    private readonly RecordOverlay _recordLight = new();

    private readonly System.Windows.Forms.Timer _cursorTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _relayout = new() { Interval = 160 };
    private readonly System.Windows.Forms.Timer _audioDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };

    /// <summary>Bar + backdrop visible (independent of whether a settings window is open).</summary>
    private bool _menuOpen;
    /// <summary>Which settings window is currently showing, or null when the menu is bar-only.</summary>
    private Page? _openPage;
    private double _chromeOpacity, _chromeTarget;
    private double _panelOpacity, _panelTarget;
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
        Controls.Add(_volumeOverlay);
        Controls.Add(_toast);
        Controls.Add(_stats);
        Controls.Add(_recordLight);

        // The bar, backdrop and settings window are separate borderless owned windows rather
        // than child controls - see the note on BarForm - so they are never added to Controls.
        _panel.Owner = this;
        _bar.Owner = this;
        _scrim.Owner = this;
        _bar.PageSelected += (_, page) => ToggleWindow(page);
        _scrim.Clicked += (_, _) => SetMenuOpen(false);
        _panel.PageShown += (_, page) => { _openPage = page; _bar.ActivePage = page; };

        // Start already open if the menu was open on the last run - no fade on launch.
        _menuOpen = _settings.PanelOpen;
        _chromeOpacity = _chromeTarget = _menuOpen ? 1 : 0;
        if (_menuOpen) _openPage = Page.Capture;
        _panelOpacity = _panelTarget = _openPage is null ? 0 : 1;

        WirePanel();
        WireEngine();
        ApplyAllSettings();

        _cursorTimer.Tick += CursorTick;
        _cursorTimer.Start();
        // Laying the cards out again is not free, so it waits until a drag-resize settles.
        _relayout.Tick += (_, _) => { _relayout.Stop(); RebuildPanelIfNeeded(); };
        _audioDebounce.Tick += (_, _) => { _audioDebounce.Stop(); RestartAudio(); };
        _fade.Tick += FadeTick;

        LayoutOverlays();
        if (_menuOpen)
        {
            _scrim.Opacity = ScrimForm.MaxOpacity;
            _bar.Opacity = 1;
            _scrim.Show();
            _bar.Show();
            _bar.BringToFront();
            if (_openPage is { } page)
            {
                _panel.ShowPage(page);
                _panel.Opacity = 1;
                _panel.Show();
                _panel.BringToFront();
                _bar.BringToFront();
                _bar.ActivePage = page;
                // See the matching call in SetOpenPage: whichever of these owned windows was
                // just Shown for the first time needs an explicit Activate(), or Windows treats
                // the user's next click as activation-only and eats it - the click handler never
                // sees it, so it takes a second click to actually do anything.
                _panel.Activate();
            }
            else
            {
                _bar.Activate();
            }
        }

        _video.MouseMove += (_, _) => NoteMouseActivity();
        _video.MouseDoubleClick += (_, _) => ToggleFullscreen();
        _video.MouseDown += (_, _) => { if (_menuOpen) SetMenuOpen(false); };
    }

    /// <summary>
    /// Lets the bar, backdrop and settings window - each its own top-level window - forward
    /// keystrokes back into the same shortcut table MainForm itself uses, so a hotkey works
    /// no matter which of them last had focus.
    /// </summary>
    internal bool DispatchShortcut(Keys keyData)
    {
        if (KeyCaptureButton.AnyCapturing) return false;
        return _shortcuts.Lookup(keyData) is { } action && RunShortcut(action);
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
        // Re-assigning TopMost - even to the same value - makes Windows reissue
        // SetWindowPos(HWND_TOPMOST) for that window, which reorders the z-stack among
        // topmost windows. Since this used to run on every settings change (not just when
        // AlwaysOnTop itself changed), it silently shuffled the scrim above the panel,
        // making the backdrop cover the menu and eat the next click as "clicked outside".
        // Only touch TopMost - and only restore the stacking order - when the value
        // genuinely changes. Guard on _bar.TopMost, the thing this block actually syncs -
        // not on this form's own TopMost, which the constructor already set to the same
        // value before the very first call here, which made that comparison a no-op and
        // left _bar/_scrim/_panel never synced at all (invisible behind an always-on-top
        // MainForm whenever AlwaysOnTop was true, which looked like the menu no longer
        // opening).
        if (_bar.TopMost != _settings.AlwaysOnTop)
        {
            TopMost = _settings.AlwaysOnTop;
            _bar.TopMost = _scrim.TopMost = _panel.TopMost = _settings.AlwaysOnTop;
            // Restore the stacking order TopMost reassignment just disturbed: scrim at the
            // back, panel above it, bar above the panel - same order SetOpenPage establishes.
            _panel.BringToFront();
            _bar.BringToFront();
        }
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
        _settings.PanelOpen = _menuOpen;
        _settings.Muted = _engine.Audio.Muted;
        _settings.MicMuted = _engine.Audio.MicMuted;
        _shortcuts.SaveInto(_settings);
        _settings.Save();
        _engine.Dispose();

        foreach (var timer in new[] { _cursorTimer, _relayout, _audioDebounce, _fade })
        {
            timer.Stop();
            timer.Dispose();
        }
        _panel.Dispose();
        _bar.Dispose();
        _scrim.Dispose();
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { /* handle went away between the check and the call */ }
    }

    // ---- menu + overlay placement -------------------------------------------------------

    /// <summary>
    /// Positions the bar (top-left inset over the picture), the backdrop (covering the
    /// picture exactly) and the settings window (centred), all as screen coordinates since
    /// each is its own top-level window rather than a child control. The settings window is
    /// rebuilt only when its width actually changes, because relaying out the cards is not free.
    /// </summary>
    private void LayoutOverlays()
    {
        int w = ClientSize.Width, h = ClientSize.Height;

        var scrimBounds = RectangleToScreen(ClientRectangle);
        _scrim.Bounds = scrimBounds;

        const int Inset = 16;
        const int PanelGap = 16;
        var barTopLeft = PointToScreen(new Point(Inset, Inset));
        _bar.Location = barTopLeft;
        _bar.Size = BarForm.DesiredSize;

        int panelWidth = Math.Clamp(w - 72, 720, 1180);
        int panelHeight = Math.Clamp(h - 72, 420, 940);
        var panelTopLeft = PointToScreen(new Point(Inset, Inset + BarForm.DesiredSize.Height + PanelGap));
        _panel.Bounds = new Rectangle(panelTopLeft, new Size(panelWidth, panelHeight));
        if (_builtWidth == 0) RebuildPanelIfNeeded();
        else if (_builtWidth != panelWidth) { _relayout.Stop(); _relayout.Start(); }

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

    /// <summary>Opens or closes the whole menu (backdrop + bar); closing also closes
    /// whichever settings window was open.</summary>
    private void SetMenuOpen(bool open)
    {
        if (_menuOpen == open) return;
        _menuOpen = open;

        if (open)
        {
            LayoutOverlays();
            if (!_scrim.Visible) { _scrim.Opacity = 0; _scrim.Show(); }
            if (!_bar.Visible) { _bar.Opacity = 0; _bar.Show(); }
            _bar.BringToFront();
            // Show() alone doesn't reliably hand this owned window real OS focus - without this,
            // the user's next click on a bar icon lands as an activate-only click that Windows
            // eats before it ever reaches OnMouseDown, so the icon needs a second click to
            // actually fire PageSelected. Cheap (one Win32 call) and only runs on the open
            // transition, so it costs nothing while the menu sits closed.
            _bar.Activate();
            ShowCursorIfHidden();
        }
        else
        {
            SetOpenPage(null);
        }
        _chromeTarget = open ? 1 : 0;
        _fade.Start();
    }

    /// <summary>Clicking a bar icon: opens its window, or closes it if it was already the
    /// one showing. The bar and backdrop stay put either way.</summary>
    private void ToggleWindow(Page page)
    {
        if (!_menuOpen) SetMenuOpen(true);
        SetOpenPage(_openPage == page ? null : page);
    }

    private void SetOpenPage(Page? page)
    {
        // ShowPage below fires PageShown synchronously, and MainForm's own handler for that
        // event updates _openPage as a side effect - so it must be read here, before that
        // call, or this always sees the page we're about to open instead of the one we're
        // replacing, and the panel's first Show() below never runs.
        bool wasClosed = _openPage is null;
        if (page is { } shown)
        {
            _panel.ShowPage(shown);
            if (wasClosed)
            {
                LayoutOverlays();
                if (!_panel.Visible) { _panel.Opacity = 0; _panel.Show(); }
                _panel.BringToFront();
                _bar.BringToFront();
                // Same reasoning as SetMenuOpen's _bar.Activate(): the panel is a freshly shown
                // owned window too, so its first click needs this or it's swallowed as an
                // activation click instead of reaching whatever control the user meant to hit.
                _panel.Activate();
            }
            _panelTarget = 1;
        }
        else
        {
            _panelTarget = 0;
        }
        _openPage = page;
        _bar.ActivePage = page;
        _fade.Start();
    }

    /// <summary>
    /// Fades the backdrop, bar and settings window toward their targets and stops itself
    /// once everything has settled, hiding whichever top-level windows reached zero so a
    /// closed menu costs nothing. Each is its own window (see BarForm), so this drives plain
    /// Form.Opacity rather than repainting anything.
    /// </summary>
    private void FadeTick(object? sender, EventArgs e)
    {
        bool moving = false;
        _chromeOpacity = Ease(_chromeOpacity, _chromeTarget, ref moving);
        _panelOpacity = Ease(_panelOpacity, _panelTarget, ref moving);

        _scrim.Opacity = _chromeOpacity * ScrimForm.MaxOpacity;
        _bar.Opacity = Math.Max(_chromeOpacity, 0.001);
        _panel.Opacity = Math.Max(_panelOpacity, 0.001);

        if (moving) return;
        _fade.Stop();
        bool closingChrome = _chromeOpacity <= 0;
        bool closingPanel = _panelOpacity <= 0;
        // Whichever of these is about to close likely still holds Windows' input focus (it
        // was the one that received the Escape keystroke, e.g. after clicking into an open
        // settings panel). Reclaiming focus *before* hiding it - rather than after - means it
        // has already let go of focus by the time Hide() runs, so Windows never has to guess
        // which top-level window to hand focus to next. Left in the other order, that guess
        // can land on a completely different app, which is what shows up as an alt-tab-like
        // flash even though we fix it a moment later.
        if (closingChrome || closingPanel) Activate();
        if (closingChrome) { _scrim.Hide(); _bar.Hide(); }
        if (closingPanel) _panel.Hide();
    }

    private static double Ease(double current, double target, ref bool moving)
    {
        double next = current + (target - current) * 0.32;
        if (Math.Abs(target - next) < 0.01) next = target;
        else moving = true;
        return next;
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

        _panel.SetLiveStatus(_engine.IsStreaming,
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
        if (_cursorHidden || _menuOpen) return;
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
        if (DispatchShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Runs an action, or returns false to let the keystroke go where it was going.</summary>
    private bool RunShortcut(ShortcutAction action)
    {
        // A letter or an arrow key belongs to whatever is being typed in or nudged. The
        // settings window is now its own top-level Form, so its focus has to be checked
        // separately from MainForm's own ActiveControl - they no longer share one focus scope.
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
                if (_panel.ActiveControl is Slider or NumericField or FlatCombo or TextBoxBase) return false;
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
