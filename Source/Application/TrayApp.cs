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
    private readonly InertiaEngine _inertia;
    private readonly Timer _bgTimer; // reconcile, VD polling
    private readonly MessageWindow _msgWindow;
    private readonly DllInjector _injector;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly VirtualDesktopService _vds;
    private readonly Dictionary<Guid, CanvasState> _desktopStates = new();
    private Guid _lastDesktopId;
    private readonly MinimapOverlay _minimap;
    private readonly SearchOverlay _search;
    private readonly OverviewOverlay _overview;
    private readonly WinEventRouter _winEvents;
    private bool _enabled = true;
    private const long ForegroundSuppressionMs = 500; // ignore focus events shortly after minimize/close/overlay
    private long _lastWindowLostTick;
    private long _lastOverlayClosedTick;

    public TrayApp()
    {
        AppConfig.Load();
        AppConfig.StartObservingChanges();
        GridRenderer.CompileShaders();
        _injector = new DllInjector();
        _vds = new VirtualDesktopService();
        _lastDesktopId = _vds.CurrentDesktopId;
        _canvas = new Canvas();
        var winApi = new Win32WindowApi();
        _wm = new WindowManager(_canvas, winApi, _injector, _vds);
        _minimap = new MinimapOverlay(_canvas);
        _search = new SearchOverlay(_canvas, _wm, winApi);
        _overview = new OverviewOverlay(_canvas, _wm, winApi);
        _overview.OverviewClosed += () => _lastOverlayClosedTick = Environment.TickCount64;
        _overview.Warmup();
        _inertia = new InertiaEngine(_canvas);
        _inertia.SetUiControl(_minimap);
        _canvas.CameraChanged += () => { _wm.Reproject(); _minimap.NotifyCanvasChanged(); };
        _mouseHook = new MouseHook();

        _mouseHook.DragStarted += () => _inertia.Cancel();

        // Hidden message window for hotkeys and input
        _msgWindow = new MessageWindow();
        _msgWindow.RegisterHandlers(
            onSearchHotkey: () => {
                if (!AppConfig.DisableSearch)
                    _search.Toggle();
            },
            onOverviewHotkey: () => { _inertia.Cancel(); _overview.Toggle(); },
            onCanvasInput: OnCanvasInput);
        _mouseHook.SetNotifyTarget(_msgWindow.Handle);

        // Background timer for reconcile and VD polling only
        _bgTimer = new System.Windows.Forms.Timer { Interval = 500 };
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
        _winEvents.WindowLost += _ => _lastWindowLostTick = Environment.TickCount64;
        _winEvents.AltTabStarted += () => { _wm.SuspendGreedyDraw = true; _wm.UnclipAll(); };
        _winEvents.AltTabEnded += () => { _wm.SuspendGreedyDraw = false; _wm.ReclipAll(); };
        _winEvents.WindowRestored += hWnd => _wm.ReprojectWindow(hWnd);
        _winEvents.WindowFocused += OnWindowFocused;
        _winEvents.WindowMoved += hWnd => { if (_canvas.HasWindow(hWnd)) _wm.ReconcileWindow(hWnd); };
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
            }
        }
    }

    /// <summary>Called immediately via WM_CANVAS_INPUT when mouse input arrives.</summary>
    private void OnCanvasInput()
    {
        if (_mouseHook.TryDrainDelta(out int dx, out int dy))
        {
            _canvas.Pan(dx, dy); // fires CameraChanged → Reproject + minimap
            _inertia.RecordDelta(dx, dy);
        }

        if (_mouseHook.TryDrainDragEnded())
            _inertia.Release();

        if (_mouseHook.TryDrainZoom())
        {
            _inertia.Cancel();
            _overview.Toggle();
        }
    }

    /// <summary>Background timer for inertia, reconcile, VD polling.</summary>
    private void OnBgTick(object? sender, EventArgs e)
    {
        if (_vds.CheckDesktopChanged())
            OnDesktopSwitched();

        _wm.DiscoverNewWindows();
        _wm.RemoveStale();
    }

    private void OnDesktopSwitched()
    {
        _inertia.Cancel();

        if (_lastDesktopId != Guid.Empty)
        {
            _wm.Reset();
            _desktopStates[_lastDesktopId] = _canvas.SaveState();
        }

        _lastDesktopId = _vds.CurrentDesktopId;

        if (_desktopStates.TryGetValue(_lastDesktopId, out var state))
            _canvas.LoadState(state); // fires CameraChanged → Reproject + minimap
        else
            _wm.Reproject(); // discover windows on fresh desktop

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
            _inertia.Cancel();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _bgTimer.Stop();
        _bgTimer.Dispose();
        _msgWindow.Dispose();
        _winEvents.Dispose();
        _inertia.Dispose(); // cancel + join thread
        _wm.Reset();
        _mouseHook.Dispose();
        _overview.Close();
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
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var pen = new Pen(Color.White, 2f);
        int cx = 16, cy = 16, len = 10, arrow = 3;

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
