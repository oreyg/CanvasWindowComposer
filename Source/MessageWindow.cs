using System;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Single hidden NativeWindow that handles custom messages:
/// - WM_HOTKEY (Alt+S search)
/// - WM_CANVAS_INPUT (mouse hook input)
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int HOTKEY_SEARCH = 1;
    private const uint VK_S = 0x53;

    public const int WM_CANVAS_INPUT = 0x0400 + 100;

    private Action? _onSearchHotkey;
    private Action? _onCanvasInput;

    public MessageWindow()
    {
        CreateHandle(new CreateParams());
    }

    public void RegisterHandlers(Action? onSearchHotkey, Action? onCanvasInput)
    {
        _onSearchHotkey = onSearchHotkey;
        _onCanvasInput = onCanvasInput;

        NativeMethods.RegisterHotKey(Handle, HOTKEY_SEARCH,
            NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VK_S);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_HOTKEY when m.WParam.ToInt32() == HOTKEY_SEARCH:
                _onSearchHotkey?.Invoke();
                return;

            case WM_CANVAS_INPUT:
                _onCanvasInput?.Invoke();
                return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_SEARCH);
        DestroyHandle();
    }
}
