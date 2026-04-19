using System;

namespace CanvasDesktop;

/// <summary>
/// Foreground-suppression policy: ignore <see cref="IInputRouter.WindowFocused"/>
/// events that fire shortly after a tracked window vanishes (minimize/destroy)
/// or an overview overlay closes, so the camera doesn't recenter on transient
/// focus blips. When a focused window is genuinely off-screen, recenter the
/// canvas on it.
/// </summary>
internal sealed class ForegroundCoordinator
{
    private const long ForegroundSuppressionMs = 500;

    private readonly Canvas _canvas;
    private readonly IClock _clock;
    private readonly IScreens _screens;

    private long _lastWindowLostTick;
    private long _lastOverlayClosedTick;

    public ForegroundCoordinator(
        Canvas canvas,
        IOverviewController overview,
        IInputRouter input,
        IClock clock,
        IScreens screens)
    {
        _canvas = canvas;
        _clock = clock;
        _screens = screens;

        overview.BeforeModeChanged += OnOverviewModeChanged;

        input.WindowDestroyed += OnWindowDestroyed;
        input.WindowFocused   += OnWindowFocused;
        input.WindowMinimized += OnWindowMinimized;
    }

    private void OnOverviewModeChanged(OverviewManager.Mode from, OverviewManager.Mode to)
    {
        if (to == OverviewManager.Mode.Hidden)
        {
            _lastOverlayClosedTick = _clock.TickCount64;
            _canvas.Commit();
        }
    }

    private void OnWindowMinimized(IntPtr hWnd)
    {
        _lastWindowLostTick = _clock.TickCount64;
    }

    private void OnWindowDestroyed(IntPtr hWnd)
    {
        _lastWindowLostTick = _clock.TickCount64;
    }

    private void OnWindowFocused(IntPtr hwnd)
    {
        long now = _clock.TickCount64;
        if (now - _lastWindowLostTick    < ForegroundSuppressionMs ||
            now - _lastOverlayClosedTick < ForegroundSuppressionMs)
            return;

        if (_canvas.HasWindow(hwnd))
        {
            var screen = _screens.PrimaryWorkingArea;
            if (!_canvas.IsWindowOnScreen(hwnd, screen.Width, screen.Height))
            {
                var world = _canvas.Windows[hwnd];
                _canvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
                _canvas.Commit();
            }
        }
    }
}
