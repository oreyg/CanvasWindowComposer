using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// Renders an adaptive infinite grid using D3D11 + HLSL pixel shader.
/// Uses Vortice.Windows for clean DirectX interop.
/// </summary>
internal sealed class GridRenderer : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11Buffer? _constantBuffer;
    private int _width, _height;

    [StructLayout(LayoutKind.Sequential)]
    private struct GridConstants
    {
        public float CamX, CamY;
        public float Zoom;
        public float ScreenW, ScreenH;
        public float Time;
        public float DpiScale;
        public float PanAccumX, PanAccumY;
        public float _pad0, _pad1;
    }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // Pre-compiled shader bytecode (compile once at startup)
    private static byte[]? _vsBytecode;
    private static byte[]? _psBytecode;

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
    float2 _pad;
};

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

    // === World-space coordinate ===
    float2 worldPos = screenPos / zoom + camPos;

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
        gridLine(worldPos.x, 1e6, originWidth) * step(abs(worldPos.x), originWidth * 3.0) +
        gridLine(worldPos.y, 1e6, originWidth) * step(abs(worldPos.y), originWidth * 3.0));

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

    return float4(color, 1.0);
}
";

    public bool Initialize(IntPtr hwnd, int width, int height)
    {
        _width = width;
        _height = height;

        var swapDesc = new SwapChainDescription
        {
            BufferCount = 1,
            BufferDescription = new ModeDescription((uint)width, (uint)height, Format.R8G8B8A8_UNorm),
            BufferUsage = Usage.RenderTargetOutput,
            OutputWindow = hwnd,
            SampleDescription = new SampleDescription(1, 0),
            Windowed = true,
            SwapEffect = SwapEffect.Discard
        };

        var hr = D3D11.D3D11CreateDeviceAndSwapChain(
            null!, DriverType.Hardware, DeviceCreationFlags.None, null!,
            swapDesc, out _swapChain, out _device, out _, out _context);

        if (hr.Failure) return false;

        CreateRenderTarget();
        if (!CreateShaders()) return false;
        CreateConstantBuffer();
        return true;
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    private bool CreateShaders()
    {
        if (_vsBytecode == null || _psBytecode == null) return false;
        _vertexShader = _device!.CreateVertexShader(_vsBytecode);
        _pixelShader = _device!.CreatePixelShader(_psBytecode);
        return true;
    }

    private void CreateConstantBuffer()
    {
        int cbSize = (Marshal.SizeOf<GridConstants>() + 15) & ~15; // align to 16
        _constantBuffer = _device!.CreateBuffer(new BufferDescription(
            (uint)cbSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private float _dpiScale = 1.0f;
    private volatile bool _running;
    private volatile bool _alive = true;
    private readonly System.Threading.ManualResetEventSlim _wakeEvent = new(false);
    private System.Threading.Thread? _renderThread;

    // Camera state read by the render thread
    private volatile float _renderCamX, _renderCamY, _renderZoom;
    private float _panAccumX, _panAccumY; // only accumulates pan, not zoom-induced cam changes

    public void SetDpiScale(float scale) => _dpiScale = scale;

    /// <summary>Accumulate pan movement (not zoom). Drives nebula parallax.</summary>
    public void AccumulatePan(double dx, double dy)
    {
        _panAccumX += (float)dx;
        _panAccumY += (float)dy;
    }

    /// <summary>Render a single frame synchronously (legacy API).</summary>
    public void Render(double camX, double camY, double zoom)
    {
        _renderCamX = (float)camX;
        _renderCamY = (float)camY;
        _renderZoom = (float)zoom;
        RenderFrame();
    }

    /// <summary>Start the render loop (wakes the suspended thread).</summary>
    public void Start(double camX, double camY, double zoom)
    {
        _renderCamX = (float)camX;
        _renderCamY = (float)camY;
        _renderZoom = (float)zoom;
        _running = true;
        _wakeEvent.Set();
    }

    /// <summary>Update camera for next frame (call from any thread).</summary>
    public void UpdateCamera(double camX, double camY, double zoom)
    {
        _renderCamX = (float)camX;
        _renderCamY = (float)camY;
        _renderZoom = (float)zoom;
    }

    /// <summary>Stop rendering and go back to sleep.</summary>
    public void Stop()
    {
        _running = false;
    }

    /// <summary>Start the background thread (call once after Initialize).</summary>
    public void StartThread()
    {
        _renderThread = new System.Threading.Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "GridRenderer"
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        while (_alive)
        {
            _wakeEvent.Wait(); // sleep until Start() signals

            while (_running && _alive)
            {
                RenderFrame();
                // Present(1) inside RenderFrame waits for VSync
            }

            _wakeEvent.Reset(); // go back to sleep
        }
    }

    private void RenderFrame()
    {
        if (_context == null || _swapChain == null) return;

        var mapped = _context.Map(_constantBuffer!, MapMode.WriteDiscard);
        var constants = new GridConstants
        {
            CamX = _renderCamX, CamY = _renderCamY,
            Zoom = _renderZoom,
            ScreenW = _width, ScreenH = _height,
            Time = (float)_clock.Elapsed.TotalSeconds,
            DpiScale = _dpiScale,
            PanAccumX = _panAccumX, PanAccumY = _panAccumY
        };
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_constantBuffer!);

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(0, 0, _width, _height);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.Draw(3, 0);

        _swapChain.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        _alive = false;
        _running = false;
        _wakeEvent.Set(); // wake to exit
        _renderThread?.Join(1000);
        _wakeEvent.Dispose();

        _rtv?.Dispose();
        _constantBuffer?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
