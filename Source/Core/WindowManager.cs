using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Consumes Canvas state and applies it to real windows.
/// Handles enumeration, positioning, DPI injection, reconciliation.
/// </summary>
internal sealed class WindowManager
{
    private const int ReconcileTolerancePx = 2;
    private const int ClipEdgeOffsetPx = 1;
    private const int FallbackScreenWidth = 1920;
    private const int FallbackScreenHeight = 1080;
    private const int ReprojectThrottleMs = 200;

    private readonly Canvas _canvas;
    private readonly IWindowApi _pos;
    private readonly DllInjector _injector;
    private readonly IAppConfig _config;
    private readonly IClock _clock;
    private readonly IVirtualDesktops? _vds;
    private readonly ProjectionWorker? _projection;

    // Track last projected screen positions to detect manual moves
    private readonly Dictionary<IntPtr, (int x, int y, int w, int h)> _lastScreen = new();

    // Windows with clipped (empty) region to prevent them from fighting off-screen
    private readonly HashSet<IntPtr> _clippedWindows = new();

    private long _lastReprojectTick;

    // Temporarily suspends greedy draw (SetWindowRgn clipping)
    public bool SuspendGreedyDraw { get; set; }

    public WindowManager(
        Canvas canvas,
        IWindowApi positioner,
        DllInjector injector,
        IAppConfig config,
        IInputRouter input,
        IClock? clock = null,
        IVirtualDesktops? vds = null,
        ProjectionWorker? projection = null)
    {
        _canvas = canvas;
        _pos = positioner;
        _injector = injector;
        _config = config;
        _clock = clock ?? SystemClock.Instance;
        _vds = vds;
        _projection = projection;

        canvas.Committed       += OnCommitted;
        canvas.CameraChanged   += OnCameraChanged;
        canvas.CollapseChanged += OnReprojectWindowEvent;
        canvas.MaximizeChanged += OnReprojectWindowEvent;

        input.WindowMinimized += OnWindowMinimizedEvent;
        input.WindowRestored  += OnWindowRestoredEvent;
        input.WindowDestroyed += OnWindowDestroyedEvent;
        input.WindowShown     += OnWindowShownEvent;
        input.WindowMoved     += OnWindowMovedEvent;
        input.AltTabStarted   += OnAltTabStarted;
        input.AltTabEnded     += OnAltTabEnded;
    }

    /// <summary>Background tick for window discovery + stale removal.</summary>
    public void Tick()
    {
        DiscoverNewWindows();
        RemoveStale();
    }

    private void OnCommitted()
    {
        Reproject();
    }

    private void OnCameraChanged()
    {
        // Overview renders its own camera + thumbnails, so real windows don't
        // need to track every frame — but clicks pass through the overlay
        // (WS_EX_TRANSPARENT) and hit whichever real window is under the
        // cursor, so we keep HWND positions roughly in sync for WindowFromPoint.
        // Throttled; final reproject on overview close comes via OnCommitted.
        long now = _clock.TickCount64;
        if (now - _lastReprojectTick > ReprojectThrottleMs)
        {
            Reproject(isTransient: true);
            _lastReprojectTick = now;
        }
    }

    private void OnReprojectWindowEvent(IntPtr hWnd)
    {
        ReprojectWindow(hWnd);
    }

