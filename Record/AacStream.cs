using System.Runtime.InteropServices;
using RipsawStudio.Interop;

namespace RipsawStudio.Record;

/// <summary>
/// One AAC audio stream on a sink writer, fed 16-bit PCM and encoded by the writer.
///
/// Shared by the recorder and by the replay join so there is exactly one description of what
/// an audio stream in one of this app's MP4s looks like. Two copies of this attribute list
/// would drift, and the way that shows up is a file that plays without sound.
/// </summary>
internal static class AacStream
{
    /// <summary>
    /// Adds an AAC output stream taking 16-bit PCM in. Returns the HRESULT rather than
    /// throwing: a take without sound beats no take, and the caller decides that.
    /// </summary>
    public static int Add(IMFSinkWriter writer, int bitrateKbps, int sampleRate, int channels, out uint stream)
    {
        stream = 0;
        MfHelpers.Check(Mf.MFCreateMediaType(out var outType), "MFCreateMediaType(audio out)");
        try
        {
            MfHelpers.SetGuid(outType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Audio);
            MfHelpers.SetGuid(outType, Mf.MF_MT_SUBTYPE, Mf.MFAudioFormat_AAC);
            MfHelpers.SetU32(outType, Mf.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            MfHelpers.SetU32(outType, Mf.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            MfHelpers.SetU32(outType, Mf.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            MfHelpers.SetU32(outType, Mf.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(bitrateKbps * 1000 / 8));
            MfHelpers.SetU32(outType, Mf.MF_MT_AAC_PAYLOAD_TYPE, 0);
            int hr = writer.AddStream(outType, out stream);
            if (MfHelpers.Failed(hr)) return hr;
        }
        finally { MfHelpers.Release(outType); }

        MfHelpers.Check(Mf.MFCreateMediaType(out var inType), "MFCreateMediaType(audio in)");
        try
        {
            MfHelpers.SetGuid(inType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Audio);
            MfHelpers.SetGuid(inType, Mf.MF_MT_SUBTYPE, Mf.MFAudioFormat_PCM);
            MfHelpers.SetU32(inType, Mf.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            MfHelpers.SetU32(inType, Mf.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
            MfHelpers.SetU32(inType, Mf.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            MfHelpers.SetU32(inType, Mf.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(2 * channels));
            MfHelpers.SetU32(inType, Mf.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(2 * channels * sampleRate));
            return writer.SetInputMediaType(stream, inType, null);
        }
        finally { MfHelpers.Release(inType); }
    }

    /// <summary>Writes one block of interleaved 16-bit PCM at the given time.</summary>
    public static int Write(IMFSinkWriter writer, uint stream, byte[] pcm, int offset, int bytes,
                            int sampleRate, int channels, long timeHns)
    {
        if (bytes <= 0) return 0;
        int frames = bytes / (2 * channels);
        if (frames <= 0) return 0;

        MfHelpers.Check(Mf.MFCreateMemoryBuffer(bytes, out var buffer), "MFCreateMemoryBuffer");
        try
        {
            MfHelpers.Check(buffer.Lock(out IntPtr dest, out _, out _), "IMFMediaBuffer::Lock");
            Marshal.Copy(pcm, offset, dest, bytes);
            buffer.Unlock();
            buffer.SetCurrentLength((uint)bytes);

            MfHelpers.Check(Mf.MFCreateSample(out var sample), "MFCreateSample");
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(timeHns);
                sample.SetSampleDuration(frames * 10_000_000L / sampleRate);
                return writer.WriteSample(stream, sample);
            }
            finally { MfHelpers.Release(sample); }
        }
        finally { MfHelpers.Release(buffer); }
    }
}
