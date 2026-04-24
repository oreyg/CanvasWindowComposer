using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Coordinator for the overview: owns mode state, camera, inertia, and one
/// OverviewOverlay per physical monitor (each with its own Form + swap chain).
/// Thumbnail state (windows + desktop + taskbars, via DWM shared surfaces)
/// lives in <see cref="OverviewThumbnails"/>; this class only drives
/// lifecycle + reconcile calls.
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

    private readonly InertiaTracker _inertia = new();
    private readonly object _inertiaQueueLock = new();
    private int _pendingInertiaDx, _pendingInertiaDy;
    private bool _inertiaPanQueued;

    // Per-monitor passes
    private readonly List<OverviewOverlay> _passes = new();

    // MMCSS registration for the UI thread while the overview is open —
    // keeps WM_PAINT / mouse-message dispatch on near-realtime scheduling.
    // Released on Hide.
    private IntPtr _mmcssHandle;

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

    // All thumbnail surfaces (desktop + taskbars + per-window). Owned here
    // so it can reference _passes; OverviewManager only drives lifecycle.
    private readonly OverviewThumbnails _thumbnails;

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
        _thumbnails = new OverviewThumbnails(_passes, _windows, _camera, _state, _win32, _screens);

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
        _thumbnails.InvalidateShellCache();

        EnsurePasses();
        WarmupPasses();

        if (wasVisible)
            TransitionTo(prev);
    }

    /// <summary>Pre-initialize D3D11 and render threads on every monitor.</summary>
    public void Warmup()
    {
        EnsurePasses();
        WarmupPasses();
    }

    /// <summary>Initialize D3D11 per pass and push screen layout to the grid shader.</summary>
    private void WarmupPasses()
    {
        var monitors = new System.Drawing.Rectangle[_passes.Count];
        for (int i = 0; i < _passes.Count; i++)
        {
            var b = _passes[i].Screen.Bounds;
            monitors[i] = new System.Drawing.Rectangle(b.X, b.Y, b.Width, b.Height);
        }

        foreach (var p in _passes)
        {
            p.Warmup();
            p.Renderer?.SetScreenLayout(p.OriginX, p.OriginY, p.Screen.Primary, monitors);
        }
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

    /// <summary>Called on a render thread after Present. Drives inertia.</summary>
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
            p.Renderer?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        _thumbnails.Reconcile();
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
        WarmupPasses();

        if (_mmcssHandle == IntPtr.Zero)
            _mmcssHandle = Mmcss.Begin("Window Manager");

        _camera.SyncFrom(_mainCanvas);

        _wm.SuspendGreedyDraw = true;
        _wm.SuspendReconcile = true;
        _wm.UnclipAll();

        _windows.Refresh();
        _thumbnails.Show();

        ApplyConfig();

        foreach (var p in _passes)
        {
            p.Show();
        }
        if (_passes.Count > 0) _passes[0].Activate();

        // Attach frame tick to the first pass's renderer (drives inertia)
        if (_passes.Count > 0 && _passes[0].Renderer != null)
            _passes[0].Renderer!.OnFrameTick = OnGridFrameTick;

        foreach (var p in _passes)
            p.Renderer?.Start(_camera.X, _camera.Y, _camera.Zoom);
    }

    private void HideInternal(bool syncCamera)
    {
        foreach (var p in _passes)
        {
            if (p.Renderer != null) p.Renderer.OnFrameTick = null;
            p.Renderer?.Stop();
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

        _thumbnails.Hide();
        foreach (var p in _passes)
            p.Hide();
        _windows.Clear();

        if (_mmcssHandle != IntPtr.Zero)
        {
            Mmcss.Revert(_mmcssHandle);
            _mmcssHandle = IntPtr.Zero;
        }
    }

    private void ApplyConfig()
    {
        // WS_EX_LAYERED is added in Panning and removed in Zooming. Panning
        // is where background-throttle freezes were seen after losing
        // foreground (click lands on an underlying window on drag release);
        // layered compositing seems to keep DWM driving the overlay. Zooming
        // holds foreground itself so it doesn't need the layered cost.
        bool wantLayered = (CurrentMode == OverviewMode.Panning);
        bool wantNoActivate = (CurrentMode == OverviewMode.Panning);
        foreach (var p in _passes)
        {
            if (p.Renderer != null) p.Renderer.DrawGrid = _cfg.GridVisible;
            p.SetModeStyle(wantLayered, wantNoActivate);
            p.SetClickThrough(!_cfg.InputEnabled);
        }
        _thumbnails.Reconcile();

        // During Zooming, real-window positions don't need to track the camera
        // (click-through is off — clicks land on the overview form, not real
        // windows). Suppressing projection eliminates the worker-job wait at
        // close-time ReprojectSync.
        _wm.SuspendProjection = (CurrentMode == OverviewMode.Zooming);
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

        _thumbnails.BringToFront(hWnd);
        _thumbnails.Reconcile();
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
            p.Renderer?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        _thumbnails.Reconcile();
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
            _thumbnails.UpdateWorldRect(entry.HWnd, entry.World);

            _mainCanvas.SetWindow(entry.HWnd, entry.World.X, entry.World.Y, entry.World.W, entry.World.H);
            _thumbnails.Reconcile();
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
                p.Renderer?.AccumulatePan(worldDx, worldDy);
                p.Renderer?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
            }
            _thumbnails.Reconcile();
        }
    }

    private void HandleMouseUp(OverviewOverlay pass, MouseEventArgs e)
    {
        if (_draggingWindow)
        {
            _wm.Reproject(true);
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
            p.Renderer?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);

        _thumbnails.Reconcile();
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
}
