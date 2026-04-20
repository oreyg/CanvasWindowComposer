using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Coordinator for the overview: owns mode state, camera, inertia, and one
/// OverviewOverlay per physical monitor (each with its own Form + swap chain).
/// </summary>
internal sealed class OverviewManager : IDisposable, IOverviewController
{
    private readonly Canvas _mainCanvas;
    private readonly WindowManager _wm;
    private readonly IWindowApi _win32;
    private readonly IScreens _screens;
    private readonly IInputRouter _input;
    private readonly OverviewState _state = new();
    private readonly OverviewCamera _camera;

    public OverviewMode CurrentMode { get { return _state.CurrentMode; } }
    private OverviewModeConfig _cfg { get { return _state.CurrentConfig; } }

    public event Action<OverviewMode, OverviewMode>? BeforeModeChanged;
    public event Action<OverviewMode, OverviewMode>? AfterModeChanged;

    private const double ExtentsPaddingRatio = 0.1;
    private const double MouseWheelDeltaPerNotch = 120.0;
    private const byte DesktopOpacityZoomedMin = 30;

    private readonly InertiaTracker _inertia = new();
    private readonly object _inertiaQueueLock = new();
    private int _pendingInertiaDx, _pendingInertiaDy;
    private bool _inertiaPanQueued;

    // Per-monitor passes
    private readonly List<OverviewOverlay> _passes = new();

    // Cached shell HWNDs — stable across overview opens, invalidated on display
    // topology change (where _passes themselves are rebuilt) and self-healing
    // via IsWindow() guards (explorer.exe restart destroys Progman / WorkerW /
    // Shell_TrayWnd and recreates them with new handles). Skips ~33ms of
    // EnumWindows + Progman SendMessage on every overview open after the first.
    private IntPtr _cachedDesktopWallpaperHwnd;
    private List<IntPtr>? _cachedTaskbarHwnds;

    /// <summary>HWNDs of all monitor forms (for IInputRouter.SetExtraPanSurfaces).</summary>
    public IReadOnlyList<IntPtr> MonitorHandles
    {
        get
        {
            var list = new List<IntPtr>(_passes.Count);
            foreach (var p in _passes) list.Add(p.Handle);
            return list;
        }
    }

    // Shared ordered list of canvas windows shown in the overview
    // (arrow navigation, hit testing). Refreshed on Show.
    private readonly OverviewWindowList _windows;

    // Pan/drag state (virtual-screen coords)
    private bool _panning;
    private int _panStartVx, _panStartVy;
    private bool _draggingWindow;
    private int _dragIndex = -1;
    private int _dragStartVx, _dragStartVy;

    public OverviewManager(Canvas mainCanvas, WindowManager wm, IWindowApi win32, IInputRouter input, IScreens? screens = null)
    {
        _mainCanvas = mainCanvas;
        _wm = wm;
        _win32 = win32;
        _input = input;
        _screens = screens ?? WinFormsScreens.Instance;
        _camera = new OverviewCamera(_screens);
        _windows = new OverviewWindowList(mainCanvas, win32);

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Reference held only to keep the binding alive for the lifetime of this manager.
        _ = new OverviewInputs(this, input, mainCanvas);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Monitor topology changed — rebuild passes to match. If overview is
        // open, close it, rebuild, reopen in the previous mode.
        OverviewMode prev = CurrentMode;
        bool wasVisible = prev != OverviewMode.Hidden;

        if (wasVisible)
            TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);

        foreach (var p in _passes)
        {
            p.Close();
            p.Dispose();
        }
        _passes.Clear();

        // Shell HWNDs may have moved (Progman / WorkerW are recreated on some
        // display changes; secondary taskbars come and go with monitors).
        _cachedDesktopWallpaperHwnd = IntPtr.Zero;
        _cachedTaskbarHwnds = null;

        EnsurePasses();
        foreach (var p in _passes)
            p.Warmup();

