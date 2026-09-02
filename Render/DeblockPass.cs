using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RipsawStudio.Render;

/// <summary>
/// A full-screen pixel-shader pass that runs after the video processor's blit, on the
/// already-scaled picture.
///
/// This is deliberately NOT a byte-exact H.264/MJPEG in-loop deblocking filter - that needs
/// the original macroblock grid, which no longer exists once the picture has been scaled and
/// colour-converted by the video processor. What it does instead is edge-aware smoothing: for
/// every pixel it blends in the neighbours whose colour is close enough to belong to the same
/// surface, and leaves anything across a real edge alone. Blocky/mosquito noise from a lossy
/// source sits exactly in that "close enough" band; real detail mostly doesn't, so it survives.
///
/// This replaced an earlier attempt that used the D3D11 video processor's own NoiseReduction
/// and EdgeEnhancement stream filters (see git history). Those did nothing useful here: driver
/// noise reduction targets random sensor/analog noise, not the deterministic block boundaries
/// a lossy encoder leaves behind, and edge enhancement sharpens indiscriminately - including
/// the block edges themselves, which is why it made the picture look worse instead of better.
/// </summary>
internal sealed class DeblockPass : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _constants;

    private ID3D11Texture2D? _scratch;
    private ID3D11ShaderResourceView? _scratchSrv;
    private ID3D11RenderTargetView? _scratchRtv;
    private int _width, _height;

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public float TexelWidth;
        public float TexelHeight;
        public float Strength;
        public float Threshold;
    }

    // A full-screen triangle generated purely from SV_VertexID - no vertex buffer needed.
    // Both entry points live in the same source so there is only one string to keep in sync.
    private const string ShaderSource = @"
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

Texture2D SourceTex : register(t0);
SamplerState LinearSampler : register(s0);

cbuffer Params : register(b0)
{
    float TexelWidth;
    float TexelHeight;
    float Strength;
    float Threshold;
};

float4 PSMain(VSOut input) : SV_TARGET
{
    float4 center = SourceTex.Sample(LinearSampler, input.uv);
    if (Strength <= 0.0001)
        return center;

    float2 texel = float2(TexelWidth, TexelHeight);
    // A 2px radius kernel instead of 1px - reaches further across a block boundary, which is
    // what actually needs bridging.
    float2 offsets[8] =
    {
        float2(-2,-2), float2(0,-2), float2(2,-2),
        float2(-2, 0),               float2(2, 0),
        float2(-2, 2), float2(0, 2), float2(2, 2)
    };

    float4 sum = center;
    float weightSum = 1.0;

    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float4 s = SourceTex.Sample(LinearSampler, input.uv + offsets[i] * texel);
        float diff = distance(s.rgb, center.rgb);
        float w = saturate(1.0 - diff / Threshold);
        w = w * w;   // steeper falloff - only near-flat neighbours pull much weight
        sum += s * w;
        weightSum += w;
    }

    float4 smoothed = sum / weightSum;
    return lerp(center, smoothed, Strength);
}";

    public DeblockPass(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        // NOTE: this was the one call with real signature risk - confirmed against a real
        // build: Compiler.Compile returns a ReadOnlyMemory<byte> (not a disposable blob), and
        // CreateVertexShader/CreatePixelShader want its .Span, not .AsSpan().
        var vsBlob = Compiler.Compile(ShaderSource, "VSMain", "DeblockPass", "vs_4_0");
        _vertexShader = _device.CreateVertexShader(vsBlob.Span);

        var psBlob = Compiler.Compile(ShaderSource, "PSMain", "DeblockPass", "ps_4_0");
        _pixelShader = _device.CreatePixelShader(psBlob.Span);

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });

        _constants = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 16,   // 4 floats - constant buffers must be 16-byte aligned
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
    }

    /// <summary>
    /// The render target the video processor should blit into instead of the swap chain's
    /// back buffer. Recreated only when the size actually changes (window resize).
    /// </summary>
    public ID3D11RenderTargetView GetSourceTarget(int width, int height, out ID3D11Texture2D texture)
    {
        EnsureScratch(width, height);
        texture = _scratch!;
        return _scratchRtv!;
    }

    private void EnsureScratch(int width, int height)
    {
        if (_scratch is not null && _width == width && _height == height) return;

        _scratchRtv?.Dispose();
        _scratchSrv?.Dispose();
        _scratch?.Dispose();

        _scratch = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _scratchRtv = _device.CreateRenderTargetView(_scratch);
        _scratchSrv = _device.CreateShaderResourceView(_scratch);
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Draws the scratch texture (whatever the video processor just blitted into it) onto
    /// <paramref name="destination"/> through the smoothing shader. Strength 0 short-circuits
    /// inside the shader itself, so leaving the slider at 0 costs one cheap full-screen pass
    /// with no visible change rather than a code-path switch here.
    /// </summary>
    public void Run(ID3D11RenderTargetView destination, int width, int height, float strength)
    {
        var map = _context.Map(_constants!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            var c = (Constants*)map.DataPointer;
            c->TexelWidth = 1f / Math.Max(1, width);
            c->TexelHeight = 1f / Math.Max(1, height);
            c->Strength = Math.Clamp(strength, 0f, 1f);
            // Wider tolerance band than the first pass - the original was too conservative
            // to be visible at all on typical block-noise contrast.
            c->Threshold = 0.08f + c->Strength * 0.35f;
        }
        _context.Unmap(_constants!, 0);

        _context.OMSetRenderTargets(destination);
        _context.RSSetViewport(0, 0, width, height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetShaderResource(0, _scratchSrv!);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetConstantBuffer(0, _constants);
        _context.Draw(3, 0);

        // Leave no stale bindings behind for whatever renders next.
        _context.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null!);
    }

    public void Dispose()
    {
        _scratchRtv?.Dispose();
        _scratchSrv?.Dispose();
        _scratch?.Dispose();
        _constants?.Dispose();
        _sampler?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
    }
}
