using System.Runtime.InteropServices;
using RipsawStudio.Interop;
using Vortice.DXGI;

namespace RipsawStudio.Record;

public enum VideoCodec { H264, Hevc }

/// <summary>
/// Plain MP4 writes its index at the very end, so a crash or a power cut leaves a file that
/// will not open. Fragmented MP4 writes a small index every couple of seconds instead, and a
/// file cut short still plays up to the last fragment.
/// </summary>
public enum ContainerFormat { FragmentedMp4, Mp4 }

public enum RateControl { Cbr, Vbr, Quality }

/// <summary>One video encoder Windows offers, as found by <see cref="EncoderCatalog"/>.</summary>
public sealed record EncoderInfo(
    VideoCodec Codec,
    bool Hardware,
    string Vendor,
    /// <summary>DXGI description of the GPU it runs on; null for software encoders.</summary>
    string? AdapterName,
    string FriendlyName)
{
    /// <summary>Stable across reboots, unlike an adapter LUID, so it is what settings store.</summary>
    public string Id => Hardware ? "hw:" + AdapterName : "sw";

    public string Label => Hardware ? $"{Vendor} ({AdapterName})" : "Software (CPU)";
}

/// <summary>What a recorder should be built with, resolved from the settings and the hardware.</summary>
internal sealed record EncoderPlan(
    VideoCodec Codec,
    bool Hardware,
    /// <summary>The DXGI device manager the sink writer should encode on, or null.</summary>
    object? DeviceManager,
    /// <summary>
    /// True when the encoder lives on a different GPU from the one the frames are on. Frames
    /// are then copied through system memory, since a texture cannot cross adapters directly.
    /// </summary>
    bool CopyToSystemMemory,
    string Description)
{
    public static EncoderPlan Software(VideoCodec codec) => new(codec, false, null, false, "Software (CPU)");
}

