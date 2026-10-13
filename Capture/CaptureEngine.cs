using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using RipsawStudio.Audio;
using RipsawStudio.Interop;
using RipsawStudio.Record;
using RipsawStudio.Render;

namespace RipsawStudio.Capture;

public sealed class EngineStats
{
    public double CaptureFps { get; init; }
    /// <summary>Frames actually put on screen per second. Below CaptureFps means the display is the bottleneck.</summary>
    public double PresentFps { get; init; }
    public double PresentMs { get; init; }
    /// <summary>Time spent waiting for the display before a frame is presented.</summary>
    public double VSyncWaitMs { get; init; }
    /// <summary>
    /// Milliseconds between the app receiving a frame and that frame being on screen.
    /// This is the delay the app is responsible for; the card's own delay is on top.
    /// </summary>
    public double PipelineMs { get; init; }
    public bool VSyncOn { get; init; }
    public double DisplayRefreshHz { get; init; }
    /// <summary>Frames handed to the display that it has not shown yet.</summary>
    public int QueuedFrames { get; init; }
    public long FramesDropped { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Format { get; init; } = "";
    public bool Gpu { get; init; }
    public double AudioBufferedMs { get; init; }
    public float AudioPeak { get; init; }
    public TimeSpan RecordingElapsed { get; init; }
    /// <summary>Size of the file being recorded, in bytes. Zero when not recording.</summary>
    public long RecordingBytes { get; init; }
    /// <summary>Free space on the drive being recorded to, or -1 when unknown.</summary>
    public long FreeDiskBytes { get; init; } = -1;
    /// <summary>The encoder the recording actually runs on, e.g. "NVIDIA NVENC".</summary>
    public string RecordingEncoder { get; init; } = "";
    /// <summary>Whether the rolling replay buffer is running.</summary>
    public bool ReplayArmed { get; init; }
    /// <summary>How much of the replay window is actually saveable right now.</summary>
    public double ReplayBufferedSeconds { get; init; }
    /// <summary>Why the buffer is not running, when that is because something failed.</summary>
    public string? ReplayError { get; init; }
    /// <summary>Seconds since the last frame arrived while live; large means no signal.</summary>
    public double SecondsSinceFrame { get; init; }
}

/// <summary>A recording started, stopped or moved on to a new file without the UI asking.</summary>
public sealed class RecordingChangedEventArgs : EventArgs
{
    public required bool Recording { get; init; }
    /// <summary>The file now being written, when still recording.</summary>
    public string? Path { get; init; }
    /// <summary>A file that was just closed and is being finalised.</summary>
    public string? FinishedPath { get; init; }
    /// <summary>Why, when it was not the user's doing.</summary>
    public string? Message { get; init; }
    public bool IsError { get; init; }
}

/// <summary>
/// Owns the whole pipeline on two dedicated MTA threads. The capture thread reads frames,
/// records them and runs commands; the render thread draws and presents. Splitting them is
/// what stops a wait on the display from delaying the next read. Both stay MTA so no COM
/// call is ever marshalled between apartments, which is a real per-frame cost at 60 Hz.
/// The UI talks to the engine only through posted commands.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    /// <summary>Recording stops by itself below this much free space, so the file can still be finalised.</summary>
    private const long MinFreeBytes = 512L * 1024 * 1024;

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _recordLock = new();
    /// <summary>Guards the presenter against the capture thread and the render thread at once.</summary>
    private readonly object _presenterLock = new();
    private readonly AutoResetEvent _frameReady = new(false);
    /// <summary>Guards the handoff slot. Kept separate from the event so neither is used as the other.</summary>
    private readonly object _slotLock = new();
    private Thread? _thread;
    private Thread? _renderThread;
    private volatile bool _alive = true;

    /// <summary>
    /// The single slot the capture thread hands frames to. Only the newest frame is kept:
    /// if the display cannot keep up, the older frame is dropped rather than queued, because
    /// showing a stale frame late is worse than not showing it at all.
    /// </summary>
    private PendingFrame? _pending;

    private sealed class PendingFrame
    {
        public required IMFSample Sample { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        /// <summary>When the capture thread got it, so we can report how stale it was on screen.</summary>
        public required long ArrivalTicks { get; init; }
        /// <summary>Which presenter the frame's surface belongs to; a GPU switch makes it unusable.</summary>
        public required int Generation { get; init; }
    }

    private Presenter? _presenter;
    private volatile int _presenterGeneration;
    private VideoSource? _source;
    /// <summary>
    /// The take being written, or null. Swapped out under <see cref="_recordLock"/> and then
    /// finalised elsewhere, so stopping never holds the capture thread through a finalise.
    /// </summary>
    private Recorder? _recorder;
    private readonly ReplayBuffer _replay = new();
    private readonly List<Task> _finalizing = new();

    private List<EncoderInfo>? _encoders;
    private readonly Dictionary<string, EncoderDevice> _encoderDevices = new(StringComparer.OrdinalIgnoreCase);
    private RecordSettings _recordSettings = new();

    private VideoDeviceInfo? _device;
    private VideoFormat? _formatInUse;
    private int _frameWidth, _frameHeight;
    private uint _fpsNum = 60, _fpsDen = 1;
    private Guid _outputSubtype;
    private bool _streaming;
    private bool _softwareFallback;
    private string? _adapter;
    private PictureSettings _picture = new();

