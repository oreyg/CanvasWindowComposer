using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// Window thumbnail pass. Owns a structured buffer of per-instance screen
/// rects (in pass-local pixels) and a parallel HWND list; draws each window
/// as a quad with its own WGC-captured texture sampled by the shader. One
/// <see cref="ID3D11DeviceContext.Draw(int, int)"/> per window so each can
/// bind its own SRV — N is bounded (&lt; 50 typical), so the per-draw
/// overhead is negligible.
/// </summary>
internal sealed class ThumbnailPass : IDisposable
{
    public const int MaxThumbnails = 256;
    private const int QuadVertexCount = 6;

    /// <summary>
    /// Per-instance thumbnail rect (pass-local screen pixels) plus the
    /// current WGC capture rate. The shader reads the rate to render a small
    /// debug color tag in the rect's top-left corner; remove the tag in
    /// <c>PSThumb</c> when the throttle heuristic is trusted.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public float Left, Top, Right, Bottom;
        public uint Rate;
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

    // Double-buffered: UI thread writes into _instancesStaging, render thread
    // copies into the GPU buffer at frame start. Lock is brief (memcpy of
    // ≤ MaxThumbnails * 16 bytes).
    private readonly Instance[] _instancesStaging = new Instance[MaxThumbnails];
    private readonly IntPtr[] _hwndsStaging = new IntPtr[MaxThumbnails];
    // Snapshot of the HWND list on the render thread. The instance GPU buffer
    // is CPU-only as far as WindowCapture lookup goes, so HWNDs live
    // alongside but never cross into GPU memory.
    private readonly IntPtr[] _hwnds = new IntPtr[MaxThumbnails];
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

    public void RegisterWindow(IntPtr hwnd, WindowCapture capture)
    {
        capture.Register(hwnd);
    }

    public void UnregisterWindow(IntPtr hwnd, WindowCapture capture)
    {
        capture.Unregister(hwnd);
    }

    public void SetInstances(ReadOnlySpan<Instance> instances, ReadOnlySpan<IntPtr> hwnds)
    {
        int n = Math.Min(Math.Min(instances.Length, hwnds.Length), MaxThumbnails);
        lock (_lock)
        {
            for (int i = 0; i < n; i++)
            {
                _instancesStaging[i] = instances[i];
                _hwndsStaging[i] = hwnds[i];
            }
            _count = n;
            _dirty = true;
        }
    }

