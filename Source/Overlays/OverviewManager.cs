using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Coordinator for the overview: owns mode state, camera, inertia, and one
/// OverviewOverlay per physical monitor (each with its own Form + swap chain).
/// </summary>
internal sealed class OverviewManager : IDisposable
{
    public enum Mode { Hidden, Panning, Zooming }

    private readonly record struct ModeConfig(
        bool GridVisible,
        byte DesktopOpacity,
        bool TaskbarVisible,
        bool InputEnabled,
        bool InertiaAllowed);

    private static readonly ModeConfig HiddenCfg = new(
        GridVisible: false,
        DesktopOpacity: 0,
        TaskbarVisible: false,
        InputEnabled: false,
        InertiaAllowed: false);

    private static readonly ModeConfig PanningCfg = new(
        GridVisible: false,
        DesktopOpacity: 255,
        TaskbarVisible: true,
        InputEnabled: false,
        InertiaAllowed: true);

    private static readonly ModeConfig ZoomingCfg = new(
        GridVisible: true,
        DesktopOpacity: 120,
        TaskbarVisible: false,
        InputEnabled: true,
        InertiaAllowed: false);

    private readonly Canvas _mainCanvas;
    private readonly WindowManager _wm;
    private readonly IWindowApi _pos;

    public Mode CurrentMode { get; private set; } = Mode.Hidden;
    private ModeConfig _cfg = HiddenCfg;

    public event Action<Mode, Mode>? BeforeModeChanged;
    public event Action<Mode, Mode>? AfterModeChanged;

    // Camera (world coords). Same semantics as before: world origin maps to
    // virtual-screen (0,0) when _camX == _camY == 0 at zoom 1.
    private double _camX, _camY, _zoom = 1.0;
    private const double ZoomMin = 0.05;
    private const double ZoomMax = 1.0;
    private const double ZoomStep = 0.1;
    private const double ExtentsPaddingRatio = 0.1;
    private const double MouseWheelDeltaPerNotch = 120.0;
    private const double ZoomEpsilon = 0.0001;
    private const byte DesktopOpacityZoomedMin = 30;

    private readonly InertiaTracker _inertia = new();
    private readonly object _inertiaQueueLock = new();
    private int _pendingInertiaDx, _pendingInertiaDy;
    private bool _inertiaPanQueued;

    // Per-monitor passes
    private readonly List<OverviewOverlay> _passes = new();

    /// <summary>HWNDs of all monitor forms (for MouseHook.ExtraPanSurface).</summary>
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
    private readonly List<(IntPtr hWnd, WorldRect world)> _visibleWindows = new();
    private int _selectedIndex = -1;

    // Pan/drag state (virtual-screen coords)
    private bool _panning;
    private int _panStartVx, _panStartVy;
    private bool _draggingWindow;
    private int _dragIndex = -1;
    private int _dragStartVx, _dragStartVy;

    /// <summary>Camera position matching the centered viewport frame in the shader.</summary>
    private (double x, double y) ViewportCamera
    {
        get
        {
            var vs = SystemInformation.VirtualScreen;
            double ox = vs.Width * (1.0 / _zoom - 1.0) / 2.0;
            double oy = vs.Height * (1.0 / _zoom - 1.0) / 2.0;
            return (_camX + ox, _camY + oy);
        }
    }