    private IntPtr _pendingHwnd;
    private int _pendingWidth, _pendingHeight;

    private long _framesDropped;
    private double _presentMsAvg;
    private double _waitMsAvg;
    private double _pipelineMsAvg;
    private long _fpsWindowStart;
    private long _fpsWindowFrames;
    private long _presentWindowFrames;
    private double _measuredFps;
    private double _measuredPresentFps;
    private long _lastFrameTicks;
    private volatile PerfTrace? _trace;

    private long _recordStartTicks;
    private string? _recordBasePath;
    private int _recordPart;
    private long _lastDiskCheck;
    private long _freeDiskBytes = -1;
    private string _recordingEncoder = "";

    private readonly List<(string Path, TaskCompletionSource<string> Completion)> _screenshots = new();

    public AudioMonitor Audio { get; } = new();

    public event EventHandler<string>? Status;
    public event EventHandler<string>? Failed;
    public event EventHandler<EngineStats>? Stats;
    /// <summary>The input changed resolution or format mid-stream.</summary>
    public event EventHandler? SourceFormatChanged;
    /// <summary>The preview stopped on its own - the card was unplugged or stopped sending.</summary>
    public event EventHandler? StreamLost;
    public event EventHandler<RecordingChangedEventArgs>? RecordingChanged;

    public bool IsStreaming => _streaming;
    public bool IsRecording => _recorder?.IsRecording == true;
    public string? RecordingPath => _recorder?.FilePath;

    /// <summary>True while the rolling replay buffer is actually keeping segments.</summary>
    public bool IsReplayArmed => _replay.IsRunning;
    /// <summary>Seconds currently saveable from the replay buffer.</summary>
    public double ReplayBufferedSeconds => _replay.BufferedSeconds;
    /// <summary>Whether what the buffer is keeping has a sound track in it.</summary>
    public bool ReplayHasAudio => _replay.HasAudio;

    private bool _replayEnabled;
    /// <summary>
    /// Whether the replay buffer should run. It only actually runs while the preview is
    /// live, so setting this before starting arms it as soon as there is something to keep.
    /// </summary>
    public bool ReplayEnabled
    {
        get => _replayEnabled;
        set { _replayEnabled = value; Post(ApplyReplayState); }
    }

    private string? _replayError;
    private int _replayBufferSeconds = 60;
    public int ReplayBufferSeconds
    {
        get => _replayBufferSeconds;
        set { _replayBufferSeconds = value; Post(() => _replay.BufferSeconds = value); }
    }

    /// <summary>
    /// Re-checks whether the buffer should be running. Worth calling once audio is up, so
    /// the first segment already has a sound stream in it - the joined clip takes its layout
    /// from the first segment, so a silent one there means a silent replay.
    /// </summary>
    public void RefreshReplay() => Post(ApplyReplayState);

    /// <summary>
    /// Takes a copy of the recording settings. A running replay buffer is restarted when the
    /// picture side changed (encoder, codec, bitrate...), since its segments must all match.
    /// </summary>
    public void SetRecordSettings(RecordSettings settings)
    {
        var copy = settings.Clone();
        Post(() =>
        {
            bool videoChanged = !SameVideo(_recordSettings, copy);
            _recordSettings = copy;
            if (videoChanged && _replay.IsRunning)
            {
                _replay.Stop();
                ApplyReplayState();
            }
        });
    }

    private static bool SameVideo(RecordSettings a, RecordSettings b) =>
        a.Codec == b.Codec && a.EncoderId == b.EncoderId && a.RateControl == b.RateControl &&
        a.VideoBitrateKbps == b.VideoBitrateKbps && a.Quality == b.Quality &&
        a.KeyframeSeconds == b.KeyframeSeconds && a.FullRangeSource == b.FullRangeSource &&
        a.AudioOffsetMs == b.AudioOffsetMs && a.AudioBitrateKbps == b.AudioBitrateKbps;

    private bool _vsync;
    public bool VSync
    {
        get => _vsync;
        set { _vsync = value; Post(() => { if (_presenter is not null) _presenter.VSync = value; }); }
    }

    private ScalingMode _scaling = ScalingMode.Fit;
    public ScalingMode Scaling
    {
        get => _scaling;
        set { _scaling = value; Post(() => { if (_presenter is not null) _presenter.Scaling = value; }); }
    }

    private AspectMode _aspect = AspectMode.Auto;
    public AspectMode Aspect
    {
        get => _aspect;
        set { _aspect = value; Post(() => { if (_presenter is not null) _presenter.Aspect = value; }); }
    }

    /// <summary>Brightness, contrast, saturation, range and matrix, applied on the GPU.</summary>
    public void SetPicture(PictureSettings picture)
    {
        var copy = picture.Clone();
        Post(() =>
        {
            _picture = copy;
            lock (_presenterLock) _presenter?.SetPicture(copy);
        });
    }

    /// <summary>Every GPU that could drive the preview.</summary>
    public Task<List<string>> ListAdaptersAsync() => Invoke(Presenter.ListAdapters);

    /// <summary>Every H.264 / HEVC encoder on this machine, hardware and software.</summary>
    public Task<List<EncoderInfo>> ListEncodersAsync() => Invoke(() => Encoders().ToList());

    /// <summary>Name of the GPU the preview is drawn on, for picking the default encoder.</summary>
    public Task<string?> PreviewAdapterAsync() => Invoke(() => _presenter?.AdapterName);

