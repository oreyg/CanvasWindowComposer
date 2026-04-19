using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Production implementation of IWindowApi wrapping Win32 APIs.
/// </summary>
internal sealed class Win32WindowApi : IWindowApi
{
    private static readonly HashSet<string> ExcludedClasses = new()
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow"
    };

    public bool IsWindowVisible(IntPtr hWnd)
    {
        return PInvoke.IsWindowVisible((HWND)hWnd);
    }

    public int GetWindowStyle(IntPtr hWnd)
    {
        return PInvoke.GetWindowLong((HWND)hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
    }

    public (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd)
    {
        PInvoke.GetWindowRect((HWND)hWnd, out RECT rect);
        return (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }

    public unsafe (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd)
    {
        PInvoke.GetWindowRect((HWND)hWnd, out RECT full);
        RECT visual;
        HRESULT hr = PInvoke.DwmGetWindowAttribute((HWND)hWnd,
            DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &visual,
            (uint)sizeof(RECT));

        if (hr.Failed)
            return (0, 0, 0, 0);

        return (
            Math.Max(0, visual.left - full.left),
            Math.Max(0, visual.top - full.top),
            Math.Max(0, full.right - visual.right),
            Math.Max(0, full.bottom - visual.bottom)
        );
    }

    public unsafe uint GetWindowProcessId(IntPtr hWnd)
    {
        uint pid;
        _ = PInvoke.GetWindowThreadProcessId((HWND)hWnd, &pid);
        return pid;
    }

    public unsafe bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false)
    {
        HWND h = (HWND)hWnd;
        if (!PInvoke.IsWindowVisible(h))
            return false;

        uint pid;
        _ = PInvoke.GetWindowThreadProcessId(h, &pid);
        if (pid == ownPid)
            return false;

        int style = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        int exStyle = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        if ((style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0)
            return false;
        if (!allowMinimized && (style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0)
            return false;

        if ((exStyle & (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & (int)WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
            return false;

        if (PInvoke.GetParent(h) != HWND.Null)
            return false;

        int cloaked;
        HRESULT cloakedHr = PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &cloaked, sizeof(int));
        if (cloakedHr.Succeeded && cloaked != 0)
            return false;

        Span<char> classBuf = stackalloc char[256];
        int classLen;
        fixed (char* p = classBuf)
        {
            classLen = PInvoke.GetClassName(h, new PWSTR((char*)p), classBuf.Length);
        }
        if (classLen == 0)
            return false;
        string className = new string(classBuf[..classLen]);
        if (ExcludedClasses.Contains(className))
            return false;

        return true;
    }

    public void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags)
    {
        PInvoke.SetWindowPos((HWND)hWnd, HWND.Null, x, y, w, h, (SET_WINDOW_POS_FLAGS)flags);
    }

    public void ClipWindow(IntPtr hWnd)
    {
        HRGN rgn = PInvoke.CreateRectRgn(0, 0, 0, 0);
        _ = PInvoke.SetWindowRgn((HWND)hWnd, rgn, true);
    }

    public void UnclipWindow(IntPtr hWnd)
    {
        _ = PInvoke.SetWindowRgn((HWND)hWnd, (HRGN)IntPtr.Zero, true);
    }

    public void BatchMove(List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)> items, bool allowAsync)
    {
        if (items.Count == 0)
            return;

        HDWP hdwp = PInvoke.BeginDeferWindowPos(items.Count);
        bool useBatch = hdwp != default(HDWP);

        foreach (var (hWnd, x, y, w, h, posOnly) in items)
        {
            SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
            if (posOnly) flags |= SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
            if (allowAsync) flags |= SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;

            HWND target = (HWND)hWnd;
            if (useBatch)
            {
                hdwp = PInvoke.DeferWindowPos(hdwp, target, HWND.Null, x, y, w, h, flags);
                if (hdwp == default(HDWP))
                {
                    useBatch = false;
                    PInvoke.SetWindowPos(target, HWND.Null, x, y, w, h, flags);
                }
            }
            else
            {
                PInvoke.SetWindowPos(target, HWND.Null, x, y, w, h, flags);
            }
        }

        if (useBatch && hdwp != default(HDWP))
            PInvoke.EndDeferWindowPos(hdwp);
    }

    public unsafe void EnumWindows(Func<IntPtr, bool> callback)
    {
        WNDENUMPROC proc = (HWND hWnd, LPARAM _) => callback(hWnd);
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);
    }

    public IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas()
    {
        var areas = new List<(int, int, int, int)>();
        foreach (var screen in Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            areas.Add((wa.Left, wa.Top, wa.Width, wa.Height));
        }
        return areas;
    }
}
