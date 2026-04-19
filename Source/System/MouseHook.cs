using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// Low-level mouse hook that detects middle-click drag and Ctrl+scroll on the desktop.
/// The callback does NO heavy work — it only accumulates deltas into
/// atomic fields that the UI-thread timer drains.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const int KeyStateDownBit = 0x8000;

    private UnhookWindowsHookExSafeHandle? _hook;
    private readonly HOOKPROC _proc;
    private bool _dragging;
    private bool _altDrag;
    private Point _lastPoint;

    // Extra HWNDs treated as valid pan-initiation surfaces (e.g., overview in pan mode).
    // Mutated on the UI thread; read from the hook thread — use HashSet + volatile swap.
    private volatile HashSet<IntPtr> _extraPanSurfaces = new();
    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles)
    {
        _extraPanSurfaces = new HashSet<IntPtr>(handles);
    }
    public void ClearExtraPanSurfaces()
    {
        _extraPanSurfaces = new HashSet<IntPtr>();
    }

    private int _pendingDx;
    private int _pendingDy;
    private volatile bool _hasPending;
    private volatile bool _dragJustEnded;

    private HWND _notifyHwnd;

    private volatile bool _zoomPending;

    public bool Enabled { get; set; } = true;

    public void SetNotifyTarget(IntPtr hwnd)
    {
        _notifyHwnd = (HWND)hwnd;
    }

    private void NotifyInput()
    {
        if (_notifyHwnd != HWND.Null)
            PInvoke.PostMessage(_notifyHwnd, (uint)MessageWindow.WM_CANVAS_INPUT, 0, 0);
    }

    public event Action? DragStarted;

    public event Action? ButtonDown;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hook = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_MOUSE_LL,
            _proc,
            PInvoke.GetModuleHandle(curModule.ModuleName),
            0);

        if (_hook.IsInvalid)
            throw new InvalidOperationException("Failed to install mouse hook.");
    }

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

    public bool IsDragging => _dragging;

    private LRESULT HookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            uint msg = (uint)wParam.Value;
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            switch (msg)
            {
                case PInvoke.WM_MBUTTONDOWN:
                    {
                        bool alt = !AppConfig.DisableAltPan
                                && IsAltDown()
                                && !IsCtrlDown()
                                && !IsShiftDown();
                        if (alt || IsDesktopOrTaskbarAt(hookStruct.pt))
                        {
                            _dragging = true;
                            _altDrag = alt;
                            _lastPoint = hookStruct.pt;
                            _pendingDx = 0;
                            _pendingDy = 0;
                            _hasPending = false;
                            DragStarted?.Invoke();
                            return (LRESULT)1;
                        }
                        ButtonDown?.Invoke();
                    }
                    break;

                case PInvoke.WM_LBUTTONDOWN:
                case PInvoke.WM_RBUTTONDOWN:
                    ButtonDown?.Invoke();
                    break;

                case PInvoke.WM_MOUSEMOVE:
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
                    }
                    break;

                case PInvoke.WM_MBUTTONUP:
                    if (_dragging)
                    {
                        _dragging = false;
                        _dragJustEnded = true;
                        NotifyInput();
                        return (LRESULT)1;
                    }
                    break;

                case PInvoke.WM_MOUSEWHEEL:
                    {
                        bool alt = IsAltDown() && !IsCtrlDown() && !IsShiftDown();
                        if (alt && IsDesktopOrTaskbarAt(hookStruct.pt))
                        {
                            _zoomPending = true;
                            NotifyInput();
                            return (LRESULT)1;
                        }
                    }
                    break;
            }
        }

        return PInvoke.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsAltDown()
    {
        return (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_MENU) & KeyStateDownBit) != 0;
    }

    private static bool IsCtrlDown()
    {
        return (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & KeyStateDownBit) != 0;
    }

    private static bool IsShiftDown()
    {
        return (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) & KeyStateDownBit) != 0;
    }

    private unsafe bool IsDesktopOrTaskbarAt(Point pt)
    {
        HWND hwnd = PInvoke.WindowFromPoint(pt);
        if (hwnd == HWND.Null)
            return true;

        HWND root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (root == HWND.Null)
            root = hwnd;

        if (_extraPanSurfaces.Contains(root))
            return true;

        HWND desktop = PInvoke.GetDesktopWindow();
        HWND shell = PInvoke.GetShellWindow();
        if (root == desktop || root == shell)
            return true;

        Span<char> classBuf = stackalloc char[256];
        int classLen;
        fixed (char* p = classBuf)
        {
            classLen = PInvoke.GetClassName(root, new PWSTR(p), classBuf.Length);
        }
        if (classLen == 0)
            return false;
        string cls = new string(classBuf[..classLen]);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return true;

        return false;
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
    }
}
