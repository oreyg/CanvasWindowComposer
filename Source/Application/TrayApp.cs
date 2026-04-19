using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly MouseHook _mouseHook;
    private readonly Timer _bgTimer; // reconcile, VD polling
    private readonly MessageWindow _msgWindow;
    private readonly DllInjector _injector;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly ProjectionWorker _projection;
    private readonly VirtualDesktopService _vds;
    private readonly Dictionary<Guid, CanvasState> _desktopStates = new();
    private Guid _lastDesktopId;
    private readonly MinimapOverlay _minimap;
    private readonly SearchOverlay _search;
    private readonly OverviewManager _overview;
    private readonly WinEventRouter _winEvents;
    private bool _enabled = true;
    private const int ReconcileTimerIntervalMs = 500;
    private const int ReprojectTimerIntervalMs = 200; // ~5 Hz; just enough to keep WindowFromPoint accurate for click-passthrough
    private const long ForegroundSuppressionMs = 500;
    private const int TrayIconSizePx = 32;
    private const float IconLineWidth = 2f;
    private const int IconArrowLength = 10;
    private const int IconArrowHead = 3; // ignore focus events shortly after minimize/close/overlay
    private long _lastWindowLostTick;
    private long _lastOverlayClosedTick;
    private long _lastReprojectTick;

    public TrayApp()
    {
        AppConfig.Load();
        AppConfig.StartObservingChanges();
        GridRenderer.CompileShaders();
        var winApi = new Win32WindowApi();
        _injector = new DllInjector();
        _vds = new VirtualDesktopService();
        _lastDesktopId = _vds.CurrentDesktopId;
        _canvas = new Canvas();
        _projection = new ProjectionWorker(winApi);
        _wm = new WindowManager(_canvas, winApi, _injector, _vds, _projection);
        _minimap = new MinimapOverlay(_canvas);
        _search = new SearchOverlay(_canvas, _wm, winApi);
        _overview = new OverviewManager(_canvas, _wm, winApi);
        _overview.BeforeModeChanged += OnOverviewModeChanged;
        _overview.Warmup();
        _canvas.CameraChanged += OnCameraChanged;
        _canvas.CollapseChanged += OnCollapseChanged;
        _canvas.MaximizeChanged += OnMaximizeChanged;
        _canvas.Committed += OnCommitted;
        _mouseHook = new MouseHook();

        _mouseHook.DragStarted += OnDragStarted;
        _mouseHook.ButtonDown += OnMouseButtonDown;

        // Hidden message window for hotkeys and input
        _msgWindow = new MessageWindow();
        _msgWindow.RegisterHandlers(
            onSearchHotkey: OnSearchHotkey,
            onOverviewHotkey: OnOverviewHotkey,
            onCanvasInput: OnCanvasInput);
        _mouseHook.SetNotifyTarget(_msgWindow.Handle);

        // Background timer for reconcile and VD polling only
        _bgTimer = new System.Windows.Forms.Timer { Interval = ReconcileTimerIntervalMs };
        _bgTimer.Tick += OnBgTick;
        _bgTimer.Start();

        var toggleItem = new ToolStripMenuItem("Enabled", null, OnToggle) { Checked = true };
        var openConfigItem = new ToolStripMenuItem("Open Config Directory", null,
            (_, _) => System.Diagnostics.Process.Start("explorer.exe", AppConfig.ConfigDir));
        var exitItem = new ToolStripMenuItem("Exit", null, OnExit);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel("Canvas Desktop") { Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggleItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Canvas Desktop - Middle-click drag to pan",
            ContextMenuStrip = menu,
            Visible = true
        };

        _wm.DiscoverNewWindows();
        _wm.Reproject();

        _mouseHook.Install();

        _winEvents = new WinEventRouter();
        _winEvents.WindowMinimized += OnWindowMinimized;
        _winEvents.WindowDestroyed += OnWindowDestroyed;
        _winEvents.AltTabStarted += OnAltTabStarted;
        _winEvents.AltTabEnded += OnAltTabEnded;
        _winEvents.WindowRestored += OnWindowRestored;
        _winEvents.WindowFocused += OnWindowFocused;
        _winEvents.WindowMoved += OnWindowMoved;
    }

    private void OnOverviewModeChanged(OverviewManager.Mode from, OverviewManager.Mode to)
    {
        if (to == OverviewManager.Mode.Panning)
            _mouseHook.SetExtraPanSurfaces(_overview.MonitorHandles);
        else
            _mouseHook.ClearExtraPanSurfaces();

        if (to == OverviewManager.Mode.Hidden)
        {
            _lastOverlayClosedTick = Environment.TickCount64;
            _canvas.Commit();
        }
    }

    private void OnCameraChanged()
    {
        _minimap.NotifyCanvasChanged();
        _overview.SyncCamera();

        // Overview renders its own camera + thumbnails, so real windows don't
        // need to track every frame — but clicks pass through the overlay
        // (WS_EX_TRANSPARENT) and hit whichever real window is under the
        // cursor, so we keep HWND positions roughly in sync for WindowFromPoint.
        // Throttled; final reproject on overview close comes via OnCommitted.
        long ticksNow = Environment.TickCount64;
        if (ticksNow - _lastReprojectTick > ReprojectTimerIntervalMs)
        {
            _wm.Reproject(allowAsync: false);
            _lastReprojectTick = ticksNow;
        }
    }

    private void OnCommitted()
    {
        _wm.Reproject();
    }

    private void OnCollapseChanged(IntPtr hWnd)
    {
        _wm.ReprojectWindow(hWnd);
        _minimap.NotifyCanvasChanged();
    }
    private void OnMaximizeChanged(IntPtr hWnd)
    {
        _wm.ReprojectWindow(hWnd);
        _minimap.NotifyCanvasChanged();
    }

    private void OnDragStarted()
    {
        _overview.TransitionTo(OverviewManager.Mode.Panning);
        _minimap.BringToFront();
    }

    private void OnMouseButtonDown()
    {
        // A non-pan click while the panning overview is up — close it so the
        // click interacts with the underlying window normally.
        if (_overview.CurrentMode == OverviewManager.Mode.Panning)
            _overview.TransitionTo(OverviewManager.Mode.Hidden);
    }

    private void OnSearchHotkey()
    {
        if (!AppConfig.DisableSearch)
            _search.Toggle();
    }

    private void OnOverviewHotkey()
    {
        if (_overview.CurrentMode == OverviewManager.Mode.Zooming)
            _overview.TransitionTo(OverviewManager.Mode.Hidden);
        else
            _overview.TransitionTo(OverviewManager.Mode.Zooming);
    }

    private void OnWindowMinimized(IntPtr hWnd)
    {
        _lastWindowLostTick = Environment.TickCount64;
        if (_canvas.HasWindow(hWnd))
            _canvas.CollapseWindow(hWnd);
    }

    private void OnWindowRestored(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _canvas.ExpandWindow(hWnd);
        _wm.ReprojectWindow(hWnd);
    }

    private void OnWindowDestroyed(IntPtr _)
    {
        _lastWindowLostTick = Environment.TickCount64;
    }

    private void OnAltTabStarted()
    {
        _wm.SuspendGreedyDraw = true;
        _wm.UnclipAll();
    }

    private void OnAltTabEnded()
    {
        _wm.SuspendGreedyDraw = false;
        _wm.ReclipAll();
    }

    private void OnWindowMoved(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _wm.ReconcileWindow(hWnd);
    }

    private void OnWindowFocused(IntPtr hwnd)
    {
        long now = Environment.TickCount64;
        if (now - _lastWindowLostTick < ForegroundSuppressionMs ||
            now - _lastOverlayClosedTick < ForegroundSuppressionMs)
            return;

        if (_canvas.HasWindow(hwnd))
        {
            var screen = Screen.PrimaryScreen!.WorkingArea;
            if (!_canvas.IsWindowOnScreen(hwnd, screen.Width, screen.Height))
            {
                var world = _canvas.Windows[hwnd];
                _canvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
                _canvas.Commit();
            }
        }
    }

    /// <summary>Called immediately via WM_CANVAS_INPUT when mouse input arrives.</summary>
    private void OnCanvasInput()
    {
        if (_mouseHook.TryDrainDelta(out int dx, out int dy))
        {
            _canvas.Pan(dx, dy); // fires CameraChanged -> Reproject + minimap
            _overview.RecordPanDelta(dx, dy);
        }

        if (_mouseHook.TryDrainDragEnded())
        {
            _overview.ReleaseInertia();
        }

        if (_mouseHook.TryDrainZoom())
        {
            if (_overview.CurrentMode == OverviewManager.Mode.Zooming)
                _overview.TransitionTo(OverviewManager.Mode.Hidden);
            else
                _overview.TransitionTo(OverviewManager.Mode.Zooming);
        }
    }

    /// <summary>Background timer for reconcile, Virtual Desktop polling.</summary>
    private void OnBgTick(object? sender, EventArgs e)
    {
        if (_vds.CheckDesktopChanged())
            OnDesktopSwitched();

        _wm.DiscoverNewWindows();
        _wm.RemoveStale();
    }

    private void OnDesktopSwitched()
    {
        _overview.CancelInertia();

        if (_lastDesktopId != Guid.Empty)
        {
            _wm.Reset();
            _desktopStates[_lastDesktopId] = _canvas.SaveState();
        }

        _lastDesktopId = _vds.CurrentDesktopId;

        if (_desktopStates.TryGetValue(_lastDesktopId, out var state))
            _canvas.LoadState(state); // fires CameraChanged (virtual update)
        _canvas.Commit(); // apply to real windows

        _minimap.ShowBriefly();
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        _enabled = !_enabled;
        _mouseHook.Enabled = _enabled;

        if (sender is ToolStripMenuItem item)
            item.Checked = _enabled;

        _trayIcon.Text = _enabled
            ? "Canvas Desktop - Middle-click drag to pan"
            : "Canvas Desktop - Disabled";

        if (!_enabled)
            _overview.CancelInertia();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _bgTimer.Stop();
        _bgTimer.Dispose();
        _msgWindow.Dispose();
        _winEvents.Dispose();
        _wm.Reset();
        _projection.Dispose();
        _mouseHook.Dispose();
        _overview.Dispose();
        _search.Close();
        _minimap.Close();
        _vds.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(TrayIconSizePx, TrayIconSizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var pen = new Pen(Color.White, IconLineWidth);
        int cx = TrayIconSizePx / 2;
        int cy = TrayIconSizePx / 2;
        int len = IconArrowLength;
        int arrow = IconArrowHead;

        g.DrawLine(pen, cx - len, cy, cx + len, cy);
        g.DrawLine(pen, cx - len, cy, cx - len + arrow, cy - arrow);
        g.DrawLine(pen, cx - len, cy, cx - len + arrow, cy + arrow);
        g.DrawLine(pen, cx + len, cy, cx + len - arrow, cy - arrow);
        g.DrawLine(pen, cx + len, cy, cx + len - arrow, cy + arrow);

        g.DrawLine(pen, cx, cy - len, cx, cy + len);
        g.DrawLine(pen, cx, cy - len, cx - arrow, cy - len + arrow);
        g.DrawLine(pen, cx, cy - len, cx + arrow, cy - len + arrow);
        g.DrawLine(pen, cx, cy + len, cx - arrow, cy + len - arrow);
        g.DrawLine(pen, cx, cy + len, cx + arrow, cy + len - arrow);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "canvas_debug.log");

    internal static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }
}