    private void OnWindowMinimizedEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _canvas.CollapseWindow(hWnd);
    }

    private void OnWindowRestoredEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _canvas.ExpandWindow(hWnd);
        ReprojectWindow(hWnd);
    }

    private void OnWindowDestroyedEvent(IntPtr hWnd)
    {
        RemoveWindow(hWnd);
    }

    private void OnWindowShownEvent(IntPtr hWnd)
    {
        TryRegisterWindow(hWnd);
    }

    private void OnWindowMovedEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            ReconcileWindow(hWnd);
    }

    private void OnAltTabStarted()
    {
        SuspendGreedyDraw = true;
        UnclipAll();
    }

    private void OnAltTabEnded()
    {
        SuspendGreedyDraw = false;
        ReclipAll();
    }

    /// <summary>
    /// Project all canvas windows to screen. Call after Pan.
    /// </summary>
    public void Reproject(bool isAsync = false, bool isTransient = false)
    {
        var batch = new List<BatchMoveItem>();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (world.State != WindowState.Normal)
                continue;

            var r = _canvas.WorldToScreen(world);
            bool onScreen = IsOnAnyScreen(r.X, r.Y, r.W, r.H);

            bool wasClipped = _clippedWindows.Contains(hWnd);
            if (!_config.DisableGreedyDraw && !SuspendGreedyDraw && !onScreen)
            {
                if (!wasClipped)
                {
                    _pos.ClipWindow(hWnd);
                    _clippedWindows.Add(hWnd);
                    var (px, py) = ClampToScreenEdge(r.X, r.Y, r.W, r.H);
                    var clipped = new WindowRect(px, py, r.W, r.H);
                    batch.Add(new BatchMoveItem(hWnd, clipped, PosOnly: false));
                    _lastScreen[hWnd] = (px, py, r.W, r.H);
                }
                continue;
            }

            if (wasClipped)
            {
                _pos.UnclipWindow(hWnd);
                _clippedWindows.Remove(hWnd);
            }

            batch.Add(new BatchMoveItem(hWnd, r, PosOnly: true));
            _lastScreen[hWnd] = (r.X, r.Y, r.W, r.H);
        }

        if (_projection != null)
            _projection.Schedule(batch, isAsync: isAsync, isTransient: isTransient);
        else
            _pos.BatchMove(batch, isAsync: isAsync, isTransient: isTransient);
    }

    /// <summary>
    /// Project a single window (e.g., after restore from minimized).
    /// Returns true if the window was reprojected, false if skipped.
    /// </summary>
    public bool ReprojectWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_pos.IsManageable(hWnd, ownPid))
            return false;

        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return false;

        var r = _canvas.WorldToScreen(world);

        _pos.SetWindowPosition(hWnd, r.X, r.Y, r.W, r.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));

        _lastScreen[hWnd] = (r.X, r.Y, r.W, r.H);
        return true;
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

        int style = _pos.GetWindowStyle(hWnd);
        bool isMaximized = (style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0;

        // Keep canvas's maximize state in sync with Win32
        if (_canvas.HasWindow(hWnd))
        {
            if (isMaximized && !_canvas.IsMaximized(hWnd))
                _canvas.MaximizeWindow(hWnd);
            else if (!isMaximized && _canvas.IsMaximized(hWnd))
                _canvas.UnmaximizeWindow(hWnd);
        }

        // Skip reprojecting maximized windows — their full-screen rect isn't a meaningful canvas position
        if (isMaximized)
            return;

        if (!_lastScreen.TryGetValue(hWnd, out var last))
            return;

        var (ax, ay, aw, ah) = _pos.GetWindowRect(hWnd);

        if (Math.Abs(ax - last.x) <= ReconcileTolerancePx && Math.Abs(ay - last.y) <= ReconcileTolerancePx &&
            Math.Abs(aw - last.w) <= ReconcileTolerancePx && Math.Abs(ah - last.h) <= ReconcileTolerancePx)
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
            if (!_pos.IsWindowVisible(hWnd))
                stale.Add(hWnd);
        }
        foreach (var hWnd in stale)
            RemoveWindow(hWnd);
    }

    /// <summary>Drop a single window from canvas and internal tracking.</summary>
    public void RemoveWindow(IntPtr hWnd)
    {
        _canvas.RemoveWindow(hWnd);
        _lastScreen.Remove(hWnd);
        _clippedWindows.Remove(hWnd);
    }

    /// <summary>Restore regions on all clipped windows (for overview thumbnails).</summary>
    public void UnclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            _pos.UnclipWindow(hWnd);
    }

    /// <summary>Re-clip windows that should be off-screen.</summary>
    public void ReclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            _pos.ClipWindow(hWnd);
    }

    /// <summary>Register a new window into the canvas from its screen position.</summary>
    public void RegisterWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_pos.IsManageable(hWnd, ownPid))
            return;

        var (sx, sy, sw, sh) = _pos.GetWindowRect(hWnd);

        _canvas.SetWindowFromScreen(hWnd, sx, sy, sw, sh);
        _lastScreen[hWnd] = (sx, sy, sw, sh);

        if (!_config.DisableDllInjection)
        {
            uint pid = _pos.GetWindowProcessId(hWnd);
            if (!_injector.IsInjected(pid))
                _injector.Inject(pid);
        }
    }

    /// <summary>
    /// Register a single HWND if it passes the full "new window" filter chain
    /// (not already tracked, manageable, on current virtual desktop).
    /// Event-driven counterpart to DiscoverNewWindows.
    /// </summary>
    public void TryRegisterWindow(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd)) return;
        if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return;
        RegisterWindow(hWnd);
    }

    /// <summary>Reset: restore all windows to world positions, clear canvas.</summary>
    public void Reset()
    {
        // Drop any in-flight worker batch so it can't stomp on the sync reset below.
        _projection?.ClearPending();

        foreach (var hWnd in _clippedWindows)
            _pos.UnclipWindow(hWnd);
        _clippedWindows.Clear();

        _canvas.ResetCamera();

        var batch = new List<BatchMoveItem>();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (!IsWindowActive(hWnd))
                continue;

            var rect = new WindowRect((int)world.X, (int)world.Y, (int)world.W, (int)world.H);
            batch.Add(new BatchMoveItem(hWnd, rect, PosOnly: false));
        }

        _pos.BatchMove(batch, isAsync: false, isTransient: false);
        _canvas.ClearWindows();
        _lastScreen.Clear();
    }

    // ==================== PRIVATE ====================

    public void DiscoverNewWindows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var toAdd = new List<IntPtr>();

        _pos.EnumWindows(hWnd =>
        {
            if (_canvas.HasWindow(hWnd)) return true;
            if (!_pos.IsManageable(hWnd, ownPid)) return true;
            if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return true;
            toAdd.Add(hWnd);
            return true;
        });

        foreach (var hWnd in toAdd)
            RegisterWindow(hWnd);
    }

    private bool IsWindowActive(IntPtr hWnd)
    {
        if (!_pos.IsWindowVisible(hWnd))
            return false;
        int style = _pos.GetWindowStyle(hWnd);
        return (style & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;
    }

    /// <summary>
    /// Clamp window position so it sits just outside the nearest screen edge.
    /// This hides DWM border/shadow effects that would bleed onto the visible area.
    /// </summary>
    private (int x, int y) ClampToScreenEdge(int sx, int sy, int sw, int sh)
    {
        var screens = _pos.GetScreenWorkingAreas();

        // Find the nearest screen
        int bestDist = int.MaxValue;
        var nearest = screens.Count > 0 ? screens[0] : (0, 0, FallbackScreenWidth, FallbackScreenHeight);

        foreach (var (left, top, width, height) in screens)
        {
            int cx = sx + sw / 2;
            int cy = sy + sh / 2;
            int scx = left + width / 2;
            int scy = top + height / 2;
            int dist = Math.Abs(cx - scx) + Math.Abs(cy - scy);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = (left, top, width, height);
            }
        }

        int nLeft = nearest.Item1;
        int nTop = nearest.Item2;
        int nRight = nLeft + nearest.Item3;
        int nBottom = nTop + nearest.Item4;

        // Park 1px inside the nearest edge so the OS considers it "on-screen"
        int px = sx, py = sy;

        if (sx + sw <= nLeft)
        {
            px = nLeft - sw + ClipEdgeOffsetPx;
        }
        else if (sx >= nRight)
        {
            px = nRight - ClipEdgeOffsetPx;
        }

        if (sy + sh <= nTop)
        {
            py = nTop - sh + ClipEdgeOffsetPx;
        }
        else if (sy >= nBottom)
        {
            py = nBottom - ClipEdgeOffsetPx;
        }

        return (px, py);
    }

    /// <summary>Check if a rect overlaps with any monitor's working area (excludes taskbars).</summary>
    private bool IsOnAnyScreen(int rx, int ry, int rw, int rh)
    {
        foreach (var (left, top, width, height) in _pos.GetScreenWorkingAreas())
        {
            int right = left + width;
            int bottom = top + height;
            if (rx + rw > left && rx < right &&
                ry + rh > top  && ry < bottom)
                return true;
        }
        return false;
    }
}