    private List<EncoderInfo> Encoders()
    {
        if (_encoders is not null) return _encoders;
        try { _encoders = EncoderCatalog.Enumerate(); }
        catch { _encoders = new List<EncoderInfo>(); }
        return _encoders;
    }

    /// <summary>
    /// Rebuilds the whole graphics pipeline on a different GPU. The capture device has to be
    /// reopened too, because its frames are allocated by the old device.
    /// </summary>
    public Task SwitchAdapterAsync(string? adapter) =>
        Invoke(() =>
        {
            if (string.Equals(adapter, _adapter, StringComparison.OrdinalIgnoreCase)) return true;
            _adapter = adapter;

            var device = _device;
            var format = _formatInUse;
            bool wasStreaming = _streaming;

            StopCore();
            _source?.Dispose();
            _source = null;

            lock (_presenterLock)
            {
                _presenterGeneration++;
                _presenter?.Dispose();
                _presenter = new Presenter(_adapter)
                {
                    VSync = _vsync,
                    Scaling = _scaling,
                    Aspect = _aspect,
                };
                _presenter.SetPicture(_picture);
                if (_pendingHwnd != IntPtr.Zero)
                    _presenter.AttachWindow(_pendingHwnd, _pendingWidth, _pendingHeight);
                Status?.Invoke(this, "Graphics: " + _presenter.AdapterDetail);
            }
            if (wasStreaming && device is not null && format is not null) StartCore(device, format);
            return true;
        });

    public CaptureEngine(string? adapter = null)
    {
        _adapter = adapter;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "RipsawStudio.Capture",
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        _renderThread = new Thread(RenderThreadMain)
        {
            IsBackground = true,
            Name = "RipsawStudio.Render",
            Priority = ThreadPriority.Highest,
        };
        _renderThread.SetApartmentState(ApartmentState.MTA);
        _renderThread.Start();
        Audio.DataAvailable += OnAudioData;
        Audio.Status += (_, m) => Status?.Invoke(this, m);
        _replay.Status += (_, m) => Status?.Invoke(this, m);
    }

    // ---- public API (callable from the UI thread) --------------------------------------

    public void SetWindow(IntPtr hwnd, int width, int height) =>
        Post(() =>
        {
            _pendingHwnd = hwnd;
            _pendingWidth = width;
            _pendingHeight = height;
            lock (_presenterLock) _presenter?.AttachWindow(hwnd, width, height);
        });

    public void Resize(int width, int height) =>
        Post(() =>
        {
            _pendingWidth = width;
            _pendingHeight = height;
            lock (_presenterLock) _presenter?.Resize(width, height);
        });

    /// <summary>
    /// Enumerated here rather than on the UI thread so every Media Foundation call in the
    /// app happens on one MTA thread, after MFStartup has definitely run.
    /// </summary>
    public Task<List<VideoDeviceInfo>> EnumerateDevicesAsync() =>
        Invoke(DeviceEnumerator.EnumerateVideoDevices);

    public Task<string> DescribeAdapterAsync() =>
        Invoke(() => _presenter?.AdapterDetail ?? "Direct3D failed to start");

    public Task<string> DescribePacingAsync() =>
        Invoke(() => _presenter?.PacingDescription ?? "no renderer");

    public Task<List<VideoFormat>> QueryFormatsAsync(VideoDeviceInfo device) =>
        Invoke(() =>
        {
            OpenSource(device);
            return _source!.EnumerateFormats();
        });

    public Task StartAsync(VideoDeviceInfo device, VideoFormat format) =>
        Invoke(() => { StartCore(device, format); return true; });

    public Task StopAsync() => Invoke(() => { StopCore(); return true; });

    public Task StartRecordingAsync(string path) =>
        Invoke(() =>
        {
            if (!_streaming) throw new InvalidOperationException("Start the preview before recording.");
            if (IsRecording) return true;
            var free = FreeBytes(path);
            if (free >= 0 && free < MinFreeBytes * 2)
                throw new InvalidOperationException($"Only {free / 1024 / 1024} MB free on that drive. Free some space first.");

            _recordBasePath = path;
            _recordPart = 1;
            StartRecorderCore(path);
            _recordStartTicks = _clock.Elapsed.Ticks;
            Status?.Invoke(this, $"Recording with {_recordingEncoder}");
            return true;
        });

    /// <summary>
    /// Stops the take and finalises it in the background. Deliberately not posted to the
    /// capture thread: that thread can be stuck inside ReadSample when the card loses its
    /// signal, and stopping a recording must work regardless.
    /// </summary>
    public async Task<string?> StopRecordingAsync()
    {
        var path = await DetachAndFinish();
        if (path is not null) Status?.Invoke(this, "Saved " + path);
        return path;
    }

    /// <summary>Capture thread only. Builds a recorder for the current stream and makes it live.</summary>
    private void StartRecorderCore(string path)
    {
        var settings = _recordSettings.Clone();
        var plan = BuildPlan(settings);
        var recorder = new Recorder();
        recorder.Start(path, settings, plan, settings.Container, _outputSubtype,
                       _frameWidth, _frameHeight, _fpsNum, _fpsDen, Audio.CaptureFormat);
        _recordingEncoder = recorder.ActiveEncoder is { Length: > 0 } active && active != "unknown"
            ? $"{active} {(plan.Codec == VideoCodec.Hevc ? "HEVC" : "H.264")}"
            : plan.Description;
        if (recorder.AudioProblem is { } problem) Failed?.Invoke(this, "Recording: " + problem);
        lock (_recordLock) _recorder = recorder;
        _lastDiskCheck = 0;
    }

