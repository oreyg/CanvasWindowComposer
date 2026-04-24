using System;
using System.Collections.Generic;
using Vortice.Direct3D11;
using Windows.Win32.Foundation;

namespace CanvasDesktop;

/// <summary>
/// Owns every thumbnail surface the overview renders: per-canvas-window,
/// desktop wallpaper, and per-monitor taskbars. Each surface is the DWM
/// shared redirection surface of the source HWND, opened as a D3D11
/// texture + SRV via <see cref="Win32DwmSurface"/>. The shader samples
/// that SRV directly; DWM continuously updates the underlying texture, so
/// no per-frame copy or capture pool is needed.
///
/// Each <see cref="OverviewOverlay"/> runs its own D3D device, so each
/// pass keeps its own SRV per source HWND — one <c>OpenSharedResource1</c>
/// per (pass, hwnd) pair. Visibility throttling: a window is tracked on a
/// pass only while its screen-space rect intersects that pass's client
/// bounds, so per-reconcile work scales with what's on-screen rather than
/// total canvas windows.
/// </summary>
internal sealed class OverviewThumbnails
{
    private const byte DesktopOpacityZoomedMin = 30;

    private readonly IReadOnlyList<OverviewOverlay> _passes;
    private readonly OverviewWindowList _windows;
    private readonly OverviewCamera _camera;
    private readonly OverviewState _state;
    private readonly IWindowApi _win32;
    private readonly IScreens _screens;

    // Shell HWND caches — stable across overview opens, invalidated on
    // display topology change via InvalidateShellCache. Self-healing via
    // IsWindow() guards (explorer.exe restart destroys Progman / WorkerW /
    // Shell_TrayWnd and recreates them with new handles).
    private IntPtr _cachedDesktopWallpaperHwnd;
    private List<IntPtr>? _cachedTaskbarHwnds;

    // Desktop shared surface is per-pass (each pass has its own D3D device).
    private readonly Dictionary<OverviewOverlay, Win32DwmSurface.OpenedSurface> _desktopByPass = new();
    private int _lastDesktopOpacity = -1;

    // Taskbars: cached HWnd list enumerated from the shell, plus per-pass
    // per-taskbar OpenedSurface. Taskbars don't move during a session, so
    // positions are recomputed on each reconcile from GetWindowRect.
    private readonly Dictionary<OverviewOverlay, Dictionary<IntPtr, Win32DwmSurface.OpenedSurface>> _taskbarSurfacesByPass = new();

    // UpdateInstances early-out: cache last-pushed camera. Same camera + same
    // world rects => same screen rects, so skip the push if nothing changed.
    // Use InvalidateCameraCache on paths that need a fresh push (new entries,
    // BringToFront, drag-time world-rect update).
    private double _lastPushedCamX = double.NaN;
    private double _lastPushedCamY = double.NaN;
    private double _lastPushedZoom = double.NaN;

    private void InvalidateCameraCache()
    {
        _lastPushedCamX = double.NaN;
    }

    // Per-pass per-window entry, z-ascending (index 0 = bottom, last = topmost).
    // This list IS the authoritative draw order — the ThumbnailPass draws in
    // instance order so the last entry ends up on top.
    private struct ActiveEntry
    {
        public IntPtr HWnd;
        public WorldRect World;
        public int InsetL, InsetT, InsetR, InsetB;
        public Win32DwmSurface.OpenedSurface Surface;
    }
    private readonly Dictionary<OverviewOverlay, List<ActiveEntry>> _windowsByPass = new();

    // Scratch target per pass (z-ascending), reused across Reconcile calls.
    private readonly Dictionary<OverviewOverlay, List<OverviewWindowList.Entry>> _scratchTargetByPass = new();

    // Per-pass scratch buffers handed to ThumbnailPass.SetInstances.
    private readonly Dictionary<OverviewOverlay, ThumbnailPass.Instance[]> _instanceScratchByPass = new();
    private readonly Dictionary<OverviewOverlay, ID3D11ShaderResourceView?[]> _srvScratchByPass = new();

    public OverviewThumbnails(
        IReadOnlyList<OverviewOverlay> passes,
        OverviewWindowList windows,
        OverviewCamera camera,
        OverviewState state,
        IWindowApi win32,
        IScreens screens)
    {
        _passes = passes;
        _windows = windows;
        _camera = camera;
        _state = state;
        _win32 = win32;
        _screens = screens;
    }

