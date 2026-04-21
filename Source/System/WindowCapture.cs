using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace CanvasDesktop;

/// <summary>
/// Captures live window contents as <see cref="ID3D11Texture2D"/>s via
/// Windows.Graphics.Capture. One session per HWND. Each session owns a
/// <see cref="Direct3D11CaptureFramePool"/> from which <see cref="Sample"/>
/// pulls the latest frame on the render thread and copies it into a
/// persistent texture so the shader can sample it freely.
///
/// The copy decouples WGC's frame lifetime from our sampling — frames
/// come back on arbitrary DispatcherQueue threads and the double-buffered
/// pool recycles them; sampling directly risks reading a frame while the
/// pool reclaims it. Copy once per arrived frame, sample forever.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    /// <summary>
    /// How often <see cref="Sample"/> actually pulls a new frame out of the
    /// WGC pool. Values other than <see cref="Realtime"/> skip the per-frame
    /// CopyResource — the shader keeps sampling the last captured texture.
    /// </summary>
    public enum Rate
    {
        Paused = 0,    // never pull — serve last frame indefinitely
        Realtime = 1,  // every render frame
        Half = 2,      // every 2nd
        Quarter = 4    // every 4th
    }
    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Dictionary<IntPtr, Session> _sessions = new();
    private readonly object _lock = new();

    /// <summary>True on Win10 1803+ where Windows.Graphics.Capture is available.</summary>
    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }

    public WindowCapture(ID3D11Device device)
    {
        _device = device;
        _winrtDevice = CreateWinRtDevice(device);
    }

    /// <summary>Start capturing the given HWND. No-op if already registered.</summary>
    public void Register(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (_sessions.ContainsKey(hwnd)) return;
            GraphicsCaptureItem? item;
            try { item = CreateCaptureItemForWindow(hwnd); }
            catch { item = null; }
            if (item == null) return;

            var pool = Direct3D11CaptureFramePool.Create(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                item.Size);
            var capture = pool.CreateCaptureSession(item);
            // IsBorderRequired was added in Win11 22000 — not in the 19041
            // projection we target. Set via reflection when present so we
            // still opt out on Win11 without forcing a Win11-min SDK.
            try
            {
                var prop = capture.GetType().GetProperty("IsBorderRequired");
                prop?.SetValue(capture, false);
            }
            catch { }
            try { capture.IsCursorCaptureEnabled = false; } catch { }
            capture.StartCapture();

            _sessions[hwnd] = new Session
            {
                Item = item,
                Pool = pool,
                Capture = capture
            };
        }
    }

    public void Unregister(IntPtr hwnd)
    {
        Session? s;
        lock (_lock)
        {
            if (!_sessions.Remove(hwnd, out s)) return;
        }
        s.Dispose();
    }

    /// <summary>Set the frame-pull cadence for a registered HWND.</summary>
    public void SetRate(IntPtr hwnd, Rate rate)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(hwnd, out var s)) s.CurrentRate = rate;
        }
    }

    /// <summary>
    /// Pull the latest captured frame for <paramref name="hwnd"/> (if any) and
    /// copy it into the session's persistent texture via the given render-thread
    /// context. Returns the SRV, or null if nothing has been captured yet.
    /// Call only from the render thread — uses the immediate context.
    /// </summary>
    public ID3D11ShaderResourceView? Sample(IntPtr hwnd, ID3D11DeviceContext ctx)
    {
        Session? s;
        lock (_lock) { _sessions.TryGetValue(hwnd, out s); }
        if (s == null) return null;

        // Throttle: Paused never pulls, and skip-based rates pull once every
        // N render frames. In between, the shader keeps sampling the last
        // captured texture stored on the session.
        if (s.CurrentRate == Rate.Paused) return s.Srv;
        s.FrameCounter++;
        if (s.FrameCounter < (int)s.CurrentRate) return s.Srv;
        s.FrameCounter = 0;

        using var frame = s.Pool.TryGetNextFrame();
        if (frame != null)
        {
            var size = frame.ContentSize;
            int w = Math.Max(1, size.Width);
            int h = Math.Max(1, size.Height);

            if (s.Persistent == null || s.Width != w || s.Height != h)
            {
                s.Srv?.Dispose();
                s.Persistent?.Dispose();
                s.Persistent = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource
                });
                s.Srv = _device.CreateShaderResourceView(s.Persistent);
                s.Width = w;
                s.Height = h;
            }

            using var frameTex = GetTextureFromSurface(frame.Surface);
            ctx.CopyResource(s.Persistent, frameTex);
        }

        return s.Srv;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }

    private sealed class Session : IDisposable
    {
        public GraphicsCaptureItem Item = null!;
        public Direct3D11CaptureFramePool Pool = null!;
        public GraphicsCaptureSession Capture = null!;
        public ID3D11Texture2D? Persistent;
        public ID3D11ShaderResourceView? Srv;
        public int Width, Height;
        public Rate CurrentRate = Rate.Realtime;
        public int FrameCounter;

        public void Dispose()
        {
            Srv?.Dispose();
            Persistent?.Dispose();
            try { Capture?.Dispose(); } catch { }
            try { Pool?.Dispose(); } catch { }
        }
    }

    // ==================== Interop ====================

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable);
        if (hr < 0 || inspectable == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed (0x{hr:X8})");
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    // IID of the IGraphicsCaptureItem WinRT interface — used when asking the
    // interop factory to materialize an HWND as a GraphicsCaptureItem.
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    private static GraphicsCaptureItem? CreateCaptureItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out IntPtr hstr);
        IGraphicsCaptureItemInterop interop;
        try
        {
            Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            RoGetActivationFactory(hstr, ref interopIid, out IntPtr factoryPtr);
            try
            {
                interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(hstr);
        }

        Guid itemIid = IID_IGraphicsCaptureItem;
        IntPtr abi = interop.CreateForWindow(hwnd, ref itemIid);
        if (abi == IntPtr.Zero) return null;
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        // Projected WinRT types (IDirect3DSurface via CsWinRT) are managed
        // wrappers around an IInspectable COM pointer — a plain C# cast to a
        // non-projected COM interface like IDirect3DDxgiInterfaceAccess throws
        // InvalidCastException. WinRT's As<T> extension walks the ABI pointer
        // and does a real QueryInterface for the requested IID.
        var access = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
        Guid iid = IID_ID3D11Texture2D;
        IntPtr ptr = access.GetInterface(ref iid);
        return new ID3D11Texture2D(ptr);
    }
}
