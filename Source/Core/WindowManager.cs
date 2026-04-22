using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Consumes Canvas state and applies it to real windows.
/// Handles enumeration, positioning, reconciliation.
/// </summary>
internal sealed class WindowManager : IDisposable
{
    private const int ReconcileTolerancePx = 2;
    private const int ClipEdgeOffsetPx = 1;
    private const int FallbackScreenWidth = 1920;
    private const int FallbackScreenHeight = 1080;
    private const int ReprojectThrottleMs = 200;

    private readonly Canvas _canvas;
    private readonly IWindowApi _win32;
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

    /// <summary>
    /// While true, <see cref="OnCameraChanged"/> short-circuits — no transient
    /// reproject is scheduled on the worker. Set during overview Zooming, when
    /// click-through is off and real-window positions don't need to track the
    /// camera for hit-testing. Eliminates the worker-job wait at overview close
    /// (we'd otherwise queue a batch from the close-time SetCamera and then
    /// immediately wait for it inside ReprojectSync). <see cref="OnCommitted"/>
    /// bypasses this gate so the explicit close commit still runs.
    /// </summary>
    public bool SuspendProjection { get; set; }

    private bool _suspendReconcile;
    private bool _reconcilePending;

    /// <summary>
    /// While true, <see cref="Reconcile"/> and <see cref="ReconcileWindow"/>
    /// short-circuit and only flag that a reconcile was requested. Set by the
    /// overview lifecycle so our own reprojections don't feed back through
    /// WindowMoved -> Reconcile and overwrite canvas world coords with
    /// rounding-drifted screen coords. When flipped back to false, if any
    /// reconcile call was suppressed during the on period, a full
    /// <see cref="Reconcile"/> runs to catch up on any drift.
    /// </summary>
    public bool SuspendReconcile
    {
        get { return _suspendReconcile; }
        set
        {
            if (_suspendReconcile == value) return;
            _suspendReconcile = value;
            if (!value && _reconcilePending)
            {
                _reconcilePending = false;
                Reconcile();
            }
        }
    }

