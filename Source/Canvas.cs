using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal struct WorldRect
{
    public double X, Y, W, H;
}

/// <summary>
/// Pure model: camera + world map + projections.
/// No Win32 knowledge — WindowManager consumes this to apply state.
/// </summary>
internal sealed class Canvas
{
    private double _camX, _camY;
    private double _zoom = 1.0;
    private const double ZoomMin = 0.3;
    private const double ZoomMax = 3.0;
    private const double ZoomStep = 0.08;

    private readonly Dictionary<IntPtr, WorldRect> _windows = new();

    public double CamX => _camX;
    public double CamY => _camY;
    public double Zoom => _zoom;
    public IReadOnlyDictionary<IntPtr, WorldRect> Windows => _windows;
    public bool IsTransformed => Math.Abs(_zoom - 1.0) > 0.001 || Math.Abs(_camX) > 0.5 || Math.Abs(_camY) > 0.5;

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
            Math.Max(200, (int)Math.Ceiling(ww * _zoom)),
            Math.Max(100, (int)Math.Ceiling(wh * _zoom))
        );
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

    public void Pan(int screenDx, int screenDy)
    {
        _camX -= screenDx / _zoom;
        _camY -= screenDy / _zoom;
    }

    public void ZoomAt(int scrollDelta, int screenCx, int screenCy)
    {
        double notches = scrollDelta / 120.0;
        double newZoom = Math.Clamp(_zoom + notches * ZoomStep, ZoomMin, ZoomMax);

        if (Math.Abs(newZoom - _zoom) < 0.001)
            return;

        var (worldX, worldY) = ScreenToWorld(screenCx, screenCy);
        _zoom = newZoom;
        _camX = worldX - screenCx / _zoom;
        _camY = worldY - screenCy / _zoom;
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

        foreach (var (_, r) in _windows)
        {
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X + r.W > maxX) maxX = r.X + r.W;
            if (r.Y + r.H > maxY) maxY = r.Y + r.H;
        }

        return (minX, minY, maxX, maxY);
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

    public void RemoveWindow(IntPtr hWnd) => _windows.Remove(hWnd);

    public void ClearWindows() => _windows.Clear();
}
