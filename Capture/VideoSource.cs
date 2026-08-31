using RipsawStudio.Interop;

namespace RipsawStudio.Capture;

/// <summary>
/// Wraps one capture device as an IMFSourceReader in synchronous (pull) mode.
/// Pull mode is deliberate: ReadSample hands us the newest frame with no internal
/// queue, which is where most of the latency in bundled capture apps comes from.
/// All members must be touched from a single MTA thread.
/// </summary>
internal sealed class VideoSource : IDisposable
{
    private object? _mediaSource;
    private IMFSourceReader? _reader;
    private bool _disposed;

    public Guid OutputSubtype { get; private set; }
    public bool UsingD3D { get; private set; }
    /// <summary>Row stride of the negotiated output; negative for bottom-up frames. 0 if unknown.</summary>
    public int DefaultStride { get; private set; }

    private VideoSource(object mediaSource, IMFSourceReader reader, bool usingD3D)
    {
        _mediaSource = mediaSource;
        _reader = reader;
        UsingD3D = usingD3D;
    }

    public static VideoSource Open(VideoDeviceInfo device, IMFDXGIDeviceManager? dxgiManager)
    {
        MfHelpers.Check(Mf.MFCreateAttributes(out var srcAttrs, 2), "MFCreateAttributes(device)");
        object mediaSource;
        try
        {
            MfHelpers.SetGuid(srcAttrs, Mf.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, Mf.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            var linkKey = Mf.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
            MfHelpers.Check(srcAttrs.SetString(ref linkKey, device.SymbolicLink), "SetString(symbolic link)");
            MfHelpers.Check(Mf.MFCreateDeviceSource(srcAttrs, out mediaSource), "MFCreateDeviceSource");
        }
        finally { MfHelpers.Release(srcAttrs); }

        MfHelpers.Check(Mf.MFCreateAttributes(out var readerAttrs, 6), "MFCreateAttributes(reader)");
        try
        {
            // Let the GPU do MJPEG decode and colour conversion when a device manager is available.
            if (dxgiManager is not null)
            {
                MfHelpers.SetUnknown(readerAttrs, Mf.MF_SOURCE_READER_D3D_MANAGER, dxgiManager);
                MfHelpers.SetU32(readerAttrs, Mf.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            }
            else
            {
                MfHelpers.SetU32(readerAttrs, Mf.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            }
            MfHelpers.SetU32(readerAttrs, Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            MfHelpers.SetU32(readerAttrs, Mf.MF_LOW_LATENCY, 1);
            MfHelpers.SetU32(readerAttrs, Mf.MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN, 1);

            int hr = Mf.MFCreateSourceReaderFromMediaSource(mediaSource, readerAttrs, out var reader);
            if (MfHelpers.Failed(hr))
            {
                MfHelpers.Release(mediaSource);
                throw new MfException(hr, "MFCreateSourceReaderFromMediaSource");
            }
            reader.SetStreamSelection(Mf.ALL_STREAMS, false);
            reader.SetStreamSelection(Mf.FIRST_VIDEO_STREAM, true);
            return new VideoSource(mediaSource, reader, dxgiManager is not null);
        }
        finally { MfHelpers.Release(readerAttrs); }
    }

    /// <summary>Every format the device itself advertises, de-duplicated and sorted best-first.</summary>
    public List<VideoFormat> EnumerateFormats()
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(VideoSource));
        var list = new List<VideoFormat>();
        for (uint i = 0; ; i++)
        {
            int hr = reader.GetNativeMediaType(Mf.FIRST_VIDEO_STREAM, i, out var type);
            if (hr == Mf.MF_E_NO_MORE_TYPES || MfHelpers.Failed(hr) || type is null) break;
            try
            {
                var (w, h) = MfHelpers.Unpack(MfHelpers.GetU64(type, Mf.MF_MT_FRAME_SIZE));
                var (num, den) = MfHelpers.Unpack(MfHelpers.GetU64(type, Mf.MF_MT_FRAME_RATE));
                if (w == 0 || h == 0) continue;
                list.Add(new VideoFormat
                {
                    TypeIndex = (int)i,
                    Subtype = MfHelpers.GetGuid(type, Mf.MF_MT_SUBTYPE),
                    Width = (int)w,
                    Height = (int)h,
                    FpsNumerator = num,
                    FpsDenominator = den == 0 ? 1 : den,
                });
            }
            finally { MfHelpers.Release(type); }
        }

        // Devices repeat the same mode under several indices; keep the first of each.
        var seen = new HashSet<string>();
        return list.Where(f => seen.Add(f.Key))
                   .OrderByDescending(f => (long)f.Width * f.Height)
                   .ThenByDescending(f => f.Fps)
                   .ThenBy(f => f.SubtypeName)
                   .ToList();
    }

    /// <summary>
    /// Pins the device to <paramref name="format"/>, then asks the reader for an
    /// uncompressed output the renderer can blit. Returns the subtype actually negotiated.
    /// </summary>
    public void SetFormat(VideoFormat format, Guid preferredOutput)
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(VideoSource));

        MfHelpers.Check(reader.GetNativeMediaType(Mf.FIRST_VIDEO_STREAM, (uint)format.TypeIndex, out var native), "GetNativeMediaType");
        if (native is null) throw new InvalidOperationException("Device stopped advertising that format.");
        try
        {
            MfHelpers.Check(reader.SetCurrentMediaType(Mf.FIRST_VIDEO_STREAM, IntPtr.Zero, native), "SetCurrentMediaType(native)");
        }
        finally { MfHelpers.Release(native); }

        // If the device already gives us something blittable, don't insert a converter at all.
        // Only on the GPU path: the software upload path understands 32-bit RGB and nothing else.
        if (format.Subtype == preferredOutput || (UsingD3D && IsBlittable(format.Subtype)))
        {
            OutputSubtype = format.Subtype;
            return;
        }

        foreach (var candidate in CandidateOutputs(preferredOutput))
        {
            if (TrySetOutput(reader, candidate, format, explicitGeometry: true) ||
                TrySetOutput(reader, candidate, format, explicitGeometry: false))
            {
                OutputSubtype = candidate;
                return;
            }
        }

        throw new InvalidOperationException(
            $"The device offers {Mf.DescribeSubtype(format.Subtype)} at {format.Width}x{format.Height}, but Windows " +
            "could not convert it to a displayable format. Pick a different format for this resolution.");
    }