    public WindowManager(
        Canvas canvas,
        IWindowApi win32,
        IAppConfig config,
        IInputRouter input,
        IClock? clock = null,
        IVirtualDesktops? vds = null,
        bool useAsyncProjection = false)
    {
        _canvas = canvas;
        _win32 = win32;
        _config = config;
        _clock = clock ?? SystemClock.Instance;
        _vds = vds;
        _projection = useAsyncProjection ? new ProjectionWorker(win32) : null;

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

    public void Dispose()
    {
        _projection?.Dispose();
    }

    private void OnCommitted()
    {
        // Sync — pan-end / overview-close commits need windows at their final
        // positions before the next paint, otherwise users see a one-frame
        // jitter as the worker batch lands after the form repaints. Cost is a
        // UI-thread block on cross-process SetWindowPos SendMessage round-trips,
        // tracked in Tracing/pan-hitch-findings.md as a known cost we accept
        // here over the visual artifact.
        ReprojectSync();
    }

    private void OnCameraChanged()
    {
        if (SuspendProjection) return;

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
        var batch = BuildReprojectBatch();

        if (_projection != null)
            _projection.Schedule(batch, isAsync: isAsync, isTransient: isTransient);
        else
            _win32.BatchMove(batch, isAsync: isAsync, isTransient: isTransient);
    }

    /// <summary>
    /// Like <see cref="Reproject"/>, but bypasses the <see cref="ProjectionWorker"/>
    /// and applies the batch synchronously on the calling thread. Use this when
    /// the caller depends on the windows being at their final positions before
    /// the next visible frame (e.g. just before the overview overlay hides).
    /// Cancels any in-flight worker batch so the sync run doesn't have to wait
    /// for it.
    /// </summary>
    public void ReprojectSync(bool isAsync = false, bool isTransient = false)
    {
        _projection?.ClearPending();
        var batch = BuildReprojectBatch();
        _win32.BatchMove(batch, isAsync: false, isTransient: false);
    }

    private List<BatchMoveItem> BuildReprojectBatch()
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
                    _win32.ClipWindow(hWnd);
                    _clippedWindows.Add(hWnd);
                    var (px, py) = ClampToScreenEdge(r.X, r.Y, r.W, r.H);
                    var clipped = new WindowRect(px, py, r.W, r.H);
                    batch.Add(new BatchMoveItem(hWnd, clipped, PosOnly: true));
                    _lastScreen[hWnd] = (px, py, r.W, r.H);
                }
                continue;
            }

            if (wasClipped)
            {
                _win32.UnclipWindow(hWnd);
                _clippedWindows.Remove(hWnd);
            }

            batch.Add(new BatchMoveItem(hWnd, r, PosOnly: true));
            _lastScreen[hWnd] = (r.X, r.Y, r.W, r.H);
        }

        return batch;
    }

    /// <summary>
    /// Project a single window (e.g., after restore from minimized).
    /// Returns true if the window was reprojected, false if skipped.
    /// </summary>
    public bool ReprojectWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid))
            return false;

        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return false;

        var r = _canvas.WorldToScreen(world);

        _win32.SetWindowPosition(hWnd, r.X, r.Y, r.W, r.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));

        _lastScreen[hWnd] = (r.X, r.Y, r.W, r.H);
        return true;
    }

    /// <summary>
    /// Detect windows the user manually moved/resized and update the canvas.
    /// </summary>
    public void Reconcile()
    {
        if (_suspendReconcile)
        {
            _reconcilePending = true;
            return;
        }
        foreach (var (hWnd, _) in _canvas.Windows)
            ReconcileWindow(hWnd);
    }

    /// <summary>Update a single window's world position from its actual screen position.</summary>
    public void ReconcileWindow(IntPtr hWnd)
    {
        if (_suspendReconcile)
        {
            _reconcilePending = true;
            return;
        }
        if (!IsWindowActive(hWnd))
            return;

        int style = _win32.GetWindowStyle(hWnd);
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

        var (ax, ay, aw, ah) = _win32.GetWindowRect(hWnd);

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
            if (!_win32.IsWindowVisible(hWnd))
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
            _win32.UnclipWindow(hWnd);
    }

    /// <summary>Re-clip windows that should be off-screen.</summary>
    public void ReclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            _win32.ClipWindow(hWnd);
    }

    /// <summary>
    /// Recovery action for the tray "Refresh" menu: enumerate every visible
    /// top-level window (not just canvas-tracked ones — third-party tools like
    /// Aero Snap or screen recorders can leave stray clip regions on windows
    /// we never registered) and clear any window region + force a full repaint.
    /// </summary>
    public unsafe void RefreshAllWindows()
    {
        _clippedWindows.Clear();
        _win32.EnumWindows(hWnd =>
        {
            if (_win32.IsWindowVisible(hWnd))
            {
                _win32.UnclipWindow(hWnd);
                PInvoke.RedrawWindow((HWND)hWnd, null, (HRGN)IntPtr.Zero,
                    REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | REDRAW_WINDOW_FLAGS.RDW_ERASE |
                    REDRAW_WINDOW_FLAGS.RDW_FRAME | REDRAW_WINDOW_FLAGS.RDW_ALLCHILDREN);
            }
            return true;
        });
    }

    /// <summary>Register a new window into the canvas from its screen position.</summary>
    public void RegisterWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid))
            return;

        var (sx, sy, sw, sh) = _win32.GetWindowRect(hWnd);

        _canvas.SetWindowFromScreen(hWnd, sx, sy, sw, sh);
        _lastScreen[hWnd] = (sx, sy, sw, sh);
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
            _win32.UnclipWindow(hWnd);
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

        _win32.BatchMove(batch, isAsync: false, isTransient: false);
        _canvas.ClearWindows();
        _lastScreen.Clear();
    }

    // ==================== PRIVATE ====================

    public void DiscoverNewWindows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var toAdd = new List<IntPtr>();

        _win32.EnumWindows(hWnd =>
        {
            if (_canvas.HasWindow(hWnd)) return true;
            if (!_win32.IsManageable(hWnd, ownPid)) return true;
            if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return true;
            toAdd.Add(hWnd);
            return true;
        });

        foreach (var hWnd in toAdd)
            RegisterWindow(hWnd);
    }

    private bool IsWindowActive(IntPtr hWnd)
    {
        if (!_win32.IsWindowVisible(hWnd))
            return false;
        int style = _win32.GetWindowStyle(hWnd);
        return (style & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;
    }

    /// <summary>
    /// Clamp window position so it sits just outside the nearest screen edge.
    /// This hides DWM border/shadow effects that would bleed onto the visible area.
    /// </summary>
    private (int x, int y) ClampToScreenEdge(int sx, int sy, int sw, int sh)
    {
        var screens = _win32.GetScreenWorkingAreas();

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
        foreach (var (left, top, width, height) in _win32.GetScreenWorkingAreas())
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
