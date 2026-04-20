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
internal sealed class VirtualDesktopService : IVirtualDesktops, IDisposable
{
    private const int PollIntervalMs = 500;

    private readonly IVirtualDesktopManager? _manager;
    private readonly System.Threading.SynchronizationContext? _uiContext;
    private readonly System.Threading.ManualResetEventSlim _stop = new(false);
    private readonly System.Threading.Thread? _pollThread;
    private Guid _currentDesktopId;

    public Guid CurrentDesktopId
    {
        get { return _currentDesktopId; }
    }

    public event Action? DesktopChanged;

    public VirtualDesktopService()
    {
        // Capture the UI sync context now (ctor runs on UI thread) so the
        // polling thread can marshal the DesktopChanged event back. Falls
        // back to firing on the polling thread if there's no sync context
        // (e.g. headless tests).
        _uiContext = System.Threading.SynchronizationContext.Current;

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

        if (_manager != null)
        {
            _pollThread = new System.Threading.Thread(PollLoop)
            {
                IsBackground = true,
                Name = "VDSPoller"
            };
            _pollThread.Start();
        }
    }

    private void PollLoop()
    {
        // ManualResetEventSlim.Wait returns true when set (Dispose), false on timeout.
        while (!_stop.Wait(PollIntervalMs))
        {
            try
            {
                Guid newId = DetectCurrentDesktop();
                if (newId == Guid.Empty || newId == _currentDesktopId) continue;

                _currentDesktopId = newId;
                Action? handler = DesktopChanged;
                if (handler == null) continue;

                if (_uiContext != null)
                    _uiContext.Post(_ => handler.Invoke(), null);
                else
                    handler.Invoke();
            }
            catch
            {
                // Swallow — next tick will retry.
            }
        }
    }

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


    private Guid DetectCurrentDesktop()
    {
        if (_manager == null) return Guid.Empty;

        // Find any visible top-level window and ask which desktop it's on
        Guid desktopId = Guid.Empty;

        WNDENUMPROC proc = (HWND hWnd, LPARAM _) =>
        {
            if (!PInvoke.IsWindowVisible(hWnd))
                return true;
            if (PInvoke.GetParent(hWnd) != HWND.Null)
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
        };
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);

        return desktopId;
    }

    public void Dispose()
    {
        _stop.Set();
        _pollThread?.Join(TimeSpan.FromSeconds(1));
        _stop.Dispose();
        if (_manager != null)
            Marshal.ReleaseComObject(_manager);
    }
}
