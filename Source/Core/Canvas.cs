using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal enum WindowState { Normal, Minimized, Maximized }

internal struct WorldRect
{
    public double X, Y, W, H;
    public WindowState State;
}

/// <summary>Screen-space projection of a <see cref="WorldRect"/>.</summary>
internal readonly record struct WindowRect(int X, int Y, int W, int H);

internal struct CanvasState
{
    public double CamX, CamY, Zoom;
    public Dictionary<IntPtr, WorldRect> Windows;
}

/// <summary>
/// Pure model: camera + world map + projections.
/// No Win32 knowledge — WindowManager consumes this to apply state.
/// </summary>
internal sealed class Canvas
{
    private const int MinWindowWidth = 200;
    private const int MinWindowHeight = 100;

    private double _camX, _camY;
    private double _zoom = 1.0;

    private readonly Dictionary<IntPtr, WorldRect> _windows = new();

    public double CamX => _camX;
    public double CamY => _camY;
    public double Zoom => _zoom;
    public IReadOnlyDictionary<IntPtr, WorldRect> Windows => _windows;
    // ==================== PROJECTIONS ====================

    public (int x, int y) WorldToScreen(double wx, double wy)
    {
        return (
            (int)((wx - _camX) * _zoom),
            (int)((wy - _camY) * _zoom)
        );
    }

    public (int w, int h) WorldToScreenSize(double ww, double wh)
    {
        return (
            Math.Max(MinWindowWidth, (int)Math.Ceiling(ww * _zoom)),
            Math.Max(MinWindowHeight, (int)Math.Ceiling(wh * _zoom))
        );
    }

    /// <summary>Project a world rect to its on-screen position + size in one shot.</summary>
    public WindowRect WorldToScreen(WorldRect world)
    {
        var (x, y) = WorldToScreen(world.X, world.Y);
        var (w, h) = WorldToScreenSize(world.W, world.H);
        return new WindowRect(x, y, w, h);
    }

    public (double x, double y) ScreenToWorld(int sx, int sy)
    {
        return (
            sx / _zoom + _camX,
            sy / _zoom + _camY
        );
    }

    public (double w, double h) ScreenToWorldSize(int sw, int sh)
    {
        return (sw / _zoom, sh / _zoom);
    }

    // ==================== CAMERA ====================

    /// <summary>Raised when the camera moves.</summary>
    public event Action? CameraChanged;

    /// <summary>Raised when a window is collapsed or expanded.</summary>
    public event Action<IntPtr>? CollapseChanged;

    /// <summary>Raised when a window is maximized or restored from maximize.</summary>
    public event Action<IntPtr>? MaximizeChanged;

    /// <summary>Raised when the caller explicitly commits canvas state to the system.</summary>
    public event Action? Committed;

    /// <summary>Propagate current canvas state to the system (reproject real windows).</summary>
    public void Commit()
    {
        Committed?.Invoke();
    }

    public void SetCamera(double camX, double camY)
    {
        _camX = camX;
        _camY = camY;
        CameraChanged?.Invoke();
    }

    public void Pan(int screenDx, int screenDy)
    {
        _camX -= screenDx / _zoom;
        _camY -= screenDy / _zoom;
        CameraChanged?.Invoke();
    }

    /// <summary>Center the camera on a world-space rectangle.</summary>
    public void CenterOn(double worldX, double worldY, double worldW, double worldH, int screenW, int screenH)
    {
        _camX = worldX + worldW / 2 - screenW / (2 * _zoom);
        _camY = worldY + worldH / 2 - screenH / (2 * _zoom);
        CameraChanged?.Invoke();
    }

    /// <summary>Save current camera + world map state.</summary>
    public CanvasState SaveState()
    {
        return new CanvasState
        {
            CamX = _camX,
            CamY = _camY,
            Zoom = _zoom,
            Windows = new Dictionary<IntPtr, WorldRect>(_windows)
        };
    }

    /// <summary>Restore camera + world map from saved state.</summary>
    public void LoadState(CanvasState state)
    {
        _camX = state.CamX;
        _camY = state.CamY;
        _zoom = state.Zoom;
        _windows.Clear();
        if (state.Windows != null)
        {
            foreach (var (k, v) in state.Windows)
                _windows[k] = v;
        }
        CameraChanged?.Invoke();
    }

