using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// Thumbnail render pass. Draws one quad per entry in a pass-local-pixel
/// instance list, each sampling its own SRV (opened by
/// <see cref="OverviewThumbnails"/> as a DWM shared surface).
///
/// One <c>Draw</c> call per instance so each can bind its own SRV — the
/// instance count is bounded (&lt; ~50 typical) and the per-draw overhead
/// is negligible. Each thumbnail is preceded by a shadow draw so the Win11
/// halo below one thumbnail overlaps neighbours beneath it.
/// </summary>
internal sealed class ThumbnailPass : IDisposable
{
    public const int MaxThumbnails = 256;
    private const int QuadVertexCount = 6;

    /// <summary>Per-instance thumbnail rect in pass-local pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public float Left, Top, Right, Bottom;
    }

    private static byte[]? _vsBytecode;
    private static byte[]? _psBytecode;
    private static byte[]? _shadowVsBytecode;
    private static byte[]? _shadowPsBytecode;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11VertexShader? _shadowVs;
    private ID3D11PixelShader? _shadowPs;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11ShaderResourceView? _instanceSrv;
    private ID3D11Buffer? _drawCb; // per-draw instance index (b1)

    // Double-buffered: UI thread writes into *_staging, render thread copies
    // under the lock at frame start. SRV lifetimes are owned by
    // OverviewThumbnails; we only hold references for the brief window
    // between SetInstances and the next Render.
    private readonly Instance[] _instancesStaging = new Instance[MaxThumbnails];
    private readonly ID3D11ShaderResourceView?[] _srvsStaging = new ID3D11ShaderResourceView?[MaxThumbnails];
    private readonly ID3D11ShaderResourceView?[] _srvs = new ID3D11ShaderResourceView?[MaxThumbnails];
    private readonly object _lock = new();
    private int _count;
    private bool _dirty;

    public static bool CompileShaders()
    {
        _vsBytecode       = Compile("VSThumb",  "vs_5_0"); if (_vsBytecode == null) return false;
        _psBytecode       = Compile("PSThumb",  "ps_5_0"); if (_psBytecode == null) return false;
        _shadowVsBytecode = Compile("VSShadow", "vs_5_0"); if (_shadowVsBytecode == null) return false;
        _shadowPsBytecode = Compile("PSShadow", "ps_5_0"); if (_shadowPsBytecode == null) return false;
        return true;
    }

    private static byte[]? Compile(string entry, string profile)
    {
        Compiler.Compile(ShaderSource, entry, "", profile, out var blob, out var err);
        if (err != null)
        {
            string text = System.Text.Encoding.ASCII.GetString(err.AsSpan()).TrimEnd('\0', '\n', '\r');
            if (!string.IsNullOrWhiteSpace(text))
            {
                string msg = $"[ThumbnailPass {entry}/{profile}] {text}";
                Console.Error.WriteLine(msg);
                System.Diagnostics.Debug.WriteLine(msg);
                TrayApp.Log(msg);
            }
            err.Dispose();
        }
        if (blob == null)
        {
            string msg = $"[ThumbnailPass {entry}/{profile}] compile returned null bytecode";
            Console.Error.WriteLine(msg);
            System.Diagnostics.Debug.WriteLine(msg);
            TrayApp.Log(msg);
            return null;
        }
        var bytecode = blob.AsSpan().ToArray();
        blob.Dispose();
        return bytecode;
    }

    public ThumbnailPass(ID3D11Device device)
    {
        if (_vsBytecode == null || _psBytecode == null
            || _shadowVsBytecode == null || _shadowPsBytecode == null)
            throw new InvalidOperationException("ThumbnailPass.CompileShaders must run before construction");
        _vs = device.CreateVertexShader(_vsBytecode);
        _ps = device.CreatePixelShader(_psBytecode);
        _shadowVs = device.CreateVertexShader(_shadowVsBytecode);
        _shadowPs = device.CreatePixelShader(_shadowPsBytecode);

        int stride = Marshal.SizeOf<Instance>();
        _instanceBuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(stride * MaxThumbnails),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)stride
        });
        _instanceSrv = device.CreateShaderResourceView(_instanceBuffer,
            new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = MaxThumbnails }
            });
        _drawCb = device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    /// <summary>
    /// Push the next frame's instance rects + matching SRVs. Lengths must
    /// match; entries beyond <see cref="MaxThumbnails"/> are dropped.
    /// </summary>
    public void SetInstances(
        ReadOnlySpan<Instance> instances,
        ReadOnlySpan<ID3D11ShaderResourceView?> srvs)
    {
        int n = Math.Min(Math.Min(instances.Length, srvs.Length), MaxThumbnails);
        lock (_lock)
        {
            for (int i = 0; i < n; i++)
            {
                _instancesStaging[i] = instances[i];
                _srvsStaging[i] = srvs[i];
            }
            _count = n;
            _dirty = true;
        }
    }

    public void Render(ID3D11DeviceContext ctx,
        ID3D11Buffer gridCb, ID3D11SamplerState sampler, ID3D11BlendState blendState)
    {
        UploadPending(ctx);
        int count = _count;
        if (count == 0) return;

        ctx.VSSetShaderResource(0, _instanceSrv);
        ctx.PSSetShaderResource(0, _instanceSrv!);
        ctx.VSSetConstantBuffer(0, gridCb);
        ctx.VSSetConstantBuffer(1, _drawCb);
        ctx.PSSetConstantBuffer(0, gridCb);
        ctx.PSSetConstantBuffer(1, _drawCb);
        ctx.PSSetSampler(0, sampler);
        ctx.OMSetBlendState(blendState, new Vortice.Mathematics.Color4(0, 0, 0, 0), 0xFFFFFFFF);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        for (int i = 0; i < count; i++)
        {
            var srv = _srvs[i];
            if (srv == null) continue;

            var mapped = ctx.Map(_drawCb!, MapMode.WriteDiscard);
            unsafe
            {
                byte* p = (byte*)mapped.DataPointer;
                *(uint*)p = (uint)i;
                *(float*)(p + 4) = 1.0f;
            }
            ctx.Unmap(_drawCb!);

            ctx.VSSetShader(_shadowVs);
            ctx.PSSetShader(_shadowPs);
            ctx.Draw(QuadVertexCount, 0);

            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_ps);
            ctx.PSSetShaderResource(1, srv);
            ctx.Draw(QuadVertexCount, 0);
        }

        ctx.OMSetBlendState(null, new Vortice.Mathematics.Color4(0, 0, 0, 0), 0xFFFFFFFF);
        ctx.VSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
    }

    private void UploadPending(ID3D11DeviceContext ctx)
    {
        int count;
        lock (_lock)
        {
            if (!_dirty) return;
            count = _count;
            for (int i = 0; i < count; i++) _srvs[i] = _srvsStaging[i];
            for (int i = count; i < _srvs.Length; i++) _srvs[i] = null;
            _dirty = false;
        }

        var mapped = ctx.Map(_instanceBuffer!, MapMode.WriteDiscard);
        int stride = Marshal.SizeOf<Instance>();
        unsafe
        {
            fixed (Instance* src = _instancesStaging)
            {
                System.Buffer.MemoryCopy(src, mapped.DataPointer.ToPointer(),
                    (long)mapped.RowPitch, (long)(stride * count));
            }
        }
        ctx.Unmap(_instanceBuffer!);
    }

    public void Dispose()
    {
        _vs?.Dispose();
        _ps?.Dispose();
        _shadowVs?.Dispose();
        _shadowPs?.Dispose();
        _instanceSrv?.Dispose();
        _instanceBuffer?.Dispose();
        _drawCb?.Dispose();
    }

    private const string ShaderSource = @"