    /// <summary>
    /// Takes the recorder out of the capture loop and finalises it on a pool thread. The
    /// returned task completes once the file is playable. Callable from any thread.
    /// </summary>
    private Task<string?> DetachAndFinish()
    {
        Recorder? recorder;
        lock (_recordLock)
        {
            recorder = _recorder;
            _recorder = null;
        }
        if (recorder is null) return Task.FromResult<string?>(null);

        string? path = recorder.FilePath;
        var task = Task.Run(() =>
        {
            try { recorder.Stop(); }
            catch (Exception ex) { Failed?.Invoke(this, "Finalising the recording failed: " + ex.Message); }
            return path;
        });
        lock (_finalizing)
        {
            _finalizing.RemoveAll(t => t.IsCompleted);
            _finalizing.Add(task);
        }
        return task;
    }

    /// <summary>Stops a take the user did not stop, and tells the UI why.</summary>
    private void EndRecordingUnasked(string message, bool isError)
    {
        if (!IsRecording) return;
        string? path = RecordingPath;
        _ = DetachAndFinish();
        RecordingChanged?.Invoke(this, new RecordingChangedEventArgs
        {
            Recording = false,
            FinishedPath = path,
            Message = message,
            IsError = isError,
        });
    }

    /// <summary>
    /// Works out which encoder to build and on which device. Capture thread only. A GPU
    /// encoder on the preview's own GPU takes the captured surfaces as they are; one on
    /// another GPU gets copies through system memory, since surfaces cannot cross adapters.
    /// </summary>
    private EncoderPlan BuildPlan(RecordSettings settings)
    {
        var previewAdapter = _presenter?.AdapterName;
        var encoder = EncoderCatalog.Resolve(Encoders(), settings.Codec, settings.EncoderId, previewAdapter);
        if (encoder is null)
        {
            // Nothing enumerated at all (a stripped-down Windows, or the enumeration failed):
            // let the sink writer find whatever it can, the way the app always used to.
            return new EncoderPlan(settings.Codec, true, _presenter?.DeviceManager, false, "Automatic");
        }
        if (!encoder.Hardware) return EncoderPlan.Software(settings.Codec);

        if (EncoderCatalog.Same(encoder.AdapterName, previewAdapter) && !_softwareFallback)
            return new EncoderPlan(settings.Codec, true, _presenter?.DeviceManager, false, encoder.Label);

        if (!_encoderDevices.TryGetValue(encoder.AdapterName!, out var device))
        {
            device = new EncoderDevice(encoder.AdapterName!);
            _encoderDevices[encoder.AdapterName!] = device;
        }
        return new EncoderPlan(settings.Codec, true, device.DeviceManager, true, encoder.Label);
    }

    /// <summary>
    /// Writes the last <paramref name="seconds"/> out of the replay buffer. Rolling the open
    /// segment happens on the capture thread; joining the segments does not.
    /// </summary>
    public Task<string> SaveReplayAsync(int seconds, string path)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                _replay.SaveAsync(seconds, path, _clock.Elapsed.Ticks).ContinueWith(task =>
                {
                    if (task.IsFaulted) tcs.TrySetException(task.Exception!.InnerExceptions);
                    else if (task.IsCanceled) tcs.TrySetCanceled();
                    else
                    {
                        tcs.TrySetResult(task.Result);
                        Status?.Invoke(this, "Saved " + task.Result);
                    }
                }, TaskScheduler.Default);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Starts or stops the buffer to match what has been asked for. Capture thread only.</summary>
    private void ApplyReplayState()
    {
        _replay.BufferSeconds = _replayBufferSeconds;
        var audioFormat = Audio.CaptureFormat;

        if (!_replayEnabled || !_streaming)
        {
            if (_replay.IsRunning) _replay.Stop();
            return;
        }

        // Nothing to re-check when audio turns up late - which it does every launch, the
        // preview starting before the audio endpoint. Segments carry picture only, and the
        // sound ring resizes itself around whatever arrives.
        if (_replay.IsRunning) return;

        _replayError = null;
        try
        {
            var settings = _recordSettings.Clone();
            _replay.Start(settings, BuildPlan(settings), _outputSubtype,
                          _frameWidth, _frameHeight, _fpsNum, _fpsDen, audioFormat, _clock.Elapsed.Ticks);
        }
        catch (Exception ex)
        {
            // Remembered as well as announced: a toast is gone in seconds, and otherwise the
            // Record page would sit there saying it was waiting for a preview that is running.
            _replayEnabled = false;
            _replayError = ex.Message;
            Failed?.Invoke(this, "Instant replay could not start: " + ex.Message);
        }
    }

