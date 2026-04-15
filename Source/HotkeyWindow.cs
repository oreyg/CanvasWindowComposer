using System;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Hidden NativeWindow that receives WM_HOTKEY messages.
/// Registers Alt+S as a global hotkey.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HOTKEY_SEARCH = 1;
    private const uint VK_S = 0x53;

    private readonly Action _onSearchHotkey;

    public HotkeyWindow(Action onSearchHotkey)
    {
        _onSearchHotkey = onSearchHotkey;

        CreateHandle(new CreateParams());
        NativeMethods.RegisterHotKey(Handle, HOTKEY_SEARCH,
            NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VK_S);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_SEARCH)
        {
            _onSearchHotkey();
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
