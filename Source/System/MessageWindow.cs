using System;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Single hidden NativeWindow that handles custom messages:
/// - WM_HOTKEY (Alt+S search, Alt+Q overview)
/// - WM_CANVAS_INPUT (mouse hook input)
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int HOTKEY_SEARCH = 1;
    private const int HOTKEY_OVERVIEW = 2;
    private const uint VK_S = 0x53;
    private const uint VK_Q = 0x51;

    public const int WM_CANVAS_INPUT = 0x0400 + 100;

    private Action? _onSearchHotkey;
    private Action? _onOverviewHotkey;
    private Action? _onCanvasInput;

    public MessageWindow()
    {
        CreateHandle(new CreateParams());
    }

    public void RegisterHandlers(Action? onSearchHotkey, Action? onOverviewHotkey, Action? onCanvasInput)
    {
        _onSearchHotkey = onSearchHotkey;
        _onOverviewHotkey = onOverviewHotkey;
        _onCanvasInput = onCanvasInput;

        const HOT_KEY_MODIFIERS modifiers = HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_SEARCH, modifiers, VK_S);
        PInvoke.RegisterHotKey((HWND)Handle, HOTKEY_OVERVIEW, modifiers, VK_Q);
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
            }
        }

        if (m.Msg == WM_CANVAS_INPUT)
        {
            _onCanvasInput?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_SEARCH);
        PInvoke.UnregisterHotKey((HWND)Handle, HOTKEY_OVERVIEW);
        DestroyHandle();
    }
}
