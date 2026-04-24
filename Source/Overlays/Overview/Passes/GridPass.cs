using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// Fullscreen-triangle grid pass. Reads the shared view CB (camera, size,
/// time, DPI, pan accumulator, pass offset, per-monitor frame data) and
/// renders the adaptive infinite grid plus nebula parallax. Falls back to
/// clearing the RT when <see cref="DrawGrid"/> is false — used in Panning
/// mode where the grid is hidden behind opaque window thumbnails.
///
/// Owns a small StructuredBuffer of monitor rects (world-space) so the
/// primary pass can draw the camera-viewport corner brackets once per
/// monitor; non-primary passes skip that loop.
/// </summary>
internal sealed class GridPass : IDisposable
{
    private const int FullscreenTriangleVertexCount = 3;
    private const int MonitorBufferCapacity = 16;
    private const int MonitorStructBytes = 16; // two float2 = (offset, size)

    private static byte[]? _vsBytecode;
    private static byte[]? _psBytecode;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _monitorBuffer;
    private ID3D11ShaderResourceView? _monitorSrv;

    private readonly float[] _monitors = new float[MonitorBufferCapacity * 4];
    private volatile int _monitorCount;
    private readonly object _monitorsLock = new();

    public bool DrawGrid { get; set; } = true;

    public static bool CompileShaders()
    {
        Compiler.Compile(ShaderSource, "VSMain", "", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob == null) { vsErr?.Dispose(); return false; }
        Compiler.Compile(ShaderSource, "PSMain", "", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob == null) { vsBlob.Dispose(); psErr?.Dispose(); return false; }
        _vsBytecode = vsBlob.AsSpan().ToArray();
        _psBytecode = psBlob.AsSpan().ToArray();
        vsBlob.Dispose();
        psBlob.Dispose();
        return true;
    }

    public GridPass(ID3D11Device device)
    {
        if (_vsBytecode == null || _psBytecode == null)
            throw new InvalidOperationException("GridPass.CompileShaders must run before construction");
        _vs = device.CreateVertexShader(_vsBytecode);
        _ps = device.CreatePixelShader(_psBytecode);

        var desc = new BufferDescription
        {
            ByteWidth = (uint)(MonitorBufferCapacity * MonitorStructBytes),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)MonitorStructBytes
        };
        _monitorBuffer = device.CreateBuffer(desc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)MonitorBufferCapacity }
        };
        _monitorSrv = device.CreateShaderResourceView(_monitorBuffer, srvDesc);
    }

    /// <summary>
    /// Push the current monitor layout for this pass. OverviewRenderer
    /// writes the shared CB (<c>passOff</c>, <c>isPrimary</c>, <c>monitorCount</c>);
    /// this call just updates the SRV that the shader indexes when
    /// <c>isPrimary != 0</c> to draw the per-monitor corner brackets.
    /// </summary>
    public void SetMonitorLayout(System.Drawing.Rectangle[] monitors)
    {
        int count = Math.Min(monitors.Length, MonitorBufferCapacity);
        lock (_monitorsLock)
        {
            for (int i = 0; i < count; i++)
            {
                _monitors[i * 4 + 0] = monitors[i].X;
                _monitors[i * 4 + 1] = monitors[i].Y;
                _monitors[i * 4 + 2] = monitors[i].Width;
                _monitors[i * 4 + 3] = monitors[i].Height;
            }
            _monitorCount = count;
        }
    }

    public int MonitorCount
    {
        get { return _monitorCount; }
    }

    public void Render(ID3D11DeviceContext ctx, ID3D11RenderTargetView rtv, ID3D11Buffer gridCb)
    {
        if (!DrawGrid)
        {
            ctx.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            return;
        }

        if (_monitorCount > 0)
        {
            var mapped = ctx.Map(_monitorBuffer!, MapMode.WriteDiscard);
            lock (_monitorsLock)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    _monitors, 0, mapped.DataPointer, _monitorCount * 4);
            }
            ctx.Unmap(_monitorBuffer!);
        }

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, gridCb);
        ctx.PSSetShaderResource(0, _monitorSrv!);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.Draw(FullscreenTriangleVertexCount, 0);
    }

    public void Dispose()
    {
        _vs?.Dispose();
        _ps?.Dispose();
        _monitorSrv?.Dispose();
        _monitorBuffer?.Dispose();
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

struct MonitorRect
{
    float2 offset;
    float2 size;
};
StructuredBuffer<MonitorRect> monitors : register(t0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    return o;
}

// Anti-aliased grid line
float gridLine(float coord, float spacing, float lineWidth)
{
    float d = abs(frac(coord / spacing + 0.5) - 0.5) * spacing;
    float aa = fwidth(coord) * 1.5;
    return 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d);
}