        if (wasVisible)
            TransitionTo(prev);
    }

    /// <summary>Pre-initialize D3D11 and grid threads on every monitor.</summary>
    public void Warmup()
    {
        EnsurePasses();
        foreach (var p in _passes)
            p.Warmup();
    }

    private void EnsurePasses()
    {
        if (_passes.Count > 0) return;
        foreach (var screen in Screen.AllScreens)
        {
            var pass = new OverviewOverlay(screen);
            pass.OnKey = HandleKeyDown;
            pass.OnMouseButtonDown = HandleMouseDown;
            pass.OnMouseMoved = HandleMouseMove;
            pass.OnMouseButtonUp = HandleMouseUp;
            pass.OnWheel = HandleMouseWheel;
            pass.OnMouseDoubleClicked = HandleDoubleClick;
            _passes.Add(pass);
        }
    }

    public void RecordPanDelta(int dx, int dy)
    {
        _inertia.RecordDelta(dx, dy);
    }

    public void ReleaseInertia()
    {
        if (!_inertia.Release() && CurrentMode != OverviewMode.Hidden)
        {
            TransitionTo(OverviewMode.Hidden);
        }
    }

    public void CancelInertia()
    {
        _inertia.Cancel();
        lock (_inertiaQueueLock)
        {
            _pendingInertiaDx = 0;
            _pendingInertiaDy = 0;
        }
    }

    /// <summary>Called on a grid render thread after Present. Drives inertia.</summary>
    private void OnGridFrameTick()
    {
        var (dx, dy, stopped) = _inertia.Tick();

        if (stopped)
        {
            if (_passes.Count > 0 && _passes[0].IsHandleCreated)
                _passes[0].BeginInvoke(() => { if (CurrentMode != OverviewMode.Hidden) TransitionTo(OverviewMode.Hidden); });
            return;
        }

        if ((dx != 0 || dy != 0) && _passes.Count > 0 && _passes[0].IsHandleCreated)
        {
            bool queue;
            lock (_inertiaQueueLock)
            {
                _pendingInertiaDx += dx;
                _pendingInertiaDy += dy;
                queue = !_inertiaPanQueued;
                if (queue) _inertiaPanQueued = true;
            }

            if (queue)
            {
                _passes[0].BeginInvoke(() =>
                {
                    int cdx, cdy;
                    lock (_inertiaQueueLock)
                    {
                        cdx = _pendingInertiaDx;
                        cdy = _pendingInertiaDy;
                        _pendingInertiaDx = 0;
                        _pendingInertiaDy = 0;
                        _inertiaPanQueued = false;
                    }
                    if (!_inertia.IsActive || (cdx == 0 && cdy == 0)) return;
                    _mainCanvas.Pan(cdx, cdy);
                });
            }
        }
    }

    /// <summary>Sync overview camera to the main canvas and update visuals on all passes.</summary>
    public void SyncCamera()
    {
        if (CurrentMode == OverviewMode.Hidden) return;
        _camera.SyncFrom(_mainCanvas);

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        UpdateAllThumbnails();
    }

    /// <summary>Single entry point for every mode change.</summary>
    public void TransitionTo(OverviewMode target, bool syncCameraOnClose = true)
    {
        CancelInertia();

        OverviewMode from = CurrentMode;
        if (!_state.SetMode(target)) return;

        BeforeModeChanged?.Invoke(from, target);

        if (target == OverviewMode.Hidden)
            HideInternal(syncCamera: syncCameraOnClose && from != OverviewMode.Hidden);
        else if (from == OverviewMode.Hidden)
            ShowInternal();
        else
            ApplyConfig();

        AfterModeChanged?.Invoke(from, target);
    }

    private void ShowInternal()
    {
        EnsurePasses();
        foreach (var p in _passes)
            p.Warmup();

        _camera.SyncFrom(_mainCanvas);

        _wm.SuspendGreedyDraw = true;
        _wm.SuspendReconcile = true;
        _wm.UnclipAll();

        _windows.Refresh();
        foreach (var p in _passes)
        {
            RegisterDesktopThumbnail(p);
            RegisterWindowThumbnails(p);
            RegisterTaskbarThumbnails(p);
        }

        ApplyConfig();

        foreach (var p in _passes)
        {
            p.Show();
        }
        if (_passes.Count > 0) _passes[0].Activate();

        // Attach frame tick to the first pass's grid (drives inertia)
        if (_passes.Count > 0 && _passes[0].Grid != null)
            _passes[0].Grid!.OnFrameTick = OnGridFrameTick;

        foreach (var p in _passes)
            p.Grid?.Start(_camera.X, _camera.Y, _camera.Zoom);
    }

    private void HideInternal(bool syncCamera)
    {
        foreach (var p in _passes)
        {
            if (p.Grid != null) p.Grid.OnFrameTick = null;
            p.Grid?.Stop();
        }

        if (syncCamera)
        {
            var (vx, vy) = _camera.ViewportCamera;
            _mainCanvas.SetCamera(vx, vy);
        }

        _wm.SuspendGreedyDraw = false;
        _wm.ReclipAll();
        _mainCanvas.Commit();
        _wm.SuspendReconcile = false;
        _wm.SuspendProjection = false;

        foreach (var p in _passes)
        {
            p.Hide();
            UnregisterTaskbarThumbnails(p);
            UnregisterWindowThumbnails(p);
            UnregisterDesktopThumbnail(p);
        }
        _windows.Clear();
    }

    private void ApplyConfig()
    {
        foreach (var p in _passes)
        {
            if (p.Grid != null) p.Grid.DrawGrid = _cfg.GridVisible;
            p.SetClickThrough(!_cfg.InputEnabled);
        }
        UpdateAllThumbnails();

        // During Zooming, real-window positions don't need to track the camera
        // (click-through is off — clicks land on the overview form, not real
        // windows). Suppressing projection eliminates the worker-job wait at
        // close-time ReprojectSync.
        _wm.SuspendProjection = (CurrentMode == OverviewMode.Zooming);
    }

    // ==================== THUMBNAILS ====================

    private void RegisterDesktopThumbnail(OverviewOverlay pass)
    {
        IntPtr desktopWnd = GetDesktopWallpaperHwnd();
        if (desktopWnd == IntPtr.Zero) return;

        HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)desktopWnd, out nint thumb);
        pass.DesktopThumb = hr.Succeeded ? thumb : IntPtr.Zero;
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

    private void UnregisterDesktopThumbnail(OverviewOverlay pass)
    {
        if (pass.DesktopThumb != IntPtr.Zero)
        {
            PInvoke.DwmUnregisterThumbnail(pass.DesktopThumb);
            pass.DesktopThumb = IntPtr.Zero;
        }
    }

    private void UpdateDesktopThumbnail(OverviewOverlay pass)
    {
        if (pass.DesktopThumb == IntPtr.Zero) return;

        byte opacity = _cfg.DesktopOpacity;
        if (CurrentMode == OverviewMode.Zooming)
        {
            double t = (_camera.Zoom - OverviewCamera.ZoomMin) / (OverviewCamera.ZoomMax - OverviewCamera.ZoomMin);
            t = Math.Clamp(t, 0.0, 1.0);
            double min = DesktopOpacityZoomedMin;
            double max = _cfg.DesktopOpacity;
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

        PInvoke.DwmUpdateThumbnailProperties(pass.DesktopThumb, props);
    }

    private void RegisterTaskbarThumbnails(OverviewOverlay pass)
    {
        foreach (var hwnd in GetTaskbarHwnds())
            AddTaskbar(pass, (HWND)hwnd);
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

    private static void AddTaskbar(OverviewOverlay pass, HWND hwnd)
    {
        HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, hwnd, out nint thumb);
        if (hr.Succeeded) pass.Taskbars.Add((hwnd, thumb));
    }

    private void UnregisterTaskbarThumbnails(OverviewOverlay pass)
    {
        foreach (var (_, thumb) in pass.Taskbars)
            PInvoke.DwmUnregisterThumbnail(thumb);
        pass.Taskbars.Clear();
    }

    private void UpdateTaskbarThumbnails(OverviewOverlay pass)
    {
        if (pass.Taskbars.Count == 0) return;

        if (!_cfg.TaskbarVisible)
        {
            var hideProps = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = PInvoke.DWM_TNP_VISIBLE,
                fVisible = false
            };
            foreach (var (_, thumb) in pass.Taskbars)
                PInvoke.DwmUpdateThumbnailProperties(thumb, hideProps);
            return;
        }

        foreach (var (hwnd, thumb) in pass.Taskbars)
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

    private void RegisterWindowThumbnails(OverviewOverlay pass)
    {
        // _windows is topmost-first (EnumWindows order). Register
        // bottom-to-top so the topmost window's thumbnail draws last (on top).
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var entry = _windows.Windows[i];
            HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)entry.HWnd, out nint thumb);
            if (hr.Succeeded) pass.Thumbnails.Add((entry.HWnd, thumb, entry.World));
        }
    }

    /// <summary>
    /// Raise the window at the given _windows index in both the system
    /// z-order and the overview thumbnail draw order; move its entry to index 0.
    /// </summary>
    private void BringWindowToFront(int index)
    {
        if (index <= 0 || index >= _windows.Count) return;

        IntPtr hWnd = _windows.Windows[index].HWnd;
        _windows.MoveToFront(index);

        // System z-order — HWND_TOP (0), no move/size/activate
        PInvoke.SetWindowPos((HWND)hWnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        // Re-register the window's thumbnail on every pass so it draws last (on top).
        foreach (var pass in _passes)
        {
            int idx = -1;
            WorldRect world = default;
            for (int i = 0; i < pass.Thumbnails.Count; i++)
            {
                if (pass.Thumbnails[i].hWnd == hWnd)
                {
                    idx = i;
                    world = pass.Thumbnails[i].world;
                    PInvoke.DwmUnregisterThumbnail(pass.Thumbnails[i].thumb);
                    break;
                }
            }
            if (idx < 0) continue;

            pass.Thumbnails.RemoveAt(idx);

            HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)hWnd, out nint newThumb);
            if (hr.Succeeded)
                pass.Thumbnails.Add((hWnd, newThumb, world));
        }

        UpdateAllThumbnails();
    }

    private void UnregisterWindowThumbnails(OverviewOverlay pass)
    {
        foreach (var (_, thumb, _) in pass.Thumbnails)
            PInvoke.DwmUnregisterThumbnail(thumb);
        pass.Thumbnails.Clear();
    }

    private void UpdateAllThumbnails()
    {
        foreach (var p in _passes)
        {
            UpdateDesktopThumbnail(p);
            UpdateTaskbarThumbnails(p);
            UpdateWindowThumbnails(p);
        }
    }

    private void UpdateWindowThumbnails(OverviewOverlay pass)
    {
        double zoom = _camera.Zoom;
        foreach (var (hWnd, thumb, world) in pass.Thumbnails)
        {
            int sx = (int)((world.X - _camera.X) * zoom);
            int sy = (int)((world.Y - _camera.Y) * zoom);
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
            PInvoke.DwmUpdateThumbnailProperties(thumb, props);
        }
    }

    // ==================== INPUT ====================

    private void HandleKeyDown(OverviewOverlay _, KeyEventArgs e)
    {
        if (!_cfg.InputEnabled) return;

        if (e.KeyCode == Keys.Escape)
        {
            TransitionTo(OverviewMode.Hidden);
            e.Handled = true;
            return;
        }

        if (_windows.Count == 0) return;

        if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
        {
            _windows.SelectNext();
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
        {
            _windows.SelectPrev();
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _windows.SelectedIndex >= 0)
        {
            var entry = _windows.Windows[_windows.SelectedIndex];
            GoToWindow(entry.HWnd, entry.World);
            e.Handled = true;
        }
    }

    private void NavigateToSelected()
    {
        if (_windows.SelectedIndex < 0 || _windows.SelectedIndex >= _windows.Count) return;
        var world = _windows.Windows[_windows.SelectedIndex].World;

        // Center overview camera on selected window (do NOT change zoom)
        _camera.CenterOnWorld(world.X, world.Y, world.W, world.H);

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        UpdateAllThumbnails();
    }

    private void HandleMouseDown(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled)
        {
            TransitionTo(OverviewMode.Hidden);
            return;
        }

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        if (e.Button == MouseButtons.Left)
        {
            var (wx, wy) = _camera.WorldFromVirtual(vx, vy);
            int hit = _windows.HitTest(wx, wy);
            if (hit >= 0)
            {
                BringWindowToFront(hit);
                _draggingWindow = true;
                _dragIndex = 0;
                _dragStartVx = vx;
                _dragStartVy = vy;
                return;
            }

            _panning = true;
            _panStartVx = vx;
            _panStartVy = vy;
        }
        else if (e.Button == MouseButtons.Middle)
        {
            _panning = true;
            _panStartVx = vx;
            _panStartVy = vy;
        }
    }

    private void HandleMouseMove(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled) return;

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        if (_draggingWindow && _dragIndex >= 0 && _dragIndex < _windows.Count)
        {
            double dx = (vx - _dragStartVx) / _camera.Zoom;
            double dy = (vy - _dragStartVy) / _camera.Zoom;
            _dragStartVx = vx;
            _dragStartVy = vy;

            _windows.TranslateAt(_dragIndex, dx, dy);
            var entry = _windows.Windows[_dragIndex];

            // Update each pass's matching thumbnail world rect too
            foreach (var p in _passes)
            {
                for (int i = 0; i < p.Thumbnails.Count; i++)
                {
                    var (thHwnd, thThumb, thWorld) = p.Thumbnails[i];
                    if (thHwnd == entry.HWnd)
                    {
                        thWorld.X = entry.World.X; thWorld.Y = entry.World.Y;
                        p.Thumbnails[i] = (thHwnd, thThumb, thWorld);
                    }
                }
            }

            _mainCanvas.SetWindow(entry.HWnd, entry.World.X, entry.World.Y, entry.World.W, entry.World.H);
            UpdateAllThumbnails();
        }
        else if (_panning)
        {
            int dx = vx - _panStartVx;
            int dy = vy - _panStartVy;
            _panStartVx = vx;
            _panStartVy = vy;

            double worldDx = dx / _camera.Zoom;
            double worldDy = dy / _camera.Zoom;
            _camera.PanByVirtual(dx, dy);

            foreach (var p in _passes)
            {
                p.Grid?.AccumulatePan(worldDx, worldDy);
                p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
            }
            UpdateAllThumbnails();
        }
    }

    private void HandleMouseUp(OverviewOverlay pass, MouseEventArgs e)
    {
        if (_draggingWindow)
        {
            _wm.Reproject();
            _draggingWindow = false;
            _dragIndex = -1;
        }
        _panning = false;
    }

    private void HandleMouseWheel(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled) return;

        double notches = e.Delta / MouseWheelDeltaPerNotch;
        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        if (!_camera.ZoomToCursor(vx, vy, notches)) return;

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);

        UpdateAllThumbnails();
    }

    private void HandleDoubleClick(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled) return;
        if (e.Button != MouseButtons.Left) return;

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;
        var (wx, wy) = _camera.WorldFromVirtual(vx, vy);

        int hit = _windows.HitTest(wx, wy);
        if (hit >= 0)
        {
            var entry = _windows.Windows[hit];
            GoToWindow(entry.HWnd, entry.World);
        }
    }

    // ==================== HELPERS ====================

    private void GoToWindow(IntPtr hWnd, WorldRect world)
    {
        if (_mainCanvas.IsCollapsed(hWnd))
            PInvoke.ShowWindow((HWND)hWnd, SHOW_WINDOW_CMD.SW_RESTORE);

        var vs = _screens.VirtualScreen;
        _mainCanvas.CenterOn(world.X, world.Y, world.W, world.H, vs.Width, vs.Height);
        PInvoke.SetForegroundWindow((HWND)hWnd);
        TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (CurrentMode != OverviewMode.Hidden)
            TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
        foreach (var p in _passes)
        {
            p.Close();
            p.Dispose();
        }
        _passes.Clear();
    }

    /// <summary>Find the window that renders the desktop wallpaper.</summary>
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
}
