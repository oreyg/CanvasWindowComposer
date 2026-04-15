using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class InertiaEngine : IDisposable
{
    private const int SampleWindow = 5;       // number of recent deltas to average
    private const double Friction = 0.92;     // velocity multiplier per tick (< 1 = deceleration)
    private const double StopThreshold = 0.5; // stop when velocity drops below this
    private const int TickIntervalMs = 16;    // ~60 fps

    private readonly List<(double dx, double dy, long ticks)> _samples = new();
    private double _vx, _vy;
    private readonly Timer _timer;

    public InertiaEngine()
    {
        _timer = new Timer { Interval = TickIntervalMs };
        _timer.Tick += OnTick;
    }

    /// <summary>Record a drag delta sample (call on every mouse move during drag).</summary>
    public void RecordDelta(int dx, int dy)
    {
        _samples.Add((dx, dy, Environment.TickCount64));

        // Keep only the last N samples
        while (_samples.Count > SampleWindow)
            _samples.RemoveAt(0);
    }

    /// <summary>Call when drag ends. Computes release velocity and starts inertia animation.</summary>
    public void Release()
    {
        if (_samples.Count < 2)
        {
            _samples.Clear();
            return;
        }

        // Average recent deltas to get release velocity (pixels per sample)
        double sumDx = 0, sumDy = 0;
        int count = 0;
        long now = Environment.TickCount64;

        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            var s = _samples[i];
            // Only consider samples from the last 100ms
            if (now - s.ticks > 100)
                break;
            sumDx += s.dx;
            sumDy += s.dy;
            count++;
        }

        _samples.Clear();

        if (count == 0)
            return;

        _vx = sumDx / count;
        _vy = sumDy / count;

        if (Math.Abs(_vx) < StopThreshold && Math.Abs(_vy) < StopThreshold)
            return;

        _timer.Start();
    }

    /// <summary>Cancel any in-progress inertia (e.g., user starts a new drag).</summary>
    public void Cancel()
    {
        _timer.Stop();
        _vx = _vy = 0;
        _samples.Clear();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _vx *= Friction;
        _vy *= Friction;

        int dx = (int)Math.Round(_vx);
        int dy = (int)Math.Round(_vy);

        if (Math.Abs(_vx) < StopThreshold && Math.Abs(_vy) < StopThreshold)
        {
            _timer.Stop();
            return;
        }

        if (dx != 0 || dy != 0)
            WindowMover.MoveAll(dx, dy);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
