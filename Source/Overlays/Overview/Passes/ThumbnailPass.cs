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

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
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
        Compiler.Compile(ShaderSource, "VSThumb", "", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob == null) { vsErr?.Dispose(); return false; }
        Compiler.Compile(ShaderSource, "PSThumb", "", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob == null) { vsBlob.Dispose(); psErr?.Dispose(); return false; }
        _vsBytecode = vsBlob.AsSpan().ToArray();
        _psBytecode = psBlob.AsSpan().ToArray();
        vsBlob.Dispose();
        psBlob.Dispose();
        return true;
    }

    public ThumbnailPass(ID3D11Device device)
    {
        if (_vsBytecode == null || _psBytecode == null)
            throw new InvalidOperationException("ThumbnailPass.CompileShaders must run before construction");
        _vs = device.CreateVertexShader(_vsBytecode);
        _ps = device.CreatePixelShader(_psBytecode);

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

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.VSSetShaderResource(0, _instanceSrv);
        ctx.PSSetShaderResource(0, _instanceSrv!); // PS also reads rate for debug tag
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

            var mappedIdx = ctx.Map(_drawCb!, MapMode.WriteDiscard);
            unsafe { *((uint*)mappedIdx.DataPointer) = (uint)i; }
            ctx.Unmap(_drawCb!);

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
    uint gInstIdx;
    uint3 _padInst;
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

#ifdef DEBUG_RATE_TAG
    // 10px square in the top-left of the thumbnail tinted by capture rate so
    // the throttle policy is visible at a glance.
    //   Realtime=green, Half=yellow, Quarter=orange, Paused=red.
    float4 r = gThumbs[gInstIdx].ltrb;
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
";
}
