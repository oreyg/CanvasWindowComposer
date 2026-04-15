using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    private readonly ZoomSharedMemory _sharedMem;
    private uint _baseDpi = 96;

    // Track last projected screen positions to detect manual moves
    private readonly Dictionary<IntPtr, (int x, int y, int w, int h)> _lastScreen = new();

    public WindowManager(Canvas canvas, DllInjector injector, ZoomSharedMemory sharedMem)
    {
        _canvas = canvas;
        _injector = injector;
        _sharedMem = sharedMem;
    }

    /// <summary>
    /// Project all canvas windows to screen. Call after Pan/ZoomAt.
    /// </summary>
    public void Reproject(bool updateDpi = false)
    {
        DiscoverNewWindows();

        bool isZoomed = Math.Abs(_canvas.Zoom - 1.0) > 0.001;

        if (updateDpi && isZoomed)
            _sharedMem.Write(_canvas.Zoom);

        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();
        var dpiWindows = (updateDpi && isZoomed) ? new List<IntPtr>() : null;

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (!IsWindowActive(hWnd))
                continue;

            var (sx, sy) = _canvas.WorldToScreen(world.X, world.Y);
            var (sw, sh) = _canvas.WorldToScreenSize(world.W, world.H);

            batch.Add((hWnd, sx, sy, sw, sh, false));
            _lastScreen[hWnd] = (sx, sy, sw, sh);

            if (dpiWindows != null)
            {
                InjectDpiHook(hWnd);
                if (IsDpiAdaptive(hWnd))
                    dpiWindows.Add(hWnd);
            }
        }

        // Send DPI changed before positioning so windows re-render
        // at the correct size before being moved
        if (dpiWindows is { Count: > 0 })
        {
            uint virtualDpi = (uint)(_baseDpi * _canvas.Zoom + 0.5);
            SendDpiChanged(dpiWindows, virtualDpi);
        }

        BatchSetPositions(batch);
    }

    /// <summary>
    /// Re-send WM_DPICHANGED to all DPI-adaptive windows at current zoom.
    /// Call once at the start of panning while zoomed, so windows re-render
    /// for their current size before being moved.
    /// </summary>
    public void RefreshDpi()
    {
        if (Math.Abs(_canvas.Zoom - 1.0) <= 0.001) return;

        var dpiWindows = new List<IntPtr>();
        foreach (var (hWnd, _) in _canvas.Windows)
        {
            if (IsWindowActive(hWnd) && IsDpiAdaptive(hWnd))
                dpiWindows.Add(hWnd);
        }

        if (dpiWindows.Count > 0)
        {
            uint virtualDpi = (uint)(_baseDpi * _canvas.Zoom + 0.5);
            SendDpiChanged(dpiWindows, virtualDpi);
        }
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

        bool isZoomed = Math.Abs(_canvas.Zoom - 1.0) > 0.001;
        if (isZoomed)
        {
            InjectDpiHook(hWnd);
            if (IsDpiAdaptive(hWnd))
            {
                uint virtualDpi = (uint)(_baseDpi * _canvas.Zoom + 0.5);
                SendDpiChanged(new List<IntPtr> { hWnd }, virtualDpi);
                NativeMethods.RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_ALLCHILDREN);
            }
        }
    }

    /// <summary>
    /// Detect windows the user manually moved/resized and update the canvas.
    /// </summary>
    public void Reconcile()
    {
        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (!IsWindowActive(hWnd))
                continue;

            if (!_lastScreen.TryGetValue(hWnd, out var last))
                continue;

            NativeMethods.GetWindowRect(hWnd, out var rect);
            int ax = rect.Left, ay = rect.Top;
            int aw = rect.Right - rect.Left, ah = rect.Bottom - rect.Top;

            if (Math.Abs(ax - last.x) > 2 || Math.Abs(ay - last.y) > 2 ||
                Math.Abs(aw - last.w) > 2 || Math.Abs(ah - last.h) > 2)
            {
                _canvas.SetWindowFromScreen(hWnd, ax, ay, aw, ah);
                _lastScreen[hWnd] = (ax, ay, aw, ah);
            }
        }
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

        if (_baseDpi == 96)
        {
            uint dpi = NativeMethods.GetDpiForWindow(hWnd);
            if (dpi > 0) _baseDpi = dpi;
        }
    }

    /// <summary>Reset: restore all windows to world positions, eject hooks, clear canvas.</summary>
    public void Reset()
    {
        _canvas.ResetCamera();
        _sharedMem.Write(1.0);

        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();
        var dpiWindows = new List<IntPtr>();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (!IsWindowActive(hWnd))
                continue;

            int sx = (int)world.X, sy = (int)world.Y;
            int sw = (int)world.W, sh = (int)world.H;

            batch.Add((hWnd, sx, sy, sw, sh, false));
            dpiWindows.Add(hWnd);
        }

        BatchSetPositions(batch);

        // Eject hooks FIRST so DPI queries return real values,
        // THEN send WM_DPICHANGED to trigger re-render at real DPI.
        _injector.EjectAll();
        ResetDpi(dpiWindows);
        ForceRepaint(dpiWindows);

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
            if (!_canvas.HasWindow(hWnd) && IsManageable(hWnd, ownPid))
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

    private static void ForceRepaint(List<IntPtr> windows)
    {
        const uint flags = NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_ALLCHILDREN;
        foreach (var hWnd in windows)
            NativeMethods.RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, flags);
    }

    // System processes whose DPI should never be hooked.
    // Injection is process-wide, so hooking explorer.exe would break
    // Alt-Tab, taskbar, Start menu, etc.
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm",
        "ShellExperienceHost",
        "SearchHost",
        "SearchUI",
        "StartMenuExperienceHost",
        "ApplicationFrameHost",
        "SystemSettings",
        "TextInputHost",
        "LockApp",
        "LogiOverlay",
        "ctfmon",
    };

    private readonly HashSet<uint> _blockedPids = new();

    private void InjectDpiHook(IntPtr hWnd)
    {
        if (!_injector.DllExists) return;
        if (!IsDpiAdaptive(hWnd)) return;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);

        // Check cached blocklist first
        if (_blockedPids.Contains(pid)) return;
        if (_injector.IsInjected(pid)) return;

        // Check process name against blocklist
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            string name = proc.ProcessName;

            if (BlockedProcesses.Contains(name) || IsSystemProcess(proc))
            {
                _blockedPids.Add(pid);
                return;
            }
        }
        catch
        {
            // Process may have exited
            return;
        }

        _injector.Inject(pid);
    }

    /// <summary>
    /// Check if a process is a system process (running from Windows directory).
    /// </summary>
    private static bool IsSystemProcess(System.Diagnostics.Process proc)
    {
        try
        {
            string? path = proc.MainModule?.FileName;
            if (path == null) return true;

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Can't access MainModule (elevated/protected process) — skip it
            return true;
        }
    }

    private static bool IsDpiAdaptive(IntPtr hWnd)
    {
        IntPtr ctx = NativeMethods.GetWindowDpiAwarenessContext(hWnd);
        if (ctx == IntPtr.Zero) return false;
        int awareness = NativeMethods.GetAwarenessFromDpiAwarenessContext(ctx);
        return awareness >= NativeMethods.DPI_AWARENESS_SYSTEM_AWARE;
    }

    private void ResetDpi(List<IntPtr> windows)
    {
        SendDpiChanged(windows, _baseDpi);
    }

    private static void SendDpiChanged(List<IntPtr> windows, uint dpi)
    {
        IntPtr wParam = (IntPtr)((dpi & 0xFFFF) | (dpi << 16));

        foreach (var hWnd in windows)
        {
            NativeMethods.GetWindowRect(hWnd, out var rect);
            IntPtr lParam = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.RECT>());
            try
            {
                Marshal.StructureToPtr(rect, lParam, false);
                NativeMethods.SendMessage(hWnd, NativeMethods.WM_DPICHANGED, wParam, lParam);
            }
            finally
            {
                Marshal.FreeHGlobal(lParam);
            }
        }
    }

    // ==================== WINDOW FILTERING & BATCH ====================

    private static readonly HashSet<string> ExcludedClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow"
    };

    /// <summary>Check whether a window should be managed by the canvas.</summary>
    public static bool IsManageable(IntPtr hWnd, uint ownPid)
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
        if ((style & (int)NativeMethods.WS_MINIMIZE) != 0)
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
