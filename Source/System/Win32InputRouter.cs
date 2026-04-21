using System;
using System.Collections.Generic;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop;

/// <summary>
/// Production <see cref="IInputRouter"/> wiring the raw-mouse polling thread,
/// hidden message window (hotkeys), and Win32 event hooks into one composed
/// source. Owns lifetime of each underlying component.
///
/// Mouse path: <see cref="RawMouseInput"/> drains WM_INPUT on a dedicated
/// vsync-paced thread, parses to <see cref="MouseEvent"/>s in a ring buffer,
/// and posts a per-frame callback to the UI sync context. The UI-thread
/// callback drains the ring, fires <see cref="DragStarted"/> / <see cref="ButtonDown"/>
/// directly, accumulates pan deltas + drag-end + zoom flags, then fires
/// <see cref="InputAvailable"/> so subscribers can drain via TryDrain*.
/// </summary>
internal sealed class Win32InputRouter : IInputRouter, IDisposable
{
    private readonly RawMouseInput _mouse;
    private readonly MessageWindow _msgWindow;
    private readonly Win32EventRouter _winEvents;

    // Drainable mouse state, populated on the UI thread by DrainRing.
    private int _pendingPanDx;
    private int _pendingPanDy;
    private bool _hasPendingPan;
    private bool _dragJustEnded;
    private bool _zoomPending;

    // WH_MOUSE_LL hook state (EnableMiddleButtonBlock). The delegate is held
    // in a field so it stays GC-rooted while the native hook references it.
    private HOOKPROC? _middleBlockProc;
    private UnhookWindowsHookExSafeHandle? _middleBlockHandle;

    public event Action? InputAvailable;
    public event Action? DragStarted;
    public event Action? ButtonDown;
    public event Action? SearchHotkey;
    public event Action? OverviewHotkey;
    public event Action? EscPressed;

    public void EnableEscHotkey()
    {
        _msgWindow.EnableEscHotkey(() => EscPressed?.Invoke());
    }

    public void DisableEscHotkey()
    {
        _msgWindow.DisableEscHotkey();
    }
    public event Action<IntPtr>? WindowMinimized;
    public event Action<IntPtr>? WindowDestroyed;
    public event Action<IntPtr>? WindowShown;
    public event Action<IntPtr>? WindowRestored;
    public event Action<IntPtr>? WindowFocused;
    public event Action<IntPtr>? WindowMoved;
    public event Action? AltTabStarted;
    public event Action? AltTabEnded;

    public Win32InputRouter(IAppConfig config)
    {
        _mouse = new RawMouseInput(config, OnInputFrame);

        _msgWindow = new MessageWindow();
        _msgWindow.RegisterHandlers(
            onSearchHotkey:   config.DisableSearch     ? null : () => SearchHotkey?.Invoke(),
            onOverviewHotkey: config.DisableZoomHotkey ? null : () => OverviewHotkey?.Invoke());

        _winEvents = new Win32EventRouter();
        _winEvents.WindowMinimized += h => WindowMinimized?.Invoke(h);
        _winEvents.WindowDestroyed += h => WindowDestroyed?.Invoke(h);
        _winEvents.WindowShown     += h => WindowShown?.Invoke(h);
        _winEvents.WindowRestored  += h => WindowRestored?.Invoke(h);
        _winEvents.WindowFocused   += h => WindowFocused?.Invoke(h);
        _winEvents.WindowMoved     += h => WindowMoved?.Invoke(h);
        _winEvents.AltTabStarted   += () => AltTabStarted?.Invoke();
        _winEvents.AltTabEnded     += () => AltTabEnded?.Invoke();

        _mouse.Install();
    }

    /// <summary>
    /// Called on the UI thread once per vsync (when <see cref="RawMouseInput"/>
    /// has new events). Drains the ring buffer: button-down / drag-start fire
    /// events synchronously; pan / drag-end / zoom go into drainable state and
    /// callers consume via TryDrain* after <see cref="InputAvailable"/>.
    /// </summary>
    private void OnInputFrame()
    {
        bool any = false;
        while (_mouse.Events.TryDequeue(out var evt))
        {
            any = true;
            switch (evt.Type)
            {
                case MouseEventType.DragStarted:
                    DragStarted?.Invoke();
                    break;
                case MouseEventType.ButtonDown:
                    ButtonDown?.Invoke();
                    break;
                case MouseEventType.Pan:
                    _pendingPanDx += evt.Dx;
                    _pendingPanDy += evt.Dy;
                    _hasPendingPan = true;
                    break;
                case MouseEventType.DragEnded:
                    _dragJustEnded = true;
                    break;
                case MouseEventType.Zoom:
                    _zoomPending = true;
                    break;
            }
        }

        if (any)
            InputAvailable?.Invoke();
    }

    public bool TryDrainPanDelta(out int dx, out int dy)
    {
        if (!_hasPendingPan)
        {
            dx = dy = 0;
            return false;
        }
        dx = _pendingPanDx;
        dy = _pendingPanDy;
        _pendingPanDx = 0;
        _pendingPanDy = 0;
        _hasPendingPan = false;
        return dx != 0 || dy != 0;
    }

    public bool TryDrainDragEnded()
    {
        if (!_dragJustEnded) return false;
        _dragJustEnded = false;
        return true;
    }

    public bool TryDrainZoom()
    {
        if (!_zoomPending) return false;
        _zoomPending = false;
        return true;
    }

    public bool Enabled
    {
        get { return _mouse.Enabled; }
        set { _mouse.Enabled = value; }
    }

    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles)
    {
        _mouse.SetExtraPanSurfaces(handles);
    }

    public void ClearExtraPanSurfaces()
    {
        _mouse.ClearExtraPanSurfaces();
    }

    /// <summary>
    /// Install WH_MOUSE_LL on the calling (UI) thread. Blocks WM_MBUTTONDOWN
    /// system-wide; matching WM_MBUTTONUP must pass through so any window
    /// that received the down before Install can complete its release.
    /// Idempotent.
    /// </summary>
    public void EnableMiddleButtonBlock()
    {
        if (_middleBlockHandle is { IsInvalid: false }) return;
        _middleBlockProc = MiddleButtonHookCallback;
        _middleBlockHandle = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_MOUSE_LL,
            _middleBlockProc,
            PInvoke.GetModuleHandle((string?)null),
            0);
        if (_middleBlockHandle.IsInvalid)
        {
            _middleBlockHandle.Dispose();
            _middleBlockHandle = null;
            _middleBlockProc = null;
        }
    }

    public void DisableMiddleButtonBlock()
    {
        if (_middleBlockHandle == null) return;
        _middleBlockHandle.Dispose();
        _middleBlockHandle = null;
        _middleBlockProc = null;
    }

    private LRESULT MiddleButtonHookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0 && (uint)wParam.Value == PInvoke.WM_MBUTTONDOWN)
            return (LRESULT)1;
        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        DisableMiddleButtonBlock();
        _winEvents.Dispose();
        _mouse.Dispose();
        _msgWindow.Dispose();
    }
}