    /// <summary>Open desktop + taskbar surfaces. Window surfaces are opened lazily by <see cref="Reconcile"/>.</summary>
    public void Show()
    {
        foreach (var pass in _passes)
        {
            RegisterDesktop(pass);
            RegisterTaskbars(pass);
        }
    }

    /// <summary>Release every surface. Call when the overview closes.</summary>
    public void Hide()
    {
        foreach (var kv in _windowsByPass)
        {
            foreach (var entry in kv.Value)
                entry.Surface.Dispose();
        }
        _windowsByPass.Clear();

        foreach (var pass in _passes)
        {
            UnregisterTaskbars(pass);
            UnregisterDesktop(pass);
            // Clear the renderer's view of what it should draw next frame.
            pass.Renderer?.SetThumbnailInstances(
                ReadOnlySpan<ThumbnailPass.Instance>.Empty,
                ReadOnlySpan<ID3D11ShaderResourceView?>.Empty);
            pass.Renderer?.SetDesktop(null, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Reconcile the per-pass target list, update desktop opacity, and push
    /// the combined window+taskbar instance list to each pass's renderer.
    /// Call after any camera / mode / window-list change.
    /// </summary>
    public void Reconcile()
    {
        UpdateDesktop();

        ComputeTarget();
        bool appended = RebuildWindows();

        PushInstances(forcePush: appended);
    }

    /// <summary>
    /// Move the entry for <paramref name="hWnd"/> to the end of each pass's
    /// active list (= topmost). No surface reopen needed — draw order is
    /// list order under WGC/shared-surface rendering, not DWM registration
    /// order.
    /// </summary>
    public void BringToFront(IntPtr hWnd)
    {
        bool moved = false;
        foreach (var pass in _passes)
        {
            if (!_windowsByPass.TryGetValue(pass, out var list)) continue;
            int idx = IndexOfHWnd(list, hWnd);
            if (idx < 0) continue;

            var entry = list[idx];
            list.RemoveAt(idx);
            list.Add(entry);
            moved = true;
        }
        if (moved) InvalidateCameraCache();
    }

    /// <summary>Sync the stored world rect for a window during a drag.</summary>
    public void UpdateWorldRect(IntPtr hWnd, WorldRect world)
    {
        foreach (var kv in _windowsByPass)
        {
            var list = kv.Value;
            int idx = IndexOfHWnd(list, hWnd);
            if (idx < 0) continue;
            var entry = list[idx];
            entry.World = world;
            list[idx] = entry;
        }
        InvalidateCameraCache();
    }

    /// <summary>Forget cached shell HWNDs — next <see cref="Show"/> re-enumerates.</summary>
    public void InvalidateShellCache()
    {
        _cachedDesktopWallpaperHwnd = IntPtr.Zero;
        _cachedTaskbarHwnds = null;
    }

    private static int IndexOfHWnd(List<ActiveEntry> list, IntPtr hWnd)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].HWnd == hWnd) return i;
        return -1;
    }

    // ==================== DESKTOP ====================

    private void RegisterDesktop(OverviewOverlay pass)
    {
        var device = pass.Renderer?.Device;
        if (device == null) return;

        IntPtr desktopWnd = GetDesktopWallpaperHwnd();
        if (desktopWnd == IntPtr.Zero) return;

        var opened = Win32DwmSurface.Open(desktopWnd, device);
        if (opened == null) return;
        _desktopByPass[pass] = opened.Value;
        _lastDesktopOpacity = -1;
    }

    private void UnregisterDesktop(OverviewOverlay pass)
    {
        if (_desktopByPass.TryGetValue(pass, out var surface))
            surface.Dispose();
        _desktopByPass.Remove(pass);
    }

