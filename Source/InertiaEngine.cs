using System;
using System.Collections.Generic;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// Inertia engine running on a dedicated thread with DwmFlush for VSync pacing.
/// Sleeps on a condition variable when idle — zero CPU when not animating.
/// </summary>
internal sealed class InertiaEngine : IDisposable
{
    private const int SampleWindow = 15;
    private const double FrictionPerFrame = 0.92;
    private const double TargetFrameMs = 16.667;
    private const double StopThresholdPxPerMs = 0.02; // ~1.2 px/frame at 60fps
    private const double MaxDeltaMs = 60;       // clamp dt to prevent hitch jumps
    private const long VelocityWindowMs = 100;

    private readonly List<(double dx, double dy, long ticks)> _samples = new();
    private double _vx, _vy;
    private volatile bool _animating;
    private volatile bool _alive = true;
    private readonly ManualResetEventSlim _wakeEvent = new(false);
    private readonly Thread _thread;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private MinimapOverlay? _minimap;
    private System.Windows.Forms.Control? _uiControl;

    public void SetMinimap(MinimapOverlay minimap) => _minimap = minimap;
    public void SetUiControl(System.Windows.Forms.Control control) => _uiControl = control;

    public InertiaEngine(Canvas canvas, WindowManager wm)
    {
        _canvas = canvas;
        _wm = wm;
        _thread = new Thread(ThreadLoop) { IsBackground = true, Name = "Inertia" };
        _thread.Start();
    }

    public void RecordDelta(int dx, int dy)
    {
        _samples.Add((dx, dy, Environment.TickCount64));
        if (_samples.Count > SampleWindow)
            _samples.RemoveRange(0, _samples.Count - SampleWindow);
    }

    public void Release()
    {
        if (_samples.Count < 2)
        {
            _samples.Clear();
            return;
        }

        // Sum deltas over the velocity window and divide by elapsed time
        // to get velocity in pixels per millisecond
        double sumDx = 0, sumDy = 0;
        long now = Environment.TickCount64;
        long oldest = now;

        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            var s = _samples[i];
            if (now - s.ticks > VelocityWindowMs) break;
            sumDx += s.dx;
            sumDy += s.dy;
            oldest = s.ticks;
        }

        _samples.Clear();

        long elapsed = now - oldest;
        if (elapsed < 1) return;

        // Pixels per millisecond
        _vx = sumDx / elapsed;
        _vy = sumDy / elapsed;

        if (Math.Abs(_vx) < StopThresholdPxPerMs && Math.Abs(_vy) < StopThresholdPxPerMs)
            return;

        _animating = true;
        _wakeEvent.Set(); // wake the thread
    }

    public void Cancel()
    {
        _animating = false;
        _vx = _vy = 0;
        _samples.Clear();
    }

    private void ThreadLoop()
    {
        while (_alive)
        {
            _wakeEvent.Wait(); // sleep until Release() signals

            long lastTick = Environment.TickCount64;

            while (_animating && _alive)
            {
                NativeMethods.DwmFlush();

                long now = Environment.TickCount64;
                double dt = Math.Clamp(now - lastTick, 1, MaxDeltaMs);
                lastTick = now;

                double decay = Math.Pow(FrictionPerFrame, dt / TargetFrameMs);
                _vx *= decay;
                _vy *= decay;

                if (Math.Abs(_vx) < StopThresholdPxPerMs && Math.Abs(_vy) < StopThresholdPxPerMs)
                {
                    _animating = false;
                    break;
                }

                // Displacement = velocity (px/ms) * dt (ms)
                int dx = (int)Math.Round(_vx * dt);
                int dy = (int)Math.Round(_vy * dt);

                if ((dx != 0 || dy != 0) && _uiControl != null)
                {
                    int cdx = dx, cdy = dy;
                    _uiControl.Invoke(() =>
                    {
                        if (!_animating) return;
                        _canvas.Pan(cdx, cdy);
                        _wm.Reproject();
                        _minimap?.NotifyCanvasChanged();
                    });
                }
            }

            _wakeEvent.Reset(); // go back to sleep
        }
    }

    public void Dispose()
    {
        _alive = false;
        _animating = false;
        _wakeEvent.Set(); // wake to exit
        _thread.Join(1000);
        _wakeEvent.Dispose();
    }
}