cbuffer GridCB : register(b0)
{
    float2 camPos;
    float zoom;
    float screenW;
    float screenH;
    float time;
    float dpiScale;
    float panAccumX;
    float panAccumY;
    float passOffX;
    float passOffY;
    int monitorCount;
    int isPrimary;
    int _pad0;
    int _pad1;
};

struct ThumbInst { float4 ltrb; };
StructuredBuffer<ThumbInst> gThumbs : register(t0);

cbuffer ThumbDrawCB : register(b1)
{
    uint  gInstIdx;
    float gShadowMul;   // 1.0 default; reserved for future active-window highlight
    float2 _padInst;
};

Texture2D gThumbTexture : register(t1);
SamplerState gThumbSampler : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

#define SHADOW_PADDING  64.0
#define SHADOW_DROP     32.0
#define SHADOW_SHRINK_X 8.0
#define SHADOW_SHRINK_Y 44.0
#define SHADOW_ALPHA    0.20
#define SHADOW_SIGMA_X  20.0
#define SHADOW_SIGMA_Y  26.0
#define SHADOW_CORNER_R 8.0

float sdRoundedBox(float2 p, float2 b, float cr)
{
    float2 q = abs(p) - b + float2(cr, cr);
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - cr;
}

VSOut VSThumb(uint vid : SV_VertexID)
{
    float2 corners[6] = {
        float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1)
    };
    float2 uv = corners[vid];

    float4 r = gThumbs[gInstIdx].ltrb;
    float2 px = lerp(r.xy, r.zw, uv);
    float2 ndc = float2(px.x / screenW * 2.0 - 1.0, 1.0 - px.y / screenH * 2.0);

    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.uv = uv;
    return o;
}

