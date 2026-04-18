using System;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

[ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out bool result);

    [PreserveSig]
    int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
}

[ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
internal class CVirtualDesktopManager { }

/// <summary>
/// Wraps the IVirtualDesktopManager COM interface.
/// Detects virtual desktop switches by polling.
/// </summary>
internal sealed class VirtualDesktopService : IDisposable
{
    private readonly IVirtualDesktopManager? _manager;
    private Guid _currentDesktopId;

    public Guid CurrentDesktopId => _currentDesktopId;

    public VirtualDesktopService()
    {
        try
        {
            _manager = (IVirtualDesktopManager)new CVirtualDesktopManager();
            _currentDesktopId = DetectCurrentDesktop();
        }
        catch
        {
            // COM not available (older OS, etc.)
            _manager = null;
        }
    }

    public bool IsAvailable => _manager != null;

    /// <summary>Check if a window is on the current virtual desktop.</summary>
    public bool IsOnCurrentDesktop(IntPtr hWnd)
    {
        if (_manager == null) return true;
        try
        {
            int hr = _manager.IsWindowOnCurrentVirtualDesktop(hWnd, out bool result);
            return hr >= 0 && result;
        }
        catch { return true; }
    }

    /// <summary>
    /// Check if the virtual desktop changed since last call.
    /// Returns true on switch. Updates CurrentDesktopId.
    /// </summary>
    public bool CheckDesktopChanged()
    {
        if (_manager == null) return false;

        Guid newId = DetectCurrentDesktop();
        if (newId == _currentDesktopId || newId == Guid.Empty)
            return false;

        _currentDesktopId = newId;
        return true;
    }

    private Guid DetectCurrentDesktop()
    {
        if (_manager == null) return Guid.Empty;

        // Find any visible top-level window and ask which desktop it's on
        Guid desktopId = Guid.Empty;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;
            if (NativeMethods.GetParent(hWnd) != IntPtr.Zero)
                return true;

            try
            {
                int hr = _manager!.IsWindowOnCurrentVirtualDesktop(hWnd, out bool onCurrent);
                if (hr >= 0 && onCurrent)
                {
                    hr = _manager.GetWindowDesktopId(hWnd, out desktopId);
                    if (hr >= 0 && desktopId != Guid.Empty)
                        return false; // found it, stop enumerating
                }
            }
            catch { }

            return true;
        }, IntPtr.Zero);

        return desktopId;
    }

    public void Dispose()
    {
        if (_manager != null)
            Marshal.ReleaseComObject(_manager);
    }
}