// Dashed pattern: returns 0 or 1
float dashPattern(float coord, float dashLen, float offset)
{
    return step(0.5, frac((coord + offset) / dashLen));
}

// X mark at grid intersections: two diagonal lines crossing
float xMark(float2 localPos, float size, float lineWidth)
{
    float aa = fwidth(localPos.x) * 1.5;
    float d1 = abs(localPos.x - localPos.y);
    float d2 = abs(localPos.x + localPos.y);
    float line1 = 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d1);
    float line2 = 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d2);
    float mask = step(abs(localPos.x), size) * step(abs(localPos.y), size);
    return saturate(line1 + line2) * mask;
}

// Tiled X marks at regular spacing
float xGrid(float2 wp, float spacing, float xSize, float lineWidth)
{
    float2 cell = frac(wp / spacing + 0.5) - 0.5;
    float2 localPos = cell * spacing;
    return xMark(localPos, xSize, lineWidth);
}

// Grid level of a line: coarsest spacing that aligns to this position.
// Uses trailing zeros of the grid index to find which zoom level the line belongs to.
float lineLevel(float coord, float spacing)
{
    uint n = (uint)abs(round(coord / spacing));
    if (n == 0) return spacing * 32.0;
    uint tz = min(firstbitlow(n), 5u);
    return spacing * pow(2.0, (float)tz);
}

// Dashed grid — lineLevel affects brightness, not spacing
float dashedGrid(float2 wp, float spacing, float lw, float z, int spMul)
{
    float logZ = log2(z * 100.0 / 80.0);
    float blend = logZ - floor(logZ);
    float ps = spacing * 0.03125 * pow(2.0, (float)spMul);
    float lineX = gridLine(wp.x, spacing, lw);
    float lineY = gridLine(wp.y, spacing, lw);
    float dashX = dashPattern(wp.y, ps, ps * 0.25);
    float dashY = dashPattern(wp.x, ps, ps * 0.25);
    float lvlX = lineLevel(wp.x, spacing);
    float lvlY = lineLevel(wp.y, spacing);
    float tzX = log2(lvlX / spacing);
    float tzY = log2(lvlY / spacing);
    float fadeX = smoothstep(0.0, 0.4, blend + tzX * 0.2);
    float fadeY = smoothstep(0.0, 0.4, blend + tzY * 0.2);
    float brightX = 1.0 + tzX * 0.3;
    float brightY = 1.0 + tzY * 0.3;
    return lineX * dashX * fadeX * brightX + lineY * dashY * fadeY * brightY;
}

// === Nebula effect (from shadertoy.com/view/sdlyz8) ===
float nebRandom(float2 st)
{
    return frac(sin(dot(st, float2(12.9898, 78.233))) * 43758.5453123);
}