float4 PSThumb(VSOut i) : SV_Target
{
    float4 c = gThumbTexture.Sample(gThumbSampler, i.uv);
    c.a = 1.0;

    float4 r = gThumbs[gInstIdx].ltrb;
    float2 center = (r.xy + r.zw) * 0.5;
    float2 half_ = (r.zw - r.xy) * 0.5;
    float dCorner = sdRoundedBox(i.pos.xy - center, half_, SHADOW_CORNER_R);
    c.a *= 1.0 - smoothstep(-0.5, 0.5, dCorner);
    return c;
}

VSOut VSShadow(uint vid : SV_VertexID)
{
    float2 corners[6] = {
        float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1)
    };
    float2 uv = corners[vid];

    float4 r = gThumbs[gInstIdx].ltrb;
    float2 rectSize = r.zw - r.xy;
    float pad = min(SHADOW_PADDING, min(rectSize.x - SHADOW_SHRINK_X, rectSize.y - SHADOW_SHRINK_Y) * 0.4);
    float2 expMin = r.xy - float2(pad, pad);
    float2 expMax = r.zw + float2(pad, pad + SHADOW_DROP);
    float2 px = lerp(expMin, expMax, uv);

    float2 ndc = float2(px.x / screenW * 2.0 - 1.0, 1.0 - px.y / screenH * 2.0);
    VSOut o;
    o.pos = float4(ndc, 0.0, 1.0);
    o.uv  = uv;
    return o;
}

float4 PSShadow(VSOut i) : SV_Target
{
    float4 r = gThumbs[gInstIdx].ltrb;

    float2 center = float2(
        (r.x + r.z) * 0.5,
        (r.y + r.w + SHADOW_DROP) * 0.5);
    float2 half_  = float2(
        max(1.0, (r.z - r.x) * 0.5 - SHADOW_SHRINK_X),
        max(1.0, (r.w - r.y + SHADOW_DROP - SHADOW_SHRINK_Y) * 0.5));

    float xScale = SHADOW_SIGMA_Y / SHADOW_SIGMA_X;
    float2 nPx   = (i.pos.xy - center) * float2(xScale, 1.0);
    float2 nHalf = half_             * float2(xScale, 1.0);
    float d = sdRoundedBox(nPx, nHalf, SHADOW_CORNER_R);
    float gaussian = exp(-d * d / (SHADOW_SIGMA_Y * SHADOW_SIGMA_Y));
    float clamped = d > 0 ? gaussian : 1.0;
    float a = SHADOW_ALPHA * gShadowMul * clamped;
    return float4(0.f, 0.0, 0.0, a);
}
";
}
