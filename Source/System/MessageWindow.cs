using System;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Single hidden NativeWindow that handles WM_HOTKEY (Alt+S search, Alt+Q overview).
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int HOTKEY_SEARCH = 1;
    private const int HOTKEY_OVERVIEW = 2;
    private const int HOTKEY_ESCAPE = 3;
    private const uint VK_S = 0x53;
    private const uint VK_Q = 0x51;
    private const uint VK_ESCAPE = 0x1B;

    private Action? _onSearchHotkey;
    private Action? _onOverviewHotkey;
    private Action? _onEscHotkey;
    private bool _escRegistered;

    public MessageWindow()
    {
        CreateHandle(new CreateParams());
    }

    public void RegisterHandlers(Action? onSearchHotkey, Action? onOverviewHotkey)
    {
        _onSearchHotkey = onSearchHotkey;
        _onOverviewHotkey = onOverviewHotkey;

        const HOT_KEY_MODIFIERS modifiers = HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_SEARCH, modifiers, VK_S);
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_OVERVIEW, modifiers, VK_Q);
    }

    /// <summary>
    /// Register Esc as a global hotkey. Caller is responsible for pairing this
    /// with <see cref="DisableEscHotkey"/> when the gate (e.g. overview open)
    /// closes — Esc is heavily used by other apps and we shouldn't hold it
    /// outside the moments we actually consume it.
    /// </summary>
    public void EnableEscHotkey(Action onEsc)
    {
        _onEscHotkey = onEsc;
        if (_escRegistered) return;
        const HOT_KEY_MODIFIERS modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        _escRegistered = PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_ESCAPE, modifiers, VK_ESCAPE);
    }

    public void DisableEscHotkey()
    {
        if (_escRegistered)
        {
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_ESCAPE);
            _escRegistered = false;
        }
        _onEscHotkey = null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)PInvoke.WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HOTKEY_SEARCH:
                    _onSearchHotkey?.Invoke();
                    return;
                case HOTKEY_OVERVIEW:
                    _onOverviewHotkey?.Invoke();
                    return;
                case HOTKEY_ESCAPE:
                    _onEscHotkey?.Invoke();
                    return;
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_SEARCH);
        PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_OVERVIEW);
        if (_escRegistered)
            PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_ESCAPE);
        DestroyHandle();
    }
}
