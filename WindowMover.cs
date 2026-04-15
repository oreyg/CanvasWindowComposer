using System;
using System.Collections.Generic;
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
    // We track a 2D affine transform: screenPos = offset + origPos * scale
    // This lets zoom-to-cursor work correctly: the point under the cursor
    // stays fixed on screen as scale changes.
    private static List<(IntPtr hWnd, int origX, int origY, int origW, int origH)>? _zoomSnapshot;
    private static double _zoomScale = 1.0;
    private static double _zoomOffsetX, _zoomOffsetY;
    private const double ZoomMin = 0.3;
    private const double ZoomMax = 3.0;
    private const double ZoomStep = 0.08; // per 120 units of WHEEL_DELTA

    public static double ZoomLevel => _zoomScale;

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
        _panSnapshot = null;
    }

    /// <summary>
    /// Standalone move for inertia (no snapshot context).
    /// </summary>
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
    }

    // ===================== ZOOM =====================

    /// <summary>
    /// Apply zoom. scrollDelta is in WHEEL_DELTA units (120 = one notch).
    /// Positive = zoom in, negative = zoom out.
    /// The point under the cursor stays fixed on screen (true zoom-to-cursor).
    ///
    /// Transform: screenPos = offset + origPos * scale
    /// When zooming by ratio r around cursor (cx,cy):
    ///   newOffset = cx * (1 - r) + oldOffset * r
    /// This keeps the cursor point invariant.
    /// </summary>
    public static void ApplyZoom(int scrollDelta, int centerX, int centerY)
    {
        // Snapshot on first zoom
        if (_zoomSnapshot == null)
        {
            _zoomSnapshot = new List<(IntPtr, int, int, int, int)>();
            _zoomScale = 1.0;
            _zoomOffsetX = 0;
            _zoomOffsetY = 0;
            uint ownPid = (uint)Environment.ProcessId;

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!ShouldMove(hWnd, ownPid))
                    return true;

                NativeMethods.GetWindowRect(hWnd, out var rect);
                _zoomSnapshot.Add((hWnd, rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top));
                return true;
            }, IntPtr.Zero);
        }

        // Compute new scale
        double notches = scrollDelta / 120.0;
        double newScale = Math.Clamp(_zoomScale + notches * ZoomStep, ZoomMin, ZoomMax);

        if (Math.Abs(newScale - _zoomScale) < 0.001)
            return;

        // Update offset so the point under the cursor stays fixed
        // ratio = newScale / oldScale
        double ratio = newScale / _zoomScale;
        _zoomOffsetX = centerX * (1.0 - ratio) + _zoomOffsetX * ratio;
        _zoomOffsetY = centerY * (1.0 - ratio) + _zoomOffsetY * ratio;
        _zoomScale = newScale;

        // Apply: screenPos = offset + origPos * scale
        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();
        foreach (var (hWnd, origX, origY, origW, origH) in _zoomSnapshot)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                continue;

            int newX = (int)(_zoomOffsetX + origX * _zoomScale);
            int newY = (int)(_zoomOffsetY + origY * _zoomScale);
            int newW = Math.Max(200, (int)(origW * _zoomScale));
            int newH = Math.Max(100, (int)(origH * _zoomScale));

            batch.Add((hWnd, newX, newY, newW, newH, false));
        }

        ApplyBatchRaw(batch);
    }

    /// <summary>Reset zoom back to 1.0 and restore original window sizes/positions.</summary>
    public static void ResetZoom()
    {
        if (_zoomSnapshot == null)
            return;

        _zoomScale = 1.0;
        _zoomOffsetX = 0;
        _zoomOffsetY = 0;

        var batch = new List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)>();
        foreach (var (hWnd, origX, origY, origW, origH) in _zoomSnapshot)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                continue;
            batch.Add((hWnd, origX, origY, origW, origH, false));
        }

        ApplyBatchRaw(batch);
        _zoomSnapshot = null;
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
