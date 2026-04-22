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

    // Desktop + taskbar per-pass state.
    private readonly Dictionary<OverviewOverlay, IntPtr> _desktopByPass = new();
    private readonly Dictionary<OverviewOverlay, List<(IntPtr hwnd, IntPtr thumb)>> _taskbarsByPass = new();

    // Window thumbnails: per-pass list in DWM registration order (z-descending).
    // The list IS the authoritative state — its order mirrors what DWM will
    // draw (first-registered at the bottom, last-registered on top). Z-index
    // is implicit in list position; no separate cutoff / sort is needed.
    private struct ActiveEntry
    {
        public IntPtr HWnd;
        public IntPtr Thumb;
        public WorldRect World;
    }
    private readonly Dictionary<OverviewOverlay, List<ActiveEntry>> _windowsByPass = new();

    // Scratch target per pass (z-descending), reused across Reconcile calls.
    private readonly Dictionary<OverviewOverlay, List<OverviewWindowList.Entry>> _scratchTargetByPass = new();

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
        foreach (var kv in _windowsByPass)
        {
            foreach (var entry in kv.Value)
                PInvoke.DwmUnregisterThumbnail(entry.Thumb);
        }
        _windowsByPass.Clear();

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
            UpdateDesktop(pass);

        ComputeTarget();
        bool appended = RebuildWindows();

        // Taskbars must sit on top of window thumbnails. DWM stacks in
        // registration order, so any window append lands above the taskbars
        // until we cycle them back to the top.
        if (appended)
            CycleTaskbarsOnTop();

        foreach (var pass in _passes)
            UpdateTaskbars(pass);

        UpdateWindowRects();
    }

    /// <summary>
    /// Re-register a window's thumbnail so it lands at the top of DWM's
    /// registration-order z-stack. No-op on passes where the window isn't
    /// currently active.
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
            PInvoke.DwmUnregisterThumbnail(entry.Thumb);
            list.RemoveAt(idx);

            HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)hWnd, out nint newThumb);
            if (hr.Succeeded)
            {
                SetVisibleOnce(newThumb);
                entry.Thumb = newThumb;
                list.Add(entry);
                moved = true;
            }
        }

        // Re-registered window now sits above taskbars — cycle them.
        if (moved)
        {
            CycleTaskbarsOnTop();
            foreach (var pass in _passes)
                UpdateTaskbars(pass);
        }
    }

    private void CycleTaskbarsOnTop()
    {
        foreach (var pass in _passes)
        {
            UnregisterTaskbars(pass);
            RegisterTaskbars(pass);
        }
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
                dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE,
                rcDestination = new RECT
                {
                    left   = r.left   - pass.OriginX,
                    top    = r.top    - pass.OriginY,
                    right  = r.right  - pass.OriginX,
                    bottom = r.bottom - pass.OriginY
                },
                fVisible = true
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

    /// <summary>
    /// Fill <see cref="_scratchTargetByPass"/> with the windows that should
    /// be registered on each pass, in z-descending order (bottom-most first,
    /// topmost last — matching DWM registration order for correct stacking).
    /// </summary>
    private void ComputeTarget()
    {
        foreach (var kv in _scratchTargetByPass)
            kv.Value.Clear();

        double zoom = _camera.Zoom;
        double camX = _camera.X;
        double camY = _camera.Y;

        // _windows is z-ascending (index 0 = topmost). Build per-pass lists
        // in that order, then reverse once at the end to flip to z-descending.
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

                if (!_scratchTargetByPass.TryGetValue(pass, out var list))
                {
                    list = new List<OverviewWindowList.Entry>();
                    _scratchTargetByPass[pass] = list;
                }
                list.Add(entry);
            }
        }

        foreach (var kv in _scratchTargetByPass)
            kv.Value.Reverse();
    }

    /// <summary>
    /// Differential rebuild per pass. Walks current + target in lockstep;
    /// items that match at the same position are preserved (World is
    /// refreshed). Items that diverge are unregistered. Remaining target
    /// items are appended. Returns true if any new window was registered —
    /// the caller must re-register taskbars so they stay on top.
    /// </summary>
    private bool RebuildWindows()
    {
        bool appended = false;

        foreach (var pass in _passes)
        {
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
                    PInvoke.DwmUnregisterThumbnail(current[ci].Thumb);
                }
            }
            if (writeIdx < current.Count)
                current.RemoveRange(writeIdx, current.Count - writeIdx);

            for (int i = k; i < targetCount; i++)
            {
                var cand = target![i];
                HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)cand.HWnd, out nint thumb);
                if (hr.Succeeded)
                {
                    SetVisibleOnce(thumb);
                    current.Add(new ActiveEntry { HWnd = cand.HWnd, Thumb = thumb, World = cand.World });
                    appended = true;
                }
            }
        }

        return appended;
    }

    /// <summary>
    /// One-shot visibility toggle for a freshly registered thumbnail. Newly
    /// registered DWM thumbnails default to invisible; after this, ongoing
    /// UpdateWindowRects only needs to push rect changes.
    /// </summary>
    private static void SetVisibleOnce(IntPtr thumb)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_VISIBLE,
            fVisible = true
        };
        PInvoke.DwmUpdateThumbnailProperties(thumb, props);
    }

    private void UpdateWindowRects()
    {
        double zoom = _camera.Zoom;
        double camX = _camera.X;
        double camY = _camera.Y;

        foreach (var kv in _windowsByPass)
        {
            var pass = kv.Key;
            var list = kv.Value;

            foreach (var entry in list)
            {
                var hWnd = entry.HWnd;
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
                    dwFlags = PInvoke.DWM_TNP_RECTDESTINATION,
                    rcDestination = new RECT { left = left, top = top, right = right, bottom = bottom }
                };
                PInvoke.DwmUpdateThumbnailProperties(entry.Thumb, props);
            }
        }
    }
}
