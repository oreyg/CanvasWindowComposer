using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// D3D11 minimap renderer. UI thread pushes a projected snapshot
/// (<see cref="UpdateSnapshot"/>) and the render thread draws at vsync.
/// No paint work on the UI thread; mirrors <see cref="GridRenderer"/>'s
/// thread + swap-chain structure.
/// </summary>
internal sealed class MinimapRenderer : IDisposable
{
    private const int FullscreenTriangleVertexCount = 3;
    private const int VsyncInterval = 1;
    private const int RenderThreadJoinTimeoutMs = 1000;
    private const int CbAlignmentMask = 15;

    // Up to 256 windows shown. Anything past that is dropped silently — a
    // minimap that dense isn't useful anyway.
    private const int WindowBufferCapacity = 256;
    private const int WindowStructBytes = 16; // two float2 = (min, max)

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11Buffer? _windowsBuffer;
    private ID3D11ShaderResourceView? _windowsSrv;
    private int _width, _height;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinimapConstants
    {
        public float ScreenW, ScreenH;
        public float MapOriginX, MapOriginY;
        public float MapW, MapH;
        public int WindowCount;
        public int _pad0;
        public float ViewportMinX, ViewportMinY, ViewportMaxX, ViewportMaxY;
    }

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
cbuffer MinimapCB : register(b0)
{
    float screenW;
    float screenH;
    float mapOriginX;
    float mapOriginY;
    float mapW;
    float mapH;
    int windowCount;
    int _pad0;
    float4 viewportRect; // minX, minY, maxX, maxY in map-local pixels
};

struct MapRect
{
    float2 mn;
    float2 mx;
};
StructuredBuffer<MapRect> windows : register(t0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    return o;
}

// Signed distance from p to a rounded rect [mn, mx] with corner radius r.
// Negative inside, positive outside, zero on the edge.
float sdfRoundedRect(float2 p, float2 mn, float2 mx, float r)
{
    float2 halfSize = (mx - mn) * 0.5;
    float2 center = (mx + mn) * 0.5;
    // Clamp radius to the smaller half-extent so small rects don't invert.
    r = min(r, min(halfSize.x, halfSize.y));
    float2 q = abs(p - center) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 screenPos = input.uv * float2(screenW, screenH);
    float2 mapPos = screenPos - float2(mapOriginX, mapOriginY);
    float2 mapSize = float2(mapW, mapH);

    bool insideMap = mapPos.x >= 0 && mapPos.y >= 0 && mapPos.x <= mapSize.x && mapPos.y <= mapSize.y;

    float3 color = float3(0.118, 0.118, 0.118);
    if (insideMap)
    {
        color = float3(0.078, 0.078, 0.078);

        // Windows — rounded-rect SDF, AA on the boundary.
        const float cornerRadius = 2.5;
        float fill = 0.0;
        float edge = 0.0;
        for (int i = 0; i < windowCount; i++)
        {
            MapRect w = windows[i];
            float sd = sdfRoundedRect(mapPos, w.mn, w.mx, cornerRadius);
            // Fill inside, smooth AA across the boundary.
            float f = 1.0 - smoothstep(-0.5, 0.5, sd);
            fill = max(fill, f);
            // Edge: thin band centred on the boundary.
            float e = 1.0 - smoothstep(0.0, 1.5, abs(sd + 0.75));
            edge = max(edge, e);
        }
        float3 winFill = float3(0.31, 0.63, 1.0) * 0.4;
        float3 winEdge = float3(0.39, 0.71, 1.0);
        color = lerp(color, lerp(winFill, winEdge, edge), fill);

        // Viewport outline (sharp corners).
        float2 vmn = viewportRect.xy;
        float2 vmx = viewportRect.zw;
        if (mapPos.x >= vmn.x && mapPos.y >= vmn.y &&
            mapPos.x <= vmx.x && mapPos.y <= vmx.y)
        {
            float2 dMn = mapPos - vmn;
            float2 dMx = vmx - mapPos;
            float d = min(min(dMn.x, dMx.x), min(dMn.y, dMx.y));
            if (d < 1.5) color = float3(1.0, 0.78, 0.20);
        }

        // Inner border.
        float2 dMnB = mapPos;
        float2 dMxB = mapSize - mapPos;
        float dB = min(min(dMnB.x, dMxB.x), min(dMnB.y, dMxB.y));
        if (dB < 1.0) color = float3(0.5, 0.5, 0.5);
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
        CreateWindowsBuffer();
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
        _pixelShader = _device.CreatePixelShader(_psBytecode);
        return true;
    }

    private void CreateConstantBuffer()
    {
        int cbSize = (Marshal.SizeOf<MinimapConstants>() + CbAlignmentMask) & ~CbAlignmentMask;
        _constantBuffer = _device!.CreateBuffer(new BufferDescription(
            (uint)cbSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private void CreateWindowsBuffer()
    {
        var desc = new BufferDescription
        {
            ByteWidth = (uint)(WindowBufferCapacity * WindowStructBytes),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)WindowStructBytes
        };
        _windowsBuffer = _device!.CreateBuffer(desc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)WindowBufferCapacity }
        };
        _windowsSrv = _device.CreateShaderResourceView(_windowsBuffer, srvDesc);
    }

    public volatile bool DrawMinimap = true;
    public Action? OnFrameTick;

    private volatile bool _running;
    private volatile bool _alive = true;
    private volatile bool _renderThreadIdle = true;
    private readonly System.Threading.ManualResetEventSlim _wakeEvent = new(false);
    private System.Threading.Thread? _renderThread;

    // Shader-facing state (UI thread writes, render thread reads).
    private volatile int _mapOriginX, _mapOriginY, _mapW, _mapH;
    private volatile float _vpMinX, _vpMinY, _vpMaxX, _vpMaxY;
    private readonly float[] _windowRects = new float[WindowBufferCapacity * 4];
    private volatile int _windowCount;
    private readonly object _windowsLock = new();

    /// <summary>
    /// Push a fresh snapshot: world extents, viewport, windows → projected to
    /// map-local pixel coords. Called from the UI thread.
    /// </summary>
    public void UpdateSnapshot(
        IReadOnlyDictionary<IntPtr, WorldRect> windows,
        (double minX, double minY, double maxX, double maxY)? extents,
        (double x, double y, double w, double h) viewport,
        int mapOriginX, int mapOriginY, int mapW, int mapH,
        double extentsPadding = 0.10,
        int minRectSizePx = 2)
    {
        _mapOriginX = mapOriginX;
        _mapOriginY = mapOriginY;
        _mapW = mapW;
        _mapH = mapH;

        // Combined frame = world extents ∪ viewport, with padding.
        double minX, minY, maxX, maxY;
        if (extents != null)
        {
            var e = extents.Value;
            minX = Math.Min(e.minX, viewport.x);
            minY = Math.Min(e.minY, viewport.y);
            maxX = Math.Max(e.maxX, viewport.x + viewport.w);
            maxY = Math.Max(e.maxY, viewport.y + viewport.h);
        }
        else
        {
            minX = viewport.x;
            minY = viewport.y;
            maxX = viewport.x + viewport.w;
            maxY = viewport.y + viewport.h;
        }

        double worldW = maxX - minX;
        double worldH = maxY - minY;
        double padX = worldW * extentsPadding;
        double padY = worldH * extentsPadding;
        minX -= padX; minY -= padY;
        maxX += padX; maxY += padY;
        worldW = maxX - minX;
        worldH = maxY - minY;

        if (worldW < 1 || worldH < 1)
        {
            _windowCount = 0;
            return;
        }

        double scaleX = (mapW - 2) / worldW;
        double scaleY = (mapH - 2) / worldH;
        double scale = Math.Min(scaleX, scaleY);

        double drawW = worldW * scale;
        double drawH = worldH * scale;
        double offX = (mapW - drawW) / 2;
        double offY = (mapH - drawH) / 2;

        _vpMinX = (float)(offX + (viewport.x - minX) * scale);
        _vpMinY = (float)(offY + (viewport.y - minY) * scale);
        _vpMaxX = (float)(offX + (viewport.x + viewport.w - minX) * scale);
        _vpMaxY = (float)(offY + (viewport.y + viewport.h - minY) * scale);

        lock (_windowsLock)
        {
            int count = 0;
            foreach (var kv in windows)
            {
                if (count >= WindowBufferCapacity) break;
                var w = kv.Value;
                if (w.State != WindowState.Normal) continue;

                float mnX = (float)(offX + (w.X - minX) * scale);
                float mnY = (float)(offY + (w.Y - minY) * scale);
                float mxX = (float)(offX + (w.X + w.W - minX) * scale);
                float mxY = (float)(offY + (w.Y + w.H - minY) * scale);
                if (mxX - mnX < minRectSizePx) mxX = mnX + minRectSizePx;
                if (mxY - mnY < minRectSizePx) mxY = mnY + minRectSizePx;

                _windowRects[count * 4 + 0] = mnX;
                _windowRects[count * 4 + 1] = mnY;
                _windowRects[count * 4 + 2] = mxX;
                _windowRects[count * 4 + 3] = mxY;
                count++;
            }
            _windowCount = count;
        }
    }

    public void Start()
    {
        _running = true;
        _wakeEvent.Set();
    }

    public void Stop()
    {
        _running = false;
    }

    public void StartThread()
    {
        _renderThread = new System.Threading.Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "MinimapRenderer"
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        while (_alive)
        {
            _renderThreadIdle = true;
            _wakeEvent.Wait();
            _renderThreadIdle = false;

            while (_running && _alive)
            {
                RenderFrame();
            }

            _wakeEvent.Reset();
        }
        _renderThreadIdle = true;
    }

    public void Resize(int width, int height)
    {
        if (_swapChain == null || (_width == width && _height == height)) return;

        bool wasRunning = _running;
        _running = false;
        _wakeEvent.Reset();
        while (!_renderThreadIdle) System.Threading.Thread.Yield();

        _width = width;
        _height = height;
        _rtv?.Dispose();
        _rtv = null;
        _swapChain.ResizeBuffers(1, (uint)width, (uint)height, Format.R8G8B8A8_UNorm, 0);
        CreateRenderTarget();

        if (wasRunning)
        {
            _running = true;
            _wakeEvent.Set();
        }
    }

    private void RenderFrame()
    {
        if (_context == null || _swapChain == null) return;

        var cbMapped = _context.Map(_constantBuffer!, MapMode.WriteDiscard);
        var constants = new MinimapConstants
        {
            ScreenW = _width,
            ScreenH = _height,
            MapOriginX = _mapOriginX,
            MapOriginY = _mapOriginY,
            MapW = _mapW,
            MapH = _mapH,
            WindowCount = _windowCount,
            ViewportMinX = _vpMinX,
            ViewportMinY = _vpMinY,
            ViewportMaxX = _vpMaxX,
            ViewportMaxY = _vpMaxY
        };
        Marshal.StructureToPtr(constants, cbMapped.DataPointer, false);
        _context.Unmap(_constantBuffer!);

        if (_windowCount > 0)
        {
            var mapped = _context.Map(_windowsBuffer!, MapMode.WriteDiscard);
            lock (_windowsLock)
            {
                Marshal.Copy(_windowRects, 0, mapped.DataPointer, _windowCount * 4);
            }
            _context.Unmap(_windowsBuffer!);
        }

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(0, 0, _width, _height);

        if (DrawMinimap)
        {
            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.PSSetConstantBuffer(0, _constantBuffer);
            _context.PSSetShaderResource(0, _windowsSrv!);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.Draw(FullscreenTriangleVertexCount, 0);
        }
        else
        {
            _context.ClearRenderTargetView(_rtv!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        }

        _swapChain.Present(VsyncInterval, PresentFlags.None);
        OnFrameTick?.Invoke();
    }

    public void Dispose()
    {
        _alive = false;
        _running = false;
        _wakeEvent.Set();
        _renderThread?.Join(RenderThreadJoinTimeoutMs);
        _wakeEvent.Dispose();

        _rtv?.Dispose();
        _constantBuffer?.Dispose();
        _windowsSrv?.Dispose();
        _windowsBuffer?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
