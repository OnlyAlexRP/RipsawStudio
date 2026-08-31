using NAudio.Wave;
using RipsawStudio.Audio;
using RipsawStudio.Interop;

namespace RipsawStudio.Record;

public sealed class RecordSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RipsawStudio");
    public int VideoBitrateKbps { get; set; } = 25_000;
    public int AudioBitrateKbps { get; set; } = 192;
    /// <summary>Positive values delay the audio relative to the picture.</summary>
    public int AudioOffsetMs { get; set; }
    public bool UseHardwareEncoder { get; set; } = true;
}

/// <summary>
/// Muxes the live capture to MP4 via the Media Foundation sink writer, feeding it the
/// very same GPU samples the preview shows, so recording costs almost no extra work.
/// Single-threaded: only the capture thread may call into it.
/// </summary>
internal sealed class Recorder : IDisposable
{
    private IMFSinkWriter? _writer;
    private uint _videoStream;
    private uint _audioStream;
    private bool _hasAudio;
    private bool _started;

    private int _outChannels;
    private int _outSampleRate;
    private long _audioSamplesWritten;
    private long _audioStartHns;
    private bool _audioAnchored;
    private long _audioOffsetHns;
    private byte[] _audioScratch = Array.Empty<byte>();

    public string? FilePath { get; private set; }
    public bool IsRecording => _started;
    public long FirstFrameHns { get; private set; } = -1;

    public void Start(string path, RecordSettings settings, object? d3dDeviceManager,
                      Guid videoSubtype, int width, int height, uint fpsNum, uint fpsDen,
                      WaveFormat? audioFormat)
    {
        Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FilePath = path;
        _audioOffsetHns = settings.AudioOffsetMs * 10_000L;

        MfHelpers.Check(Mf.MFCreateAttributes(out var attrs, 4), "MFCreateAttributes(sink)");
        try
        {
            MfHelpers.SetU32(attrs, Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, settings.UseHardwareEncoder ? 1u : 0u);
            MfHelpers.SetU32(attrs, Mf.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            MfHelpers.SetU32(attrs, Mf.MF_LOW_LATENCY, 1);
            if (d3dDeviceManager is not null && settings.UseHardwareEncoder)
                MfHelpers.SetUnknown(attrs, Mf.MF_SINK_WRITER_D3D_MANAGER, d3dDeviceManager);

            MfHelpers.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer), "MFCreateSinkWriterFromURL");
        }
        finally { MfHelpers.Release(attrs); }

        try
        {
            ConfigureVideo(settings, videoSubtype, width, height, fpsNum, fpsDen);
            if (audioFormat is not null) ConfigureAudio(settings, audioFormat);
            MfHelpers.Check(_writer!.BeginWriting(), "IMFSinkWriter::BeginWriting");
        }
        catch
        {
            // The sink writer and its half-written file would otherwise stay alive until the
            // next attempt, and that file is unfinalised so it will never play.
            Stop();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            FilePath = null;
            throw;
        }

