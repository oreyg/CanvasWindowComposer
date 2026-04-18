using System;

namespace CanvasDesktop;

/// <summary>
/// Owns Win32 event hooks and exposes typed events.
/// Decouples TrayApp from raw WinEvent dispatching.
/// </summary>
internal sealed class WinEventRouter : IDisposable
{
    public event Action<IntPtr>? WindowLost;       // minimize or destroy
    public event Action<IntPtr>? WindowRestored;   // minimize end
    public event Action<IntPtr>? WindowFocused;    // foreground
    public event Action<IntPtr>? WindowMoved;      // location change (top-level only)
    public event Action? AltTabStarted;
    public event Action? AltTabEnded;

    private IntPtr _hookMinimize;
    private IntPtr _hookForeground;
    private IntPtr _hookSwitch;
    private IntPtr _hookDestroy;
    private IntPtr _hookLocationChange;
    private readonly NativeMethods.WinEventDelegate _winEventProc;

    public WinEventRouter()
    {
        _winEventProc = OnWinEvent;

        uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

        _hookMinimize = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, flags);

        _hookForeground = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, flags);

        _hookSwitch = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_SWITCHSTART,
            NativeMethods.EVENT_SYSTEM_SWITCHEND,
            IntPtr.Zero, _winEventProc, 0, 0, flags);

        _hookDestroy = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_DESTROY,
            NativeMethods.EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _winEventProc, 0, 0, flags);

        _hookLocationChange = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, 0, 0, flags);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        switch (eventType)
        {
            case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
            case NativeMethods.EVENT_OBJECT_DESTROY:
                WindowLost?.Invoke(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_SWITCHSTART:
                AltTabStarted?.Invoke();
                break;

            case NativeMethods.EVENT_SYSTEM_SWITCHEND:
                AltTabEnded?.Invoke();
                break;

            case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                WindowRestored?.Invoke(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                WindowFocused?.Invoke(hwnd);
                break;

            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject == NativeMethods.OBJID_WINDOW)
                    WindowMoved?.Invoke(hwnd);
                break;
        }
    }

    public void Dispose()
    {
        if (_hookMinimize != IntPtr.Zero)
        { NativeMethods.UnhookWinEvent(_hookMinimize); _hookMinimize = IntPtr.Zero; }
        if (_hookForeground != IntPtr.Zero)
        { NativeMethods.UnhookWinEvent(_hookForeground); _hookForeground = IntPtr.Zero; }
        if (_hookSwitch != IntPtr.Zero)
        { NativeMethods.UnhookWinEvent(_hookSwitch); _hookSwitch = IntPtr.Zero; }
        if (_hookDestroy != IntPtr.Zero)
        { NativeMethods.UnhookWinEvent(_hookDestroy); _hookDestroy = IntPtr.Zero; }
        if (_hookLocationChange != IntPtr.Zero)
        { NativeMethods.UnhookWinEvent(_hookLocationChange); _hookLocationChange = IntPtr.Zero; }
    }
}
