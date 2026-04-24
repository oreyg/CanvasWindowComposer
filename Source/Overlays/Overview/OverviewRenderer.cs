using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CanvasDesktop;

/// <summary>
/// D3D11 renderer for one overview pass (one per monitor). Owns the device,
/// swap chain, shared view constant buffer, sampler, and blend state, plus
/// a small set of render passes (grid, desktop, thumbnails). Pass resources
/// (window thumbnails, wallpaper, taskbars) are opened as DWM shared
/// surfaces by <see cref="OverviewThumbnails"/> and pushed in as SRVs —
/// this class only wires them together per frame.
/// </summary>
internal sealed class OverviewRenderer : IDisposable
{
    private const int VsyncInterval = 1;
    private const int RenderThreadJoinTimeoutMs = 1000;
    private const int CbAlignmentMask = 15;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Buffer? _gridCb;
    private ID3D11BlendState? _blendState;
    private ID3D11SamplerState? _sampler;

    private GridPass? _gridPass;
    private DesktopPass? _desktopPass;
    private ThumbnailPass? _thumbnailPass;

    private int _width, _height;

    // Pass-layout params fed into the shared CB. OverviewManager pushes these
    // via SetScreenLayout on every Show / display change; they stay constant
    // within a single overview session for a given pass.
    private volatile float _passOffX, _passOffY;
    private volatile int _isPrimary;

    // Desktop SRV + UV sub-rect + opacity come from OverviewThumbnails each
    // reconcile; thumbnail SRVs come from SetThumbnailInstances.
    private ID3D11ShaderResourceView? _desktopSrv;

    [StructLayout(LayoutKind.Sequential)]
    private struct GridConstants
    {
        public float CamX, CamY;
        public float Zoom;
        public float ScreenW, ScreenH;
        public float Time;
        public float DpiScale;
        public float PanAccumX, PanAccumY;
        public float PassOffX, PassOffY;
        public int MonitorCount;
        public int IsPrimary;
        public int _pad0, _pad1;
    }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

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
        return true;
    }

    /// <summary>
    /// The D3D11 device backing this pass. Exposed so
    /// <see cref="OverviewThumbnails"/> can open DWM shared surfaces on the
    /// same device that will sample them.
    /// </summary>
    public ID3D11Device? Device
    {
        get { return _device; }
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

    /// <summary>
    /// Configure this pass's world-frame offset and primary-ness, plus the
    /// full monitor layout used by the grid shader to draw per-monitor
    /// camera corner brackets on the primary pass.
    /// </summary>
    public void SetScreenLayout(int passOffX, int passOffY, bool isPrimary, System.Drawing.Rectangle[] monitors)
    {
        _passOffX = passOffX;
        _passOffY = passOffY;
        _isPrimary = isPrimary ? 1 : 0;
        _gridPass?.SetMonitorLayout(monitors);
    }

    /// <summary>
    /// Push the desktop (wallpaper) SRV, UV sub-rect, and opacity for the
    /// next frame. A null SRV hides the pass.
    /// </summary>
    public void SetDesktop(ID3D11ShaderResourceView? srv, float uvL, float uvT, float uvR, float uvB, float opacity)
    {
        _desktopSrv = srv;
        _desktopPass?.SetParams(uvL, uvT, uvR, uvB, opacity);
    }

    /// <summary>
    /// Push the per-thumbnail rects + matching SRVs for the next frame.
    /// Rects are in pass-local pixels. <paramref name="srvs"/> must parallel
    /// <paramref name="instances"/>; entries with a null SRV are skipped.
    /// </summary>
    public void SetThumbnailInstances(
        ReadOnlySpan<ThumbnailPass.Instance> instances,
        ReadOnlySpan<ID3D11ShaderResourceView?> srvs)
    {
        _thumbnailPass?.SetInstances(instances, srvs);
    }

    // ==================== Render loop ====================

    public Action? OnFrameTick;

    private float _dpiScale = 1.0f;
    private volatile bool _running;
    private volatile bool _alive = true;
    private volatile bool _renderThreadIdle = true;
    private readonly System.Threading.ManualResetEventSlim _wakeEvent = new(false);
    private System.Threading.Thread? _renderThread;

    private volatile float _renderCamX, _renderCamY, _renderZoom;
    private float _panAccumX, _panAccumY;

    public void SetDpiScale(float scale)
    {
        _dpiScale = scale;
    }

    public void ResetClock()
    {
        _clock.Restart();
    }

    public void AccumulatePan(double dx, double dy)
    {
        _panAccumX += (float)dx;
        _panAccumY += (float)dy;
    }

    public void Render(double camX, double camY, double zoom)
    {
        _renderCamX = (float)camX;
        _renderCamY = (float)camY;
        _renderZoom = (float)zoom;
        RenderFrame();
    }

    public void Start(double camX, double camY, double zoom)
    {
        _renderCamX = (float)camX;
        _renderCamY = (float)camY;
        _renderZoom = (float)zoom;
        _running = true;
        _wakeEvent.Set();
    }

    public void UpdateCamera(double camX, double camY, double zoom)
    {
        _renderCamX = (float)camX;
        _renderCamY = (float)camY;
        _renderZoom = (float)zoom;
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
            Name = "OverviewRenderer"
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
            _clock.Restart();

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

        int monitorCount = _gridPass?.MonitorCount ?? 0;

        var mapped = _context.Map(_gridCb!, MapMode.WriteDiscard);
        var constants = new GridConstants
        {
            CamX = _renderCamX, CamY = _renderCamY,
            Zoom = _renderZoom,
            ScreenW = _width, ScreenH = _height,
            Time = (float)_clock.Elapsed.TotalSeconds,
            DpiScale = _dpiScale,
            PanAccumX = _panAccumX, PanAccumY = _panAccumY,
            PassOffX = _passOffX, PassOffY = _passOffY,
            MonitorCount = monitorCount,
            IsPrimary = _isPrimary
        };
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_gridCb!);

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(0, 0, _width, _height);

        _gridPass?.Render(_context, _rtv!, _gridCb!);
        _desktopPass?.Render(_context, _desktopSrv, _sampler!, _blendState!);
        _thumbnailPass?.Render(_context, _gridCb!, _sampler!, _blendState!);

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