/// <summary>
/// Lists the H.264 and HEVC encoders Windows offers - one per GPU that has an encoder block
/// (NVENC, Quick Sync, AMF), plus Microsoft's software encoders. Media Foundation must already
/// be started on the calling thread.
/// </summary>
internal static class EncoderCatalog
{
    public static List<EncoderInfo> Enumerate()
    {
        var byLuid = AdapterNamesByLuid();
        var byVendor = AdapterVendors();
        var list = new List<EncoderInfo>();
        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Hevc })
        {
            var subtype = codec == VideoCodec.H264 ? Mf.MFVideoFormat_H264 : Mf.MFVideoFormat_HEVC;

            foreach (var (name, vendorId, luid) in Find(subtype, Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_SORTANDFILTER))
            {
                // MFTEnumEx does not say which GPU a transform belongs to (only MFTEnum2's
                // filter knows), so the PCI vendor ID is matched against the adapters instead.
                // Two GPUs from one vendor would be ambiguous; the first one is taken.
                string? adapter = luid is { } l && byLuid.TryGetValue(l, out var named) ? named
                    : ParseVendor(vendorId) is { } v && byVendor.TryGetValue(v, out var vendorAdapter) ? vendorAdapter
                    : null;
                if (adapter is null) continue;
                // Some drivers register more than one transform per GPU (one per engine).
                // Picking between them is not something the sink writer allows anyway.
                if (list.Any(e => e.Codec == codec && e.Hardware && e.AdapterName == adapter)) continue;
                list.Add(new EncoderInfo(codec, true, VendorLabel(vendorId, name), adapter, name));
            }

            // Only H.264: Windows' software HEVC encoder (the store extension) refuses the
            // sink writer's media types outright, so offering it would only offer a failure.
            if (codec != VideoCodec.H264) continue;
            var software = Find(subtype, Mf.MFT_ENUM_FLAG_SYNCMFT | Mf.MFT_ENUM_FLAG_LOCALMFT | Mf.MFT_ENUM_FLAG_SORTANDFILTER);
            if (software.Count > 0)
                list.Add(new EncoderInfo(codec, false, "Microsoft", null, software[0].Name));
        }
        return list;
    }

    /// <summary>
    /// Turns the stored choice into an encoder: "auto" prefers the GPU the preview runs on,
    /// then any GPU, then the CPU. A stored GPU that has since gone falls back the same way.
    /// </summary>
    public static EncoderInfo? Resolve(IReadOnlyList<EncoderInfo> encoders, VideoCodec codec, string? id, string? previewAdapter)
    {
        var forCodec = encoders.Where(e => e.Codec == codec).ToList();
        if (forCodec.Count == 0) return null;

        if (!string.IsNullOrEmpty(id) && id != "auto")
        {
            var exact = forCodec.FirstOrDefault(e => e.Id == id);
            if (exact is not null) return exact;
        }
        return forCodec.FirstOrDefault(e => e.Hardware && Same(e.AdapterName, previewAdapter))
            ?? forCodec.FirstOrDefault(e => e.Hardware)
            ?? forCodec.FirstOrDefault(e => !e.Hardware);
    }

    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string VendorLabel(string? vendorId, string friendlyName)
    {
        string v = (vendorId ?? "").ToUpperInvariant();
        if (v.Contains("10DE")) return "NVIDIA NVENC";
        if (v.Contains("8086")) return "Intel Quick Sync";
        if (v.Contains("1002") || v.Contains("1022")) return "AMD AMF";
        return friendlyName;
    }

    private static uint? ParseVendor(string? vendorId)
    {
        // "VEN_10DE" (sometimes with more after it, e.g. "VEN_10DE&DEV_2482").
        if (vendorId is null) return null;
        int at = vendorId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + 8 > vendorId.Length) return null;
        return uint.TryParse(vendorId.AsSpan(at + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out uint v)
            ? v : null;
    }

    private static Dictionary<uint, string> AdapterVendors()
    {
        var map = new Dictionary<uint, string>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    map.TryAdd(d.VendorId, d.Description.Trim());
                }
            }
        }
        catch { }
        return map;
    }

    internal static Dictionary<ulong, string> AdapterNamesByLuid()
    {
        var map = new Dictionary<ulong, string>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    ulong luid = ((ulong)(uint)d.Luid.HighPart << 32) | d.Luid.LowPart;
                    map[luid] = d.Description.Trim();
                }
            }
        }
        catch { }
        return map;
    }

    internal static List<(string Name, string? VendorId, ulong? Luid)> Find(Guid subtype, uint flags)
    {
        var found = new List<(string, string?, ulong?)>();
        var output = new Mf.MFT_REGISTER_TYPE_INFO { guidMajorType = Mf.MFMediaType_Video, guidSubtype = subtype };
        if (Mf.MFTEnumEx(Mf.MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, ref output, out IntPtr array, out uint count) < 0)
            return found;
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (p == IntPtr.Zero) continue;
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);   // the RCW holds its own reference now
                try
                {
                    string name = MfHelpers.GetString(activate, Mf.MFT_FRIENDLY_NAME_Attribute) ?? "Unknown encoder";
                    string? vendor = MfHelpers.GetString(activate, Mf.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute);
                    var luidKey = Mf.MFT_ENUM_ADAPTER_LUID;
                    ulong? luid = activate.GetUINT64(ref luidKey, out ulong v) >= 0 ? v : null;
                    found.Add((name, vendor, luid));
                }
                finally { MfHelpers.Release(activate); }
            }
        }
        finally { Marshal.FreeCoTaskMem(array); }
        return found;
    }

    /// <summary>
    /// Which encoder a sink writer actually built for a stream, read back from the transform
    /// itself rather than assumed from what was asked for.
    /// </summary>
    public static string DescribeActive(IMFSinkWriter writer, uint stream)
    {
        var service = Guid.Empty;
        var iid = Mf.IID_IMFTransform;
        if (writer.GetServiceForStream(stream, ref service, ref iid, out IntPtr p) < 0 || p == IntPtr.Zero)
            return "unknown";
        object? transform = null;
        IMFAttributes? attrs = null;
        try
        {
            transform = Marshal.GetObjectForIUnknown(p);
            if (transform is not IMFTransform mft || mft.GetAttributes(out attrs) < 0 || attrs is null)
                return "unknown";
            string? vendor = MfHelpers.GetString(attrs, Mf.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute);
            string? url = MfHelpers.GetString(attrs, Mf.MFT_ENUM_HARDWARE_URL_Attribute);
            if (vendor is null && url is null) return "software";
            return VendorLabel(vendor, url ?? vendor ?? "hardware");
        }
        catch { return "unknown"; }
        finally
        {
            Marshal.Release(p);
            MfHelpers.Release(attrs);
            MfHelpers.Release(transform);
        }
    }
}