    /// <summary>Push desktop opacity + UV sub-rect per pass. Opacity pulls from
    /// config + zoom ramp; the UV rect is this monitor's slice of the
    /// virtual screen (WorkerW spans the whole virtual screen).</summary>
    private void UpdateDesktop()
    {
        var cfg = _state.CurrentConfig;
        byte opacity = cfg.DesktopOpacity;
        if (_state.CurrentMode == OverviewMode.Zooming)
        {
            double t = (_camera.Zoom - OverviewCamera.ZoomMin) / (OverviewCamera.ZoomMax - OverviewCamera.ZoomMin);
            t = Math.Clamp(t, 0.0, 1.0);
            double min = DesktopOpacityZoomedMin;
            double max = cfg.DesktopOpacity;
            opacity = (byte)(min + (max - min) * t);
        }

        bool opacityChanged = opacity != _lastDesktopOpacity;
        _lastDesktopOpacity = opacity;

        foreach (var pass in _passes)
        {
            if (!_desktopByPass.TryGetValue(pass, out var surface) || surface.Srv == null)
            {
                if (opacityChanged) pass.Renderer?.SetDesktop(null, 0, 0, 0, 0, 0);
                continue;
            }

            var b = pass.Screen.Bounds;
            var vs = _screens.VirtualScreen;
            float uvL = (float)(b.X - vs.X) / vs.Width;
            float uvT = (float)(b.Y - vs.Y) / vs.Height;
            float uvR = (float)(b.X - vs.X + b.Width) / vs.Width;
            float uvB = (float)(b.Y - vs.Y + b.Height) / vs.Height;
            pass.Renderer?.SetDesktop(surface.Srv, uvL, uvT, uvR, uvB, opacity / 255f);
        }
    }

    private IntPtr GetDesktopWallpaperHwnd()
    {
        if (_cachedDesktopWallpaperHwnd == IntPtr.Zero
            || !PInvoke.IsWindow((HWND)_cachedDesktopWallpaperHwnd))
        {
            _cachedDesktopWallpaperHwnd = FindDesktopWallpaperWindow();
        }
        return _cachedDesktopWallpaperHwnd;
    }

    private static IntPtr FindDesktopWallpaperWindow()
    {
        HWND progman = PInvoke.FindWindow("Progman", null);
        if (progman == HWND.Null) return IntPtr.Zero;

        PInvoke.SendMessage(progman, 0x052C, 0, 0);

        HWND workerW = HWND.Null;
        WNDENUMPROC proc = (HWND hWnd, LPARAM _) =>
        {
            HWND shell = PInvoke.FindWindowEx(hWnd, HWND.Null, "SHELLDLL_DefView", null);
            if (shell != HWND.Null)
                workerW = PInvoke.FindWindowEx(HWND.Null, hWnd, "WorkerW", null);
            return true;
        };
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);