    public void Render(ID3D11DeviceContext ctx, WindowCapture capture,
        ID3D11Buffer gridCb, ID3D11SamplerState sampler, ID3D11BlendState blendState)
    {
        UploadPending(ctx);
        int count = _count;
        if (count == 0) return;

        ctx.VSSetShaderResource(0, _instanceSrv);
        ctx.PSSetShaderResource(0, _instanceSrv!); // PS also reads rate + rect
        ctx.VSSetConstantBuffer(0, gridCb);
        ctx.VSSetConstantBuffer(1, _drawCb);
        ctx.PSSetConstantBuffer(0, gridCb);
        ctx.PSSetConstantBuffer(1, _drawCb); // PS needs gInstIdx too
        ctx.PSSetSampler(0, sampler);
        ctx.OMSetBlendState(blendState, new Vortice.Mathematics.Color4(0, 0, 0, 0), 0xFFFFFFFF);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        for (int i = 0; i < count; i++)
        {
            IntPtr hwnd = _hwnds[i];
            var srv = capture.Sample(hwnd, ctx);
            if (srv == null) continue;

            var mapped = ctx.Map(_drawCb!, MapMode.WriteDiscard);
            unsafe
            {
                byte* p = (byte*)mapped.DataPointer;
                *(uint*)p = (uint)i;
                *(float*)(p + 4) = 1.0f;
            }
            ctx.Unmap(_drawCb!);

            // Shadow first (enlarged quad behind the thumbnail). Later
            // instances' shadows land on earlier thumbnails — Win11 halo z-order.
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
        bool dirty;
        int count;
        lock (_lock)
        {
            dirty = _dirty;
            count = _count;
            if (!dirty) return;
            for (int i = 0; i < count; i++) _hwnds[i] = _hwndsStaging[i];
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
// Toggle the top-left capture-rate color tag (green/yellow/orange/red).
// Comment out to remove the tag — keeps the throttle heuristic running.
#define DEBUG_RATE_TAG 1

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
    float2 _pad;
};

struct ThumbInst { float4 ltrb; uint rate; };
StructuredBuffer<ThumbInst> gThumbs : register(t0);

// SV_InstanceID is relative to a single DrawInstanced call (always 0 when we
// draw one instance at a time), and StartInstanceLocation only offsets
// per-instance-vertex-buffer reads, not SV_InstanceID. To pick the right
// thumbnail rect per draw we pass the index via a small per-draw CB.
cbuffer ThumbDrawCB : register(b1)
{
    uint  gInstIdx;
    float gShadowMul;   // 1.0 for normal, 2.0 for the active (foreground) window
    float2 _padInst;
};

// PS-side texture sampler — per-instance window content captured via WGC,
// rebound from C# before each draw. Register numbers t0/t1 are independent
// across VS/PS stages in D3D11, so t0 on PS doesn't collide with the VS-only
// gThumbs.
Texture2D gThumbTexture : register(t1);
SamplerState gThumbSampler : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

// Shared between PSThumb (rounded-corner clip) and PSShadow (halo Gaussian).
#define SHADOW_PADDING  64.0
#define SHADOW_DROP     32.0    // extend shadow shape's bottom edge — drives the
                               // more-shadow-below look without any sample-offset
                               // or intensity-multiplier trick.
#define SHADOW_SHRINK_X 8.0    // pull shadow shape's left/right edges inward so
                               // the side halo is visibly thinner than the bottom.
#define SHADOW_SHRINK_Y 44.0   // pull shadow shape's top edge down so the halo
                               // above the window is thinner than below.
#define SHADOW_ALPHA    0.20
#define SHADOW_SIGMA_X  20.0   // horizontal Gaussian σ (tighter sides)
#define SHADOW_SIGMA_Y  26.0   // vertical Gaussian σ
#define SHADOW_CORNER_R 8.0

// Signed distance to a rounded rectangle centered at origin with half-size
// b and corner radius cr. Negative inside, positive outside, 0 on the edge.
// Standard IQ 2D SDF.
float sdRoundedBox(float2 p, float2 b, float cr)
{
    float2 q = abs(p) - b + float2(cr, cr);
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - cr;
}

// Quad layout: two triangles {0,1,2} {3,4,5}.
// Maps vertex id to a corner in [0..1]^2:
//   0 → (0,0)  1 → (1,0)  2 → (0,1)
//   3 → (0,1)  4 → (1,0)  5 → (1,1)
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
    c.a = 1.0; // Captured frames include opaque window content; force alpha to
               // avoid stale-alpha from whatever was in the texture previously.

    // Rounded-corner clip — same radius as the halo's SHADOW_CORNER_R so the
    // window silhouette matches the shadow silhouette. 1-pixel smoothstep band
    // for anti-aliased edges.
    float4 r = gThumbs[gInstIdx].ltrb;
    float2 center = (r.xy + r.zw) * 0.5;
    float2 half_ = (r.zw - r.xy) * 0.5;
    float dCorner = sdRoundedBox(i.pos.xy - center, half_, SHADOW_CORNER_R);
    c.a *= 1.0 - smoothstep(-0.5, 0.5, dCorner);

#ifdef DEBUG_RATE_TAG
    // 10px square in the top-left of the thumbnail tinted by capture rate so
    // the throttle policy is visible at a glance.
    //   Realtime=green, Half=yellow, Quarter=orange, Paused=red.
    float tagSize = 10.0;
    if (i.pos.x < r.x + tagSize && i.pos.y < r.y + tagSize)
    {
        uint rate = gThumbs[gInstIdx].rate;
        float3 tag;
        if (rate == 1)      tag = float3(0.0, 1.0, 0.0);
        else if (rate == 2) tag = float3(1.0, 1.0, 0.0);
        else if (rate == 4) tag = float3(1.0, 0.5, 0.0);
        else                tag = float3(1.0, 0.0, 0.0);
        c.rgb = tag;
    }
#endif
    return c;
}

// ==================== Shadow pass ====================
// Win11-style halo drop shadow. Drawn per-instance, BEFORE the thumbnail
// itself, so the later thumbnail overwrites the shadow inside its own rect.
// Later instances' shadows land on earlier thumbnails (z-order preserved).
//
// Shape: procedural Gaussian falloff of the box-distance to the thumbnail
// rect — no blur kernel, no extra texture. Quad is the thumbnail rect
// expanded by SHADOW_PADDING on each side, offset downward for drop.
// Defines + sdRoundedBox are above (shared with PSThumb).

VSOut VSShadow(uint vid : SV_VertexID)
{
    float2 corners[6] = {
        float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1)
    };
    float2 uv = corners[vid];

    float4 r = gThumbs[gInstIdx].ltrb;
    // Cap padding so tiny thumbnails don't get comically oversized shadows.
    float2 rectSize = r.zw - r.xy;
    float pad = min(SHADOW_PADDING, min(rectSize.x - SHADOW_SHRINK_X, rectSize.y - SHADOW_SHRINK_Y) * 0.4);
    // Bottom needs extra room for the shape's drop extension.
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

    // Anisotropic falloff: scale the horizontal axis into a normalized space
    // where SHADOW_SIGMA_Y is the effective σ. The rounded SDF runs in that
    // space, then the same iso Gaussian reads as σ_x horizontally, σ_y
    // vertically in world space.
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
