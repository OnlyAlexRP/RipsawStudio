using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RipsawStudio.Audio;

public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Raw PCM as it came off the capture endpoint, for the recorder to mux.</summary>
public sealed class AudioDataEventArgs : EventArgs
{
    public required byte[] Buffer { get; init; }
    public required int Count { get; init; }
    public required WaveFormat Format { get; init; }
}

/// <summary>How the microphone should be captured and mixed. Passed whole so a restart can put it back.</summary>
public sealed record MicOptions(string? DeviceId, bool Enabled, float Volume, bool Muted, bool Monitor, int OffsetMs,
                                bool Ducking = false)
{
    public static readonly MicOptions Off = new(null, false, 1f, false, false, 0, false);
    public bool Wanted => Enabled && !string.IsNullOrEmpty(DeviceId);
}

/// <summary>
/// Pulls audio off the capture card and plays it back on a chosen output device.
/// Buffering is kept explicitly small and trimmed whenever it drifts, because the
/// usual cause of "audio is behind the picture" is an output buffer that only ever grows.
/// </summary>
public sealed class AudioMonitor : IDisposable
{
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private MMDevice? _inDevice;
    private MMDevice? _outDevice;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProviderEx? _volume;
    private System.Threading.Timer? _watchdog;
    private MicInput? _mic;

    // Scratch for the mix, grown once rather than allocated in the audio callback.
    private float[] _mixFloat = Array.Empty<float>();
    private float[] _micFloat = Array.Empty<float>();
    private byte[] _mixBytes = Array.Empty<byte>();
    private byte[] _trimScratch = Array.Empty<byte>();
    private float[] _gainFloat = Array.Empty<float>();
    private byte[] _gainBytes = Array.Empty<byte>();

    // ---- mic ducking --------------------------------------------------------------------
    // Speech clears the game soundtrack; anything quieter (room tone, the mic's own hiss)
    // should not. -12 dB is squarely in Discord's own range for this.
    private const float DuckThreshold = 0.035f;
    private const float DuckLevel = 0.25f;
    /// <summary>Keeps the duck through the small gaps between words, so a sentence does not
    /// pump the game volume open and shut between every syllable.</summary>
    private const float DuckHoldMs = 300f;
    /// <summary>How long the fade itself takes, either way - a hard cut is what this is for.</summary>
    private const float DuckFadeMs = 20f;
    private float _duckGain = 1f;
    private float _duckHoldRemainingMs;

    private long _lastDataTicks;
    private long _startedTicks;
    private float _peak;
    private bool _restarting;

    public bool IsRunning { get; private set; }
    public string? InputDeviceId { get; private set; }
    public string? OutputDeviceId { get; private set; }
    public WaveFormat? CaptureFormat { get; private set; }
    public int TargetLatencyMs { get; private set; } = 40;
    public bool Passthrough { get; private set; } = true;
    /// <summary>Remembered so an automatic restart puts the endpoint back the way it was.</summary>
    public bool ExclusiveOutput { get; private set; }
    /// <summary>Remembered for the same reason - the watchdog restarts the mic with the rest.</summary>
    public MicOptions Mic { get; private set; } = MicOptions.Off;

    /// <summary>True once a microphone is actually open and delivering packets.</summary>
    public bool MicRunning => _mic?.IsRunning == true;

    /// <summary>Mic peak level, 0..1, for its own meter. Zero when no mic is running.</summary>
    public float MicPeakLevel => _mic?.PeakLevel ?? 0f;

    /// <summary>Mic gain, changed live without restarting anything.</summary>
    public float MicVolume
    {
        get => Mic.Volume;
        set
        {
            float gain = Math.Clamp(value, 0f, 4f);
            Mic = Mic with { Volume = gain };
            if (_mic is not null) _mic.Gain = gain;
        }
    }

    /// <summary>Mutes the mic in the recording as well as in the monitor - it is one stream.</summary>
    public bool MicMuted
    {
        get => Mic.Muted;
        set
        {
            Mic = Mic with { Muted = value };
            if (_mic is not null) _mic.Muted = value;
        }
    }

