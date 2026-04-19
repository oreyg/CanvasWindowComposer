using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Unified injectable source for everything the orchestrator listens to:
/// low-level mouse hook signals, system hotkeys, and Win32 window-lifecycle
/// events. Production wraps the real MouseHook + MessageWindow + Win32EventRouter
/// trio; tests use <c>FakeInputRouter</c> to drive synthetic input.
/// </summary>
internal interface IInputRouter
{
    /// <summary>
    /// Raised on the UI thread when the hook has accumulated mouse input
    /// that needs draining (pan delta, drag end, or zoom notch).
    /// </summary>
    event Action? InputAvailable;

    bool TryDrainPanDelta(out int dx, out int dy);
    bool TryDrainDragEnded();
    bool TryDrainZoom();

    event Action? DragStarted;
    event Action? ButtonDown;

    event Action? SearchHotkey;
    event Action? OverviewHotkey;

    event Action<IntPtr>? WindowMinimized;
    event Action<IntPtr>? WindowDestroyed;
    event Action<IntPtr>? WindowShown;
    event Action<IntPtr>? WindowRestored;
    event Action<IntPtr>? WindowFocused;
    event Action<IntPtr>? WindowMoved;
    event Action? AltTabStarted;
    event Action? AltTabEnded;

    bool Enabled { get; set; }
    void SetExtraPanSurfaces(IEnumerable<IntPtr> handles);
    void ClearExtraPanSurfaces();
}
