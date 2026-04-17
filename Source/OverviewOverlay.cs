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
    private readonly Canvas _mainCanvas;
    private readonly WindowManager _wm;
    private readonly MinimapOverlay _minimap;
    private GridRenderer? _grid;

    /// <summary>Raised when the overview is hidden (for foreground suppression).</summary>
    public event Action? OverviewClosed;

    // Overview's own camera
    private double _camX, _camY, _zoom = 1.0;
    private const double ZoomMin = 0.05;
    private const double ZoomMax = 1.0;
    private const double ZoomStep = 0.1;

    // DWM thumbnail handles
    private readonly List<(IntPtr hWnd, IntPtr thumb, WorldRect world)> _thumbnails = new();

    // Pan state
    private bool _panning;
    private Point _panStart;

    // Arrow key navigation (index into _thumbnails, -1 = none)
    private int _selectedIndex = -1;

    private enum CloseAction { SyncCamera, KeepCamera }

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
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW — no taskbar entry
            return cp;
        }
    }

    public OverviewOverlay(Canvas mainCanvas, WindowManager wm, MinimapOverlay minimap)
    {
        _mainCanvas = mainCanvas;
        _wm = wm;
        _minimap = minimap;

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
            _grid.SetDpiScale(g.DpiX / 96f);
        _grid.StartThread();
    }

    public void Toggle()
    {
        if (Visible)
            HideOverview();
        else
            ShowOverview();
    }

    private void ShowOverview()
    {
        // Cover primary screen
        var screen = Screen.PrimaryScreen!.Bounds;
        Location = new Point(screen.X, screen.Y);
        Size = new Size(screen.Width, screen.Height);

        // Initialize grid once (needs HWND), thread persists
        if (_grid == null)
        {
            _grid = new GridRenderer();
            _grid.Initialize(Handle, screen.Width, screen.Height);
            using (var g = CreateGraphics())
                _grid.SetDpiScale(g.DpiX / 96f);
            _grid.StartThread();
        }

        // Start from current canvas camera
        _camX = _mainCanvas.CamX;
        _camY = _mainCanvas.CamY;
        _zoom = _mainCanvas.Zoom;

        // Unclip all windows so DWM thumbnails can render their content
        _wm.SuspendGreedyDraw = true;
        _wm.UnclipAll();

        // Register DWM thumbnails
        RegisterThumbnails();
        UpdateThumbnails();
        _selectedIndex = -1;

        Show();
        Activate();

        // Wake the grid render thread
        _grid.Start(_camX, _camY, _zoom);
    }

    private void HideOverview(CloseAction action = CloseAction.SyncCamera)
    {
        _grid?.Stop();
        UnregisterThumbnails();

        if (action == CloseAction.SyncCamera)
        {
            var (vx, vy) = ViewportCamera;
            _mainCanvas.SetCamera(vx, vy);
            _wm.Reproject();
            _minimap.NotifyCanvasChanged();
        }

        _wm.SuspendGreedyDraw = false;
        _wm.ReclipAll();

        Hide();
        OverviewClosed?.Invoke();
    }

    /// <summary>Navigate main canvas to a window and close overview.</summary>
    private void GoToWindow(IntPtr hWnd, WorldRect world)
    {
        int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
        if ((style & (int)NativeMethods.WS_MINIMIZE) != 0)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        var screen = Screen.PrimaryScreen!.WorkingArea;
        _mainCanvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
        _wm.Reproject();
        _minimap.NotifyCanvasChanged();
        NativeMethods.SetForegroundWindow(hWnd);
        HideOverview(CloseAction.KeepCamera);
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
        double padX = worldW * 0.1;
        double padY = worldH * 0.1;
        minX -= padX; minY -= padY;
        worldW += padX * 2; worldH += padY * 2;

        if (worldW < 1 || worldH < 1) { _zoom = 1.0; return; }

        // Fit: screen = world * zoom → zoom = screen / world
        _zoom = Math.Min((double)screenW / worldW, (double)screenH / worldH);
        _zoom = Math.Clamp(_zoom, ZoomMin, ZoomMax);

        // Center
        double cx = minX + worldW / 2;
        double cy = minY + worldH / 2;
        _camX = cx - screenW / (2 * _zoom);
        _camY = cy - screenH / (2 * _zoom);
    }

    private void RegisterThumbnails()
    {
        // Enumerate in Z-order (EnumWindows returns topmost first).
        // Register bottom-to-top so the topmost window's thumbnail draws last (on top).
        var zOrder = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (_mainCanvas.HasWindow(hWnd))
                zOrder.Add(hWnd);
            return true;
        }, IntPtr.Zero);

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
        foreach (var (hWnd, thumb, world) in _thumbnails)
        {
            // Project world → screen using overview camera
            int sx = (int)((world.X - _camX) * _zoom);
            int sy = (int)((world.Y - _camY) * _zoom);
            int sw = Math.Max(1, (int)(world.W * _zoom));
            int sh = Math.Max(1, (int)(world.H * _zoom));

            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY,
                rcDestination = new NativeMethods.RECT { Left = sx, Top = sy, Right = sx + sw, Bottom = sy + sh },
                fVisible = true,
                opacity = 255
            };

            NativeMethods.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    // ==================== INPUT ====================

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            HideOverview();
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
        double notches = e.Delta / 120.0;
        double newZoom = Math.Clamp(_zoom + notches * ZoomStep * _zoom, ZoomMin, ZoomMax);

        if (Math.Abs(newZoom - _zoom) < 0.0001) return;

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
            HideOverview();
            return;
        }

        _grid?.Dispose();
        _grid = null;
    }
}