        _started = true;
        FirstFrameHns = -1;
        _audioSamplesWritten = 0;
        _audioAnchored = false;
    }

    private void ConfigureVideo(RecordSettings settings, Guid subtype, int width, int height, uint fpsNum, uint fpsDen)
    {
        MfHelpers.Check(Mf.MFCreateMediaType(out var outType), "MFCreateMediaType(video out)");
        try
        {
            MfHelpers.SetGuid(outType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            MfHelpers.SetGuid(outType, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264);
            MfHelpers.SetU32(outType, Mf.MF_MT_AVG_BITRATE, (uint)(settings.VideoBitrateKbps * 1000));
            MfHelpers.SetU32(outType, Mf.MF_MT_INTERLACE_MODE, 2 /* progressive */);
            MfHelpers.SetU32(outType, Mf.MF_MT_MPEG2_PROFILE, 100 /* eAVEncH264VProfile_High */);
            MfHelpers.SetU64(outType, Mf.MF_MT_FRAME_SIZE, MfHelpers.Pack((uint)width, (uint)height));
            MfHelpers.SetU64(outType, Mf.MF_MT_FRAME_RATE, MfHelpers.Pack(fpsNum, fpsDen == 0 ? 1 : fpsDen));
            MfHelpers.SetU64(outType, Mf.MF_MT_PIXEL_ASPECT_RATIO, MfHelpers.Pack(1, 1));
            MfHelpers.Check(_writer!.AddStream(outType, out _videoStream), "AddStream(video)");
        }
        finally { MfHelpers.Release(outType); }

        MfHelpers.Check(Mf.MFCreateMediaType(out var inType), "MFCreateMediaType(video in)");
        try
        {
            MfHelpers.SetGuid(inType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            MfHelpers.SetGuid(inType, Mf.MF_MT_SUBTYPE, subtype);
            MfHelpers.SetU32(inType, Mf.MF_MT_INTERLACE_MODE, 2);
            MfHelpers.SetU64(inType, Mf.MF_MT_FRAME_SIZE, MfHelpers.Pack((uint)width, (uint)height));
            MfHelpers.SetU64(inType, Mf.MF_MT_FRAME_RATE, MfHelpers.Pack(fpsNum, fpsDen == 0 ? 1 : fpsDen));
            MfHelpers.SetU64(inType, Mf.MF_MT_PIXEL_ASPECT_RATIO, MfHelpers.Pack(1, 1));
            int hr = _writer.SetInputMediaType(_videoStream, inType, null);
            if (MfHelpers.Failed(hr))
                throw new MfException(hr, $"SetInputMediaType(video, {Mf.DescribeSubtype(subtype)} {width}x{height})");
        }
        finally { MfHelpers.Release(inType); }
    }

    private void ConfigureAudio(RecordSettings settings, WaveFormat format)
    {
        _outChannels = Math.Min(2, Math.Max(1, format.Channels));
        _outSampleRate = format.SampleRate;

        // Shared with the replay join, so there is one description of what an audio stream in
        // one of this app's MP4s looks like. Recording video without sound beats failing the
        // whole take, which is why this does not throw.
        _hasAudio = !MfHelpers.Failed(
            AacStream.Add(_writer!, settings.AudioBitrateKbps, _outSampleRate, _outChannels, out _audioStream));
    }

    public void WriteVideo(IMFSample sample, long timeHns, long durationHns)
    {
        if (!_started || _writer is null) return;
        if (FirstFrameHns < 0) FirstFrameHns = timeHns;
        long t = timeHns - FirstFrameHns;
        sample.SetSampleTime(t);
        sample.SetSampleDuration(durationHns);
        int hr = _writer.WriteSample(_videoStream, sample);
        if (MfHelpers.Failed(hr)) throw new MfException(hr, "WriteSample(video)");
    }

    public void WriteAudio(byte[] buffer, int count, WaveFormat format, long timeHns)
    {
        if (!_started || !_hasAudio || _writer is null || FirstFrameHns < 0) return;

        int maxFrames = format.BlockAlign > 0 ? count / format.BlockAlign : 0;
        int maxBytes = maxFrames * _outChannels * 2;
        if (maxBytes <= 0) return;
        if (_audioScratch.Length < maxBytes) _audioScratch = new byte[Math.Max(maxBytes, 8192)];

        int frames = PcmConvert.ToPcm16(buffer, count, format, _outChannels, _audioScratch);
        if (frames <= 0) return;
        int pcmBytes = frames * _outChannels * 2;

        if (!_audioAnchored)
        {
            _audioStartHns = timeHns - FirstFrameHns + _audioOffsetHns;
            _audioAnchored = true;
            _audioSamplesWritten = 0;
        }

        long sampleClock = _audioStartHns + _audioSamplesWritten * 10_000_000L / _outSampleRate;
        long arrivalClock = timeHns - FirstFrameHns + _audioOffsetHns;
        // The sound card and the system clock run at slightly different rates; re-anchor
        // if they have pulled more than 100 ms apart so a long take cannot drift out of sync.
        if (Math.Abs(sampleClock - arrivalClock) > 1_000_000L)
        {
            _audioStartHns = arrivalClock;
            _audioSamplesWritten = 0;
            sampleClock = arrivalClock;
        }
        if (sampleClock < 0) return;

        MfHelpers.Check(
            AacStream.Write(_writer, _audioStream, _audioScratch, 0, pcmBytes, _outSampleRate, _outChannels, sampleClock),
            "WriteSample(audio)");

        _audioSamplesWritten += frames;
    }

    public void Stop()
    {
        if (_writer is not null)
        {
            if (_started)
            {
                try { _writer.FinalizeWriting(); } catch { }
            }
            MfHelpers.Release(_writer);
            _writer = null;
        }
        _started = false;
        _hasAudio = false;
        FirstFrameHns = -1;
    }

    public void Dispose() => Stop();
}
