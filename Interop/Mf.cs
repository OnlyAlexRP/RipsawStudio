using System.Runtime.InteropServices;

namespace RipsawStudio.Interop;

/// <summary>
/// Hand-rolled Media Foundation interop. Only the surface this app needs, so that
/// every latency-relevant attribute is set explicitly rather than hidden behind a wrapper.
/// </summary>
internal static class Mf
{
    public const int MF_VERSION = 0x00020070;   // MF_SDK_VERSION << 16 | MF_API_VERSION

    public const uint FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint ALL_STREAMS = 0xFFFFFFFE;

    public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    public const int MF_E_INVALIDSTREAMNUMBER = unchecked((int)0xC00D36B3);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);
    public const int MF_E_UNSUPPORTED_D3D_TYPE = unchecked((int)0xC00D3EA2);
    public const int MF_E_HW_MFT_FAILED_START_STREAMING = unchecked((int)0xC00D3E85);

    // ---- Source reader sample flags -------------------------------------------------
    [Flags]
    public enum SourceReaderFlags : uint
    {
        None = 0,
        Error = 0x1,
        EndOfStream = 0x2,
        NewStream = 0x4,
        NativeMediaTypeChanged = 0x10,
        CurrentMediaTypeChanged = 0x20,
        StreamTick = 0x100,
        AllEffectsRemoved = 0x200,
    }

    // ---- GUIDs ----------------------------------------------------------------------
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");

    public static readonly Guid MFVideoFormat_NV12 = FourCc("NV12");
    public static readonly Guid MFVideoFormat_YUY2 = FourCc("YUY2");
    public static readonly Guid MFVideoFormat_UYVY = FourCc("UYVY");
    public static readonly Guid MFVideoFormat_P010 = FourCc("P010");
    public static readonly Guid MFVideoFormat_H264 = FourCc("H264");
    public static readonly Guid MFVideoFormat_RGB32 = FromD3dFormat(22);
    public static readonly Guid MFVideoFormat_ARGB32 = FromD3dFormat(21);
    public static readonly Guid MFVideoFormat_RGB24 = FromD3dFormat(20);

    public static readonly Guid MFAudioFormat_PCM = FromD3dFormat(1);
    public static readonly Guid MFAudioFormat_AAC = FromD3dFormat(0x1610);

    // Source reader / sink writer attributes
    public static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid MF_SINK_WRITER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN = new("56b67165-219e-456d-a22e-2d3004c7fe56");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // Interface IIDs
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static Guid FourCc(string cc)
    {
        uint v = (uint)(cc[0] | (cc[1] << 8) | (cc[2] << 16) | (cc[3] << 24));
        return FromD3dFormat(v);
    }

    /// <summary>Builds the MFVideoFormat_* / MFAudioFormat_* GUID for a FOURCC or format tag.</summary>
    private static Guid FromD3dFormat(uint fmt) =>
        new(fmt, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    public static string DescribeSubtype(Guid subtype)
    {
        if (subtype == MFVideoFormat_RGB32) return "RGB32";
        if (subtype == MFVideoFormat_ARGB32) return "ARGB32";
        if (subtype == MFVideoFormat_RGB24) return "RGB24";
        var b = subtype.ToByteArray();
        // FOURCC-style subtypes carry the code in the first four bytes.
        bool printable = b.Take(4).All(c => c >= 0x20 && c < 0x7F);
        return printable ? new string(b.Take(4).Select(c => (char)c).ToArray()) : subtype.ToString();
    }

    // ---- Functions ------------------------------------------------------------------
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, int cInitialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager ppDeviceManager);


    [DllImport("mf.dll", ExactSpelling = true)]
    public static extern int MFEnumDeviceSources(IMFAttributes pAttributes, out IntPtr pppSourceActivate, out uint pcSourceActivate);

    [DllImport("mf.dll", ExactSpelling = true)]
    public static extern int MFCreateDeviceSource(IMFAttributes pAttributes,
        [MarshalAs(UnmanagedType.Interface)] out object ppSource);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromMediaSource(
        [MarshalAs(UnmanagedType.Interface)] object pMediaSource, IMFAttributes? pAttributes, out IMFSourceReader ppSourceReader);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string pwszURL, IMFAttributes? pAttributes,
        out IMFSourceReader ppSourceReader);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string pwszOutputURL, IntPtr pByteStream,
        IMFAttributes? pAttributes, out IMFSinkWriter ppSinkWriter);
}
