using System.Runtime.InteropServices;
using RipsawStudio.Interop;

namespace RipsawStudio.Capture;

public sealed record VideoDeviceInfo(string Name, string SymbolicLink)
{
    public override string ToString() => Name;
}

/// <summary>One selectable capture format, taken verbatim from the device's own type list.</summary>
public sealed class VideoFormat
{
    public required int TypeIndex { get; init; }
    public required Guid Subtype { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required uint FpsNumerator { get; init; }
    public required uint FpsDenominator { get; init; }

    public double Fps => FpsDenominator == 0 ? 0 : (double)FpsNumerator / FpsDenominator;
    public string SubtypeName => Mf.DescribeSubtype(Subtype);


    public override string ToString() => $"{Width}x{Height} @ {Fps:0.##} Hz  ({SubtypeName})";

    /// <summary>Key used to remember a selection across restarts, since type indices move around.</summary>
    public string Key => $"{Width}x{Height}@{FpsNumerator}/{FpsDenominator}:{SubtypeName}";
}

public static class DeviceEnumerator
{
    public static List<VideoDeviceInfo> EnumerateVideoDevices()
    {
        var result = new List<VideoDeviceInfo>();
        MfHelpers.Check(Mf.MFCreateAttributes(out var attrs, 1), "MFCreateAttributes");
        try
        {
            var key = Mf.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            var val = Mf.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            MfHelpers.Check(attrs.SetGUID(ref key, ref val), "SetGUID(source type)");

            MfHelpers.Check(Mf.MFEnumDeviceSources(attrs, out var pArray, out uint count), "MFEnumDeviceSources");
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr pUnk = Marshal.ReadIntPtr(pArray, i * IntPtr.Size);
                    if (pUnk == IntPtr.Zero) continue;
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pUnk);
                    try
                    {
                        string name = MfHelpers.GetString(activate, Mf.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? "Unknown device";
                        string link = MfHelpers.GetString(activate, Mf.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK) ?? "";
                        if (link.Length > 0) result.Add(new VideoDeviceInfo(name, link));
                    }
                    finally
                    {
                        MfHelpers.Release(activate);
                        Marshal.Release(pUnk);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pArray);
            }
        }
        finally
        {
            MfHelpers.Release(attrs);
        }
        return result;
    }
}
