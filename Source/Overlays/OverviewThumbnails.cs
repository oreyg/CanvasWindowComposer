using System;
using System.Collections.Generic;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CanvasDesktop;

/// <summary>
/// All DWM thumbnail state for the overview: desktop wallpaper, taskbars,
/// and per-canvas-window thumbnails. Registered per pass
/// (<see cref="OverviewOverlay"/>); the manager only drives lifecycle
/// (<see cref="Show"/> / <see cref="Hide"/> / <see cref="Reconcile"/>).
///
/// Window thumbnails are throttled: a window is kept registered on a pass
/// iff its screen-space rect intersects that pass's client bounds, so
/// DwmUpdateThumbnailProperties work scales with what's on-screen rather
/// than with the total number of canvas windows.
/// </summary>
internal sealed class OverviewThumbnails
{
    // Desktop opacity floor when fully zoomed in (Zooming mode only). The
    // ramp interpolates between this and the mode's configured DesktopOpacity
    // based on camera zoom.
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

    // Per-pass state.
    private readonly Dictionary<OverviewOverlay, IntPtr> _desktopByPass = new();
    private readonly Dictionary<OverviewOverlay, List<(IntPtr hwnd, IntPtr thumb)>> _taskbarsByPass = new();

    // Window thumbnails keyed by (pass, hWnd).
    private readonly struct Key : IEquatable<Key>
    {
        public readonly OverviewOverlay Pass;
        public readonly IntPtr HWnd;
        public Key(OverviewOverlay pass, IntPtr hWnd) { Pass = pass; HWnd = hWnd; }
        public bool Equals(Key other) { return Pass == other.Pass && HWnd == other.HWnd; }
        public override bool Equals(object? obj) { return obj is Key k && Equals(k); }
        public override int GetHashCode() { return HashCode.Combine(Pass, HWnd); }
    }

    private struct Active
    {
        public IntPtr Thumb;
        public WorldRect World;
    }

    private readonly Dictionary<Key, Active> _windowActive = new();
    // Reused across Reconcile calls to avoid per-frame allocation.
    private readonly List<Candidate> _scratch = new();

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

    /// <summary>Register desktop + taskbar thumbnails on every pass. Window thumbnails are registered lazily by <see cref="Reconcile"/>.</summary>
    public void Show()
    {
        foreach (var pass in _passes)
        {
            RegisterDesktop(pass);
            RegisterTaskbars(pass);
        }
    }

    /// <summary>Unregister everything. Call when the overview closes.</summary>
    public void Hide()
    {
        foreach (var kv in _windowActive)
            PInvoke.DwmUnregisterThumbnail(kv.Value.Thumb);
        _windowActive.Clear();

        foreach (var pass in _passes)
        {
            UnregisterTaskbars(pass);
            UnregisterDesktop(pass);
        }
    }

    /// <summary>
    /// Reconcile the window-thumbnail set against the current camera +
    /// window list, then update props on desktop, taskbar, and window
    /// thumbnails. Call after any camera / mode / window-list change.
    /// </summary>
    public void Reconcile()
    {
        foreach (var pass in _passes)
        {
            UpdateDesktop(pass);
            UpdateTaskbars(pass);
        }

        ComputeCandidates(_scratch);
        if (SetChanged(_scratch))
            RebuildWindows(_scratch);
        UpdateWindowRects();
        _scratch.Clear();
    }

