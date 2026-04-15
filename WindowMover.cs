using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CanvasDesktop;

internal static class WindowMover
{
    private static readonly HashSet<string> ExcludedClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow"
    };

    // --- Pan state ---
    private static List<(IntPtr hWnd, int origX, int origY)>? _panSnapshot;
    private static int _cumDx, _cumDy;

    // --- Zoom state ---
    private static List<(IntPtr hWnd, uint pid, int origX, int origY, int origW, int origH)>? _zoomSnapshot;
    private static double _zoomScale = 1.0;
    private static double _zoomOffsetX, _zoomOffsetY;
    private const double ZoomMin = 0.3;
    private const double ZoomMax = 3.0;
    private const double ZoomStep = 0.08;

    // --- DPI injection ---
    private static DllInjector? _injector;
    private static ZoomSharedMemory? _sharedMem;
    private static uint _baseDpi = 96;

    public static double ZoomLevel => _zoomScale;
    public static bool IsZoomActive => _zoomSnapshot != null;

    /// <summary>Set the injector and shared memory from TrayApp on startup.</summary>
    public static void SetDpiHookResources(DllInjector injector, ZoomSharedMemory sharedMem)
    {
        _injector = injector;
        _sharedMem = sharedMem;
    }

    // ===================== PAN =====================

    public static void BeginMove()
    {
        _panSnapshot = new List<(IntPtr, int, int)>();
        _cumDx = 0;
        _cumDy = 0;

        uint ownPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!ShouldMove(hWnd, ownPid))
                return true;

            NativeMethods.GetWindowRect(hWnd, out var rect);
            _panSnapshot.Add((hWnd, rect.Left, rect.Top));
            return true;
        }, IntPtr.Zero);
    }

    public static void ApplyDelta(int dx, int dy)
    {
        if (_panSnapshot == null || (dx == 0 && dy == 0))
            return;

        _cumDx += dx;
        _cumDy += dy;

        ApplyBatch(_panSnapshot, (hWnd, origX, origY) =>
            (origX + _cumDx, origY + _cumDy, 0, 0, true));
    }

    public static void EndMove()
    {
        // Update zoom snapshot so it reflects where windows are after panning
        if (_panSnapshot != null && _zoomSnapshot != null && (_cumDx != 0 || _cumDy != 0))
        {
            // Translate the pan delta back into "original" coordinate space
            double invScale = _zoomScale != 0 ? 1.0 / _zoomScale : 1.0;
            int origDx = (int)(_cumDx * invScale);
            int origDy = (int)(_cumDy * invScale);

            for (int i = 0; i < _zoomSnapshot.Count; i++)
            {
                var (hWnd, pid, ox, oy, ow, oh) = _zoomSnapshot[i];
                _zoomSnapshot[i] = (hWnd, pid, ox + origDx, oy + origDy, ow, oh);
            }

            // Also shift the zoom offset so current screen positions stay correct
            _zoomOffsetX += _cumDx - origDx * _zoomScale;
            _zoomOffsetY += _cumDy - origDy * _zoomScale;
        }

        _panSnapshot = null;
    }

    /// <summary>Standalone move for inertia (no snapshot context).</summary>
    public static void MoveAll(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;

        var windows = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();
        uint ownPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!ShouldMove(hWnd, ownPid))
                return true;

            NativeMethods.GetWindowRect(hWnd, out var rect);
            windows.Add((hWnd, rect.Left + dx, rect.Top + dy, 0, 0, true));
            return true;
        }, IntPtr.Zero);

        ApplyBatchRaw(windows);

        // Keep zoom snapshot in sync with inertia movement
        if (_zoomSnapshot != null)
        {
            double invScale = _zoomScale != 0 ? 1.0 / _zoomScale : 1.0;
            int origDx = (int)(dx * invScale);
            int origDy = (int)(dy * invScale);

            for (int i = 0; i < _zoomSnapshot.Count; i++)
            {
                var (hWnd, pid, ox, oy, ow, oh) = _zoomSnapshot[i];
                _zoomSnapshot[i] = (hWnd, pid, ox + origDx, oy + origDy, ow, oh);
            }

            _zoomOffsetX += dx - origDx * _zoomScale;
            _zoomOffsetY += dy - origDy * _zoomScale;
        }
    }

    // ===================== ZOOM =====================

    public static void ApplyZoom(int scrollDelta, int centerX, int centerY)
    {
        // Snapshot on first zoom
        if (_zoomSnapshot == null)
        {
            _zoomSnapshot = new List<(IntPtr, uint, int, int, int, int)>();
            _zoomScale = 1.0;
            _zoomOffsetX = 0;
            _zoomOffsetY = 0;
            uint ownPid = (uint)Environment.ProcessId;

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!ShouldMove(hWnd, ownPid))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                NativeMethods.GetWindowRect(hWnd, out var rect);
                _zoomSnapshot.Add((hWnd, pid, rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top));
                return true;
            }, IntPtr.Zero);

            // Get base DPI from the first window (or system default)
            if (_zoomSnapshot.Count > 0)
            {
                uint dpi = NativeMethods.GetDpiForWindow(_zoomSnapshot[0].hWnd);
                if (dpi > 0) _baseDpi = dpi;
            }
        }

        // Compute new scale
        double notches = scrollDelta / 120.0;
        double newScale = Math.Clamp(_zoomScale + notches * ZoomStep, ZoomMin, ZoomMax);

        if (Math.Abs(newScale - _zoomScale) < 0.001)
            return;

        // 1. Reconcile snapshot with actual positions.
        //    If the user manually moved/resized a window, update its origin
        //    so the zoom pivots from where the window actually is now.
        for (int i = 0; i < _zoomSnapshot.Count; i++)
        {
            var (hWnd, pid, origX, origY, origW, origH) = _zoomSnapshot[i];

            if (!NativeMethods.IsWindowVisible(hWnd))
                continue;

            int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
            if ((style & (int)NativeMethods.WS_MINIMIZE) != 0)
                continue;

            NativeMethods.GetWindowRect(hWnd, out var rect);

            int expectedX = (int)(_zoomOffsetX + origX * _zoomScale);
            int expectedY = (int)(_zoomOffsetY + origY * _zoomScale);

            if (Math.Abs(rect.Left - expectedX) > 2 || Math.Abs(rect.Top - expectedY) > 2)
            {
                // Window was moved — recompute origin from its actual position
                origX = (int)((rect.Left - _zoomOffsetX) / _zoomScale);
                origY = (int)((rect.Top - _zoomOffsetY) / _zoomScale);
                origW = (int)((rect.Right - rect.Left) / _zoomScale);
                origH = (int)((rect.Bottom - rect.Top) / _zoomScale);
                _zoomSnapshot[i] = (hWnd, pid, origX, origY, origW, origH);
            }
        }

        // 2. Pick up any new windows
        var knownHandles = new HashSet<IntPtr>();
        foreach (var (hWnd, _, _, _, _, _) in _zoomSnapshot)
            knownHandles.Add(hWnd);

        uint ownPid2 = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (knownHandles.Contains(hWnd) || !ShouldMove(hWnd, ownPid2))
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            NativeMethods.GetWindowRect(hWnd, out var rect);

            int origX = (int)((rect.Left - _zoomOffsetX) / _zoomScale);
            int origY = (int)((rect.Top - _zoomOffsetY) / _zoomScale);
            int origW = (int)((rect.Right - rect.Left) / _zoomScale);
            int origH = (int)((rect.Bottom - rect.Top) / _zoomScale);

            _zoomSnapshot.Add((hWnd, pid, origX, origY, origW, origH));
            return true;
        }, IntPtr.Zero);

        // 3. Update transform
        double ratio = newScale / _zoomScale;
        _zoomOffsetX = centerX * (1.0 - ratio) + _zoomOffsetX * ratio;
        _zoomOffsetY = centerY * (1.0 - ratio) + _zoomOffsetY * ratio;
        _zoomScale = newScale;

        _sharedMem?.Write(_zoomScale);

        // 4. Apply new positions to all windows
        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();
        var windowsToNotify = new List<IntPtr>();

        foreach (var (hWnd, pid, origX, origY, origW, origH) in _zoomSnapshot)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                continue;

            int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
            if ((style & (int)NativeMethods.WS_MINIMIZE) != 0)
                continue;

            int newX = (int)(_zoomOffsetX + origX * _zoomScale);
            int newY = (int)(_zoomOffsetY + origY * _zoomScale);
            int newW = Math.Max(200, (int)Math.Ceiling(origW * _zoomScale));
            int newH = Math.Max(100, (int)Math.Ceiling(origH * _zoomScale));

            batch.Add((hWnd, newX, newY, newW, newH, false));

            if (_injector != null && _injector.DllExists)
                _injector.Inject(pid);

            windowsToNotify.Add(hWnd);
        }

        ApplyBatchRaw(batch);

        uint virtualDpi = (uint)(_baseDpi * _zoomScale + 0.5);
        SendDpiChanged(windowsToNotify, virtualDpi);
    }

    public static void ResetZoom()
    {
        if (_zoomSnapshot == null)
            return;

        _zoomScale = 1.0;
        _zoomOffsetX = 0;
        _zoomOffsetY = 0;

        // Reset shared memory
        _sharedMem?.Write(1.0);

        // Restore original positions
        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();
        var windowsToNotify = new List<IntPtr>();

        foreach (var (hWnd, pid, origX, origY, origW, origH) in _zoomSnapshot)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                continue;
            batch.Add((hWnd, origX, origY, origW, origH, false));
            windowsToNotify.Add(hWnd);
        }

        ApplyBatchRaw(batch);

        // Send original DPI to reset rendering
        SendDpiChanged(windowsToNotify, _baseDpi);

        // Eject hook DLLs
        _injector?.EjectAll();

        _zoomSnapshot = null;
    }

    /// <summary>
    /// Apply zoom to a specific window that was just restored from minimized.
    /// If it's already in the snapshot, re-apply the transform.
    /// If not, add it as a new window.
    /// </summary>
    public static void ZoomWindow(IntPtr hWnd)
    {
        if (_zoomSnapshot == null)
            return;

        uint ownPid = (uint)Environment.ProcessId;
        if (!ShouldMove(hWnd, ownPid))
            return;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        NativeMethods.GetWindowRect(hWnd, out var rect);

        // Check if already in snapshot
        int idx = -1;
        for (int i = 0; i < _zoomSnapshot.Count; i++)
        {
            if (_zoomSnapshot[i].hWnd == hWnd) { idx = i; break; }
        }

        int origX, origY, origW, origH;
        if (idx >= 0)
        {
            // Already tracked — use stored originals
            (_, _, origX, origY, origW, origH) = _zoomSnapshot[idx];
        }
        else
        {
            // New window — back-compute originals from current position
            origX = (int)((rect.Left - _zoomOffsetX) / _zoomScale);
            origY = (int)((rect.Top - _zoomOffsetY) / _zoomScale);
            origW = rect.Right - rect.Left;
            origH = rect.Bottom - rect.Top;
            _zoomSnapshot.Add((hWnd, pid, origX, origY, origW, origH));
        }

        int newX = (int)(_zoomOffsetX + origX * _zoomScale);
        int newY = (int)(_zoomOffsetY + origY * _zoomScale);
        int newW = Math.Max(200, (int)Math.Ceiling(origW * _zoomScale));
        int newH = Math.Max(100, (int)Math.Ceiling(origH * _zoomScale));

        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, newX, newY, newW, newH,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (_injector != null && _injector.DllExists)
            _injector.Inject(pid);

        uint virtualDpi = (uint)(_baseDpi * _zoomScale + 0.5);
        SendDpiChanged(new List<IntPtr> { hWnd }, virtualDpi);
    }

    /// <summary>
    /// Scan for new windows not in the snapshot and apply zoom to them.
    /// Does NOT touch existing windows (respects user manual moves).
    /// </summary>
    public static void ScanNewWindows()
    {
        if (_zoomSnapshot == null)
            return;

        var knownHandles = new HashSet<IntPtr>();
        foreach (var (hWnd, _, _, _, _, _) in _zoomSnapshot)
            knownHandles.Add(hWnd);

        var newWindows = new List<IntPtr>();
        uint ownPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!knownHandles.Contains(hWnd) && ShouldMove(hWnd, ownPid))
                newWindows.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hWnd in newWindows)
            ZoomWindow(hWnd);
    }

    private static void SendDpiChanged(List<IntPtr> windows, uint dpi)
    {
        // WM_DPICHANGED: wParam = MAKELONG(dpiX, dpiY), lParam = RECT* (suggested rect)
        IntPtr wParam = (IntPtr)((dpi & 0xFFFF) | (dpi << 16));

        foreach (var hWnd in windows)
        {
            // Get current rect for the suggested rect parameter
            NativeMethods.GetWindowRect(hWnd, out var rect);

            // Allocate RECT in our process — SendMessage marshals cross-process
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

    // ===================== BATCH HELPERS =====================

    private delegate (int x, int y, int w, int h, bool posOnly) ComputeTarget(
        IntPtr hWnd, int origX, int origY);

    private static void ApplyBatch(
        List<(IntPtr hWnd, int origX, int origY)> snapshot,
        ComputeTarget compute)
    {
        IntPtr hdwp = NativeMethods.BeginDeferWindowPos(snapshot.Count);
        bool useBatch = hdwp != IntPtr.Zero;

        foreach (var (hWnd, origX, origY) in snapshot)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                continue;

            var (x, y, w, h, posOnly) = compute(hWnd, origX, origY);
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

    private static void ApplyBatchRaw(
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

    // ===================== FILTER =====================

    private static bool ShouldMove(IntPtr hWnd, uint ownPid)
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
}
