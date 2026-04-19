using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Production <see cref="IInputRouter"/> wiring the low-level mouse hook,
/// hidden message window (hotkeys + WM_CANVAS_INPUT), and WinEvent hooks
/// into one composed source. Owns lifetime of each underlying component.
/// </summary>
internal sealed class Win32InputRouter : IInputRouter, IDisposable
{
    private readonly MouseHook _mouseHook;
    private readonly MessageWindow _msgWindow;
    private readonly Win32EventRouter _winEvents;

    public event Action? InputAvailable;
    public event Action? DragStarted;
    public event Action? ButtonDown;
    public event Action? SearchHotkey;
    public event Action? OverviewHotkey;
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
        _mouseHook = new MouseHook(config);
        _mouseHook.DragStarted += () => DragStarted?.Invoke();
        _mouseHook.ButtonDown  += () => ButtonDown?.Invoke();

        _msgWindow = new MessageWindow();
        _msgWindow.RegisterHandlers(
            onSearchHotkey:   () => SearchHotkey?.Invoke(),
            onOverviewHotkey: () => OverviewHotkey?.Invoke(),
            onCanvasInput:    () => InputAvailable?.Invoke());
        _mouseHook.SetNotifyTarget(_msgWindow.Handle);

        _winEvents = new Win32EventRouter();
        _winEvents.WindowMinimized += h => WindowMinimized?.Invoke(h);
        _winEvents.WindowDestroyed += h => WindowDestroyed?.Invoke(h);
        _winEvents.WindowShown     += h => WindowShown?.Invoke(h);
        _winEvents.WindowRestored  += h => WindowRestored?.Invoke(h);
        _winEvents.WindowFocused   += h => WindowFocused?.Invoke(h);
        _winEvents.WindowMoved     += h => WindowMoved?.Invoke(h);
        _winEvents.AltTabStarted   += () => AltTabStarted?.Invoke();
        _winEvents.AltTabEnded     += () => AltTabEnded?.Invoke();

        _mouseHook.Install();
    }

    public bool TryDrainPanDelta(out int dx, out int dy)
    {
        return _mouseHook.TryDrainDelta(out dx, out dy);
    }

    public bool TryDrainDragEnded()
    {
        return _mouseHook.TryDrainDragEnded();
    }

    public bool TryDrainZoom()
    {
        return _mouseHook.TryDrainZoom();
    }

    public bool Enabled
    {
        get { return _mouseHook.Enabled; }
        set { _mouseHook.Enabled = value; }
    }

    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles)
    {
        _mouseHook.SetExtraPanSurfaces(handles);
    }

    public void ClearExtraPanSurfaces()
    {
        _mouseHook.ClearExtraPanSurfaces();
    }

    public void Dispose()
    {
        _winEvents.Dispose();
        _mouseHook.Dispose();
        _msgWindow.Dispose();
    }
}
