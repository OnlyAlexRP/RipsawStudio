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

    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    /// <summary>"auto", "sw", or "hw:" + a GPU's name. See <see cref="EncoderInfo.Id"/>.</summary>
    public string EncoderId { get; set; } = "auto";
    public ContainerFormat Container { get; set; } = ContainerFormat.FragmentedMp4;
    public RateControl RateControl { get; set; } = RateControl.Cbr;
    /// <summary>0-100, used when <see cref="RateControl"/> is Quality.</summary>
    public int Quality { get; set; } = 70;
    public int KeyframeSeconds { get; set; } = 2;
    /// <summary>
    /// The source sends full-range (0-255) video. Tagged into the file so players do not
    /// squash it again - the picture settings only reach the preview, not the encoder.
    /// </summary>
    public bool FullRangeSource { get; set; }

    public RecordSettings Clone() => (RecordSettings)MemberwiseClone();
}

/// <summary>
/// Muxes the live capture to MP4 via the Media Foundation sink writer, feeding it the
/// very same GPU samples the preview shows, so recording costs almost no extra work.
/// Writes come from one thread at a time (the caller locks); <see cref="Stop"/> may run on
/// any MTA thread once the recorder has been detached from the capture loop.
/// </summary>
internal sealed class Recorder : IDisposable
{
    private IMFSinkWriter? _writer;
    private uint _videoStream;
    private uint _audioStream;
    private bool _hasAudio;
    private bool _started;
    private bool _copyFrames;

    private int _outChannels;
    private int _outSampleRate;
    private long _audioSamplesWritten;
    private long _audioStartHns;
    private bool _audioAnchored;
    private long _audioOffsetHns;
    private byte[] _audioScratch = Array.Empty<byte>();
    private PcmResampler? _resampler;

    public string? FilePath { get; private set; }
    public bool IsRecording => _started;
    public long FirstFrameHns { get; private set; } = -1;
    /// <summary>The encoder the sink writer actually built, e.g. "NVIDIA NVENC" or "software".</summary>
    public string ActiveEncoder { get; private set; } = "";
    /// <summary>Set when the take has no sound track although sound was asked for.</summary>
    public string? AudioProblem { get; private set; }

    public void Start(string path, RecordSettings settings, EncoderPlan plan, ContainerFormat container,
                      Guid videoSubtype, int width, int height, uint fpsNum, uint fpsDen,
                      WaveFormat? audioFormat)
    {
        Stop();
        // Windows' fragmented-MP4 sink refuses HEVC outright (measured: MF_E_INVALIDMEDIATYPE at
        // BeginWriting, on NVENC and Quick Sync alike), so HEVC always goes in a plain MP4.
        if (plan.Codec == VideoCodec.Hevc) container = ContainerFormat.Mp4;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FilePath = path;
        _audioOffsetHns = settings.AudioOffsetMs * 10_000L;
        _copyFrames = plan.CopyToSystemMemory;
        AudioProblem = null;

        MfHelpers.Check(Mf.MFCreateAttributes(out var attrs, 5), "MFCreateAttributes(sink)");
        try
        {
            MfHelpers.SetU32(attrs, Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, plan.Hardware ? 1u : 0u);
            MfHelpers.SetU32(attrs, Mf.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            // Keeps a hardware encoder from holding on to a queue of input frames. Those are the
            // capture card's own surfaces, and a deep encoder queue starves the source reader.
            MfHelpers.SetU32(attrs, Mf.MF_LOW_LATENCY, 1);
            if (plan.DeviceManager is not null && plan.Hardware)
                MfHelpers.SetUnknown(attrs, Mf.MF_SINK_WRITER_D3D_MANAGER, plan.DeviceManager);
            MfHelpers.SetGuid(attrs, Mf.MF_TRANSCODE_CONTAINERTYPE, container == ContainerFormat.FragmentedMp4
                ? Mf.MFTranscodeContainerType_FMPEG4 : Mf.MFTranscodeContainerType_MPEG4);

            MfHelpers.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer), "MFCreateSinkWriterFromURL");
        }
        finally { MfHelpers.Release(attrs); }

