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

// Dotted pattern: returns 0 or 1 based on position along the line
float dotPattern(float coord, float dotSpacing)
{
    return step(0.5, frac(coord / dotSpacing));
}

// Dashed pattern: returns 0 or 1
float dashPattern(float coord, float dashLen)
{
    return step(0.35, frac(coord / dashLen));
}

// X mark at grid intersections: two diagonal lines crossing
float xMark(float2 localPos, float size, float lineWidth)
{
    float aa = fwidth(localPos.x) * 1.5;
    // Two diagonals: |x-y| and |x+y|
    float d1 = abs(localPos.x - localPos.y);
    float d2 = abs(localPos.x + localPos.y);
    float line1 = 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d1);
    float line2 = 1.0 - smoothstep(lineWidth - aa, lineWidth + aa, d2);
    // Only draw within the X's bounding box
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

// Dashed grid for foreground
float dashedGrid(float2 wp, float spacing, float lw, float patternScale)
{
    float lineX = gridLine(wp.x, spacing, lw);
    float lineY = gridLine(wp.y, spacing, lw);
    float dashX = dashPattern(wp.y, patternScale);
    float dashY = dashPattern(wp.x, patternScale);
    return lineX * dashX + lineY * dashY;
}

// Dotted grid for foreground
float dottedGrid(float2 wp, float spacing, float lw, float patternScale)
{
    float lineX = gridLine(wp.x, spacing, lw);
    float lineY = gridLine(wp.y, spacing, lw);
    float dotX = dotPattern(wp.y, patternScale);
    float dotY = dotPattern(wp.x, patternScale);
    return lineX * dotX + lineY * dotY;
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

    // === Parallax layers ===
    float2 layer0 = screenPos / zoom + camPos;
    float2 layer1 = screenPos / zoom + camPos * 0.6;
    float2 layer2 = screenPos / zoom + camPos * 0.3;

    // === Adaptive spacing ===
    float logZoom = log2(zoom * 100.0 / 80.0);
    float level = floor(logZoom);
    float fade = logZoom - level;

    float baseSpacing = 100.0 * pow(2.0, -level);
    float fineSpacing = baseSpacing * 0.5;
    float majorSpacing = baseSpacing * 5.0;

    float lw = 0.4 / zoom;
    float xSize = 3.0 / zoom;

    // === Solid line detection for each level (used for priority) ===
    float minPx = dpiScale / zoom; // 1 physical pixel in world units
    float originLw = max(0.15 / zoom, minPx * 0.5);
    float onOriginX = gridLine(layer0.x, 1e6, originLw) * step(abs(layer0.x), originLw * 3.0);
    float onOriginY = gridLine(layer0.y, 1e6, originLw) * step(abs(layer0.y), originLw * 3.0);
    float onOrigin = saturate(onOriginX + onOriginY);

    float onMajorX = gridLine(layer0.x, majorSpacing, lw);
    float onMajorY = gridLine(layer0.y, majorSpacing, lw);
    float onMajor = saturate(onMajorX + onMajorY);

    float onMajorFinerX = gridLine(layer0.x, majorSpacing * 0.5, lw * 0.8);
    float onMajorFinerY = gridLine(layer0.y, majorSpacing * 0.5, lw * 0.8);
    float onMajorFiner = saturate(onMajorFinerX + onMajorFinerY);

    float onFineX = gridLine(layer0.x, baseSpacing, lw * 0.7);
    float onFineY = gridLine(layer0.y, baseSpacing, lw * 0.7);
    float onFine = saturate(onFineX + onFineY);

    float onFinerX = gridLine(layer0.x, fineSpacing, lw * 0.6);
    float onFinerY = gridLine(layer0.y, fineSpacing, lw * 0.6);
    float onFiner = saturate(onFinerX + onFinerY);

    // === Determine which level this pixel belongs to (highest wins) ===
    float intensity = smoothstep(0.02, 1.5, zoom);

    // Nebula background fades in at max zoom-out, scales with zoom
    float nebulaBlend = smoothstep(0.15, 0.05, zoom);
    // Screen UVs + slight camera offset — zoom doesnt scale it, only panning shifts it slowly
    // Screen-fixed with slight pan parallax (zoom does not affect it)
    float2 nebulaUV = input.uv * float2(screenW / screenH, 1.0) - float2(panAccumX, panAccumY) * 0.000002;
    float3 neb = nebula(nebulaUV, time);
    float3 bg = lerp(float3(0.04, 0.045, 0.05), neb, nebulaBlend);
    float3 color = bg;

    // Background X marks (only where no foreground grid)
    float deepSpacing = baseSpacing * 16.0;
    float midSpacing = baseSpacing * 8.0;

    if (onOrigin > 0.01)
    {
        // Origin — brightest, thin
        float originI = 0.5 + 0.5 * smoothstep(0.01, 0.5, zoom);
        color = lerp(color, float3(0.4, 0.85, 1.0), onOrigin * 0.8 * originI);
    }
    else if (onMajor > 0.01)
    {
        // Major dashed grid
        float pattern = dashedGrid(layer0, majorSpacing, lw * 0.8, majorSpacing * 0.05);
        color = lerp(color, float3(0.75, 0.85, 0.9), saturate(pattern) * 0.30 * intensity);
    }
    else if (onMajorFiner > 0.01)
    {
        // Major finer (fading in)
        float pattern = dashedGrid(layer0, majorSpacing * 0.5, lw * 0.6, majorSpacing * 0.025);
        color = lerp(color, float3(0.65, 0.75, 0.8), saturate(pattern) * 0.20 * intensity * fade);
    }
    else if (onFine > 0.01)
    {
        // Fine dotted grid
        float pattern = dottedGrid(layer0, baseSpacing, lw * 0.6, baseSpacing * 0.06);
        color = lerp(color, float3(0.7, 0.75, 0.8), saturate(pattern) * 0.18 * intensity);
    }
    else if (onFiner > 0.01)
    {
        // Finer dotted (fading in)
        float pattern = dottedGrid(layer0, fineSpacing, lw * 0.5, fineSpacing * 0.06);
        color = lerp(color, float3(0.6, 0.65, 0.7), saturate(pattern) * 0.12 * intensity * fade);
    }
    else
    {
        // Background X marks only where no grid lines exist
        float deepX = xGrid(layer2, deepSpacing, xSize * 1.5, lw * 0.4);
        float midX = xGrid(layer1, midSpacing, xSize * 1.2, lw * 0.4);
        color = lerp(color, float3(0.15, 0.4, 0.45), saturate(deepX) * 0.15 * intensity);
        color = lerp(color, float3(0.2, 0.45, 0.5), saturate(midX) * 0.13 * intensity);
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
        if (!CompileShaders()) return false;
        CreateConstantBuffer();
        return true;
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    private bool CompileShaders()
    {
        Compiler.Compile(ShaderSource, "VSMain", "", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob == null) { vsErr?.Dispose(); return false; }

        Compiler.Compile(ShaderSource, "PSMain", "", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob == null) { vsBlob.Dispose(); psErr?.Dispose(); return false; }

        _vertexShader = _device!.CreateVertexShader(vsBlob.AsBytes());
        _pixelShader = _device!.CreatePixelShader(psBlob.AsBytes());

        vsBlob.Dispose();
        psBlob.Dispose();
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