    /// <summary>Check if a window's projected screen rect overlaps with the screen.</summary>
    public bool IsWindowOnScreen(IntPtr hWnd, int screenW, int screenH)
    {
        if (!_windows.TryGetValue(hWnd, out var world))
            return false;

        var r = WorldToScreen(world);
        return r.X + r.W > 0 && r.X < screenW &&
               r.Y + r.H > 0 && r.Y < screenH;
    }

    public void ResetCamera()
    {
        _camX = 0;
        _camY = 0;
        _zoom = 1.0;
    }

    // ==================== WORLD MAP ====================

    /// <summary>Register a window at the given world position.</summary>
    public void SetWindow(IntPtr hWnd, double wx, double wy, double ww, double wh)
    {
        _windows[hWnd] = new WorldRect { X = wx, Y = wy, W = ww, H = wh };
    }

    /// <summary>Register a window from its current screen position.</summary>
    public void SetWindowFromScreen(IntPtr hWnd, int sx, int sy, int sw, int sh)
    {
        var (wx, wy) = ScreenToWorld(sx, sy);
        var (ww, wh) = ScreenToWorldSize(sw, sh);
        _windows[hWnd] = new WorldRect { X = wx, Y = wy, W = ww, H = wh };
    }

    /// <summary>
    /// Compute the bounding box of all windows in world space.
    /// Returns (minX, minY, maxX, maxY) or null if no windows.
    /// </summary>
    public (double minX, double minY, double maxX, double maxY)? GetWorldExtents()
    {
        if (_windows.Count == 0) return null;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var (hWnd, r) in _windows)
        {
            if (r.State != WindowState.Normal) continue;
            any = true;
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X + r.W > maxX) maxX = r.X + r.W;
            if (r.Y + r.H > maxY) maxY = r.Y + r.H;
        }

        return any ? (minX, minY, maxX, maxY) : null;
    }

    /// <summary>
    /// Get the camera viewport in world space.
    /// screenW/screenH are the monitor dimensions.
    /// </summary>
    public (double x, double y, double w, double h) GetViewport(int screenW, int screenH)
    {
        var (wx, wy) = ScreenToWorld(0, 0);
        return (wx, wy, screenW / _zoom, screenH / _zoom);
    }

    public bool HasWindow(IntPtr hWnd) => _windows.ContainsKey(hWnd);

    public void RemoveWindow(IntPtr hWnd)
    {
        _windows.Remove(hWnd);
    }

    public void ClearWindows()
    {
        _windows.Clear();
    }

    // ==================== WINDOW STATE ====================

    public WindowState GetWindowState(IntPtr hWnd)
    {
        return _windows.TryGetValue(hWnd, out var r) ? r.State : WindowState.Normal;
    }

    public bool IsCollapsed(IntPtr hWnd)
    {
        return GetWindowState(hWnd) == WindowState.Minimized;
    }

    public bool IsMaximized(IntPtr hWnd)
    {
        return GetWindowState(hWnd) == WindowState.Maximized;
    }

    public void CollapseWindow(IntPtr hWnd)
    {
        SetWindowState(hWnd, WindowState.Minimized);
    }

    public void ExpandWindow(IntPtr hWnd)
    {
        if (GetWindowState(hWnd) == WindowState.Minimized)
            SetWindowState(hWnd, WindowState.Normal);
    }

    public void MaximizeWindow(IntPtr hWnd)
    {
        SetWindowState(hWnd, WindowState.Maximized);
    }

    public void UnmaximizeWindow(IntPtr hWnd)
    {
        if (GetWindowState(hWnd) == WindowState.Maximized)
            SetWindowState(hWnd, WindowState.Normal);
    }

    private void SetWindowState(IntPtr hWnd, WindowState state)
    {
        if (!_windows.TryGetValue(hWnd, out var r)) return;
        WindowState old = r.State;
        if (old == state) return;

        r.State = state;
        _windows[hWnd] = r;

        if (old == WindowState.Minimized || state == WindowState.Minimized)
            CollapseChanged?.Invoke(hWnd);
        if (old == WindowState.Maximized || state == WindowState.Maximized)
            MaximizeChanged?.Invoke(hWnd);
    }
}
