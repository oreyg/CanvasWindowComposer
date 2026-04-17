using System;
using System.Collections.Generic;
using System.Text;

namespace CanvasDesktop;

/// <summary>
/// Consumes Canvas state and applies it to real windows.
/// Handles enumeration, positioning, DPI injection, reconciliation.
/// </summary>
internal sealed class WindowManager
{
    private readonly Canvas _canvas;
    private readonly DllInjector _injector;
    private readonly VirtualDesktopService? _vds;

    // Track last projected screen positions to detect manual moves
    private readonly Dictionary<IntPtr, (int x, int y, int w, int h)> _lastScreen = new();

    // Windows with clipped (empty) region to prevent them from fighting off-screen
    private readonly HashSet<IntPtr> _clippedWindows = new();

    // Temporarily suspends greedy draw (SetWindowRgn clipping)
    public bool SuspendGreedyDraw { get; set; }

    public WindowManager(Canvas canvas, DllInjector injector, VirtualDesktopService? vds = null)
    {
        _canvas = canvas;
        _injector = injector;
        _vds = vds;
    }

    /// <summary>
    /// Project all canvas windows to screen. Call after Pan.
    /// </summary>
    public void Reproject()
    {
        DiscoverNewWindows();

        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (!IsWindowActive(hWnd))
                continue;

            int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
            if ((style & (int)NativeMethods.WS_MAXIMIZE) != 0)
                continue;

            var (sx, sy) = _canvas.WorldToScreen(world.X, world.Y);
            var (sw, sh) = _canvas.WorldToScreenSize(world.W, world.H);
            bool onScreen = IsOnAnyScreen(sx, sy, sw, sh);

            bool wasClipped = _clippedWindows.Contains(hWnd);
            if (!AppConfig.DisableGreedyDraw && !SuspendGreedyDraw && !onScreen)
            {
                if (!wasClipped)
                {
                    NativeMethods.SetWindowRgn(hWnd, NativeMethods.CreateRectRgn(0, 0, 0, 0), true);
                    _clippedWindows.Add(hWnd);
                    var (px, py) = ClampToScreenEdge(sx, sy, sw, sh);
                    batch.Add((hWnd, px, py, sw, sh, false));
                    _lastScreen[hWnd] = (px, py, sw, sh);
                }
                continue;
            }

            if (wasClipped)
            {
                NativeMethods.SetWindowRgn(hWnd, IntPtr.Zero, true);
                _clippedWindows.Remove(hWnd);
            }

            batch.Add((hWnd, sx, sy, sw, sh, true));
            _lastScreen[hWnd] = (sx, sy, sw, sh);
        }

