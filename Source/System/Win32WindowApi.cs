using System;
using System.Collections.Generic;
using System.Text;
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

    public bool IsWindowVisible(IntPtr hWnd) =>
        NativeMethods.IsWindowVisible(hWnd);

    public int GetWindowStyle(IntPtr hWnd) =>
        NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);

    public (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd)
    {
        NativeMethods.GetWindowRect(hWnd, out var rect);
        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd)
    {
        NativeMethods.GetWindowRect(hWnd, out var full);
        int hr = NativeMethods.DwmGetWindowAttribute(hWnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT visual,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>());

        if (hr != 0)
            return (0, 0, 0, 0);

        return (
            Math.Max(0, visual.Left - full.Left),
            Math.Max(0, visual.Top - full.Top),
            Math.Max(0, full.Right - visual.Right),
            Math.Max(0, full.Bottom - visual.Bottom)
        );
    }

    public uint GetWindowProcessId(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        return pid;
    }

    public bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false)
    {
        if (!NativeMethods.IsWindowVisible(hWnd))
            return false;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == ownPid)
            return false;

        int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
        int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);

        if ((style & (int)NativeMethods.WS_MAXIMIZE) != 0)
            return false;
        if (!allowMinimized && (style & (int)NativeMethods.WS_MINIMIZE) != 0)
            return false;

        if ((exStyle & (int)NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & (int)NativeMethods.WS_EX_APPWINDOW) == 0)
            return false;

        if (NativeMethods.GetParent(hWnd) != IntPtr.Zero)
            return false;

        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, className, 256);
        if (ExcludedClasses.Contains(className.ToString()))
            return false;

        return true;
    }

    public void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags) =>
        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, flags);

    public void ClipWindow(IntPtr hWnd) =>
        NativeMethods.SetWindowRgn(hWnd, NativeMethods.CreateRectRgn(0, 0, 0, 0), true);

    public void UnclipWindow(IntPtr hWnd) =>
        NativeMethods.SetWindowRgn(hWnd, IntPtr.Zero, true);

    public void BatchMove(List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)> items)
    {
        if (items.Count == 0)
            return;

        IntPtr hdwp = NativeMethods.BeginDeferWindowPos(items.Count);
        bool useBatch = hdwp != IntPtr.Zero;

        foreach (var (hWnd, x, y, w, h, posOnly) in items)
        {
            uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
            if (posOnly) flags |= NativeMethods.SWP_NOSIZE;

            if (useBatch)
            {
                hdwp = NativeMethods.DeferWindowPos(hdwp, hWnd, IntPtr.Zero,
                    x, y, w, h, flags);
                if (hdwp == IntPtr.Zero)
                {
                    useBatch = false;
                    NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, flags);
                }
            }
            else
            {
                NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, flags);
            }
        }

        if (useBatch && hdwp != IntPtr.Zero)
            NativeMethods.EndDeferWindowPos(hdwp);
    }

    public void EnumWindows(Func<IntPtr, bool> callback) =>
        NativeMethods.EnumWindows((hWnd, _) => callback(hWnd), IntPtr.Zero);

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
