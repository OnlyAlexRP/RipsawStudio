using System.Runtime.InteropServices;

namespace RipsawStudio.Interop;

internal static class MfHelpers
{
    private static readonly object StartupGate = new();
    private static int _startupCount;

    /// <summary>
    /// Reference-counted MFStartup. It has to hold a lock rather than just interlock the
    /// counter: with a bare increment, the second thread sees a non-zero count and returns
    /// while the first is still inside MFStartup, so it can call into Media Foundation
    /// before the platform is up. More than one thread starts here - the capture thread and
    /// the replay buffer's worker - so that race is reachable.
    /// </summary>
    public static void Startup()
    {
        lock (StartupGate)
        {
            if (_startupCount == 0) Check(Mf.MFStartup(Mf.MF_VERSION, 0), "MFStartup");
            _startupCount++;
        }
    }

    public static void Shutdown()
    {
        lock (StartupGate)
        {
            if (_startupCount == 0) return;
            if (--_startupCount == 0) Mf.MFShutdown();
        }
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MfException(hr, what);
    }

    public static bool Failed(int hr) => hr < 0;

    public static string? GetString(IMFAttributes attrs, Guid key)
    {
        int hr = attrs.GetAllocatedString(ref key, out IntPtr p, out _);
        if (hr < 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static uint GetU32(IMFAttributes attrs, Guid key, uint fallback = 0)
        => attrs.GetUINT32(ref key, out uint v) >= 0 ? v : fallback;

    public static ulong GetU64(IMFAttributes attrs, Guid key, ulong fallback = 0)
        => attrs.GetUINT64(ref key, out ulong v) >= 0 ? v : fallback;

    public static Guid GetGuid(IMFAttributes attrs, Guid key)
        => attrs.GetGUID(ref key, out Guid v) >= 0 ? v : Guid.Empty;

    public static void SetU32(IMFAttributes attrs, Guid key, uint value)
        => Check(attrs.SetUINT32(ref key, value), $"SetUINT32({key})");

    public static void SetU64(IMFAttributes attrs, Guid key, ulong value)
        => Check(attrs.SetUINT64(ref key, value), $"SetUINT64({key})");

    public static void SetGuid(IMFAttributes attrs, Guid key, Guid value)
        => Check(attrs.SetGUID(ref key, ref value), $"SetGUID({key})");

    public static void SetUnknown(IMFAttributes attrs, Guid key, object? value)
        => Check(attrs.SetUnknown(ref key, value), $"SetUnknown({key})");

    /// <summary>Packs two 32-bit values the way MF_MT_FRAME_SIZE / MF_MT_FRAME_RATE expect.</summary>
    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    public static (uint High, uint Low) Unpack(ulong v) => ((uint)(v >> 32), (uint)(v & 0xFFFFFFFF));

    /// <summary>
    /// A second sample over the same buffers. Two writers cannot share one sample, because
    /// each stamps its own time on it and a sink writer may still be reading that time after
    /// WriteSample returns - so the replay buffer gets a clone rather than the frame itself.
    /// No pixels are copied: only the buffer references are taken.
    /// </summary>
    public static IMFSample CloneSample(IMFSample source)
    {
        Check(Mf.MFCreateSample(out var clone), "MFCreateSample(clone)");
        try
        {
            if (source.GetBufferCount(out uint count) < 0) return clone;
            for (uint i = 0; i < count; i++)
            {
                if (source.GetBufferByIndex(i, out var buffer) < 0) continue;
                try { clone.AddBuffer(buffer); }
                finally { Release(buffer); }
            }
            return clone;
        }
        catch
        {
            Release(clone);
            throw;
        }
    }

    /// <summary>
    /// Copies a frame into a plain system-memory sample, for an encoder on another GPU. A GPU
    /// buffer's IMF2DBuffer does the read-back itself and lays the planes out packed, which is
    /// the layout a memory sample is assumed to have.
    /// </summary>
    public static IMFSample CopyToMemorySample(IMFSample source)
    {
        Check(source.GetBufferByIndex(0, out var buffer), "GetBufferByIndex(copy)");
        IMFMediaBuffer? copy = null;
        try
        {
            var twoD = buffer as IMF2DBuffer;
            uint length = 0;
            if (twoD is null || twoD.GetContiguousLength(out length) < 0)
            {
                Check(source.ConvertToContiguousBuffer(out var contiguous), "ConvertToContiguousBuffer");
                Release(buffer);
                buffer = contiguous;
                Check(buffer.GetCurrentLength(out length), "GetCurrentLength");
                twoD = null;
            }

            Check(Mf.MFCreateMemoryBuffer((int)length, out copy), "MFCreateMemoryBuffer(copy)");
            Check(copy.Lock(out IntPtr dest, out _, out _), "Lock(copy)");
            try
            {
                if (twoD is not null)
                {
                    Check(twoD.ContiguousCopyTo(dest, length), "ContiguousCopyTo");
                }
                else
                {
                    Check(buffer.Lock(out IntPtr src, out _, out _), "Lock(source)");
                    try { unsafe { Buffer.MemoryCopy((void*)src, (void*)dest, length, length); } }
                    finally { buffer.Unlock(); }
                }
            }
            finally { copy.Unlock(); }
            copy.SetCurrentLength(length);

            Check(Mf.MFCreateSample(out var sample), "MFCreateSample(copy)");
            sample.AddBuffer(copy);
            return sample;
        }
        finally
        {
            Release(buffer);
            Release(copy);
        }
    }

    /// <summary>Releases a COM object without waiting for the GC. Safe to call with null.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); } catch (ArgumentException) { }
        }
    }
}

public sealed class MfException : Exception
{
    public MfException(int hr, string what)
        : base($"{what} failed: 0x{hr:X8}{Describe(hr)}")
        => HResult = hr;

    /// <summary>
    /// Named codes come from <see cref="Mf"/> rather than repeated literals, so a code and
    /// its name cannot drift apart - which they had, with one label sitting on the wrong
    /// number. The rest are plain Win32/COM codes with no MF constant to point at.
    /// </summary>
    private static string Describe(int hr) => hr switch
    {
        Mf.MF_E_INVALIDMEDIATYPE => " (MF_E_INVALIDMEDIATYPE: the device rejected that format)",
        Mf.MF_E_INVALIDSTREAMNUMBER => " (MF_E_INVALIDSTREAMNUMBER)",
        Mf.MF_E_NO_MORE_TYPES => " (MF_E_NO_MORE_TYPES: the end of the format list)",
        Mf.MF_E_TOPO_CODEC_NOT_FOUND => " (MF_E_TOPO_CODEC_NOT_FOUND: no codec for this format)",
        Mf.MF_E_UNSUPPORTED_D3D_TYPE => " (MF_E_UNSUPPORTED_D3D_TYPE)",
        Mf.MF_E_HW_MFT_FAILED_START_STREAMING =>
            " (MF_E_HW_MFT_FAILED_START_STREAMING: the hardware transform would not start)",
        unchecked((int)0x8007001F) => " (ERROR_GEN_FAILURE: device busy or unplugged)",
        unchecked((int)0x80070005) => " (E_ACCESSDENIED: another app holds the device, or camera privacy is blocking it)",
        unchecked((int)0x8004027B) => " (the device refused to start, it is most likely already open elsewhere)",
        unchecked((int)0xC00DABE3) => " (device invalidated, it was unplugged)",
        _ => "",
    };
}
