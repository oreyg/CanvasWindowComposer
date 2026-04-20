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

    /// <summary>Raised when the active virtual desktop changes.</summary>
    event Action? DesktopChanged;
}
