using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// Low-level mouse hook that detects middle-click drag and Ctrl+scroll on the desktop.
/// The callback does NO heavy work — it only accumulates deltas into
/// atomic fields that the UI-thread timer drains.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private bool _dragging;
    private bool _altDrag; // true when drag was initiated with Alt (consume all input)
    private NativeMethods.POINT _lastPoint;

    // Accumulated drag delta (written by hook, read by message handler)
    private int _pendingDx;
    private int _pendingDy;
    private volatile bool _hasPending;
    private volatile bool _dragJustEnded;

    // Target window to post input notifications to
    private IntPtr _notifyHwnd;
    // Uses MessageWindow.WM_CANVAS_INPUT

    // Accumulated zoom scroll (written by hook, read by timer)
    private int _pendingZoomDelta;
    private int _zoomCenterX;
    private int _zoomCenterY;
    private volatile bool _hasZoomPending;

    public bool Enabled { get; set; } = true;

    /// <summary>Set the window handle that receives WM_CANVAS_INPUT when input arrives.</summary>
    public void SetNotifyTarget(IntPtr hwnd) => _notifyHwnd = hwnd;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private void NotifyInput()
    {
        if (_notifyHwnd != IntPtr.Zero)
            PostMessage(_notifyHwnd, (uint)MessageWindow.WM_CANVAS_INPUT, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Called once when middle-click drag starts on the desktop.</summary>
    public event Action? DragStarted;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(curModule.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install mouse hook.");
    }

    /// <summary>
    /// Drain accumulated drag delta (called by UI-thread timer).
    /// </summary>
    public bool TryDrainDelta(out int dx, out int dy)
    {
        if (!_hasPending)
        {
            dx = dy = 0;
            return false;
        }

        dx = Interlocked.Exchange(ref _pendingDx, 0);
        dy = Interlocked.Exchange(ref _pendingDy, 0);
        _hasPending = false;
        return dx != 0 || dy != 0;
    }

    /// <summary>Check and clear the drag-ended flag.</summary>
    public bool TryDrainDragEnded()
    {
        if (!_dragJustEnded) return false;
        _dragJustEnded = false;
        return true;
    }

    /// <summary>
    /// Drain accumulated zoom scroll delta (called by UI-thread timer).
    /// scrollDelta is in units of WHEEL_DELTA (120 = one notch).
    /// </summary>
    public bool TryDrainZoom(out int scrollDelta, out int centerX, out int centerY)
    {
        if (!_hasZoomPending)
        {
            scrollDelta = centerX = centerY = 0;
            return false;
        }

        scrollDelta = Interlocked.Exchange(ref _pendingZoomDelta, 0);
        centerX = _zoomCenterX;
        centerY = _zoomCenterY;
        _hasZoomPending = false;
        return scrollDelta != 0;
    }

    public bool IsDragging => _dragging;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = wParam.ToInt32();
            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

            switch (msg)
            {
                case NativeMethods.WM_MBUTTONDOWN:
                    // Middle-click on desktop, or Alt-only+middle-click anywhere
                    bool alt = IsAltDown() && !IsCtrlDown() && !IsShiftDown();
                    if (IsDesktopOrTaskbarAt(hookStruct.pt) || alt)
                    {
                        _dragging = true;
                        _altDrag = alt;
                        _lastPoint = hookStruct.pt;
                        _pendingDx = 0;
                        _pendingDy = 0;
                        _hasPending = false;
                        DragStarted?.Invoke();
                        return (IntPtr)1; // consume
                    }
                    break;

                case NativeMethods.WM_MOUSEMOVE:
                    if (_dragging)
                    {
                        int dx = hookStruct.pt.X - _lastPoint.X;
                        int dy = hookStruct.pt.Y - _lastPoint.Y;
                        _lastPoint = hookStruct.pt;

                        if (dx != 0 || dy != 0)
                        {
                            Interlocked.Add(ref _pendingDx, dx);
                            Interlocked.Add(ref _pendingDy, dy);
                            _hasPending = true;
                            NotifyInput();
                        }

                        // Don't consume moves — cursor must move freely.
                        // The underlying window won't scroll because we
                        // consumed WM_MBUTTONDOWN so it never saw the click.
                    }
                    break;

                case NativeMethods.WM_MBUTTONUP:
                    if (_dragging)
                    {
                        _dragging = false;
                        _dragJustEnded = true;
                        NotifyInput();
                        return (IntPtr)1; // consume
                    }
                    break;

                case NativeMethods.WM_MOUSEWHEEL:
                    // Alt+ScrollWheel on desktop = zoom
                    if (IsAltDown() && IsDesktopOrTaskbarAt(hookStruct.pt))
                    {
                        // mouseData high word = scroll delta (positive = scroll up = zoom in)
                        int delta = (short)(hookStruct.mouseData >> 16);
                        Interlocked.Add(ref _pendingZoomDelta, delta);
                        _zoomCenterX = hookStruct.pt.X;
                        _zoomCenterY = hookStruct.pt.Y;
                        _hasZoomPending = true;
                        NotifyInput();
                        return (IntPtr)1; // consume to prevent other scroll behavior
                    }
                    break;
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsAltDown() =>
        (NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

    private static bool IsCtrlDown() =>
        (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;

    private static bool IsShiftDown() =>
        (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

    private static bool IsDesktopOrTaskbarAt(NativeMethods.POINT pt)
    {
        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero)
            return true;

        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
            root = hwnd;

        IntPtr desktop = NativeMethods.GetDesktopWindow();
        IntPtr shell = NativeMethods.GetShellWindow();
        if (root == desktop || root == shell)
            return true;

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(root, className, 256);
        string cls = className.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return true;

        return false;
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
