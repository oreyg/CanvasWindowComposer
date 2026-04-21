using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CanvasDesktop;

/// <summary>
/// D3D11 renderer for the overview. Owns the device + swap chain, the shared
/// view constant buffer, sampler, blend state, and WGC capture service, plus
/// a small set of render passes (grid, desktop, thumbnails). Each pass knows
/// how to draw itself and what resources it needs — this class wires them
/// together per-frame. Uses Vortice.Windows for clean DirectX interop.
/// </summary>
internal sealed class OverviewRenderer : IDisposable
{
    private const int VsyncInterval = 1;
    private const int RenderThreadJoinTimeoutMs = 1000;
    private const int CbAlignmentMask = 15; // 16-byte alignment

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Buffer? _gridCb;            // shared view CB (b0)
    private ID3D11BlendState? _blendState;    // standard src-alpha blend
    private ID3D11SamplerState? _sampler;     // bilinear clamp
    private WindowCapture? _capture;

    private GridPass? _gridPass;
    private DesktopPass? _desktopPass;
    private ThumbnailPass? _thumbnailPass;

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

    /// <summary>Compile pass shaders once at startup.</summary>
    public static bool CompileShaders()
    {
        return GridPass.CompileShaders()
            && DesktopPass.CompileShaders()
            && ThumbnailPass.CompileShaders();
    }

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
        CreateSharedResources();
        _gridPass = new GridPass(_device!);
        _desktopPass = new DesktopPass(_device!);
        _thumbnailPass = new ThumbnailPass(_device!);
        _capture = new WindowCapture(_device!);
        return true;
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    private void CreateSharedResources()
    {
        int cbSize = (Marshal.SizeOf<GridConstants>() + CbAlignmentMask) & ~CbAlignmentMask;
        _gridCb = _device!.CreateBuffer(new BufferDescription(
            (uint)cbSize, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blendState = _device.CreateBlendState(blendDesc);

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue
        });
    }

    // ==================== Public API forwarded to passes ====================

    public bool DrawGrid
    {
        get { return _gridPass?.DrawGrid ?? true; }
        set { if (_gridPass != null) _gridPass.DrawGrid = value; }
    }

    public void RegisterCaptureWindow(IntPtr hwnd)
    {
        if (_thumbnailPass != null && _capture != null)
            _thumbnailPass.RegisterWindow(hwnd, _capture);
    }

    public void UnregisterCaptureWindow(IntPtr hwnd)
    {
        if (_thumbnailPass != null && _capture != null)
            _thumbnailPass.UnregisterWindow(hwnd, _capture);
    }

    /// <summary>Set the WGC frame-pull cadence for a registered HWND.</summary>
    public void SetCaptureRate(IntPtr hwnd, WindowCapture.Rate rate)
    {
        _capture?.SetRate(hwnd, rate);
    }

    public void SetThumbnailInstances(ReadOnlySpan<ThumbnailPass.Instance> instances, ReadOnlySpan<IntPtr> hwnds)
    {
        _thumbnailPass?.SetInstances(instances, hwnds);
    }

    public void RegisterDesktopWindow(IntPtr hwnd)
    {
        if (_desktopPass != null && _capture != null)
            _desktopPass.RegisterWindow(hwnd, _capture);
    }

    public void UnregisterDesktopWindow()
    {
        if (_desktopPass != null && _capture != null)
            _desktopPass.UnregisterWindow(_capture);
    }

    public void SetDesktopParams(float uvL, float uvT, float uvR, float uvB, float opacity)
    {
        _desktopPass?.SetParams(uvL, uvT, uvR, uvB, opacity);
    }

    // ==================== Render loop ====================

    /// <summary>Fired on the render thread after each Present. Use for vsync-paced ticks.</summary>
    public Action? OnFrameTick;

    private float _dpiScale = 1.0f;
    private volatile bool _running;
    private volatile bool _alive = true;
    private volatile bool _renderThreadIdle = true;
    private readonly System.Threading.ManualResetEventSlim _wakeEvent = new(false);
    private System.Threading.Thread? _renderThread;

    // Camera state read by the render thread
    private volatile float _renderCamX, _renderCamY, _renderZoom;
    private float _panAccumX, _panAccumY; // only accumulates pan, not zoom-induced cam changes

    public void SetDpiScale(float scale) => _dpiScale = scale;
    public void ResetClock() => _clock.Restart();

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
            Name = "OverviewRenderer"
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        while (_alive)
        {
            _renderThreadIdle = true;
            _wakeEvent.Wait(); // sleep until Start() signals
            _renderThreadIdle = false;
            _clock.Restart(); // reset time for fade-in blend

            while (_running && _alive)
            {
                RenderFrame();
                // Present(1) inside RenderFrame waits for VSync
            }

            _wakeEvent.Reset(); // go back to sleep
        }
        _renderThreadIdle = true;
    }

    /// <summary>Resize swap chain. Pauses render thread, resizes, resumes if was running.</summary>
    public void Resize(int width, int height)
    {
        if (_swapChain == null || (_width == width && _height == height)) return;

        bool wasRunning = _running;
        _running = false;
        _wakeEvent.Reset();
        // Spin until render thread finishes the current frame and goes idle
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

        var mapped = _context.Map(_gridCb!, MapMode.WriteDiscard);
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
        _context.Unmap(_gridCb!);

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(0, 0, _width, _height);

        _gridPass?.Render(_context, _rtv!, _gridCb!);
        if (_capture != null)
        {
            _desktopPass?.Render(_context, _capture, _sampler!, _blendState!);
            _thumbnailPass?.Render(_context, _capture, _gridCb!, _sampler!, _blendState!);
        }

        _swapChain.Present(VsyncInterval, PresentFlags.None);
        OnFrameTick?.Invoke();
    }

    public void Dispose()
    {
        _alive = false;
        _running = false;
        _wakeEvent.Set(); // wake to exit
        _renderThread?.Join(RenderThreadJoinTimeoutMs);
        _wakeEvent.Dispose();

        _capture?.Dispose();
        _gridPass?.Dispose();
        _desktopPass?.Dispose();
        _thumbnailPass?.Dispose();
        _rtv?.Dispose();
        _gridCb?.Dispose();
        _blendState?.Dispose();
        _sampler?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
