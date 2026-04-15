using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class InertiaEngine : IDisposable
{
    private const int SampleWindow = 5;
    private const double Friction = 0.92;
    private const double StopThreshold = 0.5;
    private const int TickIntervalMs = 16;

    private readonly List<(double dx, double dy, long ticks)> _samples = new();
    private double _vx, _vy;
    private readonly Timer _timer;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private MinimapOverlay? _minimap;

    public void SetMinimap(MinimapOverlay minimap) => _minimap = minimap;

    public InertiaEngine(Canvas canvas, WindowManager wm)
    {
        _canvas = canvas;
        _wm = wm;
        _timer = new Timer { Interval = TickIntervalMs };
        _timer.Tick += OnTick;
    }

    public void RecordDelta(int dx, int dy)
    {
        _samples.Add((dx, dy, Environment.TickCount64));
        while (_samples.Count > SampleWindow)
            _samples.RemoveAt(0);
    }

    public void Release()
    {
        if (_samples.Count < 2)
        {
            _samples.Clear();
            return;
        }

        double sumDx = 0, sumDy = 0;
        int count = 0;
        long now = Environment.TickCount64;

        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            var s = _samples[i];
            if (now - s.ticks > 100) break;
            sumDx += s.dx;
            sumDy += s.dy;
            count++;
        }

        _samples.Clear();
        if (count == 0) return;

        _vx = sumDx / count;
        _vy = sumDy / count;

        if (Math.Abs(_vx) < StopThreshold && Math.Abs(_vy) < StopThreshold)
            return;

        _timer.Start();
    }

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
        {
            _canvas.Pan(dx, dy);
            _wm.Reproject();
            _minimap?.NotifyCanvasChanged();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
