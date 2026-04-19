using System;

namespace CanvasDesktop;

/// <summary>
/// Owns Win32 event hooks and exposes typed events.
/// Decouples TrayApp from raw WinEvent dispatching.
/// </summary>
internal sealed class WinEventRouter : IDisposable
{
    public event Action<IntPtr>? WindowMinimized;   // minimize start
    public event Action<IntPtr>? WindowDestroyed;  // object destroy
    public event Action<IntPtr>? WindowRestored;   // minimize end
    public event Action<IntPtr>? WindowFocused;    // foreground
    public event Action<IntPtr>? WindowMoved;      // location change (top-level only)
    public event Action? AltTabStarted;
    public event Action? AltTabEnded;

    private readonly UnhookWinEventSafeHandle _hookMinimize;
    private readonly UnhookWinEventSafeHandle _hookForeground;
    private readonly UnhookWinEventSafeHandle _hookSwitch;
    private readonly UnhookWinEventSafeHandle _hookDestroy;
    private readonly UnhookWinEventSafeHandle _hookLocationChange;
    private readonly WINEVENTPROC _winEventProc;

    public WinEventRouter()
    {
        _winEventProc = OnWinEvent;

        uint flags = PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS;

        _hookMinimize = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_MINIMIZESTART,
            PInvoke.EVENT_SYSTEM_MINIMIZEEND,
            null, _winEventProc, 0, 0, flags);

        _hookForeground = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            null, _winEventProc, 0, 0, flags);

        _hookSwitch = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_SWITCHSTART,
            PInvoke.EVENT_SYSTEM_SWITCHEND,
            null, _winEventProc, 0, 0, flags);

        _hookDestroy = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_DESTROY,
            PInvoke.EVENT_OBJECT_DESTROY,
            null, _winEventProc, 0, 0, flags);

        _hookLocationChange = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
            PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
            null, _winEventProc, 0, 0, flags);
    }

    private void OnWinEvent(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        switch (eventType)
        {
            case PInvoke.EVENT_SYSTEM_MINIMIZESTART:
                WindowMinimized?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_OBJECT_DESTROY:
                WindowDestroyed?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_SYSTEM_SWITCHSTART:
                AltTabStarted?.Invoke();
                break;

            case PInvoke.EVENT_SYSTEM_SWITCHEND:
                AltTabEnded?.Invoke();
                break;

            case PInvoke.EVENT_SYSTEM_MINIMIZEEND:
                WindowRestored?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_SYSTEM_FOREGROUND:
                WindowFocused?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW)
                    WindowMoved?.Invoke(hwnd);
                break;
        }
    }

    public void Dispose()
    {
        _hookMinimize.Dispose();
        _hookForeground.Dispose();
        _hookSwitch.Dispose();
        _hookDestroy.Dispose();
        _hookLocationChange.Dispose();
    }
}