        BatchSetPositions(batch);
    }

    /// <summary>
    /// Project a single window (e.g., after restore from minimized).
    /// </summary>
    public void ReprojectWindow(IntPtr hWnd)
    {
        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return;

        var (sx, sy) = _canvas.WorldToScreen(world.X, world.Y);
        var (sw, sh) = _canvas.WorldToScreenSize(world.W, world.H);

        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, sx, sy, sw, sh,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        _lastScreen[hWnd] = (sx, sy, sw, sh);
    }

    /// <summary>
    /// Detect windows the user manually moved/resized and update the canvas.
    /// </summary>
    public void Reconcile()
    {
        foreach (var (hWnd, _) in _canvas.Windows)
            ReconcileWindow(hWnd);
    }

    /// <summary>Update a single window's world position from its actual screen position.</summary>
    public void ReconcileWindow(IntPtr hWnd)
    {
        if (!IsWindowActive(hWnd))
            return;

        if (!_lastScreen.TryGetValue(hWnd, out var last))
            return;

        NativeMethods.GetWindowRect(hWnd, out var rect);
        int ax = rect.Left, ay = rect.Top;
        int aw = rect.Right - rect.Left, ah = rect.Bottom - rect.Top;

        if (Math.Abs(ax - last.x) <= 2 && Math.Abs(ay - last.y) <= 2 &&
            Math.Abs(aw - last.w) <= 2 && Math.Abs(ah - last.h) <= 2)
            return;

        // Don't reconcile clipped windows — they're hidden and we don't
        // care where the app thinks they are
        if (_clippedWindows.Contains(hWnd))
            return;

        _canvas.SetWindowFromScreen(hWnd, ax, ay, aw, ah);
        _lastScreen[hWnd] = (ax, ay, aw, ah);
    }

    /// <summary>Remove windows from canvas that no longer exist.</summary>
    public void RemoveStale()
    {
        var stale = new List<IntPtr>();
        foreach (var hWnd in _canvas.Windows.Keys)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                stale.Add(hWnd);
        }
        foreach (var hWnd in stale)
        {
            _canvas.RemoveWindow(hWnd);
            _lastScreen.Remove(hWnd);
        }
    }

    /// <summary>Restore regions on all clipped windows (for overview thumbnails).</summary>
    public void UnclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            NativeMethods.SetWindowRgn(hWnd, IntPtr.Zero, true);
    }

    /// <summary>Re-clip windows that should be off-screen.</summary>
    public void ReclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            NativeMethods.SetWindowRgn(hWnd, NativeMethods.CreateRectRgn(0, 0, 0, 0), true);
    }

    /// <summary>Register a new window into the canvas from its screen position.</summary>
    public void RegisterWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!IsManageable(hWnd, ownPid))
            return;

        NativeMethods.GetWindowRect(hWnd, out var rect);
        int sx = rect.Left, sy = rect.Top;
        int sw = rect.Right - rect.Left, sh = rect.Bottom - rect.Top;

        _canvas.SetWindowFromScreen(hWnd, sx, sy, sw, sh);
        _lastScreen[hWnd] = (sx, sy, sw, sh);

        if (!AppConfig.DisableDllInjection)
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (!_injector.IsInjected(pid))
                _injector.Inject(pid);
        }
    }

    /// <summary>Reset: restore all windows to world positions, clear canvas.</summary>
    public void Reset()
    {
        foreach (var hWnd in _clippedWindows)
            NativeMethods.SetWindowRgn(hWnd, IntPtr.Zero, true);
        _clippedWindows.Clear();

        _canvas.ResetCamera();

        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (!IsWindowActive(hWnd))
                continue;

            int sx = (int)world.X, sy = (int)world.Y;
            int sw = (int)world.W, sh = (int)world.H;

            batch.Add((hWnd, sx, sy, sw, sh, false));
        }

        BatchSetPositions(batch);

        _canvas.ClearWindows();
        _lastScreen.Clear();
    }

    // ==================== PRIVATE ====================

    private void DiscoverNewWindows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var toAdd = new List<IntPtr>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (_canvas.HasWindow(hWnd)) return true;
            if (!IsManageable(hWnd, ownPid)) return true;
            if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return true;
            toAdd.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hWnd in toAdd)
            RegisterWindow(hWnd);
    }

    private bool IsWindowActive(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd))
            return false;
        int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
        return (style & (int)NativeMethods.WS_MINIMIZE) == 0;
    }

    /// <summary>
    /// Clamp window position so it sits just outside the nearest screen edge.
    /// This hides DWM border/shadow effects that would bleed onto the visible area.
    /// </summary>
    private static (int x, int y) ClampToScreenEdge(int sx, int sy, int sw, int sh)
    {
        // Find the nearest screen
        int bestDist = int.MaxValue;
        var nearest = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            int cx = sx + sw / 2;
            int cy = sy + sh / 2;
            int scx = wa.Left + wa.Width / 2;
            int scy = wa.Top + wa.Height / 2;
            int dist = Math.Abs(cx - scx) + Math.Abs(cy - scy);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = wa;
            }
        }

        // Park 1px inside the nearest edge so the OS considers it "on-screen"
        int px = sx, py = sy;

        if (sx + sw <= nearest.Left)   px = nearest.Left - sw + 1;
        else if (sx >= nearest.Right)  px = nearest.Right - 1;

        if (sy + sh <= nearest.Top)    py = nearest.Top - sh + 1;
        else if (sy >= nearest.Bottom) py = nearest.Bottom - 1;

        return (px, py);
    }

    /// <summary>Check if a rect overlaps with any monitor's working area (excludes taskbars).</summary>
    private static bool IsOnAnyScreen(int rx, int ry, int rw, int rh)
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            if (rx + rw > wa.Left && rx < wa.Right &&
                ry + rh > wa.Top  && ry < wa.Bottom)
                return true;
        }
        return false;
    }

    // ==================== WINDOW FILTERING & BATCH ====================

    private static readonly HashSet<string> ExcludedClasses = new()
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow"
    };

    /// <summary>Check whether a window should be managed by the canvas.</summary>
    public static bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false)
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

    /// <summary>
    /// Batch-apply positions/sizes. Falls back to individual SetWindowPos if DeferWindowPos fails.
    /// </summary>
    private static void BatchSetPositions(
        List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)> items)
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
}