float nebNoise(float2 st)
{
    float2 i = floor(st);
    float2 f = frac(st);
    float a = nebRandom(i);
    float b = nebRandom(i + float2(1.0, 0.0));
    float c = nebRandom(i + float2(0.0, 1.0));
    float d = nebRandom(i + float2(1.0, 1.0));
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

float nebFbm(float2 st)
{
    float v = 0.0;
    float a = 0.5;
    float2 shift = float2(100.0, 100.0);
    float cs = cos(0.5);
    float sn = sin(0.5);
    float2x2 rot = float2x2(cs, sn, -sn, cs);
    for (int i = 0; i < 5; i++)
    {
        v += a * nebNoise(st);
        st = mul(rot, st) * 2.0 + shift;
        a *= 0.5;
    }
    return v;
}

float3 nebula(float2 uv, float t)
{
    float2 st = uv * 3.0;
    float2 q;
    q.x = nebFbm(st);
    q.y = nebFbm(st + float2(1.0, 0.0));
    float2 r;
    r.x = nebFbm(st + q + float2(1.7, 9.2) + 0.15 * t);
    r.y = nebFbm(st + q + float2(8.3, 2.8) + 0.126 * t);
    float f = nebFbm(st + r);
    float3 color = lerp(float3(0.05, 0.0, 0.1), float3(0.1, 0.1, 0.08), saturate(f * f * 4.0));
    color = lerp(color, float3(0.0, 0.0, 0.03), saturate(length(q)));
    color = lerp(color, float3(0.1, 0.15, 0.15), saturate(length(r.x)));
    return (f * f * f + 0.6 * f * f + 0.5 * f) * color;
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 screenPos = input.uv * float2(screenW, screenH);

    float2 worldPos = (screenPos + float2(passOffX, passOffY)) / zoom + camPos;

    // === Adaptive grid spacing (doubles per zoom level) ===
    float logZoom = log2(zoom * 100.0 / 80.0);
    float zoomLevel = floor(logZoom);
    float levelBlend = logZoom - zoomLevel;

    float gridBase  = 100.0 * pow(2.0, -zoomLevel);
    float gridMajor = gridBase * 5.0;
    float gridSub   = gridMajor * 0.5;

    float lineWidth = 0.4 / zoom;

    // === Line detection per grid tier (highest priority wins) ===
    float minPx = dpiScale / zoom;
    float originWidth = max(0.15 / zoom, minPx * 0.5);

    float origin = saturate(
        gridLine(worldPos.x, 1e6, originWidth) +
        gridLine(worldPos.y, 1e6, originWidth));

    float major = saturate(
        gridLine(worldPos.x, gridMajor, lineWidth) +
        gridLine(worldPos.y, gridMajor, lineWidth));

    float sub = saturate(
        gridLine(worldPos.x, gridSub, lineWidth * 0.8) +
        gridLine(worldPos.y, gridSub, lineWidth * 0.8));

    float brightness = smoothstep(0.005, 0.3, zoom);

    // === Nebula background (screen-fixed, fades in at max zoom-out) ===
    float nebulaBlend = smoothstep(0.7, 0.05, zoom);
    float2 nebulaUV = input.uv * float2(screenW / screenH, 1.0) - float2(panAccumX, panAccumY) * 0.000002;
    float3 color = lerp(float3(0.04, 0.045, 0.05), nebula(nebulaUV, time), nebulaBlend);

    // === Grid lines (exclusive priority chain — no overlaps) ===
    float3 gridGlow = float3(0, 0, 0);
    if (origin > 0.01)
    {
        float g = 0.5 + 0.5 * smoothstep(0.01, 0.5, zoom);
        gridGlow = float3(0.4, 0.85, 1.0) * origin * 0.8 * g;
    }
    else if (major > 0.01)
    {
        float p = dashedGrid(worldPos, gridMajor, lineWidth * 0.8, zoom, 0);
        gridGlow = float3(0.75, 0.85, 0.9) * saturate(p) * 0.70 * brightness;
    }
    else if (sub > 0.01)
    {
        float p = dashedGrid(worldPos, gridSub, lineWidth * 0.6, zoom, 1);
        gridGlow = float3(0.65, 0.75, 0.8) * saturate(p) * 0.40 * brightness * levelBlend;
    }
    color += gridGlow;

    {
        // Decorative marks with pan-parallax
        float2 pan = float2(panAccumX, panAccumY);
        float spacing = 0.5;
        float markSize = 0.012;
        float msqrSize = 0.008;
        float dotsSize = 0.0015;
        float markWidth = 0.001;
        float t = saturate((zoom - 0.4) / 0.4);
        float fade = t * t;
        float2 farPos = screenPos * 0.0015;
        float2 markUV = farPos - pan * 0.0005;
        float2 msqrUV = farPos - pan * 0.00045;
        float2 dotsUV = farPos - pan * 0.0004;
        float marks = xGrid(markUV,                 spacing,       markSize, markWidth);
        float msqr  = xGrid(msqrUV + spacing * 0.5, spacing,       msqrSize, msqrSize);
        float dots  = xGrid(dotsUV + spacing,       spacing * 0.5, dotsSize, dotsSize);

        float3 glow = float3(0.0, 0.9, 1.0) * saturate(marks) * 0.8
                    + float3(0.0, 0.7, 0.9) * saturate(msqr) * 0.2
                    + float3(0.0, 0.9, 1.0) * saturate(dots) * 0.4;
        color += glow * fade;
    }

    if (isPrimary != 0)
    {
        for (int mi = 0; mi < monitorCount; mi++)
        {
            float2 mOff  = monitors[mi].offset;
            float2 mSize = monitors[mi].size;

            float2 mOrigin = float2(screenW, screenH) * 0.5
                            - mSize * zoom * 0.5
                            + (mOff - float2(passOffX, passOffY)) * zoom;
            float vw = mSize.x * zoom;
            float vh = mSize.y * zoom;
            float arm = min(vw, vh) * 0.025;
            float lw = 3.0;

            float2 p = screenPos - mOrigin;
            float nearX = min(p.x, vw - p.x);
            float nearY = min(p.y, vh - p.y);
            bool inside = p.x >= 0 && p.x <= vw && p.y >= 0 && p.y <= vh;
            float onEdgeX = step(nearX, lw) * step(nearY, arm);
            float onEdgeY = step(nearY, lw) * step(nearX, arm);
            float corners = inside ? saturate(onEdgeX + onEdgeY) : 0;

            color += float3(0.0, 0.7, 1.0) * saturate(corners) * 0.4;
        }
    }

    return float4(color, 1.0);
}
";
}
