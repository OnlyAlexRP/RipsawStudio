using System.Runtime.InteropServices;
using RipsawStudio.Interop;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RipsawStudio.Render;

/// <summary>
/// Owns the D3D11 device, the swap chain for the preview window, and the video processor
/// that converts + scales each captured frame straight into the back buffer. Callers must
/// serialise their own access: the engine holds one lock around every entry point, because
/// the capture thread (screenshots, resizes) and the render thread (presents) both come here.
/// </summary>
internal sealed class Presenter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IDXGIFactory2 _factory;
    private readonly bool _tearingSupported;

    /// <summary>
    /// Three, not two. With two buffers in independent flip one is scanning out and one is
    /// the back buffer, so the frame-latency waitable signals immediately (a buffer is free)
    /// while Present blocks waiting for the flip. That inverts which call blocks: the frame
    /// gets chosen before the block instead of after it, and is a whole refresh stale by the
    /// time it reaches the glass. A third buffer gives Present somewhere to queue into, so
    /// MaximumFrameLatency stays the thing that paces us - and it paces us before we choose.
    /// </summary>
    private const uint BufferCountForLatency = 3;

    private SwapChainFlags SwapChainFlags =>
        SwapChainFlags.FrameLatencyWaitableObject |
        (_tearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None);

    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private IntPtr _frameLatencyWaitable;
    private uint _maxFrameLatency;
    private IntPtr _hwnd;
    private int _backBufferWidth, _backBufferHeight;

    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _processor;
    private int _vpInputWidth, _vpInputHeight;
    private VideoProcessorFilterCaps _filterCaps;
    private RawRect _lastDest;
    private int _lastTargetWidth, _lastTargetHeight;
    private bool _vpRectsValid;
    private PictureSettings _picture = new();

    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _backBufferRtv;
    private ID3D11VideoProcessorOutputView? _scratchVpView;
    private DeblockPass? _deblock;

    private ID3D11Texture2D? _uploadStaging;   // software-path scratch
    private ID3D11Texture2D? _uploadTexture;
    private int _uploadWidth, _uploadHeight;

    private readonly Dictionary<(IntPtr Resource, uint Index), ID3D11VideoProcessorInputView> _inputViews = new();
    /// <summary>Reused so the per-frame path allocates nothing the GC has to collect later.</summary>
    private readonly VideoProcessorStream[] _blitStreams = new VideoProcessorStream[1];
    private readonly object _deviceRcw;

    public IMFDXGIDeviceManager DeviceManager { get; }
    /// <summary>Adapter description, matching the entries from <see cref="ListAdapters"/>.</summary>
    public string AdapterName { get; } = "unknown";
    /// <summary>Adapter name plus feature level, for diagnostics.</summary>
    public string AdapterDetail { get; } = "unknown";
    /// <summary>
    /// Refresh rate of the display the window is on. With vsync, this is the hard floor on
    /// how long a frame can wait: a 60 Hz panel means up to 16.7 ms, a 144 Hz panel 6.9 ms.
    /// </summary>
    public double DisplayRefreshHz { get; private set; }

    /// <summary>
    /// Frames handed to DXGI that the display has not shown yet. This is the latency that
    /// hides after Present returns, which no timing around our own calls can see.
    /// </summary>
    public int QueuedFrames { get; private set; }

    /// <summary>How frames are paced, so a trace can state it rather than implying it.</summary>
    public string PacingDescription => _frameLatencyWaitable != IntPtr.Zero
        ? $"waitable swap chain, {BufferCountForLatency} buffers, max frame latency {_maxFrameLatency}"
        : $"NO waitable object - Present will block ({BufferCountForLatency} buffers)";
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public AspectMode Aspect { get; set; } = AspectMode.Auto;
    /// <summary>Row stride for system-memory frames; negative means bottom-up. 0 = assume packed top-down.</summary>
    public int SoftwareStrideHint { get; set; }
    public bool VSync { get; set; }

    /// <summary>Letterbox colour behind the picture.</summary>
    private static readonly Color4 Background = new(0.05f, 0.05f, 0.06f, 1f);

    /// <summary>Every GPU that could drive the preview, in adapter order.</summary>
    public static List<string> ListAdapters()
    {
        var names = new List<string>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0) continue;
                    names.Add(description.Description.Trim());
                }
            }
        }
        catch { /* an empty list just means "let Windows choose" */ }
        return names;
    }

    /// <param name="preferredAdapter">Adapter description to use, or null for whatever Windows picks.</param>
    public Presenter(string? preferredAdapter = null)
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };

        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        using (var adapter = FindAdapter(preferredAdapter))
        {
            // DriverType must be Unknown whenever an explicit adapter is passed.
            var driverType = adapter is null ? DriverType.Hardware : DriverType.Unknown;
            var hr = D3D11.D3D11CreateDevice(adapter, driverType, flags, levels, out _device!, out _context!);
            if (hr.Failure)
                hr = D3D11.D3D11CreateDevice(adapter, driverType, DeviceCreationFlags.BgraSupport, levels, out _device!, out _context!);
            if (hr.Failure && adapter is not null)
                hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out _device!, out _context!);
            hr.CheckError();
        }

        // MF hands samples to us from its own threads, so the device must be thread-safe.
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);

        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice1>())
        {
            dxgiDevice.MaximumFrameLatency = 1;   // do not let DXGI buffer ahead
            using var adapter = dxgiDevice.GetAdapter();
            AdapterName = adapter.Description.Description.Trim();
            AdapterDetail = $"{AdapterName} (feature level {_device.FeatureLevel})";
        }

        using (var factory5 = _factory.QueryInterfaceOrNull<IDXGIFactory5>())
            _tearingSupported = factory5?.PresentAllowTearing ?? false;

        MfHelpers.Check(Mf.MFCreateDXGIDeviceManager(out uint resetToken, out var manager), "MFCreateDXGIDeviceManager");
        // MF needs the D3D device as a plain COM object, not a Vortice wrapper.
        _deviceRcw = Marshal.GetObjectForIUnknown(_device.NativePointer);
        MfHelpers.Check(manager.ResetDevice(_deviceRcw, resetToken), "IMFDXGIDeviceManager::ResetDevice");
        DeviceManager = manager;

        _deblock = new DeblockPass(_device, _context);
    }

    /// <summary>Applies picture adjustments. Cheap enough to call whenever a slider moves.</summary>
    public void SetPicture(PictureSettings settings)
    {
        _picture = settings.Clone();
        if (_processor is not null) ApplyPicture();
    }

    private void ApplyPicture()
    {
        var processor = _processor;
        if (processor is null) return;

        ApplyFilter(processor, VideoProcessorFilter.Brightness, VideoProcessorFilterCaps.Brightness,
                    _picture.Brightness, neutral: 0f);
        ApplyFilter(processor, VideoProcessorFilter.Contrast, VideoProcessorFilterCaps.Contrast,
                    _picture.Contrast, neutral: 1f);
        ApplyFilter(processor, VideoProcessorFilter.Saturation, VideoProcessorFilterCaps.Saturation,
                    _picture.Saturation, neutral: 1f);

        ApplyColorSpace(processor);
    }

    /// <summary>
    /// Maps a normalised value onto the driver's own filter range: the neutral value lands on
    /// the driver default, and -1 / +1 on its minimum and maximum.
    /// </summary>
    private void ApplyFilter(ID3D11VideoProcessor processor, VideoProcessorFilter filter,
                             VideoProcessorFilterCaps cap, float value, float neutral)
    {
        if ((_filterCaps & cap) == 0) return;

        if (Math.Abs(value - neutral) <= 0.0001f)
        {
            _videoContext.VideoProcessorSetStreamFilter(processor, 0, filter, false, 0);
            return;
        }

        var range = _vpEnum!.GetVideoProcessorFilterRange(filter);
        float offset = value - neutral;                              // -1 .. +1
        float level = offset >= 0
            ? range.Default + offset * (range.Maximum - range.Default)
            : range.Default + offset * (range.Default - range.Minimum);
        int clamped = (int)Math.Round(Math.Clamp(level, range.Minimum, range.Maximum));
        _videoContext.VideoProcessorSetStreamFilter(processor, 0, filter, true, clamped);
    }

    private void ApplyColorSpace(ID3D11VideoProcessor processor)
    {
        // Nominal_Range takes D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE, where Undefined means
        // "use whatever the media type says" - which is exactly what Auto should do.
        uint undefined = (uint)VideoProcessorNominalRange.Undefined;
        uint limited = (uint)VideoProcessorNominalRange.Range_16_235;
        uint full = (uint)VideoProcessorNominalRange.Range_0_255;
        const uint Bt601 = 0, Bt709 = 1;    // YCbCr_Matrix is a one-bit field, not an enum

        var input = new VideoProcessorColorSpace();
        var output = new VideoProcessorColorSpace();

        switch (_picture.Range)
        {
            case RangeMode.Expand:      // 16-235 source stretched across the full display range
                input.Nominal_Range = limited;
                output.Nominal_Range = full;
                break;
            case RangeMode.Compress:    // full-range source squeezed into 16-235
                input.Nominal_Range = full;
                output.Nominal_Range = limited;
                break;
            default:
                input.Nominal_Range = undefined;
                output.Nominal_Range = undefined;
                break;
        }

        // The matrix field has no "unspecified" value, so Auto uses the same rule the
        // driver would: BT.709 for HD, BT.601 below it.
        input.YCbCr_Matrix = _picture.Matrix switch
        {
            ColorMatrix.Bt709 => Bt709,
            ColorMatrix.Bt601 => Bt601,
            _ => _vpInputHeight >= 720 ? Bt709 : Bt601,
        };

        _videoContext.VideoProcessorSetStreamColorSpace(processor, 0, input);
        _videoContext.VideoProcessorSetOutputColorSpace(processor, output);
    }

    private void UpdateDisplayRefresh()
    {
        try
        {
            string? deviceName = null;
            using (var output = _swapChain?.GetContainingOutput())
                deviceName = output?.Description.DeviceName;

            var mode = new DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode))
                DisplayRefreshHz = mode.dmDisplayFrequency;
        }
        catch { /* a refresh rate we cannot read is only a missing status line */ }
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    /// <summary>
    /// Only dmDisplayFrequency is read, but every field has to be declared: the struct is
    /// passed to Win32 by layout, and dropping the unused members would shift that offset.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    private IDXGIAdapter1? FindAdapter(string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return null;
        using var factory1 = _factory.QueryInterface<IDXGIFactory1>();
        for (uint i = 0; factory1.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (adapter.Description1.Description.Trim().Equals(preferred, StringComparison.OrdinalIgnoreCase))
                return adapter;
            adapter.Dispose();
        }
        return null;
    }

    public void AttachWindow(IntPtr hwnd, int width, int height)
    {
        if (_hwnd == hwnd && _swapChain is not null) { Resize(width, height); return; }
        DisposeSwapChain();
        _hwnd = hwnd;
        _backBufferWidth = Math.Max(1, width);
        _backBufferHeight = Math.Max(1, height);

        var desc = new SwapChainDescription1
        {
            Width = (uint)_backBufferWidth,
            Height = (uint)_backBufferHeight,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = BufferCountForLatency,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Vortice.DXGI.Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags,
        };

        ReleaseBackBufferView();
        _swapChain = _factory.CreateSwapChainForHwnd(_device, hwnd, desc);
        _factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        // A waitable swap chain moves the vsync wait to *before* we fetch and draw a frame,
        // instead of blocking inside Present afterwards. Same tear-free output, but the frame
        // we hand to the display is one refresh fresher, and the capture thread is not stalled
        // holding a frame it has already drawn.
        UpdateDisplayRefresh();
        _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (_swapChain2 is not null)
        {
            _swapChain2.MaximumFrameLatency = 1;
            _frameLatencyWaitable = _swapChain2.FrameLatencyWaitableObject;
            _maxFrameLatency = _swapChain2.MaximumFrameLatency;
        }
    }

    public void Resize(int width, int height)
    {
        if (_swapChain is null) return;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _backBufferWidth && height == _backBufferHeight) return;

        ReleaseBackBufferView();
        _context.Flush();
        _swapChain.ResizeBuffers(BufferCountForLatency, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags).CheckError();
        _backBufferWidth = width;
        _backBufferHeight = height;
        _vpRectsValid = false;
        UpdateDisplayRefresh();
    }

    /// <summary>
    /// Blocks until the display is ready for another frame. Call this *before* fetching the
    /// frame to draw: that is what keeps the presented picture as fresh as possible, and it is
    /// the whole point of a waitable swap chain.
    /// </summary>
    public void WaitForDisplay()
    {
        if (_frameLatencyWaitable == IntPtr.Zero) return;
        // A timeout rather than INFINITE so a lost display cannot wedge the capture thread.
        WaitForSingleObject(_frameLatencyWaitable, 100);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    private void UpdateQueueDepth()
    {
        try
        {
            if (_swapChain is null) return;
            // Statistics are unavailable for a moment after a mode change; that is not an error.
            if (_swapChain.GetFrameStatistics(out var stats).Failure) return;
            QueuedFrames = (int)unchecked(_swapChain.LastPresentCount - stats.PresentCount);
        }
        catch { /* diagnostics only - never let this disturb presentation */ }
    }

    /// <summary>Blits one captured frame to the back buffer and presents it.</summary>
    public void Present(IMFSample sample, int frameWidth, int frameHeight)
    {
        if (_swapChain is null) return;

        var (texture, subresource, owned) = ResolveTexture(sample, frameWidth, frameHeight);
        if (texture is null) return;
        try
        {
            EnsureProcessor(frameWidth, frameHeight);
            var outputView = GetScratchVpView();
            var inputView = GetInputView(texture, subresource);

            // These are driver state changes, and they only differ when the window or the
            // frame size does - so they are not worth making sixty times a second.
            var dest = ComputeDestRect(frameWidth, frameHeight);
            if (!_vpRectsValid || !SameRect(dest, _lastDest) ||
                _lastTargetWidth != _backBufferWidth || _lastTargetHeight != _backBufferHeight)
            {
                _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, frameWidth, frameHeight));
                _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, dest);
                _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, new RawRect(0, 0, _backBufferWidth, _backBufferHeight));
                _videoContext.VideoProcessorSetOutputBackgroundColor(_processor!, false, new VideoColor
                {
                    Rgba = new VideoColorRgba { R = Background.R, G = Background.G, B = Background.B, A = 1f }
                });
                _lastDest = dest;
                _lastTargetWidth = _backBufferWidth;
                _lastTargetHeight = _backBufferHeight;
                _vpRectsValid = true;
            }

            _blitStreams[0] = new VideoProcessorStream { Enable = true, InputSurface = inputView };
            _videoContext.VideoProcessorBlt(_processor!, outputView, 0, 1, _blitStreams);

            _deblock!.Run(GetBackBufferRtv(), _backBufferWidth, _backBufferHeight, _picture.ArtifactSmoothing);

            var presentFlags = (!VSync && _tearingSupported) ? PresentFlags.AllowTearing : PresentFlags.None;
            _swapChain.Present(VSync ? 1u : 0u, presentFlags);
            UpdateQueueDepth();
        }
        finally
        {
            if (owned) texture.Dispose();
        }
    }

    /// <summary>Renders the frame at native resolution into a CPU-readable BGRA buffer.</summary>
    public (byte[] Pixels, int Stride) ReadFrameBgra(IMFSample sample, int frameWidth, int frameHeight)
    {
        var (texture, subresource, owned) = ResolveTexture(sample, frameWidth, frameHeight);
        if (texture is null) throw new InvalidOperationException("Frame carried no usable surface.");
        try
        {
            EnsureProcessor(frameWidth, frameHeight);
            using var target = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)frameWidth,
                Height = (uint)frameHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            using var outputView = _videoDevice.CreateVideoProcessorOutputView(target, _vpEnum!,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

            _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, frameWidth, frameHeight));
            _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new RawRect(0, 0, frameWidth, frameHeight));
            _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, new RawRect(0, 0, frameWidth, frameHeight));
            _vpRectsValid = false;   // this pass overwrote what the preview had set
            var stream = new VideoProcessorStream { Enable = true, InputSurface = GetInputView(texture, subresource) };
            _videoContext.VideoProcessorBlt(_processor!, outputView, 0, 1, new[] { stream });

            using var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)frameWidth,
                Height = (uint)frameHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _context.CopyResource(staging, target);

            var map = _context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
            try
            {
                int stride = frameWidth * 4;
                var pixels = new byte[stride * frameHeight];
                for (int y = 0; y < frameHeight; y++)
                    Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, pixels, y * stride, stride);
                return (pixels, stride);
            }
            finally { _context.Unmap(staging, 0); }
        }
        finally
        {
            if (owned) texture.Dispose();
        }
    }

    private static bool SameRect(RawRect a, RawRect b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private RawRect ComputeDestRect(int frameWidth, int frameHeight)
    {
        int bw = _backBufferWidth, bh = _backBufferHeight;
        if (Scaling == ScalingMode.Stretch) return new RawRect(0, 0, bw, bh);

        // The picture is laid out at the display aspect, which is the source's own unless
        // the user has forced one because the card reports it wrongly.
        double displayAspect = Aspect switch
        {
            AspectMode.Wide16x9 => 16.0 / 9.0,
            AspectMode.Standard4x3 => 4.0 / 3.0,
            AspectMode.Wide16x10 => 16.0 / 10.0,
            _ => (double)frameWidth / frameHeight,
        };

        int targetW, targetH;
        if (Scaling == ScalingMode.OneToOne)
        {
            targetH = Math.Min(frameHeight, bh);
            targetW = (int)Math.Round(targetH * displayAspect);
            if (targetW > bw)
            {
                targetW = bw;
                targetH = (int)Math.Round(targetW / displayAspect);
            }
        }
        else
        {
            double scale = Math.Min(bw / displayAspect, (double)bh) / frameHeight;
            targetH = Math.Max(1, (int)Math.Round(frameHeight * scale));
            targetW = Math.Max(1, (int)Math.Round(targetH * displayAspect));
        }

        int x = (bw - targetW) / 2, y = (bh - targetH) / 2;
        return new RawRect(x, y, x + targetW, y + targetH);
    }

    private void EnsureProcessor(int width, int height)
    {
        if (_processor is not null && _vpInputWidth == width && _vpInputHeight == height) return;
        _processor?.Dispose();
        _vpEnum?.Dispose();
        ClearInputViews();
        ReleaseBackBufferView();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.PlaybackNormal,
        };
        _vpEnum = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_vpEnum, 0);
        _vpInputWidth = width;
        _vpInputHeight = height;

        // Skip driver "enhancements"; they cost latency and change the picture.
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamOutputRate(_processor, 0, VideoProcessorOutputRate.Normal, false, null);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        _vpRectsValid = false;
        _filterCaps = _vpEnum.VideoProcessorCaps.FilterCaps;
        ApplyPicture();
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D texture, uint subresource)
    {
        var key = (texture.NativePointer, subresource);
        if (_inputViews.TryGetValue(key, out var cached)) return cached;

        var view = _videoDevice.CreateVideoProcessorInputView(texture, _vpEnum!, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = subresource },
        });
        // MF recycles a small pool of surfaces, so this cache stays tiny in practice.
        if (_inputViews.Count > 64) ClearInputViews();
        _inputViews[key] = view;
        return view;
    }

    /// <summary>
    /// The video processor now blits into <see cref="DeblockPass"/>'s scratch texture instead
    /// of the swap chain's back buffer directly - the deblock shader reads that scratch
    /// texture and writes the real back buffer as a second, cheap full-screen pass. Both
    /// views are the same size as the window, so both are rebuilt together on resize.
    /// </summary>
    private ID3D11VideoProcessorOutputView GetScratchVpView()
    {
        if (_scratchVpView is not null) return _scratchVpView;
        _deblock!.GetSourceTarget(_backBufferWidth, _backBufferHeight, out var scratchTexture);
        _scratchVpView = _videoDevice.CreateVideoProcessorOutputView(scratchTexture, _vpEnum!,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        return _scratchVpView;
    }

    private ID3D11RenderTargetView GetBackBufferRtv()
    {
        if (_backBufferRtv is not null) return _backBufferRtv;
        _backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _backBufferRtv = _device.CreateRenderTargetView(_backBuffer);
        return _backBufferRtv;
    }

    private void ReleaseBackBufferView()
    {
        _scratchVpView?.Dispose();
        _scratchVpView = null;
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
    }

    private void ClearInputViews()
    {
        foreach (var v in _inputViews.Values) v.Dispose();
        _inputViews.Clear();
    }

    /// <summary>
    /// Gets a D3D texture for the sample. GPU samples are used directly; system-memory
    /// samples (RGB32, when the D3D path is unavailable) are uploaded once per frame.
    /// </summary>
    private (ID3D11Texture2D? Texture, uint Subresource, bool Owned) ResolveTexture(IMFSample sample, int width, int height)
    {
        if (sample.GetBufferByIndex(0, out var buffer) < 0 || buffer is null) return (null, 0, false);
        try
        {
            if (buffer is IMFDXGIBuffer dxgi)
            {
                var iid = Mf.IID_ID3D11Texture2D;
                if (dxgi.GetResource(ref iid, out IntPtr pTexture) >= 0 && pTexture != IntPtr.Zero)
                {
                    dxgi.GetSubresourceIndex(out uint index);
                    return (new ID3D11Texture2D(pTexture), index, true);
                }
            }
            return (UploadSoftwareFrame(buffer, width, height), 0, false);
        }
        finally { MfHelpers.Release(buffer); }
    }

    private ID3D11Texture2D? UploadSoftwareFrame(IMFMediaBuffer buffer, int width, int height)
    {
        EnsureUploadTextures(width, height);

        IntPtr scan0;
        int pitch;
        IMF2DBuffer? locked2D = null;
        if (buffer is IMF2DBuffer buffer2D && buffer2D.Lock2D(out scan0, out pitch) >= 0)
        {
            locked2D = buffer2D;
        }
        else
        {
            if (buffer.Lock(out scan0, out _, out _) < 0) return null;
            pitch = SoftwareStrideHint != 0 ? SoftwareStrideHint : width * 4;
            if (pitch < 0)
            {
                // Bottom-up: the buffer starts at the last display row.
                scan0 += -pitch * (height - 1);
            }
        }

        try
        {
            var map = _context.Map(_uploadStaging!, 0, Vortice.Direct3D11.MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                {
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)(scan0 + y * pitch),
                                          (void*)(map.DataPointer + y * (int)map.RowPitch),
                                          rowBytes, rowBytes);
                    }
                }
            }
            finally { _context.Unmap(_uploadStaging!, 0); }
        }
        finally
        {
            if (locked2D is not null) locked2D.Unlock2D();
            else buffer.Unlock();
        }

        _context.CopyResource(_uploadTexture!, _uploadStaging!);
        return _uploadTexture;
    }

    private void EnsureUploadTextures(int width, int height)
    {
        if (_uploadTexture is not null && _uploadWidth == width && _uploadHeight == height) return;
        _uploadTexture?.Dispose();
        _uploadStaging?.Dispose();
        ClearInputViews();

        var common = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
        };
        _uploadStaging = _device.CreateTexture2D(common with
        {
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        _uploadTexture = _device.CreateTexture2D(common with
        {
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        _uploadWidth = width;
        _uploadHeight = height;
    }

    private void DisposeSwapChain()
    {
        ReleaseBackBufferView();
        // The waitable handle belongs to the swap chain; it goes away with it.
        _frameLatencyWaitable = IntPtr.Zero;
        _swapChain2?.Dispose();
        _swapChain2 = null;
        if (_swapChain is null) return;
        _context.ClearState();
        _context.Flush();
        _swapChain.Dispose();
        _swapChain = null;
    }

    public void Dispose()
    {
        ClearInputViews();
        _deblock?.Dispose();
        _processor?.Dispose();
        _vpEnum?.Dispose();
        _uploadTexture?.Dispose();
        _uploadStaging?.Dispose();
        ReleaseBackBufferView();
        DisposeSwapChain();
        MfHelpers.Release(DeviceManager);
        MfHelpers.Release(_deviceRcw);
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _factory.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