    /// <summary>
    /// Ducks the game audio while the mic hears you talking - changed live, without
    /// restarting anything, same as <see cref="MicVolume"/>. Turning it off snaps the duck
    /// back open at once rather than leaving it faded down until the next block happens to
    /// notice, in case it was switched off mid-sentence.
    /// </summary>
    public bool MicDucking
    {
        get => Mic.Ducking;
        set
        {
            Mic = Mic with { Ducking = value };
            if (!value) { _duckGain = 1f; _duckHoldRemainingMs = 0f; }
        }
    }

    /// <summary>
    /// Restart the endpoint every N minutes even when it looks healthy. 0 disables it.
    /// Some cards degrade slowly rather than stopping outright, which the silence watchdog
    /// cannot see; a scheduled restart is the blunt fix for that.
    /// </summary>
    public int RestartIntervalMinutes { get; set; }

    /// <summary>
    /// Peak level of the last block, 0..1, for the UI meter. Reads zero once packets stop,
    /// so a frozen meter cannot be mistaken for a live signal.
    /// </summary>
    public float PeakLevel => Environment.TickCount64 - _lastDataTicks > 250 ? 0f : _peak;

    /// <summary>Milliseconds currently sitting in the playback buffer - the audio-side latency.</summary>
    public double BufferedMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;

    public event EventHandler<AudioDataEventArgs>? DataAvailable;
    public event EventHandler<string>? Status;