    private static IEnumerable<Guid> CandidateOutputs(Guid preferred)
    {
        yield return preferred;
        if (preferred != Mf.MFVideoFormat_NV12) yield return Mf.MFVideoFormat_NV12;
        if (preferred != Mf.MFVideoFormat_YUY2) yield return Mf.MFVideoFormat_YUY2;
        if (preferred != Mf.MFVideoFormat_RGB32) yield return Mf.MFVideoFormat_RGB32;
    }

    private static bool TrySetOutput(IMFSourceReader reader, Guid subtype, VideoFormat format, bool explicitGeometry)
    {
        MfHelpers.Check(Mf.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            MfHelpers.SetGuid(type, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            MfHelpers.SetGuid(type, Mf.MF_MT_SUBTYPE, subtype);
            if (explicitGeometry)
            {
                MfHelpers.SetU64(type, Mf.MF_MT_FRAME_SIZE, MfHelpers.Pack((uint)format.Width, (uint)format.Height));
                MfHelpers.SetU64(type, Mf.MF_MT_FRAME_RATE, MfHelpers.Pack(format.FpsNumerator, format.FpsDenominator));
                MfHelpers.SetU64(type, Mf.MF_MT_PIXEL_ASPECT_RATIO, MfHelpers.Pack(1, 1));
                MfHelpers.SetU32(type, Mf.MF_MT_INTERLACE_MODE, 2 /* MFVideoInterlace_Progressive */);
            }
            return !MfHelpers.Failed(reader.SetCurrentMediaType(Mf.FIRST_VIDEO_STREAM, IntPtr.Zero, type));
        }
        finally { MfHelpers.Release(type); }
    }

    private static bool IsBlittable(Guid subtype) =>
        subtype == Mf.MFVideoFormat_NV12 || subtype == Mf.MFVideoFormat_YUY2 ||
        subtype == Mf.MFVideoFormat_UYVY || subtype == Mf.MFVideoFormat_RGB32 ||
        subtype == Mf.MFVideoFormat_ARGB32 || subtype == Mf.MFVideoFormat_P010;

    /// <summary>
    /// Re-reads the negotiated output type. Called after the reader signals a format
    /// change, which is what happens when the console or PC on the HDMI input switches mode.
    /// </summary>
    public (Guid Subtype, int Width, int Height, uint FpsNum, uint FpsDen)? GetCurrentOutput()
    {
        var reader = _reader;
        if (reader is null) return null;
        if (MfHelpers.Failed(reader.GetCurrentMediaType(Mf.FIRST_VIDEO_STREAM, out var type)) || type is null) return null;
        try
        {
            var (w, h) = MfHelpers.Unpack(MfHelpers.GetU64(type, Mf.MF_MT_FRAME_SIZE));
            var (num, den) = MfHelpers.Unpack(MfHelpers.GetU64(type, Mf.MF_MT_FRAME_RATE));
            if (w == 0 || h == 0) return null;
            // A negative default stride means the frame is stored bottom-up.
            DefaultStride = unchecked((int)MfHelpers.GetU32(type, Mf.MF_MT_DEFAULT_STRIDE, 0));
            return (MfHelpers.GetGuid(type, Mf.MF_MT_SUBTYPE), (int)w, (int)h, num, den == 0 ? 1 : den);
        }
        finally { MfHelpers.Release(type); }
    }

    /// <summary>Blocks until the next frame. Returns null on a tick/gap; caller owns the sample.</summary>
    public IMFSample? ReadSample(out Mf.SourceReaderFlags flags, out long timestampHns)
    {
        var reader = _reader ?? throw new ObjectDisposedException(nameof(VideoSource));
        int hr = reader.ReadSample(Mf.FIRST_VIDEO_STREAM, 0, out _, out uint rawFlags, out timestampHns, out var sample);
        flags = (Mf.SourceReaderFlags)rawFlags;
        if (MfHelpers.Failed(hr))
        {
            MfHelpers.Release(sample);
            throw new MfException(hr, "ReadSample");
        }
        return sample;
    }

    public void Flush()
    {
        try { _reader?.Flush(Mf.FIRST_VIDEO_STREAM); } catch { /* device may be gone */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_reader is not null) { MfHelpers.Release(_reader); _reader = null; }
        if (_mediaSource is IMFMediaSource src)
        {
            try { src.Shutdown(); } catch { }
        }
        if (_mediaSource is not null) { MfHelpers.Release(_mediaSource); _mediaSource = null; }
    }
}
