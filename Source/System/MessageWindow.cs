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

        NativeMethods.RegisterHotKey(Handle, HOTKEY_SEARCH,
            NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VK_S);
        NativeMethods.RegisterHotKey(Handle, HOTKEY_OVERVIEW,
            NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VK_Q);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
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
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_SEARCH);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_OVERVIEW);
        DestroyHandle();
    }
}
