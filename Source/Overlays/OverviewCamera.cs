using System;

namespace CanvasDesktop;

/// <summary>
/// World-space camera for the overview overlays. Pure math — owns position +
/// zoom, exposes pan / zoom-to-cursor / center / world-from-virtual operations.
/// Reads <see cref="IScreens.VirtualScreen"/> for the centering math.
/// </summary>
internal sealed class OverviewCamera
{
    public const double ZoomMin = 0.05;
    public const double ZoomMax = 1.0;
    public const double ZoomStep = 0.1;
    public const double ZoomEpsilon = 0.0001;

    private readonly IScreens _screens;

    public double X { get; private set; }
    public double Y { get; private set; }
    public double Zoom { get; private set; } = 1.0;

    public OverviewCamera(IScreens screens)
    {
        _screens = screens;
    }

    public void SetTo(double x, double y, double zoom)
    {
        X = x;
        Y = y;
        Zoom = zoom;
    }

    /// <summary>Copy camera state from the main canvas.</summary>
    public void SyncFrom(Canvas canvas)
    {
        X = canvas.CamX;
        Y = canvas.CamY;
        Zoom = canvas.Zoom;
    }

    /// <summary>Pan in virtual-screen pixels; converted to world via current zoom.</summary>
    public void PanByVirtual(int virtualDx, int virtualDy)
    {
        X -= virtualDx / Zoom;
        Y -= virtualDy / Zoom;
    }

    /// <summary>
    /// Zoom by <paramref name="notches"/> (positive = in) keeping the world
    /// point under the cursor fixed. Returns true if zoom changed.
    /// </summary>
    public bool ZoomToCursor(int virtualX, int virtualY, double notches)
    {
        double newZoom = Math.Clamp(Zoom + notches * ZoomStep * Zoom, ZoomMin, ZoomMax);
        if (Math.Abs(newZoom - Zoom) < ZoomEpsilon) return false;

        var (wx, wy) = WorldFromVirtual(virtualX, virtualY);
        Zoom = newZoom;
        X = wx - virtualX / Zoom;
        Y = wy - virtualY / Zoom;
        return true;
    }

    /// <summary>Center on a world rect (does not change zoom).</summary>
    public void CenterOnWorld(double worldX, double worldY, double worldW, double worldH)
    {
        var vs = _screens.VirtualScreen;
        X = worldX + worldW / 2 - vs.Width / (2 * Zoom);
        Y = worldY + worldH / 2 - vs.Height / (2 * Zoom);
    }

    /// <summary>World coordinates corresponding to a virtual-screen pixel.</summary>
    public (double x, double y) WorldFromVirtual(int virtualX, int virtualY)
    {
        return (virtualX / Zoom + X, virtualY / Zoom + Y);
    }

    /// <summary>
    /// Camera position matching the centered viewport frame in the grid shader.
    /// Used when handing the overview camera back to the main canvas on close.
    /// </summary>
    public (double x, double y) ViewportCamera
    {
        get
        {
            var vs = _screens.VirtualScreen;
            double ox = vs.Width * (1.0 / Zoom - 1.0) / 2.0;
            double oy = vs.Height * (1.0 / Zoom - 1.0) / 2.0;
            return (X + ox, Y + oy);
        }
    }
}
