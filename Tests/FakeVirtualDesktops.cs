using System;

namespace CanvasDesktop.Tests;

internal sealed class FakeVirtualDesktops : IVirtualDesktops
{
    public Guid CurrentDesktopId { get; set; } = Guid.Empty;
    public event Action? DesktopChanged;

    public bool DefaultIsOnCurrent = true;

    public bool IsOnCurrentDesktop(IntPtr hWnd)
    {
        return DefaultIsOnCurrent;
    }

    /// <summary>Test helper: change <see cref="CurrentDesktopId"/> and raise <see cref="DesktopChanged"/>.</summary>
    public void SwitchTo(Guid newId)
    {
        CurrentDesktopId = newId;
        DesktopChanged?.Invoke();
    }
}
