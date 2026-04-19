using System;

namespace CanvasDesktop;

/// <summary>
/// Abstracts virtual-desktop discovery so window-manager / state-cache code
/// can be tested without the COM <c>IVirtualDesktopManager</c>.
/// </summary>
internal interface IVirtualDesktops
{
    Guid CurrentDesktopId { get; }
    bool IsOnCurrentDesktop(IntPtr hWnd);

    /// <summary>Raised after <see cref="Tick"/> detects a desktop switch.</summary>
    event Action? DesktopChanged;

    /// <summary>Poll the OS; fire <see cref="DesktopChanged"/> if the active desktop changed.</summary>
    void Tick();
}