        try
        {
            ConfigureVideo(settings, plan.Codec, videoSubtype, width, height, fpsNum, fpsDen);
            if (audioFormat is not null) ConfigureAudio(settings, audioFormat);
            MfHelpers.Check(_writer!.BeginWriting(), "IMFSinkWriter::BeginWriting");
            ActiveEncoder = EncoderCatalog.DescribeActive(_writer, _videoStream);
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

    private void ConfigureVideo(RecordSettings settings, VideoCodec codec, Guid subtype, int width, int height, uint fpsNum, uint fpsDen)
    {
        fpsDen = fpsDen == 0 ? 1 : fpsDen;
        uint range = settings.FullRangeSource ? 1u /* MFNominalRange_0_255 */ : 2u /* 16_235 */;

        MfHelpers.Check(Mf.MFCreateMediaType(out var outType), "MFCreateMediaType(video out)");
        try
        {
            MfHelpers.SetGuid(outType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            MfHelpers.SetGuid(outType, Mf.MF_MT_SUBTYPE, codec == VideoCodec.Hevc ? Mf.MFVideoFormat_HEVC : Mf.MFVideoFormat_H264);
            MfHelpers.SetU32(outType, Mf.MF_MT_AVG_BITRATE, (uint)(settings.VideoBitrateKbps * 1000));
            MfHelpers.SetU32(outType, Mf.MF_MT_INTERLACE_MODE, 2 /* progressive */);
            MfHelpers.SetU32(outType, Mf.MF_MT_MPEG2_PROFILE,
                codec == VideoCodec.Hevc ? Mf.eAVEncH265VProfile_Main_420_8 : Mf.eAVEncH264VProfile_High);
            MfHelpers.SetU64(outType, Mf.MF_MT_FRAME_SIZE, MfHelpers.Pack((uint)width, (uint)height));
            MfHelpers.SetU64(outType, Mf.MF_MT_FRAME_RATE, MfHelpers.Pack(fpsNum, fpsDen));
            MfHelpers.SetU64(outType, Mf.MF_MT_PIXEL_ASPECT_RATIO, MfHelpers.Pack(1, 1));
            MfHelpers.SetU32(outType, Mf.MF_MT_VIDEO_NOMINAL_RANGE, range);
            MfHelpers.Check(_writer!.AddStream(outType, out _videoStream), "AddStream(video)");
        }
        finally { MfHelpers.Release(outType); }

        MfHelpers.Check(Mf.MFCreateMediaType(out var inType), "MFCreateMediaType(video in)");
        IMFAttributes? encoding = null;
        try
        {
            MfHelpers.SetGuid(inType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            MfHelpers.SetGuid(inType, Mf.MF_MT_SUBTYPE, subtype);
            MfHelpers.SetU32(inType, Mf.MF_MT_INTERLACE_MODE, 2);
            MfHelpers.SetU64(inType, Mf.MF_MT_FRAME_SIZE, MfHelpers.Pack((uint)width, (uint)height));
            MfHelpers.SetU64(inType, Mf.MF_MT_FRAME_RATE, MfHelpers.Pack(fpsNum, fpsDen));
            MfHelpers.SetU64(inType, Mf.MF_MT_PIXEL_ASPECT_RATIO, MfHelpers.Pack(1, 1));
            MfHelpers.SetU32(inType, Mf.MF_MT_VIDEO_NOMINAL_RANGE, range);

            encoding = EncodingParameters(settings, fpsNum, fpsDen);
            int hr = _writer.SetInputMediaType(_videoStream, inType, encoding);
            // An encoder that rejects one of the settings still records fine on its defaults,
            // which beats no recording at all.
            if (MfHelpers.Failed(hr) && encoding is not null)
                hr = _writer.SetInputMediaType(_videoStream, inType, null);
            if (MfHelpers.Failed(hr))
                throw new MfException(hr, $"SetInputMediaType(video, {Mf.DescribeSubtype(subtype)} {width}x{height})");
        }
        finally
        {
            MfHelpers.Release(inType);
            MfHelpers.Release(encoding);
        }
    }

    private static IMFAttributes EncodingParameters(RecordSettings settings, uint fpsNum, uint fpsDen)
    {
        MfHelpers.Check(Mf.MFCreateAttributes(out var attrs, 5), "MFCreateAttributes(encoding)");
        uint bitrate = (uint)(settings.VideoBitrateKbps * 1000);
        switch (settings.RateControl)
        {
            case RateControl.Quality:
                MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncCommonRateControlMode, Mf.eAVEncCommonRateControlMode_Quality);
                MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncCommonQuality, (uint)Math.Clamp(settings.Quality, 1, 100));
                break;
            case RateControl.Vbr:
                MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncCommonRateControlMode, Mf.eAVEncCommonRateControlMode_PeakConstrainedVBR);
                MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncCommonMeanBitRate, bitrate);
                MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncCommonMaxBitRate, bitrate + bitrate / 2);
                break;
            default:
                MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncCommonRateControlMode, Mf.eAVEncCommonRateControlMode_CBR);
                MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncCommonMeanBitRate, bitrate);
                break;
        }
        double fps = (double)fpsNum / Math.Max(1u, fpsDen);
        uint gop = (uint)Math.Max(1, Math.Round(fps * Math.Clamp(settings.KeyframeSeconds, 1, 10)));
        MfHelpers.SetU32(attrs, Mf.CODECAPI_AVEncMPVGOPSize, gop);
        return attrs;
    }

    private void ConfigureAudio(RecordSettings settings, WaveFormat format)
    {
        _outChannels = Math.Min(2, Math.Max(1, format.Channels));
        // The AAC encoder only takes 44.1 or 48 kHz; anything else is resampled on the way in.
        _outSampleRate = PcmResampler.AacRate(format.SampleRate);
        _resampler = new PcmResampler(_outChannels);

        // Shared with the replay join, so there is one description of what an audio stream in
        // one of this app's MP4s looks like. Recording video without sound beats failing the
        // whole take, which is why this does not throw - but it does say so.
        int hr = AacStream.Add(_writer!, settings.AudioBitrateKbps, _outSampleRate, _outChannels, out _audioStream);
        _hasAudio = !MfHelpers.Failed(hr);
        if (!_hasAudio) AudioProblem = $"the sound could not be encoded (0x{hr:X8}), this take is silent";
    }

    public void WriteVideo(IMFSample sample, long timeHns, long durationHns)
    {
        if (!_started || _writer is null) return;
        if (FirstFrameHns < 0) FirstFrameHns = timeHns;
        long t = timeHns - FirstFrameHns;

        IMFSample? copy = _copyFrames ? MfHelpers.CopyToMemorySample(sample) : null;
        try
        {
            var target = copy ?? sample;
            target.SetSampleTime(t);
            target.SetSampleDuration(durationHns);
            int hr = _writer.WriteSample(_videoStream, target);
            if (MfHelpers.Failed(hr)) throw new MfException(hr, "WriteSample(video)");
        }
        finally { MfHelpers.Release(copy); }
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
        var (pcm, outFrames) = _resampler!.Process(_audioScratch, frames, format.SampleRate, _outSampleRate);
        if (outFrames <= 0) return;
        int pcmBytes = outFrames * _outChannels * 2;

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
            AacStream.Write(_writer, _audioStream, pcm, 0, pcmBytes, _outSampleRate, _outChannels, sampleClock),
            "WriteSample(audio)");

        _audioSamplesWritten += outFrames;
    }

    /// <summary>Finalises the file. Can take a few hundred ms, so callers keep it off the capture thread.</summary>
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
