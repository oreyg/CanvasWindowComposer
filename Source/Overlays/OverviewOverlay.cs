using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Fullscreen overlay showing live DWM thumbnails of all canvas windows.
/// Own pan/zoom camera for navigation. Click to navigate main canvas.
/// </summary>
internal sealed class OverviewOverlay : Form
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
    private GridRenderer? _grid;

    public Mode CurrentMode { get; private set; } = Mode.Hidden;
    private ModeConfig _cfg = HiddenCfg;

    /// <summary>Fired when the overview transitions between modes (from, to).</summary>
    public event Action<Mode, Mode>? ModeChanged;

    // Overview's own camera
    private double _camX, _camY, _zoom = 1.0;
    private const double ZoomMin = 0.05;
    private const double ZoomMax = 1.0;
    private const double ZoomStep = 0.1;
    private const double ExtentsPaddingRatio = 0.1;
    private const double MouseWheelDeltaPerNotch = 120.0;
    private const double ZoomEpsilon = 0.0001;
    private const float StandardDpi = 96f;
    private readonly InertiaTracker _inertia = new();
    private readonly object _inertiaQueueLock = new();
    private int _pendingInertiaDx, _pendingInertiaDy;
    private bool _inertiaPanQueued;

    // DWM thumbnail handles
    private readonly List<(IntPtr hWnd, IntPtr thumb, WorldRect world)> _thumbnails = new();

    // Desktop wallpaper thumbnail (rendered behind all windows)
    private IntPtr _desktopThumb;

    // Taskbar thumbnail (pinned to screen edge during panning)
    private IntPtr _taskbarThumb;
    private IntPtr _taskbarHwnd;

    // Pan state
    private bool _panning;
    private Point _panStart;

    // Arrow key navigation (index into _thumbnails, -1 = none)
    private int _selectedIndex = -1;

    /// <summary>Camera position matching the centered viewport frame in the shader.</summary>
    private (double x, double y) ViewportCamera
    {
        get
        {
            // Matches shader: ox = (screenW - screenW * zoom) / 2, in world space = ox / zoom
            double ox = Width * (1.0 / _zoom - 1.0) / 2.0;
            double oy = Height * (1.0 / _zoom - 1.0) / 2.0;
            return (_camX + ox, _camY + oy);
        }
    }

    // Window drag state
    private bool _draggingWindow;
    private int _dragIndex = -1;
    private Point _dragWindowStart;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public OverviewOverlay(Canvas mainCanvas, WindowManager wm, IWindowApi positioner)
    {
        _mainCanvas = mainCanvas;
        _wm = wm;
        _pos = positioner;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(15, 15, 15);
        DoubleBuffered = true;
        KeyPreview = true;

        KeyDown += OnKeyDown;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseWheel += OnMouseWheel;
        MouseClick += OnMouseClick;
    }

    /// <summary>Pre-initialize D3D11 and grid thread to avoid hitch on first open.</summary>
    public void Warmup()
    {
        if (_grid != null) return;

        var screen = Screen.PrimaryScreen!.Bounds;
        Location = new Point(screen.X, screen.Y);
        Size = new Size(screen.Width, screen.Height);

        // Force HWND creation without showing
        _ = Handle;

        _grid = new GridRenderer();
        _grid.Initialize(Handle, screen.Width, screen.Height);
        using (var g = CreateGraphics())
            _grid.SetDpiScale(g.DpiX / StandardDpi);
        _grid.StartThread();
    }

    public void RecordPanDelta(int dx, int dy)
    {
        _inertia.RecordDelta(dx, dy);
    }

    public void ReleaseInertia()
    {
        if (!_inertia.Release() && CurrentMode != Mode.Hidden)
        {
            // No velocity — close overview immediately
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

    /// <summary>Called on grid render thread after each Present. Drives inertia.</summary>
    private void OnGridFrameTick()
    {
        var (dx, dy, stopped) = _inertia.Tick();

        if (stopped)
        {
            if (IsHandleCreated)
                BeginInvoke(() => { if (CurrentMode != Mode.Hidden) TransitionTo(Mode.Hidden); });
            return;
        }

        if ((dx != 0 || dy != 0) && IsHandleCreated)
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
                BeginInvoke(() =>
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

    /// <summary>Sync overview camera to the main canvas and update visuals.</summary>
    public void SyncCamera()
    {
        if (CurrentMode == Mode.Hidden) return;
        _camX = _mainCanvas.CamX;
        _camY = _mainCanvas.CamY;
        _zoom = _mainCanvas.Zoom;

        _grid?.UpdateCamera(_camX, _camY, _zoom);
        UpdateThumbnails();
    }

    /// <summary>Single entry point for every mode change.</summary>
    public void TransitionTo(Mode target, bool syncCameraOnClose = true)
    {
        // Any call cancels inertia — transitioning into a mode means starting fresh,
        // and this also handles the same-mode case (e.g. re-entering Panning during inertia).
        _inertia.Cancel();

        if (CurrentMode == target) return;

        Mode from = CurrentMode;
        ModeConfig cfg = target switch
        {
            Mode.Panning => PanningCfg,
            Mode.Zooming => ZoomingCfg,
            _            => HiddenCfg
        };

        // Update state BEFORE running internals that read _cfg
        _cfg = cfg;
        CurrentMode = target;

        if (target == Mode.Hidden)
            HideInternal(syncCamera: syncCameraOnClose && from != Mode.Hidden);
        else if (from == Mode.Hidden)
            ShowInternal();
        else
            ApplyConfig(); // pan <-> zoom switch

        ModeChanged?.Invoke(from, target);
    }

    private void ShowInternal()
    {
        var screen = Screen.PrimaryScreen!.Bounds;
        Location = new Point(screen.X, screen.Y);
        Size = new Size(screen.Width, screen.Height);

        if (_grid == null)
        {
            _grid = new GridRenderer();
            _grid.Initialize(Handle, screen.Width, screen.Height);
            using (var g = CreateGraphics())
                _grid.SetDpiScale(g.DpiX / StandardDpi);
            _grid.StartThread();
        }

        _camX = _mainCanvas.CamX;
        _camY = _mainCanvas.CamY;
        _zoom = _mainCanvas.Zoom;

        _wm.SuspendGreedyDraw = true;
        _wm.UnclipAll();

        RegisterDesktopThumbnail();
        RegisterTaskbarThumbnail();
        RegisterThumbnails();

        ApplyConfig();
        _selectedIndex = -1;

        Show();
        Activate();

        _grid.OnFrameTick = OnGridFrameTick;
        _grid.Start(_camX, _camY, _zoom);
    }

    private void HideInternal(bool syncCamera)
    {
        if (_grid != null) _grid.OnFrameTick = null;
        _grid?.Stop();

        if (syncCamera)
        {
            var (vx, vy) = ViewportCamera;
            _mainCanvas.SetCamera(vx, vy);
        }

        _wm.SuspendGreedyDraw = false;
        _wm.ReclipAll();

        Hide();
        UnregisterTaskbarThumbnail();
        UnregisterDesktopThumbnail();
        UnregisterThumbnails();
    }

    private void ApplyConfig()
    {
        if (_grid != null) _grid.DrawGrid = _cfg.GridVisible;
        UpdateThumbnails();
    }

    /// <summary>Navigate main canvas to a window and close overview.</summary>
    private void GoToWindow(IntPtr hWnd, WorldRect world)
    {
        int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
        if ((style & (int)NativeMethods.WS_MINIMIZE) != 0)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        var screen = Screen.PrimaryScreen!.WorkingArea;
        _mainCanvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
        NativeMethods.SetForegroundWindow(hWnd);
        TransitionTo(Mode.Hidden, syncCameraOnClose: false);
    }

    private void FitToExtents(int screenW, int screenH)
    {
        var extents = _mainCanvas.GetWorldExtents();
        if (extents == null)
        {
            _camX = 0; _camY = 0; _zoom = 1.0;
            return;
        }

        var (minX, minY, maxX, maxY) = extents.Value;
        double worldW = maxX - minX;
        double worldH = maxY - minY;

        // Add 10% padding
        double padX = worldW * ExtentsPaddingRatio;
        double padY = worldH * ExtentsPaddingRatio;
        minX -= padX; minY -= padY;
        worldW += padX * 2; worldH += padY * 2;

        if (worldW < 1 || worldH < 1) { _zoom = 1.0; return; }

        // Fit: screen = world * zoom -> zoom = screen / world
        _zoom = Math.Min((double)screenW / worldW, (double)screenH / worldH);
        _zoom = Math.Clamp(_zoom, ZoomMin, ZoomMax);

        // Center
        double cx = minX + worldW / 2;
        double cy = minY + worldH / 2;
        _camX = cx - screenW / (2 * _zoom);
        _camY = cy - screenH / (2 * _zoom);
    }

    /// <summary>Find the window that renders the desktop wallpaper.</summary>
    private static IntPtr FindDesktopWallpaperWindow()
    {
        // The wallpaper is rendered by a WorkerW window that sits behind Progman.
        // Sending Progman a special message (0x052C) ensures the WorkerW is created.
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;

        // Trigger WorkerW creation
        NativeMethods.SendMessage(progman, 0x052C, IntPtr.Zero, IntPtr.Zero);

        // Find the WorkerW that has a SHELLDLL_DefView child — the one behind Progman
        IntPtr workerW = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            IntPtr shell = NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shell != IntPtr.Zero)
            {
                // The wallpaper WorkerW is the next sibling after this one
                workerW = NativeMethods.FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        // Fall back to Progman if no WorkerW found
        return workerW != IntPtr.Zero ? workerW : progman;
    }

    private void RegisterDesktopThumbnail()
    {
        IntPtr desktopWnd = FindDesktopWallpaperWindow();
        if (desktopWnd == IntPtr.Zero)
            return;

        int hr = NativeMethods.DwmRegisterThumbnail(Handle, desktopWnd, out _desktopThumb);
        if (hr != 0)
            _desktopThumb = IntPtr.Zero;
    }

    private void UnregisterDesktopThumbnail()
    {
        if (_desktopThumb != IntPtr.Zero)
        {
            NativeMethods.DwmUnregisterThumbnail(_desktopThumb);
            _desktopThumb = IntPtr.Zero;
        }
    }

    private void UpdateDesktopThumbnail()
    {
        if (_desktopThumb == IntPtr.Zero)
            return;

        // Pinned to the overlay, filling the entire screen
        byte opacity = _cfg.DesktopOpacity;
        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY,
            rcDestination = new NativeMethods.RECT { Left = 0, Top = 0, Right = Width, Bottom = Height },
            fVisible = true,
            opacity = opacity
        };

        NativeMethods.DwmUpdateThumbnailProperties(_desktopThumb, ref props);
    }

    private void RegisterTaskbarThumbnail()
    {
        _taskbarHwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (_taskbarHwnd == IntPtr.Zero)
            return;

        int hr = NativeMethods.DwmRegisterThumbnail(Handle, _taskbarHwnd, out _taskbarThumb);
        if (hr != 0)
        {
            _taskbarThumb = IntPtr.Zero;
            _taskbarHwnd = IntPtr.Zero;
        }
    }

    private void UnregisterTaskbarThumbnail()
    {
        if (_taskbarThumb != IntPtr.Zero)
        {
            NativeMethods.DwmUnregisterThumbnail(_taskbarThumb);
            _taskbarThumb = IntPtr.Zero;
            _taskbarHwnd = IntPtr.Zero;
        }
    }

    private void UpdateTaskbarThumbnail()
    {
        if (_taskbarThumb == IntPtr.Zero)
            return;

        if (!_cfg.TaskbarVisible)
        {
            var hideProps = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_VISIBLE,
                fVisible = false
            };
            NativeMethods.DwmUpdateThumbnailProperties(_taskbarThumb, ref hideProps);
            return;
        }

        // Get the taskbar's actual screen rect to pin it at the correct edge
        NativeMethods.GetWindowRect(_taskbarHwnd, out var tbRect);
        int tbW = tbRect.Right - tbRect.Left;
        int tbH = tbRect.Bottom - tbRect.Top;

        // Use the taskbar's screen position directly (it's pinned, not projected)
        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY,
            rcDestination = new NativeMethods.RECT
            {
                Left = tbRect.Left,
                Top = tbRect.Top,
                Right = tbRect.Left + tbW,
                Bottom = tbRect.Top + tbH
            },
            fVisible = true,
            opacity = 255
        };

        NativeMethods.DwmUpdateThumbnailProperties(_taskbarThumb, ref props);
    }

    private void RegisterThumbnails()
    {
        // Enumerate in Z-order (EnumWindows returns topmost first).
        // Register bottom-to-top so the topmost window's thumbnail draws last (on top).
        var zOrder = new List<IntPtr>();
        _pos.EnumWindows(hWnd =>
        {
            if (_mainCanvas.HasWindow(hWnd) && !_mainCanvas.IsCollapsed(hWnd))
                zOrder.Add(hWnd);
            return true;
        });

        // Reverse: register bottom windows first, topmost last
        for (int i = zOrder.Count - 1; i >= 0; i--)
        {
            IntPtr hWnd = zOrder[i];
            if (_mainCanvas.Windows.TryGetValue(hWnd, out var world))
            {
                int hr = NativeMethods.DwmRegisterThumbnail(Handle, hWnd, out IntPtr thumb);
                if (hr == 0)
                    _thumbnails.Add((hWnd, thumb, world));
            }
        }
    }

    private void UnregisterThumbnails()
    {
        foreach (var (_, thumb, _) in _thumbnails)
            NativeMethods.DwmUnregisterThumbnail(thumb);
        _thumbnails.Clear();
    }

    private void UpdateThumbnails()
    {
        UpdateDesktopThumbnail();
        UpdateTaskbarThumbnail();

        foreach (var (hWnd, thumb, world) in _thumbnails)
        {
            // Project world -> screen using overview camera
            int sx = (int)((world.X - _camX) * _zoom);
            int sy = (int)((world.Y - _camY) * _zoom);
            int sw = Math.Max(1, (int)(world.W * _zoom));
            int sh = Math.Max(1, (int)(world.H * _zoom));

            // Shrink destination rect by the DWM invisible frame (shadow)
            // so thumbnails match the visual window size, not GetWindowRect
            var (iL, iT, iR, iB) = _pos.GetFrameInset(hWnd);
            int fL = (int)(iL * _zoom);
            int fT = (int)(iT * _zoom);
            int fR = (int)(iR * _zoom);
            int fB = (int)(iB * _zoom);

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY,
                rcDestination = new NativeMethods.RECT
                {
                    Left = sx + fL,
                    Top = sy + fT,
                    Right = sx + sw - fR,
                    Bottom = sy + sh - fB
                },
                fVisible = true,
                opacity = 255
            };

            NativeMethods.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    // ==================== INPUT ====================

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_cfg.InputEnabled) return;

        if (e.KeyCode == Keys.Escape)
        {
            TransitionTo(Mode.Hidden);
            e.Handled = true;
            return;
        }

        if (_thumbnails.Count == 0) return;

        // Arrow keys cycle through windows by Z-order
        if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
        {
            _selectedIndex = (_selectedIndex + 1) % _thumbnails.Count;
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
        {
            _selectedIndex = (_selectedIndex - 1 + _thumbnails.Count) % _thumbnails.Count;
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _selectedIndex >= 0)
        {
            var (hWnd, _, world) = _thumbnails[_selectedIndex];
            GoToWindow(hWnd, world);
            e.Handled = true;
        }
    }

    private void NavigateToSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _thumbnails.Count) return;
        var (_, _, world) = _thumbnails[_selectedIndex];
        var screen = Screen.PrimaryScreen!.Bounds;

        // Center overview camera on selected window
        _camX = world.X + world.W / 2 - screen.Width / (2 * _zoom);
        _camY = world.Y + world.H / 2 - screen.Height / (2 * _zoom);

        _grid?.UpdateCamera(_camX, _camY, _zoom);
        UpdateThumbnails();
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled)
        {
            // Any click during panning mode stops inertia and closes
            TransitionTo(Mode.Hidden);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            // Check if clicking on a window thumbnail
            double wx = e.X / _zoom + _camX;
            double wy = e.Y / _zoom + _camY;

            for (int i = _thumbnails.Count - 1; i >= 0; i--)
            {
                var (_, _, world) = _thumbnails[i];
                if (wx >= world.X && wx <= world.X + world.W &&
                    wy >= world.Y && wy <= world.Y + world.H)
                {
                    _draggingWindow = true;
                    _dragIndex = i;
                    _dragWindowStart = e.Location;
                    return;
                }
            }

            // Empty space — pan
            _panning = true;
            _panStart = e.Location;
        }
        else if (e.Button == MouseButtons.Middle)
        {
            _panning = true;
            _panStart = e.Location;
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled) return;

        if (_draggingWindow && _dragIndex >= 0 && _dragIndex < _thumbnails.Count)
        {
            double dx = (e.X - _dragWindowStart.X) / _zoom;
            double dy = (e.Y - _dragWindowStart.Y) / _zoom;
            _dragWindowStart = e.Location;

            var (hWnd, thumb, world) = _thumbnails[_dragIndex];
            world.X += dx;
            world.Y += dy;
            _thumbnails[_dragIndex] = (hWnd, thumb, world);

            // Update the main canvas world position
            _mainCanvas.SetWindow(hWnd, world.X, world.Y, world.W, world.H);

            UpdateThumbnails();
        }
        else if (_panning)
        {
            int dx = e.X - _panStart.X;
            int dy = e.Y - _panStart.Y;
            _panStart = e.Location;

            _camX -= dx / _zoom;
            _camY -= dy / _zoom;

            _grid?.AccumulatePan(dx / _zoom, dy / _zoom);
            _grid?.UpdateCamera(_camX, _camY, _zoom);
            UpdateThumbnails();
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_draggingWindow)
        {
            // Reproject the main canvas to apply the new position
            _wm.Reproject();
            _draggingWindow = false;
            _dragIndex = -1;
        }
        _panning = false;
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled) return;

        double notches = e.Delta / MouseWheelDeltaPerNotch;
        double newZoom = Math.Clamp(_zoom + notches * ZoomStep * _zoom, ZoomMin, ZoomMax);

        if (Math.Abs(newZoom - _zoom) < ZoomEpsilon) return;

        // Zoom to cursor
        double worldX = e.X / _zoom + _camX;
        double worldY = e.Y / _zoom + _camY;
        _zoom = newZoom;
        _camX = worldX - e.X / _zoom;
        _camY = worldY - e.Y / _zoom;

        _grid?.UpdateCamera(_camX, _camY, _zoom);

        UpdateThumbnails();
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        // Double-click on a window navigates to it
        // Single click is handled by drag (mousedown/up without move)
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (!_cfg.InputEnabled) return;
        if (e.Button != MouseButtons.Left) return;

        double wx = e.X / _zoom + _camX;
        double wy = e.Y / _zoom + _camY;

        foreach (var (hWnd, _, world) in _thumbnails)
        {
            if (wx >= world.X && wx <= world.X + world.W &&
                wy >= world.Y && wy <= world.Y + world.H)
            {
                GoToWindow(hWnd, world);
                return;
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            TransitionTo(Mode.Hidden);
            return;
        }

        _grid?.Dispose();
        _grid = null;
    }
}