        return workerW != HWND.Null ? workerW : progman;
    }

    // ==================== TASKBARS ====================

    private void RegisterTaskbars(OverviewOverlay pass)
    {
        var device = pass.Renderer?.Device;
        if (device == null) return;

        var map = new Dictionary<IntPtr, Win32DwmSurface.OpenedSurface>();
        foreach (var hwnd in GetTaskbarHwnds())
        {
            var opened = Win32DwmSurface.Open(hwnd, device);
            if (opened != null) map[hwnd] = opened.Value;
        }
        _taskbarSurfacesByPass[pass] = map;
    }

    private void UnregisterTaskbars(OverviewOverlay pass)
    {
        if (!_taskbarSurfacesByPass.TryGetValue(pass, out var map)) return;
        foreach (var kv in map) kv.Value.Dispose();
        _taskbarSurfacesByPass.Remove(pass);
    }

    private List<IntPtr> GetTaskbarHwnds()
    {
        if (_cachedTaskbarHwnds == null || !AllAlive(_cachedTaskbarHwnds))
            _cachedTaskbarHwnds = EnumerateTaskbarHwnds();
        return _cachedTaskbarHwnds;
    }

    private static bool AllAlive(List<IntPtr> hwnds)
    {
        foreach (var h in hwnds)
        {
            if (!PInvoke.IsWindow((HWND)h)) return false;
        }
        return true;
    }

    private static unsafe List<IntPtr> EnumerateTaskbarHwnds()
    {
        var result = new List<IntPtr>();

        HWND primary = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (primary != HWND.Null) result.Add(primary);

        WNDENUMPROC proc = (HWND hWnd, LPARAM _) =>
        {
            Span<char> buf = stackalloc char[64];
            int len;
            fixed (char* p = buf)
            {
                len = PInvoke.GetClassName(hWnd, new PWSTR(p), buf.Length);
            }
            if (len > 0 && new string(buf[..len]) == "Shell_SecondaryTrayWnd")
                result.Add(hWnd);
            return true;
        };
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);

        return result;
    }

    // ==================== WINDOWS ====================

    /// <summary>
    /// Fill <see cref="_scratchTargetByPass"/> with the windows that should be
    /// registered on each pass, in z-ascending order (bottom-most first,
    /// topmost last — matching the shader's draw order so the topmost window
    /// renders on top).
    /// </summary>
    private void ComputeTarget()
    {
        foreach (var kv in _scratchTargetByPass)
            kv.Value.Clear();

        double zoom = _camera.Zoom;
        double camX = _camera.X;
        double camY = _camera.Y;

        // _windows is z-descending (index 0 = topmost). Walk in reverse so
        // the per-pass scratch lists end up z-ascending.
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var entry = _windows.Windows[i];

            int sx = (int)((entry.World.X - camX) * zoom);
            int sy = (int)((entry.World.Y - camY) * zoom);
            int sw = Math.Max(1, (int)(entry.World.W * zoom));
            int sh = Math.Max(1, (int)(entry.World.H * zoom));
            int right = sx + sw;
            int bottom = sy + sh;

            foreach (var pass in _passes)
            {
                var bounds = pass.Screen.Bounds;
                if (right <= bounds.Left || sx >= bounds.Right ||
                    bottom <= bounds.Top || sy >= bounds.Bottom) continue;

                if (!_scratchTargetByPass.TryGetValue(pass, out var list))
                {
                    list = new List<OverviewWindowList.Entry>();
                    _scratchTargetByPass[pass] = list;
                }
                list.Add(entry);
            }
        }
    }

    /// <summary>
    /// Differential rebuild per pass. Walks current + target in lockstep;
    /// items that match at the same position are preserved (World is
    /// refreshed). Items that diverge are disposed. Remaining target items
    /// are appended (opening their shared surface). Returns true if any new
    /// entry was opened — the caller uses this to force an instance push
    /// even under a camera-unchanged early-out.
    /// </summary>
    private bool RebuildWindows()
    {
        bool appended = false;

        foreach (var pass in _passes)
        {
            var device = pass.Renderer?.Device;
            if (device == null) continue;

            _scratchTargetByPass.TryGetValue(pass, out var target);
            int targetCount = target?.Count ?? 0;

            if (!_windowsByPass.TryGetValue(pass, out var current))
            {
                if (targetCount == 0) continue;
                current = new List<ActiveEntry>();
                _windowsByPass[pass] = current;
            }

            int writeIdx = 0;
            int k = 0;
            for (int ci = 0; ci < current.Count; ci++)
            {
                if (k < targetCount && current[ci].HWnd == target![k].HWnd)
                {
                    var entry = current[ci];
                    entry.World = target[k].World;
                    current[writeIdx++] = entry;
                    k++;
                }
                else
                {
                    current[ci].Surface.Dispose();
                }
            }
            if (writeIdx < current.Count)
                current.RemoveRange(writeIdx, current.Count - writeIdx);

            for (int i = k; i < targetCount; i++)
            {
                var cand = target![i];
                var opened = Win32DwmSurface.Open(cand.HWnd, device);
                if (opened == null) continue;

                var (iL, iT, iR, iB) = _win32.GetFrameInset(cand.HWnd);
                current.Add(new ActiveEntry
                {
                    HWnd = cand.HWnd,
                    World = cand.World,
                    InsetL = iL, InsetT = iT, InsetR = iR, InsetB = iB,
                    Surface = opened.Value
                });
                InvalidateCameraCache();
                appended = true;
            }
        }

        return appended;
    }

    /// <summary>
    /// Build each pass's <see cref="ThumbnailPass.Instance"/>[] + parallel
    /// SRV[] and push via the renderer. Windows first (z-ascending), taskbars
    /// appended at the end so they render on top.
    /// </summary>
    private void PushInstances(bool forcePush)
    {
        double zoom = _camera.Zoom;
        double camX = _camera.X;
        double camY = _camera.Y;

        bool camSame =
            camX == _lastPushedCamX &&
            camY == _lastPushedCamY &&
            zoom == _lastPushedZoom;
        if (camSame && !forcePush) return;
        _lastPushedCamX = camX;
        _lastPushedCamY = camY;
        _lastPushedZoom = zoom;

        bool taskbarsVisible = _state.CurrentConfig.TaskbarVisible;

        foreach (var pass in _passes)
        {
            _windowsByPass.TryGetValue(pass, out var windows);
            _taskbarSurfacesByPass.TryGetValue(pass, out var taskbarMap);

            int windowCount = windows?.Count ?? 0;
            int taskbarCount = taskbarsVisible && taskbarMap != null ? CountTaskbarsOnPass(pass, taskbarMap) : 0;
            int total = Math.Min(windowCount + taskbarCount, ThumbnailPass.MaxThumbnails);

            var instances = EnsureInstanceScratch(pass);
            var srvs = EnsureSrvScratch(pass);

            int w = 0;
            for (int i = 0; i < windowCount && w < total; i++, w++)
            {
                var entry = windows![i];
                instances[w] = BuildWindowInstance(entry, pass, zoom, camX, camY);
                srvs[w] = entry.Surface.Srv;
            }

            if (taskbarCount > 0 && taskbarMap != null)
            {
                foreach (var kv in taskbarMap)
                {
                    if (w >= total) break;
                    if (!TryBuildTaskbarInstance(pass, kv.Key, out var inst)) continue;
                    instances[w] = inst;
                    srvs[w] = kv.Value.Srv;
                    w++;
                }
            }

            pass.Renderer?.SetThumbnailInstances(
                new ReadOnlySpan<ThumbnailPass.Instance>(instances, 0, w),
                new ReadOnlySpan<ID3D11ShaderResourceView?>(srvs, 0, w));
        }
    }

    private static int CountTaskbarsOnPass(
        OverviewOverlay pass,
        Dictionary<IntPtr, Win32DwmSurface.OpenedSurface> map)
    {
        int count = 0;
        foreach (var kv in map)
        {
            if (IsTaskbarOnPass(pass, kv.Key)) count++;
        }
        return count;
    }

    private static bool IsTaskbarOnPass(OverviewOverlay pass, IntPtr hwnd)
    {
        if (!PInvoke.GetWindowRect((HWND)hwnd, out RECT r)) return false;
        var b = pass.Screen.Bounds;
        int cx = (r.left + r.right) / 2;
        int cy = (r.top + r.bottom) / 2;
        return cx >= b.X && cx < b.Right && cy >= b.Y && cy < b.Bottom;
    }

    private static bool TryBuildTaskbarInstance(
        OverviewOverlay pass, IntPtr hwnd, out ThumbnailPass.Instance inst)
    {
        inst = default;
        if (!IsTaskbarOnPass(pass, hwnd)) return false;
        if (!PInvoke.GetWindowRect((HWND)hwnd, out RECT r)) return false;
        inst = new ThumbnailPass.Instance
        {
            Left = r.left - pass.OriginX,
            Top = r.top - pass.OriginY,
            Right = r.right - pass.OriginX,
            Bottom = r.bottom - pass.OriginY
        };
        return true;
    }

    private static ThumbnailPass.Instance BuildWindowInstance(
        ActiveEntry entry, OverviewOverlay pass,
        double zoom, double camX, double camY)
    {
        var world = entry.World;
        int sx = (int)((world.X - camX) * zoom);
        int sy = (int)((world.Y - camY) * zoom);
        int sw = Math.Max(1, (int)(world.W * zoom));
        int sh = Math.Max(1, (int)(world.H * zoom));

        int fL = (int)(entry.InsetL * zoom);
        int fT = (int)(entry.InsetT * zoom);
        int fR = (int)(entry.InsetR * zoom);
        int fB = (int)(entry.InsetB * zoom);

        return new ThumbnailPass.Instance
        {
            Left = sx + fL - pass.OriginX,
            Top = sy + fT - pass.OriginY,
            Right = sx + sw - fR - pass.OriginX,
            Bottom = sy + sh - fB - pass.OriginY
        };
    }

    private ThumbnailPass.Instance[] EnsureInstanceScratch(OverviewOverlay pass)
    {
        if (!_instanceScratchByPass.TryGetValue(pass, out var arr))
        {
            arr = new ThumbnailPass.Instance[ThumbnailPass.MaxThumbnails];
            _instanceScratchByPass[pass] = arr;
        }
        return arr;
    }

    private ID3D11ShaderResourceView?[] EnsureSrvScratch(OverviewOverlay pass)
    {
        if (!_srvScratchByPass.TryGetValue(pass, out var arr))
        {
            arr = new ID3D11ShaderResourceView?[ThumbnailPass.MaxThumbnails];
            _srvScratchByPass[pass] = arr;
        }
        return arr;
    }
}
