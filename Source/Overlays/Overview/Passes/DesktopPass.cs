using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// Desktop wallpaper pass. Samples the DWM shared surface of Progman/WorkerW
/// (owned + opened by <see cref="OverviewThumbnails"/>) at a UV sub-rect
/// corresponding to this monitor's slice of the virtual screen. Opacity is
/// computed upstream (mode + zoom aware); this pass just renders the params
/// it was last handed against the SRV it was last handed.
/// </summary>
internal sealed class DesktopPass : IDisposable
{
    private const int FullscreenTriangleVertexCount = 3;

    private static byte[]? _vsBytecode;
    private static byte[]? _psBytecode;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _cb;

    private readonly object _lock = new();
    private float _uvL, _uvT, _uvR, _uvB;
    private float _opacity;

    public static bool CompileShaders()
    {
        Compiler.Compile(ShaderSource, "VSDesktop", "", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob == null) { vsErr?.Dispose(); return false; }
        Compiler.Compile(ShaderSource, "PSDesktop", "", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob == null) { vsBlob.Dispose(); psErr?.Dispose(); return false; }
        _vsBytecode = vsBlob.AsSpan().ToArray();
        _psBytecode = psBlob.AsSpan().ToArray();
        vsBlob.Dispose();
        psBlob.Dispose();
        return true;
    }

    public DesktopPass(ID3D11Device device)
    {
        if (_vsBytecode == null || _psBytecode == null)
            throw new InvalidOperationException("DesktopPass.CompileShaders must run before construction");
        _vs = device.CreateVertexShader(_vsBytecode);
        _ps = device.CreatePixelShader(_psBytecode);
        // DesktopCB: 2x float2 (UV sub-rect) + float (opacity) + float3 pad = 32 bytes.
        _cb = device.CreateBuffer(new BufferDescription(
            32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    public void SetParams(float uvL, float uvT, float uvR, float uvB, float opacity)
    {
        lock (_lock)
        {
            _uvL = uvL; _uvT = uvT; _uvR = uvR; _uvB = uvB;
            _opacity = opacity;
        }
    }

    public void Render(ID3D11DeviceContext ctx, ID3D11ShaderResourceView? srv,
        ID3D11SamplerState sampler, ID3D11BlendState blendState)
    {
        if (srv == null) return;

        float uvL, uvT, uvR, uvB, op;
        lock (_lock)
        {
            uvL = _uvL; uvT = _uvT; uvR = _uvR; uvB = _uvB;
            op = _opacity;
        }
        if (op <= 0f) return;

        var mapped = ctx.Map(_cb!, MapMode.WriteDiscard);
        unsafe
        {
            float* p = (float*)mapped.DataPointer;
            p[0] = uvL; p[1] = uvT;
            p[2] = uvR; p[3] = uvB;
            p[4] = op; // remaining 3 floats are padding
        }
        ctx.Unmap(_cb!);

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(1, _cb);
        ctx.PSSetSampler(0, sampler);
        ctx.PSSetShaderResource(0, srv);
        ctx.OMSetBlendState(blendState, new Vortice.Mathematics.Color4(0, 0, 0, 0), 0xFFFFFFFF);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.Draw(FullscreenTriangleVertexCount, 0);
        ctx.OMSetBlendState(null, new Vortice.Mathematics.Color4(0, 0, 0, 0), 0xFFFFFFFF);
        ctx.PSSetShaderResource(0, null!);
    }

    public void Dispose()
    {
        _vs?.Dispose();
        _ps?.Dispose();
        _cb?.Dispose();
    }

    private const string ShaderSource = @"
// Fullscreen wallpaper pass. Samples the (virtual-screen-sized) WGC capture
// of Progman/WorkerW using a UV sub-rect specified by the manager — this
// pass's monitor slice of the virtual screen. Opacity is applied here so the
// grid beneath shows through in Zooming mode.
cbuffer DesktopCB : register(b1)
{
    float2 gDesktopUvMin;
    float2 gDesktopUvMax;
    float  gDesktopOpacity;
    float3 _padDesktop;
};

Texture2D gDesktopTex : register(t0);
SamplerState gDesktopSampler : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOut VSDesktop(uint vid : SV_VertexID)
{
    VSOut o;
    o.uv = float2((vid << 1) & 2, vid & 2);
    o.pos = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    return o;
}

float4 PSDesktop(VSOut i) : SV_Target
{
    float2 uv = lerp(gDesktopUvMin, gDesktopUvMax, i.uv);
    float4 c = gDesktopTex.Sample(gDesktopSampler, uv);
    c.a = gDesktopOpacity;
    return c;
}
";
}