    public OverviewManager(Canvas mainCanvas, WindowManager wm, IWindowApi positioner)
    {
        _mainCanvas = mainCanvas;
        _wm = wm;
        _pos = positioner;

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Monitor topology changed — rebuild passes to match. If overview is
        // open, close it, rebuild, reopen in the previous mode.
        Mode prev = CurrentMode;
        bool wasVisible = prev != Mode.Hidden;

        if (wasVisible)
            TransitionTo(Mode.Hidden, syncCameraOnClose: false);

        foreach (var p in _passes)
        {
            p.Close();
            p.Dispose();
        }
        _passes.Clear();

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
            pass.OnDoubleClick = HandleDoubleClick;
            _passes.Add(pass);
        }
    }

    public void RecordPanDelta(int dx, int dy)
    {
        _inertia.RecordDelta(dx, dy);
    }

    public void ReleaseInertia()
    {
        if (!_inertia.Release() && CurrentMode != Mode.Hidden)
        {
            TransitionTo(Mode.Hidden);
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
                _passes[0].BeginInvoke(() => { if (CurrentMode != Mode.Hidden) TransitionTo(Mode.Hidden); });
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
        if (CurrentMode == Mode.Hidden) return;
        _camX = _mainCanvas.CamX;
        _camY = _mainCanvas.CamY;
        _zoom = _mainCanvas.Zoom;

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camX, _camY, _zoom);
        UpdateAllThumbnails();
    }

    /// <summary>Single entry point for every mode change.</summary>
    public void TransitionTo(Mode target, bool syncCameraOnClose = true)
    {
        _inertia.Cancel();

        if (CurrentMode == target) return;

        Mode from = CurrentMode;
        ModeConfig cfg = target switch
        {
            Mode.Panning => PanningCfg,
            Mode.Zooming => ZoomingCfg,
            _            => HiddenCfg
        };

        _cfg = cfg;
        CurrentMode = target;

        BeforeModeChanged?.Invoke(from, target);

        if (target == Mode.Hidden)
            HideInternal(syncCamera: syncCameraOnClose && from != Mode.Hidden);
        else if (from == Mode.Hidden)
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

        _camX = _mainCanvas.CamX;
        _camY = _mainCanvas.CamY;
        _zoom = _mainCanvas.Zoom;

        _wm.SuspendGreedyDraw = true;
        _wm.UnclipAll();

        RefreshVisibleWindows();
        foreach (var p in _passes)
        {
            RegisterDesktopThumbnail(p);
            RegisterWindowThumbnails(p);
            RegisterTaskbarThumbnails(p);
        }

        ApplyConfig();
        _selectedIndex = -1;

        foreach (var p in _passes)
        {
            p.Show();
        }
        if (_passes.Count > 0) _passes[0].Activate();

        // Attach frame tick to the first pass's grid (drives inertia)
        if (_passes.Count > 0 && _passes[0].Grid != null)
            _passes[0].Grid!.OnFrameTick = OnGridFrameTick;

        foreach (var p in _passes)
            p.Grid?.Start(_camX, _camY, _zoom);
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
            var (vx, vy) = ViewportCamera;
            _mainCanvas.SetCamera(vx, vy);
        }

        _wm.SuspendGreedyDraw = false;
        _wm.ReclipAll();

        foreach (var p in _passes)
        {
            p.Hide();
            UnregisterTaskbarThumbnails(p);
            UnregisterWindowThumbnails(p);
            UnregisterDesktopThumbnail(p);
        }
        _visibleWindows.Clear();
    }

    private void ApplyConfig()
    {
        foreach (var p in _passes)
        {
            if (p.Grid != null) p.Grid.DrawGrid = _cfg.GridVisible;
            p.SetClickThrough(!_cfg.InputEnabled);
        }
        UpdateAllThumbnails();
    }

    /// <summary>Rebuild the shared list of windows to show, sorted by Z-order.</summary>
    private void RefreshVisibleWindows()
    {
        _visibleWindows.Clear();
        // Use EnumWindows for Z-order; filter to canvas windows in Normal state.
        _pos.EnumWindows(hWnd =>
        {
            if (_mainCanvas.Windows.TryGetValue(hWnd, out var world) &&
                world.State == CanvasDesktop.WindowState.Normal)
            {
                _visibleWindows.Add((hWnd, world));
            }
            return true;
        });
    }

    // ==================== THUMBNAILS ====================

    private void RegisterDesktopThumbnail(OverviewOverlay pass)
    {
        IntPtr desktopWnd = FindDesktopWallpaperWindow();
        if (desktopWnd == IntPtr.Zero) return;

        HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)desktopWnd, out nint thumb);
        pass.DesktopThumb = hr.Succeeded ? thumb : IntPtr.Zero;
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
        if (CurrentMode == Mode.Zooming)
        {
            double t = (_zoom - ZoomMin) / (ZoomMax - ZoomMin);
            t = Math.Clamp(t, 0.0, 1.0);
            double min = DesktopOpacityZoomedMin;
            double max = _cfg.DesktopOpacity;
            opacity = (byte)(min + (max - min) * t);
        }

        // WorkerW spans the entire virtual screen. Position this monitor's
        // slice of it over this form's client area by setting a source rect.
        var b = pass.Screen.Bounds;
        var vs = SystemInformation.VirtualScreen;
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

    private unsafe void RegisterTaskbarThumbnails(OverviewOverlay pass)
    {
        HWND primary = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (primary != HWND.Null) AddTaskbar(pass, primary);

        WNDENUMPROC proc = (HWND hWnd, LPARAM _) =>
        {
            Span<char> buf = stackalloc char[64];
            int len;
            fixed (char* p = buf)
            {
                len = PInvoke.GetClassName(hWnd, new PWSTR(p), buf.Length);
            }
            if (len > 0 && new string(buf[..len]) == "Shell_SecondaryTrayWnd")
                AddTaskbar(pass, hWnd);
            return true;
        };
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);
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
        // _visibleWindows is topmost-first (EnumWindows order). Register
        // bottom-to-top so the topmost window's thumbnail draws last (on top).
        for (int i = _visibleWindows.Count - 1; i >= 0; i--)
        {
            var (hWnd, world) = _visibleWindows[i];
            HRESULT hr = PInvoke.DwmRegisterThumbnail((HWND)pass.Handle, (HWND)hWnd, out nint thumb);
            if (hr.Succeeded) pass.Thumbnails.Add((hWnd, thumb, world));
        }
    }

    /// <summary>
    /// Raise the window at the given _visibleWindows index in both the system
    /// z-order and the overview thumbnail draw order; move its entry to index 0.
    /// </summary>
    private void BringWindowToFront(int index)
    {
        if (index <= 0 || index >= _visibleWindows.Count) return;

        IntPtr hWnd = _visibleWindows[index].hWnd;

        var entry = _visibleWindows[index];
        _visibleWindows.RemoveAt(index);
        _visibleWindows.Insert(0, entry);

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
        foreach (var (hWnd, thumb, world) in pass.Thumbnails)
        {
            int sx = (int)((world.X - _camX) * _zoom);
            int sy = (int)((world.Y - _camY) * _zoom);
            int sw = Math.Max(1, (int)(world.W * _zoom));
            int sh = Math.Max(1, (int)(world.H * _zoom));

            var (iL, iT, iR, iB) = _pos.GetFrameInset(hWnd);
            int fL = (int)(iL * _zoom);
            int fT = (int)(iT * _zoom);
            int fR = (int)(iR * _zoom);
            int fB = (int)(iB * _zoom);

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
            TransitionTo(Mode.Hidden);
            e.Handled = true;
            return;
        }

        if (_visibleWindows.Count == 0) return;

        if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
        {
            _selectedIndex = (_selectedIndex + 1) % _visibleWindows.Count;
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
        {
            _selectedIndex = (_selectedIndex - 1 + _visibleWindows.Count) % _visibleWindows.Count;
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _selectedIndex >= 0)
        {
            var (hWnd, world) = _visibleWindows[_selectedIndex];
            GoToWindow(hWnd, world);
            e.Handled = true;
        }
    }

    private void NavigateToSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _visibleWindows.Count) return;
        var (_, world) = _visibleWindows[_selectedIndex];
        var vs = SystemInformation.VirtualScreen;

        // Center overview camera on selected window (do NOT change zoom)
        _camX = world.X + world.W / 2 - vs.Width / (2 * _zoom);
        _camY = world.Y + world.H / 2 - vs.Height / (2 * _zoom);

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camX, _camY, _zoom);
        UpdateAllThumbnails();
    }

    private void HandleMouseDown(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled)
        {
            TransitionTo(Mode.Hidden);
            return;
        }

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        if (e.Button == MouseButtons.Left)
        {
            double wx = vx / _zoom + _camX;
            double wy = vy / _zoom + _camY;

            // _visibleWindows is topmost-first — iterate forward to hit the topmost window
            for (int i = 0; i < _visibleWindows.Count; i++)
            {
                var (_, world) = _visibleWindows[i];
                if (wx >= world.X && wx <= world.X + world.W &&
                    wy >= world.Y && wy <= world.Y + world.H)
                {
                    BringWindowToFront(i);
                    _draggingWindow = true;
                    _dragIndex = 0;
                    _dragStartVx = vx;
                    _dragStartVy = vy;
                    return;
                }
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

        if (_draggingWindow && _dragIndex >= 0 && _dragIndex < _visibleWindows.Count)
        {
            double dx = (vx - _dragStartVx) / _zoom;
            double dy = (vy - _dragStartVy) / _zoom;
            _dragStartVx = vx;
            _dragStartVy = vy;

            var (hWnd, world) = _visibleWindows[_dragIndex];
            world.X += dx;
            world.Y += dy;
            _visibleWindows[_dragIndex] = (hWnd, world);

            // Update each pass's matching thumbnail world rect too
            foreach (var p in _passes)
            {
                for (int i = 0; i < p.Thumbnails.Count; i++)
                {
                    var (thHwnd, thThumb, thWorld) = p.Thumbnails[i];
                    if (thHwnd == hWnd)
                    {
                        thWorld.X = world.X; thWorld.Y = world.Y;
                        p.Thumbnails[i] = (thHwnd, thThumb, thWorld);
                    }
                }
            }

            _mainCanvas.SetWindow(hWnd, world.X, world.Y, world.W, world.H);
            UpdateAllThumbnails();
        }
        else if (_panning)
        {
            int dx = vx - _panStartVx;
            int dy = vy - _panStartVy;
            _panStartVx = vx;
            _panStartVy = vy;

            _camX -= dx / _zoom;
            _camY -= dy / _zoom;

            foreach (var p in _passes)
            {
                p.Grid?.AccumulatePan(dx / _zoom, dy / _zoom);
                p.Grid?.UpdateCamera(_camX, _camY, _zoom);
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
        double newZoom = Math.Clamp(_zoom + notches * ZoomStep * _zoom, ZoomMin, ZoomMax);

        if (Math.Abs(newZoom - _zoom) < ZoomEpsilon) return;

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        // Zoom to cursor (in virtual-screen coords)
        double worldX = vx / _zoom + _camX;
        double worldY = vy / _zoom + _camY;
        _zoom = newZoom;
        _camX = worldX - vx / _zoom;
        _camY = worldY - vy / _zoom;

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camX, _camY, _zoom);

        UpdateAllThumbnails();
    }

    private void HandleDoubleClick(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled) return;
        if (e.Button != MouseButtons.Left) return;

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;
        double wx = vx / _zoom + _camX;
        double wy = vy / _zoom + _camY;

        foreach (var (hWnd, world) in _visibleWindows)
        {
            if (wx >= world.X && wx <= world.X + world.W &&
                wy >= world.Y && wy <= world.Y + world.H)
            {
                GoToWindow(hWnd, world);
                return;
            }
        }
    }

    // ==================== HELPERS ====================

    private void GoToWindow(IntPtr hWnd, WorldRect world)
    {
        if (_mainCanvas.IsCollapsed(hWnd))
            PInvoke.ShowWindow((HWND)hWnd, SHOW_WINDOW_CMD.SW_RESTORE);

        var vs = SystemInformation.VirtualScreen;
        _mainCanvas.CenterOn(world.X, world.Y, world.W, world.H, vs.Width, vs.Height);
        PInvoke.SetForegroundWindow((HWND)hWnd);
        TransitionTo(Mode.Hidden, syncCameraOnClose: false);
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (CurrentMode != Mode.Hidden)
            TransitionTo(Mode.Hidden, syncCameraOnClose: false);
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
