using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Saves the canvas state per virtual desktop and swaps it on
/// <see cref="IVirtualDesktops.DesktopChanged"/>. Cancels overview inertia
/// on switch and resets the WM so projected positions don't bleed across
/// desktops. Fires <see cref="AfterStateLoaded"/> once the new desktop's
/// canvas is in place — the minimap subscribes to that for its bring-up flash.
/// </summary>
internal sealed class DesktopStateCache
{
    private readonly Dictionary<Guid, CanvasState> _states = new();
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly IOverviewController _overview;
    private readonly IVirtualDesktops _vds;
    private Guid _lastDesktopId;

    public event Action? AfterStateLoaded;

    public DesktopStateCache(Canvas canvas, WindowManager wm, IOverviewController overview, IVirtualDesktops vds)
    {
        _canvas = canvas;
        _wm = wm;
        _overview = overview;
        _vds = vds;
        _lastDesktopId = vds.CurrentDesktopId;

        vds.DesktopChanged += OnDesktopChanged;
    }

    private void OnDesktopChanged()
    {
        _overview.CancelInertia();

        if (_lastDesktopId != Guid.Empty)
        {
            _states[_lastDesktopId] = _canvas.SaveState();
            _wm.Reset();
        }

        _lastDesktopId = _vds.CurrentDesktopId;

        if (_states.TryGetValue(_lastDesktopId, out var state))
            _canvas.LoadState(state);
        _canvas.Commit();

        AfterStateLoaded?.Invoke();
    }
}
