using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CanvasDesktop;

/// <summary>
/// Thin wrapper around <c>dwmapi.dll#100 DwmDxGetWindowSharedSurface</c> —
/// the API documented (for Windows 7) as the way for a graphics driver
/// or runtime to obtain the DXGI shared surface backing a window. The
/// export persists unchanged through Windows 10 / 11 and is the path
/// Task View, Alt+Tab previews, and shell replacements use to read
/// window content without <c>DwmRegisterThumbnail</c>.
///
/// The returned surface is the memory DWM composes from. Opening it via
/// <see cref="ID3D11Device.OpenSharedResource"/> gives a sampleable
/// texture that auto-updates as DWM re-composes the window — no capture
/// pool, no per-frame <c>CopyResource</c>.
///
/// Caller contract:
/// <list type="bullet">
///   <item>Must know the adapter LUID to query on. We derive it from the
///         D3D11 device passed to <see cref="Open"/>. If DWM is composing
///         on a different adapter (hybrid GPU laptop) the call returns
///         <c>DWM_E_ADAPTER_NOT_FOUND</c> and <see cref="Open"/> returns
///         null.</item>
///   <item><c>phDxSurface</c> is a legacy (D3D9Ex-style) shared handle,
///         not an NT kernel handle. Open with <c>OpenSharedResource</c>
///         (NOT <c>OpenSharedResource1</c>). Do not <c>CloseHandle</c>
///         it.</item>
///   <item>The handle value can change across calls (surface resize,
///         minimize/restore, display change). Callers caching a view
///         should re-query and reopen when the returned <c>SharedHandle</c>
///         differs from the cached one.</item>
///   <item>The API also returns an update ID. For the documented
///         write-path, a driver must pair each call with
///         <c>DwmDxUpdateWindowSharedSurface</c>; we skip that since we
///         only read from the surface.</item>
/// </list>
/// </summary>
internal static class Win32DwmSurface
{
    // DWM_REDIRECTION_FLAG_WAIT (value 0) causes the call to block until
    // vsync. We call per-reconcile (not per-frame) so the block is
    // acceptable; add DWM_REDIRECTION_FLAG_SUPPORT_PRESENT_TO_GDI_SURFACE
    // (0x10) only if we ever want GDI interop.
    private const uint FlagsDefault = 0;

    private const int S_OK = 0;

    [DllImport("dwmapi.dll", EntryPoint = "#100", PreserveSig = true)]
    private static extern int DwmDxGetWindowSharedSurface(
        IntPtr hwnd,
        long luidAdapter,            // LUID passed by value; layout is LowPart:uint + HighPart:int = 8 bytes.
        IntPtr hmonitorAssociation,  // reserved, pass Zero.
        uint dwFlags,
        ref uint pfmtWindow,         // in: desired DXGI_FORMAT (0 = UNKNOWN, let DWM pick); out: actual.
        out IntPtr phDxSurface,
        out ulong puiUpdateId);

    /// <summary>Raw result of a single query.</summary>
    public struct Query
    {
        public IntPtr SharedHandle;   // legacy shared handle — do not close.
        public long AdapterLuid;
        public ulong UpdateId;        // monotonically advances when surface content is republished.
        public uint Format;           // DXGI_FORMAT as returned by DWM.
    }

    /// <summary>
    /// Call <c>DwmDxGetWindowSharedSurface</c> for <paramref name="hwnd"/>
    /// on the adapter DWM composed the D3D11 <paramref name="device"/>'s
    /// device on. Returns false on any failure (no DWM composition,
    /// adapter mismatch, window gone).
    /// </summary>
    public static bool TryGet(IntPtr hwnd, ID3D11Device device, out Query result)
    {
        result = default;
        long luid = GetAdapterLuid(device);
        if (luid == 0) return false;

        uint fmt = 0; // DXGI_FORMAT_UNKNOWN — accept whatever DWM has.
        int hr = DwmDxGetWindowSharedSurface(
            hwnd, luid, IntPtr.Zero, FlagsDefault,
            ref fmt, out IntPtr sharedHandle, out ulong updateId);
        if (hr != S_OK || sharedHandle == IntPtr.Zero) return false;

        result = new Query
        {
            SharedHandle = sharedHandle,
            AdapterLuid = luid,
            UpdateId = updateId,
            Format = fmt
        };
        return true;
    }

    /// <summary>
    /// Open the DWM shared surface for <paramref name="hwnd"/>, copy its
    /// current contents into a standalone texture on
    /// <paramref name="device"/>, and return an SRV onto that copy. The
    /// copy decouples us from DWM's ongoing composition — the thumbnail
    /// is a snapshot at open time and does not auto-update as the source
    /// window re-renders. Re-call <see cref="Open"/> to refresh.
    /// Returns null on any failure. Caller owns the returned texture +
    /// SRV and must dispose them.
    /// </summary>
    public static OpenedSurface? Open(IntPtr hwnd, ID3D11Device device)
    {
        if (!TryGet(hwnd, device, out var q)) return null;

        ID3D11Texture2D? shared = null;
        ID3D11Texture2D? snapshot = null;
        ID3D11ShaderResourceView? srv = null;
        try
        {
            // Legacy (non-NT) shared handle — OpenSharedResource, NOT OpenSharedResource1.
            shared = device.OpenSharedResource<ID3D11Texture2D>(q.SharedHandle);
            if (shared == null) return null;

            var desc = shared.Description;
            snapshot = device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });

            device.ImmediateContext.CopyResource(snapshot, shared);
            srv = device.CreateShaderResourceView(snapshot);

            shared.Dispose();
            shared = null;

            return new OpenedSurface
            {
                SharedHandle = q.SharedHandle,
                AdapterLuid = q.AdapterLuid,
                UpdateId = q.UpdateId,
                Texture = snapshot,
                Srv = srv
            };
        }
        catch
        {
            srv?.Dispose();
            snapshot?.Dispose();
            shared?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// D3D11 view onto a snapshot of a DWM shared surface. Contents are
    /// fixed at open time — re-call <see cref="Open"/> to refresh from
    /// DWM.
    /// </summary>
    public struct OpenedSurface : IDisposable
    {
        public IntPtr SharedHandle;
        public long AdapterLuid;
        public ulong UpdateId;
        public ID3D11Texture2D? Texture;
        public ID3D11ShaderResourceView? Srv;

        public void Dispose()
        {
            Srv?.Dispose();
            Texture?.Dispose();
            Srv = null;
            Texture = null;
        }
    }

    private static long GetAdapterLuid(ID3D11Device device)
    {
        IDXGIDevice? dxgiDevice = null;
        IDXGIAdapter? adapter = null;
        try
        {
            dxgiDevice = device.QueryInterface<IDXGIDevice>();
            adapter = dxgiDevice.GetAdapter();
            var luid = adapter.Description.Luid;
            // Pack LowPart + HighPart into a single int64 matching the in-
            // memory layout of LUID (LowPart at offset 0, HighPart at 4).
            return ((long)(uint)luid.HighPart << 32) | luid.LowPart;
        }
        catch
        {
            return 0;
        }
        finally
        {
            adapter?.Dispose();
            dxgiDevice?.Dispose();
        }
    }
}
