using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RipsawStudio.Audio;

/// <summary>
/// Converts between whatever WASAPI hands out and plain interleaved floats. Every endpoint
/// in the chain runs at its own bit depth, and mixing is only sane in one of them.
/// </summary>
internal static class PcmConvert
{
    /// <summary>Reads <paramref name="count"/> bytes as interleaved floats. Returns the frame count.</summary>
    public static int ToFloat(byte[] source, int count, WaveFormat format, float[] destination)
    {
        int channels = format.Channels;
        int frameBytes = format.BlockAlign;
        int bytesPerSample = format.BitsPerSample / 8;
        if (frameBytes <= 0 || channels <= 0) return 0;
        int frames = Math.Min(count / frameBytes, destination.Length / channels);

        for (int f = 0; f < frames; f++)
        {
            int frameStart = f * frameBytes;
            for (int ch = 0; ch < channels; ch++)
                destination[f * channels + ch] = ReadSample(source, frameStart + ch * bytesPerSample, format);
        }
        return frames;
    }

    /// <summary>Writes interleaved floats back in the same format they were read in, clamped.</summary>
    public static void FromFloat(float[] source, int samples, WaveFormat format, byte[] destination)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        bool isFloat = IsFloat(format);

        for (int i = 0; i < samples; i++)
        {
            float value = Math.Clamp(source[i], -1f, 1f);
            int at = i * bytesPerSample;
            if (at + bytesPerSample > destination.Length) return;

            if (isFloat && format.BitsPerSample == 32)
            {
                BitConverter.TryWriteBytes(destination.AsSpan(at, 4), value);
            }
            else if (format.BitsPerSample == 16)
            {
                BitConverter.TryWriteBytes(destination.AsSpan(at, 2), (short)(value * 32767f));
            }
            else if (format.BitsPerSample == 32)
            {
                BitConverter.TryWriteBytes(destination.AsSpan(at, 4), (int)(value * 2147483647.0));
            }
            else if (format.BitsPerSample == 24)
            {
                int s = (int)(value * 8388607f);
                destination[at] = (byte)s;
                destination[at + 1] = (byte)(s >> 8);
                destination[at + 2] = (byte)(s >> 16);
            }
        }
    }

    public static float ReadSample(byte[] buffer, int index, WaveFormat format)
    {
        if (index + format.BitsPerSample / 8 > buffer.Length) return 0f;
        if (IsFloat(format) && format.BitsPerSample == 32) return BitConverter.ToSingle(buffer, index);
        return format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer, index) / 32768f,
            32 => BitConverter.ToInt32(buffer, index) / 2147483648f,
            24 => (buffer[index] | (buffer[index + 1] << 8) | ((sbyte)buffer[index + 2] << 16)) / 8388608f,
            8 => (buffer[index] - 128) / 128f,
            _ => 0f,
        };
    }

    /// <summary>
    /// Normalises whatever WASAPI gave us into interleaved 16-bit PCM, folded to
    /// <paramref name="outChannels"/>. Returns the frame count written.
    /// </summary>
    public static int ToPcm16(byte[] source, int count, WaveFormat format, int outChannels, byte[] destination)
    {
        int srcChannels = Math.Max(1, format.Channels);
        int srcFrameBytes = format.BlockAlign;
        int frames = srcFrameBytes > 0 ? count / srcFrameBytes : 0;
        if (frames == 0) return 0;
        frames = Math.Min(frames, destination.Length / (outChannels * 2));
        int bytesPerSample = format.BitsPerSample / 8;

        for (int f = 0; f < frames; f++)
        {
            int frameStart = f * srcFrameBytes;
            for (int ch = 0; ch < outChannels; ch++)
            {
                int srcCh = Math.Min(ch, srcChannels - 1);
                float value = ReadSample(source, frameStart + srcCh * bytesPerSample, format);
                short s = (short)Math.Clamp(value * 32767f, -32768f, 32767f);
                int dst = (f * outChannels + ch) * 2;
                destination[dst] = (byte)(s & 0xFF);
                destination[dst + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        return frames;
    }

    public static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
}

/// <summary>
/// A second capture endpoint - your microphone - resampled to the capture card's own rate
/// and channel count, sitting in a small FIFO that the card's audio callback drains a
/// block at a time. Pulling from a FIFO rather than pushing into a mixer is what keeps the
/// mixed stream on exactly the card's clock, which is the clock the recorder is timestamped
/// against; anything else drifts over a long take.
///
/// Resampling is linear, with a box average when downsampling. It is speech going under a
/// game soundtrack, not a music master, and a linear step costs a few instructions per
/// sample against a filter bank's hundreds - on a path that runs every audio callback.
/// </summary>
internal sealed class MicInput : IDisposable
{
    private readonly object _lock = new();
    private readonly int _channels;
    private readonly int _sampleRate;

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private WaveFormat? _sourceFormat;

    /// <summary>Interleaved floats already at the target rate and channel count.</summary>
    private float[] _ring;
    private int _readIndex;
    private int _fill;
    private float[] _scratch = new float[8192];
    private float[] _resampled = new float[8192];

    private double _resamplePosition;

    private float _peak;
    private long _lastDataTicks;

    public string DeviceId { get; }
    public bool IsRunning { get; private set; }
    public float Gain { get; set; } = 1f;
    public bool Muted { get; set; }

    /// <summary>Reads zero once packets stop, so a stuck meter cannot look like a live mic.</summary>
    public float PeakLevel => !IsRunning || Environment.TickCount64 - _lastDataTicks > 250 ? 0f : _peak;

    /// <summary>Milliseconds of mic audio waiting to be mixed - the mic's share of the delay.</summary>
    public double BufferedMs
    {
        get { lock (_lock) return _fill / (double)_channels / _sampleRate * 1000.0; }
    }

    public event EventHandler<string>? Status;

    /// <summary>
    /// Opens the endpoint. <paramref name="delayMs"/> primes the FIFO with silence, which is
    /// how the mic is pushed later relative to the game sound without a separate delay line.
    /// </summary>
    public MicInput(string deviceId, int sampleRate, int channels, int latencyMs, int delayMs)
    {
        DeviceId = deviceId;
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);

        // Two seconds of headroom, so a stall in the game-audio callback cannot wrap the ring.
        _ring = new float[Math.Max(_sampleRate * _channels * 2, 8192)];

        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId)
            ?? throw new InvalidOperationException("That microphone no longer exists.");

        _capture = new WasapiCapture(_device, true, Math.Max(5, latencyMs / 2));
        _sourceFormat = _capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        int priming = Math.Clamp(delayMs, 0, 1000) * _sampleRate / 1000 * _channels;
        if (priming > 0) WriteRing(new float[priming], priming);

        _capture.StartRecording();
        _lastDataTicks = Environment.TickCount64;
        IsRunning = true;
        Status?.Invoke(this, $"Microphone: {_sourceFormat.SampleRate / 1000.0:0.#} kHz, {_sourceFormat.Channels} ch");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _sourceFormat is null) return;
        _lastDataTicks = Environment.TickCount64;

        var format = _sourceFormat;
        if (format.BlockAlign <= 0) return;
        int srcFrames = e.BytesRecorded / format.BlockAlign;
        if (srcFrames == 0) return;

        // 1. de-interleave into floats at the source rate, folding to the target channel count
        int needed = srcFrames * _channels;
        if (_scratch.Length < needed) _scratch = new float[needed * 2];
        MapChannels(e.Buffer, srcFrames, format, _scratch);
        MeasurePeak(_scratch, needed);

        // 2. rate-convert into the ring
        if (format.SampleRate == _sampleRate) WriteRing(_scratch, needed);
        else Resample(_scratch, srcFrames, format.SampleRate);
    }

    /// <summary>Folds the endpoint's channels onto the card's: mono spreads, many-to-one averages.</summary>
    private void MapChannels(byte[] buffer, int frames, WaveFormat format, float[] destination)
    {
        int srcChannels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;

        for (int f = 0; f < frames; f++)
        {
            int frameStart = f * format.BlockAlign;
            if (_channels == 1 && srcChannels > 1)
            {
                float sum = 0;
                for (int ch = 0; ch < srcChannels; ch++)
                    sum += PcmConvert.ReadSample(buffer, frameStart + ch * bytesPerSample, format);
                destination[f] = sum / srcChannels;
                continue;
            }
            for (int ch = 0; ch < _channels; ch++)
            {
                int srcCh = Math.Min(ch, srcChannels - 1);
                destination[f * _channels + ch] =
                    PcmConvert.ReadSample(buffer, frameStart + srcCh * bytesPerSample, format);
            }
        }
    }

    /// <summary>
    /// Linear interpolation up, box average down.
    ///
    /// The fractional read position carries over between blocks, which is what keeps the mic
    /// on the card's clock rather than drifting a fraction of a sample every packet. It always
    /// lands inside the next block - after consuming up to position p the remainder is
    /// p + step - frames, which is less than one frame for any upward conversion - so no part
    /// of the previous block is ever needed to interpolate the join.
    /// </summary>
    private void Resample(float[] source, int srcFrames, int srcRate)
    {
        double step = (double)srcRate / _sampleRate;
        int capacity = (int)(srcFrames / step + 4) * _channels;
        if (_resampled.Length < capacity) _resampled = new float[capacity * 2];
        int written = 0;

        if (step > 1.0)
        {
            // Downsampling: average each source window, so what is discarded is not aliased in.
            // The window is clipped at the end of the block rather than stopping short of it,
            // so the read position always lands past the block and its phase carries over -
            // resetting it every block would drift against the card's clock.
            int span = (int)Math.Ceiling(step);
            double pos = _resamplePosition;
            for (; pos < srcFrames; pos += step)
            {
                int start = (int)pos;
                int window = Math.Min(span, srcFrames - start);
                for (int ch = 0; ch < _channels; ch++)
                {
                    float sum = 0;
                    for (int k = 0; k < window; k++) sum += source[(start + k) * _channels + ch];
                    _resampled[written++] = sum / window;
                }
            }
            _resamplePosition = Math.Max(0, pos - srcFrames);
        }
        else
        {
            double pos = _resamplePosition;
            for (; pos < srcFrames; pos += step)
            {
                int index = (int)pos;
                float fraction = (float)(pos - index);
                for (int ch = 0; ch < _channels; ch++)
                {
                    float a = source[index * _channels + ch];
                    float b = index + 1 < srcFrames ? source[(index + 1) * _channels + ch] : a;
                    _resampled[written++] = a + (b - a) * fraction;
                }
            }
            _resamplePosition = Math.Max(0, pos - srcFrames);
        }

        if (written > 0) WriteRing(_resampled, written);
    }

    private void WriteRing(float[] source, int count)
    {
        lock (_lock)
        {
            int capacity = _ring.Length;
            if (count >= capacity) return;                 // an absurd block; dropping it beats wrapping
            if (_fill + count > capacity) Drop(_fill + count - capacity);

            int write = (_readIndex + _fill) % capacity;
            int first = Math.Min(count, capacity - write);
            Array.Copy(source, 0, _ring, write, first);
            if (count > first) Array.Copy(source, first, _ring, 0, count - first);
            _fill += count;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the next block, gain applied, silence where
    /// the mic has not caught up. Also trims a backlog: a FIFO that only grows is pure delay,
    /// exactly as on the monitor path.
    /// </summary>
    public void Read(float[] destination, int frames, int maxBufferedMs)
    {
        int wanted = frames * _channels;
        float gain = Muted ? 0f : Gain;

        lock (_lock)
        {
            int allowed = maxBufferedMs * _sampleRate / 1000 * _channels;
            if (allowed > 0 && _fill > allowed + wanted) Drop(_fill - allowed);

            // Copied as at most two runs rather than a modulo per sample: this is the game
            // audio callback, and it runs for every block whether the mic is speaking or not.
            int available = Math.Min(wanted, _fill);
            int first = Math.Min(available, _ring.Length - _readIndex);
            Array.Copy(_ring, _readIndex, destination, 0, first);
            if (available > first) Array.Copy(_ring, 0, destination, first, available - first);
            Array.Clear(destination, available, wanted - available);

            if (gain != 1f)
                for (int i = 0; i < available; i++) destination[i] *= gain;

            _readIndex = (_readIndex + available) % _ring.Length;
            _fill -= available;
        }
    }

    private void Drop(int samples)
    {
        samples = Math.Min(samples, _fill);
        _readIndex = (_readIndex + samples) % _ring.Length;
        _fill -= samples;
    }

    private void MeasurePeak(float[] samples, int count)
    {
        float peak = 0;
        for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
        peak *= Muted ? 0f : Gain;
        _peak = Math.Max(Math.Min(peak, 1f), _peak * 0.75f);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
        if (e.Exception is not null) Status?.Invoke(this, "Microphone stopped: " + e.Exception.Message);
    }

    public void Dispose()
    {
        IsRunning = false;
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        try { _device?.Dispose(); } catch { }
        _device = null;
        lock (_lock) { _fill = 0; _readIndex = 0; }
    }
}