    /// <summary>
    /// Saves the next frame as a PNG. Several requests in a row each get a file; the pixels are
    /// read back on the capture thread, but the PNG is encoded on the pool - encoding a 1080p
    /// frame takes long enough to drop several frames if it ran inline.
    /// </summary>
    public Task<string> ScreenshotAsync(string path)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (!_streaming) { tcs.TrySetException(new InvalidOperationException("No live picture to capture.")); return; }
            _screenshots.Add((path, tcs));
        });
        return tcs.Task;
    }

    /// <summary>
    /// Records per-frame timings for a few seconds and returns a written report.
    /// Averages hide the problems that matter here, which are almost always occasional
    /// long frames rather than a raised mean.
    /// </summary>
    public Task<string> RunTraceAsync(int seconds)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (!_streaming)
            {
                tcs.TrySetException(new InvalidOperationException("Start the preview before tracing."));
                return;
            }
            _trace = new PerfTrace(_clock.Elapsed.Ticks, seconds, _framesDropped, tcs);
        });
        return tcs.Task;
    }

    private void CheckTrace()
    {
        var trace = _trace;
        if (trace is null || !trace.IsFinished(_clock.Elapsed.Ticks)) return;
        _trace = null;
        try
        {
            trace.Completion.TrySetResult(trace.BuildReport(new TraceContext
            {
                Width = _frameWidth,
                Height = _frameHeight,
                Format = _outputSubtype == Guid.Empty ? "" : Mf.DescribeSubtype(_outputSubtype),
                SourceFps = _fpsDen == 0 ? 0 : (double)_fpsNum / _fpsDen,
                Gpu = _presenter?.AdapterDetail ?? "none",
                DisplayHz = _presenter?.DisplayRefreshHz ?? 0,
                VSync = _vsync,
                Pacing = _presenter?.PacingDescription ?? "no renderer",
                SoftwarePath = _softwareFallback,
                DroppedAtEnd = Interlocked.Read(ref _framesDropped),
                AudioBufferedMs = Audio.BufferedMs,
                AudioFormat = Audio.CaptureFormat is { } f ? $"{f.SampleRate} Hz, {f.Channels} ch" : "not running",
                ReplayArmed = _replay.IsRunning,
                Recording = IsRecording,
                MicRunning = Audio.MicRunning,
            }));
        }
        catch (Exception ex)
        {
            trace.Completion.TrySetException(ex);
        }
    }

    /// <summary>Drops any frames sitting in the driver, which resets accumulated delay.</summary>
    public void FlushPipeline() => Post(() => _source?.Flush());

    // ---- engine thread -----------------------------------------------------------------

    private void ThreadMain()
    {
        MfHelpers.Startup();
        try
        {
            var presenter = new Presenter(_adapter) { VSync = _vsync, Scaling = _scaling, Aspect = _aspect };
            if (_pendingHwnd != IntPtr.Zero)
                presenter.AttachWindow(_pendingHwnd, _pendingWidth, _pendingHeight);
            lock (_presenterLock) _presenter = presenter;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, "Direct3D could not start: " + ex.Message);
        }

        long lastStats = 0;
        while (_alive)
        {
            DrainCommands();
            if (!_alive) break;

            if (_streaming)
            {
                PumpOneFrame();
            }
            else
            {
                _wake.Wait(50);
                _wake.Reset();
            }

            long now = _clock.ElapsedMilliseconds;
            if (now - lastStats >= 500)
            {
                lastStats = now;
                CheckDisk(now);
                PublishStats();
                CheckTrace();
            }
        }

        _replay.Dispose();
        // At shutdown the finalise has to happen before the process goes, so it runs inline.
        Recorder? last;
        lock (_recordLock) { last = _recorder; _recorder = null; }
        try { last?.Dispose(); } catch { }
        _source?.Dispose();
        lock (_presenterLock)
        {
            _presenter?.Dispose();
            _presenter = null;
        }
        foreach (var device in _encoderDevices.Values) device.Dispose();
        _encoderDevices.Clear();
        MfHelpers.Shutdown();
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Failed?.Invoke(this, ex.Message); }
        }
    }

    private void PumpOneFrame()
    {
        IMFSample? sample = null;
        try
        {
            sample = _source!.ReadSample(out var flags, out _);

            if (flags.HasFlag(Mf.SourceReaderFlags.EndOfStream))
            {
                Failed?.Invoke(this, "The capture device stopped sending video (unplugged, or claimed by another app).");
                StopCore();
                StreamLost?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (flags.HasFlag(Mf.SourceReaderFlags.CurrentMediaTypeChanged) ||
                flags.HasFlag(Mf.SourceReaderFlags.NativeMediaTypeChanged))
            {
                ApplyFormatChange();
            }
            if (sample is null) return;   // stream tick: no picture this time round
            _lastFrameTicks = _clock.Elapsed.Ticks;

            if (_screenshots.Count > 0) TakeScreenshots(sample);

            long frameDuration = _fpsNum == 0 ? 166_667 : 10_000_000L * _fpsDen / _fpsNum;

            // Two writers cannot share one sample: each stamps its own time on it, and a sink
            // writer may still be reading that time after WriteSample returns. So when both
            // are running the replay gets a clone - buffer references only, no pixels copied.
            // With only one of them running there is nothing to collide with, and the clone
            // is skipped: it is three COM calls and an allocation on the per-frame path.
            if (_replay.IsRunning)
            {
                bool contested = IsRecording;
                IMFSample? clone = null;
                try
                {
                    if (contested) clone = MfHelpers.CloneSample(sample);
                    _replay.WriteVideo(clone ?? sample, _clock.Elapsed.Ticks, frameDuration);
                }
                catch (Exception ex) { Failed?.Invoke(this, "Instant replay: " + ex.Message); }
                finally { MfHelpers.Release(clone); }
            }

            string? writeError = null;
            lock (_recordLock)
            {
                if (_recorder is { IsRecording: true } recorder)
                {
                    try { recorder.WriteVideo(sample, _clock.Elapsed.Ticks, frameDuration); }
                    catch (Exception ex) { writeError = ex.Message; }
                }
            }
            // A failed write does not get better by retrying every frame - a full disk, a
            // pulled USB drive. Close what has been written so far so it stays playable.
            if (writeError is not null) EndRecordingUnasked("Recording stopped: " + writeError, isError: true);

            // Ownership moves to the render thread here, so this thread must not release it
            // and must go straight back to reading. Never blocking on the display is the
            // whole point: a stalled reader means frames pile up in the driver.
            HandOff(sample, _frameWidth, _frameHeight);
            sample = null;
            _fpsWindowFrames++;
            _trace?.AddCapture(_clock.Elapsed.Ticks);
        }
        catch (MfException ex)
        {
            Failed?.Invoke(this, ex.Message);
            StopCore();
            StreamLost?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            long dropped = Interlocked.Increment(ref _framesDropped);
            if (dropped % 60 == 1) Failed?.Invoke(this, ex.Message);
        }
        finally
        {
            MfHelpers.Release(sample);
        }
    }

    private void HandOff(IMFSample sample, int width, int height)
    {
        PendingFrame? displaced;
        var frame = new PendingFrame
        {
            Sample = sample,
            Width = width,
            Height = height,
            ArrivalTicks = _clock.Elapsed.Ticks,
            Generation = _presenterGeneration,
        };
        lock (_slotLock)
        {
            displaced = _pending;
            _pending = frame;
        }
        if (displaced is not null)
        {
            // The render thread never got to it - that is a dropped frame, by design.
            MfHelpers.Release(displaced.Sample);
            Interlocked.Increment(ref _framesDropped);
        }
        _frameReady.Set();
    }

    private PendingFrame? TakePending()
    {
        lock (_slotLock)
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }

    private void DiscardPending()
    {
        var frame = TakePending();
        if (frame is not null) MfHelpers.Release(frame.Sample);
    }

    /// <summary>
    /// Draws and presents, on its own thread. Separating this from capture is what stops the
    /// vsync wait from delaying the next ReadSample - with them on one thread, every refresh
    /// spent waiting was a refresh the card spent queueing frames we would then show late.
    /// </summary>
    private void RenderThreadMain()
    {
        while (_alive)
        {
            if (!_streaming)
            {
                _frameReady.WaitOne(50);
                continue;
            }

            // 1. Get something to show BEFORE waiting on the display. Waiting first and then
            //    finding nothing to draw costs a whole refresh, because the next attempt has
            //    to wait for the following vblank - which halves the rate we present at
            //    whenever capture and display drift out of phase.
            var frame = TakePending();
            if (frame is null)
            {
                _frameReady.WaitOne(20);
                continue;
            }

            // 2. Block until the display can accept a frame - ALWAYS, not only with vsync on.
            //    The frame-latency object is a semaphore that each Present consumes, so
            //    presenting without waiting unbalances it permanently: the queue stays full
            //    and the block moves inside Present, where it costs a refresh of delay
            //    because the frame is then chosen before the block instead of after it.
            //    With vsync off the wait costs nothing, because tearing presents retire at once.
            //    Held under the lock so a GPU switch cannot dispose the presenter mid-wait.
            long waitStart = _clock.Elapsed.Ticks;
            lock (_presenterLock) _presenter?.WaitForDisplay();
            double frameWaitMs = (_clock.Elapsed.Ticks - waitStart) / 10_000.0;
            _waitMsAvg = _waitMsAvg == 0 ? frameWaitMs : _waitMsAvg * 0.9 + frameWaitMs * 0.1;

            // 3. A newer frame may have landed while we waited. Show that one instead:
            //    the frame we were holding is already stale by a refresh.
            var newer = TakePending();
            if (newer is not null)
            {
                MfHelpers.Release(frame.Sample);
                Interlocked.Increment(ref _framesDropped);
                frame = newer;
            }

            try
            {
                // A frame captured before a GPU switch is a surface on the old device.
                if (frame.Generation != _presenterGeneration) continue;

                long t0 = _clock.Elapsed.Ticks;
                lock (_presenterLock) _presenter?.Present(frame.Sample, frame.Width, frame.Height);
                long done = _clock.Elapsed.Ticks;
                double ms = (done - t0) / 10_000.0;
                _presentMsAvg = _presentMsAvg == 0 ? ms : _presentMsAvg * 0.9 + ms * 0.1;
                double lag = (done - frame.ArrivalTicks) / 10_000.0;
                _pipelineMsAvg = _pipelineMsAvg == 0 ? lag : _pipelineMsAvg * 0.9 + lag * 0.1;
                Interlocked.Increment(ref _presentWindowFrames);
                _trace?.AddPresent(done, frameWaitMs, ms, lag, _presenter?.QueuedFrames ?? 0);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(this, "Display: " + ex.Message);
            }
            finally
            {
                MfHelpers.Release(frame.Sample);
            }
        }
        DiscardPending();
    }

    private void ApplyFormatChange()
    {
        var current = _source?.GetCurrentOutput();
        if (current is null) return;
        var (subtype, w, h, num, den) = current.Value;
        bool sameFrames = w == _frameWidth && h == _frameHeight && subtype == _outputSubtype;
        bool sameRate = (ulong)num * _fpsDen == (ulong)_fpsNum * den;
        if (sameFrames && sameRate) return;

        _frameWidth = w;
        _frameHeight = h;
        _fpsNum = num;
        _fpsDen = den;
        _outputSubtype = subtype;
        if (_presenter is not null) _presenter.SoftwareStrideHint = _source?.DefaultStride ?? 0;
        Status?.Invoke(this, $"Input changed to {w}x{h} @ {(double)num / den:0.##} Hz");
        SourceFormatChanged?.Invoke(this, EventArgs.Empty);

        // One file cannot change resolution midway, so the take carries on in a new file
        // rather than just stopping - a console switching mode (docking a Switch, a game
        // going to 4K) used to end the recording while the UI still showed it running.
        if (IsRecording)
        {
            string? finished = RecordingPath;
            _ = DetachAndFinish();
            try
            {
                string next = NextPartPath();
                StartRecorderCore(next);
                RecordingChanged?.Invoke(this, new RecordingChangedEventArgs
                {
                    Recording = true,
                    Path = next,
                    FinishedPath = finished,
                    Message = $"Input changed to {w}x{h}, recording continues in {Path.GetFileName(next)}",
                });
            }
            catch (Exception ex)
            {
                RecordingChanged?.Invoke(this, new RecordingChangedEventArgs
                {
                    Recording = false,
                    FinishedPath = finished,
                    Message = "Recording stopped: the input changed and a new file could not be started (" + ex.Message + ")",
                    IsError = true,
                });
            }
        }

        // Segments of two different sizes cannot be joined, so the buffer starts over.
        if (_replay.IsRunning)
        {
            _replay.Stop();
            Status?.Invoke(this, "Instant replay buffer cleared: the input resolution changed.");
        }
        ApplyReplayState();
    }

    private string NextPartPath()
    {
        _recordPart++;
        string basePath = _recordBasePath ?? Path.Combine(_recordSettings.OutputFolder, "Capture.mp4");
        string dir = Path.GetDirectoryName(basePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(basePath);
        return Path.Combine(dir, $"{name}_part{_recordPart}{Path.GetExtension(basePath)}");
    }

    private void OpenSource(VideoDeviceInfo device)
    {
        StopCore();
        _source?.Dispose();
        _source = null;
        _softwareFallback = false;

        try
        {
            _source = VideoSource.Open(device, _presenter?.DeviceManager);
        }
        catch (MfException)
        {
            // Some drivers refuse the D3D path outright; system memory still works.
            _source = VideoSource.Open(device, null);
            _softwareFallback = true;
        }
        _device = device;
    }

    private void StartCore(VideoDeviceInfo device, VideoFormat format)
    {
        if (_source is null || !ReferenceEquals(_device, device))
            OpenSource(device);

        Guid preferred = _softwareFallback ? Mf.MFVideoFormat_RGB32 : Mf.MFVideoFormat_NV12;
        try
        {
            _source!.SetFormat(format, preferred);
        }
        catch (MfException) when (!_softwareFallback)
        {
            // Retry the whole device without GPU surfaces before giving up on the format.
            _source?.Dispose();
            _source = VideoSource.Open(device, null);
            _softwareFallback = true;
            _source.SetFormat(format, Mf.MFVideoFormat_RGB32);
            Status?.Invoke(this, "GPU path unavailable for this format, using the software path.");
        }

        _formatInUse = format;
        _frameWidth = format.Width;
        _frameHeight = format.Height;
        _fpsNum = format.FpsNumerator;
        _fpsDen = format.FpsDenominator;
        _outputSubtype = _source.OutputSubtype;

        var current = _source.GetCurrentOutput();
        if (current is not null)
        {
            _frameWidth = current.Value.Width;
            _frameHeight = current.Value.Height;
            _outputSubtype = current.Value.Subtype;
        }
        if (_presenter is not null) _presenter.SoftwareStrideHint = _source.DefaultStride;

        Interlocked.Exchange(ref _framesDropped, 0);
        _fpsWindowFrames = 0;
        _fpsWindowStart = _clock.ElapsedMilliseconds;
        _lastFrameTicks = _clock.Elapsed.Ticks;
        _streaming = true;
        ApplyReplayState();
        Status?.Invoke(this, $"Preview on: {_frameWidth}x{_frameHeight} {Mf.DescribeSubtype(_outputSubtype)}" +
                             (_softwareFallback ? " (software)" : " (GPU)"));
    }

    private void StopCore()
    {
        if (!_streaming) return;
        _streaming = false;
        DiscardPending();
        lock (_presenterLock) _presenter?.ReleaseSurfaces();
        _replay.Stop();
        EndRecordingUnasked("Recording stopped: the preview stopped.", isError: false);
        _source?.Flush();
        foreach (var (_, completion) in _screenshots)
            completion.TrySetException(new InvalidOperationException("The preview stopped."));
        _screenshots.Clear();
    }

    private void TakeScreenshots(IMFSample sample)
    {
        var requests = _screenshots.ToArray();
        _screenshots.Clear();
        int width = _frameWidth, height = _frameHeight;
        byte[] pixels;
        int stride;
        try
        {
            if (_presenter is null) throw new InvalidOperationException("The renderer is not ready.");
            lock (_presenterLock) (pixels, stride) = _presenter.ReadFrameBgra(sample, width, height);
        }
        catch (Exception ex)
        {
            foreach (var (_, completion) in requests) completion.TrySetException(ex);
            Failed?.Invoke(this, "Screenshot failed: " + ex.Message);
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (var (path, completion) in requests)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                    var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                                               ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        for (int y = 0; y < height; y++)
                            System.Runtime.InteropServices.Marshal.Copy(pixels, y * stride, data.Scan0 + y * data.Stride, stride);
                    }
                    finally { bitmap.UnlockBits(data); }
                    bitmap.Save(path, ImageFormat.Png);
                    completion.TrySetResult(path);
                    Status?.Invoke(this, "Saved " + path);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                    Failed?.Invoke(this, "Screenshot failed: " + ex.Message);
                }
            }
        });
    }

    private void OnAudioData(object? sender, AudioDataEventArgs e)
    {
        if (_replay.IsRunning)
        {
            try { _replay.WriteAudio(e.Buffer, e.Count, e.Format, _clock.Elapsed.Ticks); }
            catch (Exception ex) { Failed?.Invoke(this, "Instant replay: " + ex.Message); }
        }

        if (!IsRecording) return;
        lock (_recordLock)
        {
            if (_recorder is not { IsRecording: true } recorder) return;
            try { recorder.WriteAudio(e.Buffer, e.Count, e.Format, _clock.Elapsed.Ticks); }
            catch (Exception ex) { Failed?.Invoke(this, "Audio write failed: " + ex.Message); }
        }
    }

    /// <summary>Every couple of seconds while recording: stop cleanly before the disk fills.</summary>
    private void CheckDisk(long nowMs)
    {
        var path = RecordingPath;
        if (path is null) { _freeDiskBytes = -1; return; }
        if (nowMs - _lastDiskCheck < 2000) return;
        _lastDiskCheck = nowMs;
        _freeDiskBytes = FreeBytes(path);
        if (_freeDiskBytes >= 0 && _freeDiskBytes < MinFreeBytes)
            EndRecordingUnasked("Recording stopped: the drive is almost full.", isError: true);
    }

    private static long FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    private void PublishStats()
    {
        long now = _clock.ElapsedMilliseconds;
        long window = now - _fpsWindowStart;
        if (window >= 500)
        {
            _measuredFps = _fpsWindowFrames * 1000.0 / window;
            _measuredPresentFps = Interlocked.Exchange(ref _presentWindowFrames, 0) * 1000.0 / window;
            _fpsWindowFrames = 0;
            _fpsWindowStart = now;
        }

        long recordingBytes = 0;
        if (RecordingPath is { } path)
        {
            try { recordingBytes = new FileInfo(path).Length; } catch { }
        }

        Stats?.Invoke(this, new EngineStats
        {
            CaptureFps = _measuredFps,
            PresentFps = _measuredPresentFps,
            PresentMs = _presentMsAvg,
            VSyncWaitMs = _waitMsAvg,
            PipelineMs = _pipelineMsAvg,
            VSyncOn = _vsync,
            DisplayRefreshHz = _presenter?.DisplayRefreshHz ?? 0,
            QueuedFrames = _presenter?.QueuedFrames ?? 0,
            FramesDropped = Interlocked.Read(ref _framesDropped),
            Width = _frameWidth,
            Height = _frameHeight,
            Format = _outputSubtype == Guid.Empty ? "" : Mf.DescribeSubtype(_outputSubtype),
            Gpu = !_softwareFallback,
            AudioBufferedMs = Audio.BufferedMs,
            AudioPeak = Audio.PeakLevel,
            RecordingElapsed = IsRecording
                ? TimeSpan.FromTicks(_clock.Elapsed.Ticks - _recordStartTicks)
                : TimeSpan.Zero,
            RecordingBytes = recordingBytes,
            FreeDiskBytes = _freeDiskBytes,
            RecordingEncoder = IsRecording ? _recordingEncoder : "",
            ReplayArmed = _replay.IsRunning,
            ReplayBufferedSeconds = Math.Min(_replay.BufferedSeconds, _replayBufferSeconds),
            ReplayError = _replayError ?? _replay.LastError,
            SecondsSinceFrame = _streaming ? (_clock.Elapsed.Ticks - _lastFrameTicks) / 10_000_000.0 : 0,
        });
    }

    // ---- plumbing ----------------------------------------------------------------------

    private void Post(Action action)
    {
        _commands.Enqueue(action);
        _wake.Set();
    }

    private Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        Audio.Dispose();
        _alive = false;
        _wake.Set();
        _frameReady.Set();
        bool renderStopped = _renderThread?.Join(2000) ?? true;
        bool captureStopped = _thread?.Join(5000) ?? true;
        _renderThread = null;
        _thread = null;

        // A take stopped a moment before closing is still being finalised on the pool, and
        // the pool's threads die with the process - which would leave that file unplayable.
        Task[] pending;
        lock (_finalizing) pending = _finalizing.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length > 0) Task.WaitAll(pending, TimeSpan.FromSeconds(15));

        // Only release the wait handles once nothing can still be waiting on them. A thread
        // that missed its deadline would otherwise fault on a disposed handle.
        if (renderStopped && captureStopped)
        {
            _wake.Dispose();
            _frameReady.Dispose();
        }
    }
}
