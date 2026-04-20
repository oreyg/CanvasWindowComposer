using System;
using System.Collections.Generic;

namespace CanvasDesktop.Tests;

internal sealed class FakeInputRouter : IInputRouter
{
    public event Action? InputAvailable;
    public event Action? DragStarted;
    public event Action? ButtonDown;
    public event Action? SearchHotkey;
    public event Action? OverviewHotkey;
    public event Action? EscPressed;

    public int EnableEscHotkeyCalls;
    public int DisableEscHotkeyCalls;
    public bool EscHotkeyEnabled;

    public void EnableEscHotkey()
    {
        EnableEscHotkeyCalls++;
        EscHotkeyEnabled = true;
    }

    public void DisableEscHotkey()
    {
        DisableEscHotkeyCalls++;
        EscHotkeyEnabled = false;
    }

    public void RaiseEscPressed()
    {
        EscPressed?.Invoke();
    }
    public event Action<IntPtr>? WindowMinimized;
    public event Action<IntPtr>? WindowDestroyed;
    public event Action<IntPtr>? WindowShown;
    public event Action<IntPtr>? WindowRestored;
    public event Action<IntPtr>? WindowFocused;
    public event Action<IntPtr>? WindowMoved;
    public event Action? AltTabStarted;
    public event Action? AltTabEnded;

    public bool Enabled { get; set; } = true;

    public List<IntPtr> ExtraPanSurfaces = new();
    public int SetExtraCalls;
    public int ClearExtraCalls;

    // Drainable input
    public int PendingPanDx;
    public int PendingPanDy;
    public bool PendingDragEnded;
    public bool PendingZoom;

    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles)
    {
        ExtraPanSurfaces = new List<IntPtr>(handles);
        SetExtraCalls++;
    }

    public void ClearExtraPanSurfaces()
    {
        ExtraPanSurfaces.Clear();
        ClearExtraCalls++;
    }

    public bool TryDrainPanDelta(out int dx, out int dy)
    {
        dx = PendingPanDx;
        dy = PendingPanDy;
        bool any = dx != 0 || dy != 0;
        PendingPanDx = 0;
        PendingPanDy = 0;
        return any;
    }

    public bool TryDrainDragEnded()
    {
        bool was = PendingDragEnded;
        PendingDragEnded = false;
        return was;
    }

    public bool TryDrainZoom()
    {
        bool was = PendingZoom;
        PendingZoom = false;
        return was;
    }

    // ==================== Test helpers ====================

    public void RaiseInputAvailable()
    {
        InputAvailable?.Invoke();
    }

    public void RaiseDragStarted()
    {
        DragStarted?.Invoke();
    }

    public void RaiseButtonDown()
    {
        ButtonDown?.Invoke();
    }

    public void RaiseSearchHotkey()
    {
        SearchHotkey?.Invoke();
    }

    public void RaiseOverviewHotkey()
    {
        OverviewHotkey?.Invoke();
    }

    public void RaiseWindowMinimized(IntPtr h)
    {
        WindowMinimized?.Invoke(h);
    }

    public void RaiseWindowDestroyed(IntPtr h)
    {
        WindowDestroyed?.Invoke(h);
    }

    public void RaiseWindowShown(IntPtr h)
    {
        WindowShown?.Invoke(h);
    }

    public void RaiseWindowRestored(IntPtr h)
    {
        WindowRestored?.Invoke(h);
    }

    public void RaiseWindowFocused(IntPtr h)
    {
        WindowFocused?.Invoke(h);
    }

    public void RaiseWindowMoved(IntPtr h)
    {
        WindowMoved?.Invoke(h);
    }

    public void RaiseAltTabStarted()
    {
        AltTabStarted?.Invoke();
    }

    public void RaiseAltTabEnded()
    {
        AltTabEnded?.Invoke();
    }

    /// <summary>Convenience: queue a pan delta and fire InputAvailable.</summary>
    public void QueuePanAndRaise(int dx, int dy)
    {
        PendingPanDx += dx;
        PendingPanDy += dy;
        InputAvailable?.Invoke();
    }
}