    private float _gain = 1f;
    /// <summary>
    /// Shapes the signal itself now, not just what plays back locally through passthrough -
    /// applied once in <see cref="OnDataAvailable"/>, upstream of both the monitor and the
    /// mix that goes to the recorder and the replay buffer, so a source that is genuinely too
    /// hot actually comes down in what gets saved, not only in what you hear back. The
    /// monitor's own <see cref="VolumeSampleProviderEx"/> no longer carries a second copy of
    /// this gain - see the constant 1f in <see cref="StartCore"/> and below - since applying
    /// it twice would make the monitor quieter than the take for the same slider position.
    /// </summary>
    public float Volume
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 4f);
    }

    private bool _muted;
    /// <summary>Silences your own ears only - see <see cref="Volume"/> - so muting the monitor never touches the recording.</summary>
    public bool Muted
    {
        get => _muted;
        set { _muted = value; if (_volume is not null) _volume.Gain = value ? 0f : 1f; }
    }

    public static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try { list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName)); }
            finally { device.Dispose(); }
        }
        return list;
    }

    /// <summary>Picks the endpoint that looks like it belongs to the chosen capture card.</summary>
    public static AudioDeviceInfo? GuessCaptureEndpoint(IEnumerable<AudioDeviceInfo> devices, string videoDeviceName)
    {
        var words = videoDeviceName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                   .Where(w => w.Length > 3)
                                   .ToArray();
        return devices.FirstOrDefault(d => words.Any(w => d.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    public void Start(string inputDeviceId, string? outputDeviceId, int targetLatencyMs, bool passthrough,
                      bool exclusiveOutput, MicOptions? mic = null)
    {
        lock (_lock)
        {
            StopCore();
            try
            {
                StartCore(inputDeviceId, outputDeviceId, targetLatencyMs, passthrough, exclusiveOutput,
                          mic ?? MicOptions.Off);
            }
            catch
            {
                // Half-built graphs hold on to an endpoint and a capture client until the
                // next attempt, which may never come. Tear it down here instead - format
                // included, since nothing is going to set it now and anything that reads it
                // would otherwise record a silent audio track against a dead endpoint.
                StopCore(clearFormat: true);
                throw;
            }
        }
    }

    private void StartCore(string inputDeviceId, string? outputDeviceId, int targetLatencyMs, bool passthrough,
                           bool exclusiveOutput, MicOptions mic)
    {
        InputDeviceId = inputDeviceId;
        OutputDeviceId = outputDeviceId;
        TargetLatencyMs = Math.Clamp(targetLatencyMs, 10, 500);
        Passthrough = passthrough;
        ExclusiveOutput = exclusiveOutput;
        Mic = mic;

        using var enumerator = new MMDeviceEnumerator();
        // WasapiCapture/WasapiOut do not take ownership of the MMDevice, so these are
        // tracked and released on Stop. Without that, every restart leaked an endpoint.
        var inDevice = enumerator.GetDevice(inputDeviceId)
            ?? throw new InvalidOperationException("That audio input no longer exists.");
        _inDevice = inDevice;

        // A short capture buffer is what keeps the delay down; WASAPI still gives us
        // whole packets, so this is a ceiling rather than a per-callback size.
        _capture = new WasapiCapture(inDevice, true, Math.Max(5, TargetLatencyMs / 2));
        CaptureFormat = _capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        if (passthrough && !string.IsNullOrEmpty(outputDeviceId))
        {
            var outDevice = enumerator.GetDevice(outputDeviceId)
                ?? throw new InvalidOperationException("That audio output no longer exists.");
            _outDevice = outDevice;
            _buffer = new BufferedWaveProvider(CaptureFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
                BufferDuration = TimeSpan.FromMilliseconds(Math.Max(200, TargetLatencyMs * 6)),
            };
            _volume = new VolumeSampleProviderEx(_buffer.ToSampleProvider()) { Gain = _muted ? 0f : 1f };
            var mode = exclusiveOutput ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
            _output = new WasapiOut(outDevice, mode, true, TargetLatencyMs);
            _output.Init(_volume);
            _output.Play();
        }

        StartMic(mic);

        _lastDataTicks = Environment.TickCount64;
        _startedTicks = _lastDataTicks;
        _capture.StartRecording();
        IsRunning = true;

        _watchdog = new System.Threading.Timer(CheckAlive, null, 2000, 2000);
        Status?.Invoke(this, $"Audio: {CaptureFormat.SampleRate / 1000.0:0.#} kHz, {CaptureFormat.Channels} ch");
    }

    /// <summary>
    /// Opens the mic at the card's own rate and channel count, so mixing is an addition and
    /// nothing downstream - not the recorder, not the monitor - has to know a mic exists.
    /// A mic that will not open must not take the game sound down with it.
    /// </summary>
    private void StartMic(MicOptions mic)
    {
        if (!mic.Wanted || CaptureFormat is null) return;
        try
        {
            _mic = new MicInput(mic.DeviceId!, CaptureFormat.SampleRate, CaptureFormat.Channels,
                                TargetLatencyMs, mic.OffsetMs)
            {
                Gain = Math.Clamp(mic.Volume, 0f, 4f),
                Muted = mic.Muted,
            };
            _mic.Status += (_, m) => Status?.Invoke(this, m);
        }
        catch (Exception ex)
        {
            _mic = null;
            Status?.Invoke(this, "Microphone: " + ex.Message);
        }
    }

    /// <summary>
    /// Adds the mic into a copy of the card's block, in the card's own format, and returns
    /// that copy. The original is left untouched so the monitor can still be given the game
    /// sound alone - hearing yourself through any delay is unpleasant.
    /// </summary>
    private byte[] MixMic(MicInput mic, byte[] source, int count, WaveFormat format)
    {
        int channels = format.Channels;
        int frames = format.BlockAlign > 0 ? count / format.BlockAlign : 0;
        int samples = frames * channels;
        if (samples == 0) return source;

        // The mix is written back sample by sample, which only lands in the right places if
        // the frames are packed with no padding. Nothing WASAPI hands out is padded, but a
        // silently misplaced mix would be far worse than no mix.
        if (format.BlockAlign != channels * (format.BitsPerSample / 8)) return source;

        if (_mixFloat.Length < samples) _mixFloat = new float[samples * 2];
        if (_micFloat.Length < samples) _micFloat = new float[samples * 2];
        if (_mixBytes.Length < count) _mixBytes = new byte[count * 2];

        PcmConvert.ToFloat(source, count, format, _mixFloat);
        // The allowance has to clear the mic's own delay, which is held as primed silence in
        // the same FIFO - trimming to the monitor's figure alone would eat the delay whole.
        mic.Read(_micFloat, frames, Mic.OffsetMs + TargetLatencyMs * 3);

        if (Mic.Ducking)
        {
            float micLevel = 0f;
            for (int i = 0; i < samples; i++) micLevel = Math.Max(micLevel, Math.Abs(_micFloat[i]));
            ApplyDucking(_mixFloat, frames, channels, format.SampleRate, micLevel);
        }
        else if (_duckGain != 1f)
        {
            // Switched off mid-fade - snap back rather than leave the game quiet forever.
            _duckGain = 1f;
            _duckHoldRemainingMs = 0f;
        }

        for (int i = 0; i < samples; i++) _mixFloat[i] += _micFloat[i];
        PcmConvert.FromFloat(_mixFloat, samples, format, _mixBytes);
        return _mixBytes;
    }

    /// <summary>
    /// Ducks the game track in <paramref name="mix"/> while <paramref name="micLevel"/> says
    /// you are talking, and eases it back once you stop - the same idea as Discord's
    /// voice-activated ducking. Detection runs once per block, but the gain itself is stepped
    /// frame by frame at a fixed rate (<see cref="DuckFadeMs"/> from fully open to fully
    /// ducked), so the transition is a fade rather than a jump even when a block is longer
    /// than the fade - and it keeps fading smoothly across a block boundary rather than
    /// snapping to a new level at the start of the next one.
    /// </summary>
    private void ApplyDucking(float[] mix, int frames, int channels, int sampleRate, float micLevel)
    {
        bool talking = micLevel >= DuckThreshold;
        if (talking) _duckHoldRemainingMs = DuckHoldMs;
        float target = _duckHoldRemainingMs > 0f ? DuckLevel : 1f;
        if (!talking) _duckHoldRemainingMs = Math.Max(0f, _duckHoldRemainingMs - frames * 1000f / sampleRate);

        float step = 1000f / sampleRate / DuckFadeMs;
        for (int f = 0; f < frames; f++)
        {
            _duckGain = _duckGain < target
                ? Math.Min(target, _duckGain + step)
                : Math.Max(target, _duckGain - step);
            if (_duckGain == 1f) continue;
            int at = f * channels;
            for (int ch = 0; ch < channels; ch++) mix[at + ch] *= _duckGain;
        }
    }

    /// <summary>
    /// Scales the card's own block by <see cref="Volume"/>, in place in a scratch buffer, so a
    /// source that comes in too hot can actually be brought down - not just in the monitor,
    /// in the recording and the replay buffer too. Runs before the mic is mixed in, so it
    /// never touches the mic's own level, which has its own separate gain.
    /// </summary>
    private byte[] ApplyGain(byte[] source, int count, WaveFormat format, float gain)
    {
        int channels = format.Channels;
        int frames = format.BlockAlign > 0 ? count / format.BlockAlign : 0;
        int samples = frames * channels;
        if (samples == 0) return source;
        if (format.BlockAlign != channels * (format.BitsPerSample / 8)) return source;

        if (_gainFloat.Length < samples) _gainFloat = new float[samples * 2];
        if (_gainBytes.Length < count) _gainBytes = new byte[count * 2];

        PcmConvert.ToFloat(source, count, format, _gainFloat);
        for (int i = 0; i < samples; i++) _gainFloat[i] *= gain;
        PcmConvert.FromFloat(_gainFloat, samples, format, _gainBytes);
        return _gainBytes;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        _lastDataTicks = Environment.TickCount64;

        var format = CaptureFormat;
        if (format is not null) MeasurePeak(e.Buffer, e.BytesRecorded, format);

        // Volume is applied here so it shapes everything downstream alike - the monitor, the
        // recorder and the replay buffer - rather than only the local playback. A gain of
        // exactly 1 skips the pass entirely, which is the common case.
        byte[] source = e.Buffer;
        if (format is not null && _gain != 1f)
            source = ApplyGain(e.Buffer, e.BytesRecorded, format, _gain);

        // The recording always gets the mix; the monitor only does if you asked to hear yourself.
        byte[] recorded = source;
        var mic = _mic;
        if (mic is not null && format is not null && mic.IsRunning)
            recorded = MixMic(mic, source, e.BytesRecorded, format);
        else if (_duckGain != 1f) { _duckGain = 1f; _duckHoldRemainingMs = 0f; }
        byte[] monitored = Mic.Monitor ? recorded : source;

        var buffer = _buffer;
        if (buffer is not null)
        {
            // Trim rather than let the queue grow: a persistent backlog is pure added delay.
            double allowed = TargetLatencyMs * 2.0;
            if (buffer.BufferedDuration.TotalMilliseconds > allowed)
            {
                int excess = (int)(format!.AverageBytesPerSecond * (buffer.BufferedDuration.TotalMilliseconds - TargetLatencyMs) / 1000.0);
                excess -= excess % format.BlockAlign;
                if (excess > 0)
                {
                    // Reused rather than allocated: trimming happens on the audio callback,
                    // and a fresh array every time it runs is garbage on the one path where
                    // a collection is most likely to be heard.
                    if (_trimScratch.Length < excess) _trimScratch = new byte[excess * 2];
                    buffer.Read(_trimScratch, 0, excess);
                }
            }
            buffer.AddSamples(monitored, 0, e.BytesRecorded);
        }

        var handler = DataAvailable;
        if (handler is not null && format is not null)
            handler(this, new AudioDataEventArgs { Buffer = recorded, Count = e.BytesRecorded, Format = format });
    }

    private void MeasurePeak(byte[] buffer, int count, WaveFormat format)
    {
        float peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 3 < count; i += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < count; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i)) / 32768f);
        }
        // Decay slowly so the meter is readable rather than flickering.
        _peak = Math.Max(peak, _peak * 0.75f);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Status?.Invoke(this, "Audio capture stopped: " + e.Exception.Message);
    }

    /// <summary>
    /// Capture cards routinely stop delivering packets after an HDMI resync without
    /// raising any error. Restarting the endpoint is what actually fixes it.
    /// </summary>
    private void CheckAlive(object? _)
    {
        if (!IsRunning || _restarting) return;

        long silentMs = Environment.TickCount64 - _lastDataTicks;
        bool silent = silentMs >= 3000;
        bool scheduled = RestartIntervalMinutes > 0 &&
                         Environment.TickCount64 - _startedTicks >= RestartIntervalMinutes * 60_000L;
        if (!silent && !scheduled) return;

        _restarting = true;
        try
        {
            Status?.Invoke(this, silent
                ? $"No audio for {silentMs / 1000}s - restarting the audio device..."
                : "Scheduled audio restart");
            var input = InputDeviceId;
            var output = OutputDeviceId;
            if (input is not null)
                Start(input, output, TargetLatencyMs, Passthrough, ExclusiveOutput, Mic);
        }
        catch (Exception ex)
        {
            Status?.Invoke(this, "Audio restart failed: " + ex.Message);
        }
        finally { _restarting = false; }
    }

    public void Stop()
    {
        lock (_lock) StopCore(clearFormat: true);
    }

    /// <param name="clearFormat">
    /// False while restarting, where the format is about to be set again: nulling it would
    /// open a window in which a recording could start and decide there was no audio at all.
    /// </param>
    private void StopCore(bool clearFormat = false)
    {
        IsRunning = false;
        _watchdog?.Dispose();
        _watchdog = null;
        _mic?.Dispose();
        _mic = null;
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        if (_output is not null)
        {
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            _output = null;
        }
        // Released after the clients that were using them.
        try { _inDevice?.Dispose(); } catch { }
        try { _outDevice?.Dispose(); } catch { }
        _inDevice = null;
        _outDevice = null;

        _buffer = null;
        _volume = null;
        _peak = 0;
        _duckGain = 1f;
        _duckHoldRemainingMs = 0f;
        if (clearFormat) CaptureFormat = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Gain stage kept separate so volume changes never restart the audio graph.</summary>
internal sealed class VolumeSampleProviderEx : ISampleProvider
{
    private readonly ISampleProvider _source;
    public float Gain { get; set; } = 1f;

    public VolumeSampleProviderEx(ISampleProvider source) => _source = source;
    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float gain = Gain;
        if (gain != 1f)
            for (int i = 0; i < read; i++) buffer[offset + i] *= gain;
        return read;
    }
}