    /// <summary>
    /// Re-register a window's thumbnail on every pass where it's active, so
    /// it lands at the top of DWM's registration-order z-stack. No-op on
    /// passes where the window isn't currently active.
    /// </summary>
    public void BringToFront(IntPtr hWnd)
    {
        foreach (var pass in _passes)
        {
            var key = new Key(pass, hWnd);
            if (!_windowActive.TryGetValue(key, out var entry)) continue;

            PInvoke.DwmUnregisterThumbnail(entry.Thumb);
            HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)hWnd, out nint newThumb);
            if (hr.Succeeded)
            {
                entry.Thumb = newThumb;
                _windowActive[key] = entry;
            }
            else
            {
                _windowActive.Remove(key);
            }
        }
    }

    /// <summary>Sync the stored world rect for a window during a drag.</summary>
    public void UpdateWorldRect(IntPtr hWnd, WorldRect world)
    {
        foreach (var pass in _passes)
        {
            var key = new Key(pass, hWnd);
            if (!_windowActive.TryGetValue(key, out var entry)) continue;
            entry.World = world;
            _windowActive[key] = entry;
        }
    }

    /// <summary>Forget cached shell HWNDs — next <see cref="Show"/> re-enumerates.</summary>
    public void InvalidateShellCache()
    {
        _cachedDesktopWallpaperHwnd = IntPtr.Zero;
        _cachedTaskbarHwnds = null;
    }

    // ==================== DESKTOP ====================

    private void RegisterDesktop(OverviewOverlay pass)
    {
        IntPtr desktopWnd = GetDesktopWallpaperHwnd();
        if (desktopWnd == IntPtr.Zero) return;

        HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)desktopWnd, out nint thumb);
        _desktopByPass[pass] = hr.Succeeded ? thumb : IntPtr.Zero;
    }

    private void UnregisterDesktop(OverviewOverlay pass)
    {
        if (_desktopByPass.TryGetValue(pass, out var thumb) && thumb != IntPtr.Zero)
            PInvoke.DwmUnregisterThumbnail(thumb);
        _desktopByPass.Remove(pass);
    }

    private void UpdateDesktop(OverviewOverlay pass)
    {
        if (!_desktopByPass.TryGetValue(pass, out var thumb) || thumb == IntPtr.Zero) return;

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

        // WorkerW spans the entire virtual screen. Position this monitor's
        // slice of it over this form's client area by setting a source rect.
        var b = pass.Screen.Bounds;
        var vs = _screens.VirtualScreen;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_RECTSOURCE |
                      PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_OPACITY,
            rcDestination = new RECT { left = 0, top = 0, right = b.Width, bottom = b.Height },
            rcSource = new RECT
            {
                left   = b.X - vs.X,
                top    = b.Y - vs.Y,
                right  = b.X - vs.X + b.Width,
                bottom = b.Y - vs.Y + b.Height
            },
            fVisible = true,
            opacity = opacity
        };

        PInvoke.DwmUpdateThumbnailProperties(thumb, props);
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
        var list = new List<(IntPtr hwnd, IntPtr thumb)>();
        foreach (var hwnd in GetTaskbarHwnds())
        {
            HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)hwnd, out nint thumb);
            if (hr.Succeeded) list.Add((hwnd, thumb));
        }
        _taskbarsByPass[pass] = list;
    }

    private void UnregisterTaskbars(OverviewOverlay pass)
    {
        if (!_taskbarsByPass.TryGetValue(pass, out var list)) return;
        foreach (var (_, thumb) in list)
            PInvoke.DwmUnregisterThumbnail(thumb);
        _taskbarsByPass.Remove(pass);
    }

    private void UpdateTaskbars(OverviewOverlay pass)
    {
        if (!_taskbarsByPass.TryGetValue(pass, out var list) || list.Count == 0) return;

        if (!_state.CurrentConfig.TaskbarVisible)
        {
            var hideProps = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = PInvoke.DWM_TNP_VISIBLE,
                fVisible = false
            };
            foreach (var (_, thumb) in list)
                PInvoke.DwmUpdateThumbnailProperties(thumb, hideProps);
            return;
        }

        foreach (var (hwnd, thumb) in list)
        {
            PInvoke.GetWindowRect((HWND)hwnd, out RECT r);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_OPACITY,
                rcDestination = new RECT
                {
                    left   = r.left   - pass.OriginX,
                    top    = r.top    - pass.OriginY,
                    right  = r.right  - pass.OriginX,
                    bottom = r.bottom - pass.OriginY
                },
                fVisible = true,
                opacity = 255
            };
            PInvoke.DwmUpdateThumbnailProperties(thumb, props);
        }
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

    private readonly struct Candidate
    {
        public readonly OverviewOverlay Pass;
        public readonly IntPtr HWnd;
        public readonly WorldRect World;
        public readonly int ZIndex; // _windows order: 0 = topmost.

        public Candidate(OverviewOverlay pass, IntPtr hWnd, WorldRect world, int zIndex)
        {
            Pass = pass;
            HWnd = hWnd;
            World = world;
            ZIndex = zIndex;
        }
    }

    private static int CompareByZDescending(Candidate a, Candidate b)
    {
        // Sort so higher ZIndex (bottom-most) comes first — registering
        // bottom-to-top puts the topmost window last, drawn on top.
        return b.ZIndex.CompareTo(a.ZIndex);
    }

    private void ComputeCandidates(List<Candidate> result)
    {
        result.Clear();

        double zoom = _camera.Zoom;
        double camX = _camera.X;
        double camY = _camera.Y;

        for (int i = 0; i < _windows.Count; i++)
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

                result.Add(new Candidate(pass, entry.HWnd, entry.World, i));
            }
        }
    }

    private bool SetChanged(List<Candidate> candidates)
    {
        if (candidates.Count != _windowActive.Count) return true;
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!_windowActive.ContainsKey(new Key(c.Pass, c.HWnd))) return true;
        }
        return false;
    }

    private void RebuildWindows(List<Candidate> candidates)
    {
        foreach (var kv in _windowActive)
            PInvoke.DwmUnregisterThumbnail(kv.Value.Thumb);
        _windowActive.Clear();

        candidates.Sort(CompareByZDescending);
        foreach (var c in candidates)
        {
            HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)c.Pass.Handle, (HWND)c.HWnd, out nint thumb);
            if (hr.Succeeded)
                _windowActive[new Key(c.Pass, c.HWnd)] = new Active { Thumb = thumb, World = c.World };
        }
    }

    private void UpdateWindowRects()
    {
        double zoom = _camera.Zoom;
        double camX = _camera.X;
        double camY = _camera.Y;

        foreach (var kv in _windowActive)
        {
            var pass = kv.Key.Pass;
            var hWnd = kv.Key.HWnd;
            var entry = kv.Value;
            var world = entry.World;

            int sx = (int)((world.X - camX) * zoom);
            int sy = (int)((world.Y - camY) * zoom);
            int sw = Math.Max(1, (int)(world.W * zoom));
            int sh = Math.Max(1, (int)(world.H * zoom));

            var (iL, iT, iR, iB) = _win32.GetFrameInset(hWnd);
            int fL = (int)(iL * zoom);
            int fT = (int)(iT * zoom);
            int fR = (int)(iR * zoom);
            int fB = (int)(iB * zoom);

            int left   = sx + fL - pass.OriginX;
            int top    = sy + fT - pass.OriginY;
            int right  = sx + sw - fR - pass.OriginX;
            int bottom = sy + sh - fB - pass.OriginY;

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_OPACITY,
                rcDestination = new RECT { left = left, top = top, right = right, bottom = bottom },
                fVisible = true,
                opacity = 255
            };
            PInvoke.DwmUpdateThumbnailProperties(entry.Thumb, props);
        }
    }
}
