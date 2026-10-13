using System.Runtime.InteropServices;
using RipsawStudio.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RipsawStudio.Render;

/// <summary>
/// A bare D3D11 device on a GPU other than the preview's, for encoding there - Quick Sync on
/// the integrated GPU while an NVIDIA card draws the preview, for example. The sink writer
/// picks its hardware encoder from the adapter behind the device manager it is given, so this
/// is what steers it to the chosen GPU. No swap chain, no video processor: just the device.
/// </summary>
internal sealed class EncoderDevice : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly object _deviceRcw;

    public IMFDXGIDeviceManager DeviceManager { get; }
    public string AdapterName { get; }

    public EncoderDevice(string adapterName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? adapter = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var candidate).Success; i++)
        {
            if (candidate.Description1.Description.Trim().Equals(adapterName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                adapter = candidate;
                break;
            }
            candidate.Dispose();
        }
        if (adapter is null) throw new InvalidOperationException($"The GPU \"{adapterName}\" is not available.");

        using (adapter)
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                levels, out _device!, out _context!).CheckError();
            AdapterName = adapter.Description1.Description.Trim();
        }

        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);

        MfHelpers.Check(Mf.MFCreateDXGIDeviceManager(out uint resetToken, out var manager), "MFCreateDXGIDeviceManager");
        _deviceRcw = Marshal.GetObjectForIUnknown(_device.NativePointer);
        MfHelpers.Check(manager.ResetDevice(_deviceRcw, resetToken), "IMFDXGIDeviceManager::ResetDevice");
        DeviceManager = manager;
    }

    /// <summary>
    /// A GPU frame like the capture card would hand over, filled with <paramref name="nv12"/>
    /// (luma plane then interleaved chroma, both at a pitch of <paramref name="width"/>).
    /// Used by the encoder self-test, which needs real surfaces without a card attached.
    /// </summary>
    public IMFSample CreateNv12Sample(int width, int height, byte[] nv12)
    {
        var handle = GCHandle.Alloc(nv12, GCHandleType.Pinned);
        try
        {
            using var texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            }, new[] { new SubresourceData(handle.AddrOfPinnedObject(), (uint)width) });

            var iid = Mf.IID_ID3D11Texture2D;
            MfHelpers.Check(Mf.MFCreateDXGISurfaceBuffer(ref iid, texture.NativePointer, 0, false, out var buffer),
                            "MFCreateDXGISurfaceBuffer");
            try
            {
                MfHelpers.Check(buffer.GetMaxLength(out uint length), "GetMaxLength");
                buffer.SetCurrentLength(length);
                MfHelpers.Check(Mf.MFCreateSample(out var sample), "MFCreateSample");
                sample.AddBuffer(buffer);
                return sample;
            }
            finally { MfHelpers.Release(buffer); }
        }
        finally { handle.Free(); }
    }

    public void Dispose()
    {
        MfHelpers.Release(DeviceManager);
        MfHelpers.Release(_deviceRcw);
        _context.Dispose();
        _device.Dispose();
    }
}
