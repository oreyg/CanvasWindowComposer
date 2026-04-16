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
    private readonly Timer _moveTimer;
    private readonly ZoomSharedMemory _sharedMem;
    private readonly DllInjector _injector;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly VirtualDesktopService _vds;
    private readonly Dictionary<Guid, CanvasState> _desktopStates = new();
    private Guid _lastDesktopId;
    private readonly MinimapOverlay _minimap;
    private readonly SearchOverlay _search;
    private readonly HotkeyWindow _hotkeyWindow;
    private bool _enabled = true;
    private int _reconcileCounter;
    private const long ForegroundSuppressionMs = 100; // ignore focus events shortly after minimize/close
    private long _lastWindowLostTick;
    private IntPtr _winEventHook_System_Minimize;
    private IntPtr _winEventHook_Object_Destroy;
    private IntPtr _winEventHook_System_Foreground;
    private IntPtr _winEventHook_Object_LocationChange;
    private readonly NativeMethods.WinEventDelegate _winEventProc;

    public TrayApp()
    {
        _sharedMem = new ZoomSharedMemory();
        _injector = new DllInjector();
        _vds = new VirtualDesktopService();
        _lastDesktopId = _vds.CurrentDesktopId;
        _canvas = new Canvas();
        _wm = new WindowManager(_canvas, _injector, _sharedMem, _vds);
        _minimap = new MinimapOverlay(_canvas);
        _search = new SearchOverlay(_canvas, _wm, _minimap);
        _hotkeyWindow = new HotkeyWindow(() => _search.Toggle());
        _inertia = new InertiaEngine(_canvas, _wm);
        _inertia.SetMinimap(_minimap);
        _mouseHook = new MouseHook();

        _mouseHook.DragStarted += () =>
        {
            _inertia.Cancel();
        };

        _moveTimer = new Timer { Interval = 16 };
        _moveTimer.Tick += OnMoveTick;
        _moveTimer.Start();

        var toggleItem = new ToolStripMenuItem("Enabled", null, OnToggle) { Checked = true };
        var resetZoomItem = new ToolStripMenuItem("Reset Zoom", null, (_, _) => _wm.Reset());
        var exitItem = new ToolStripMenuItem("Exit", null, OnExit);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel("Canvas Desktop") { Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggleItem);
        menu.Items.Add(resetZoomItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Canvas Desktop - Middle-click drag to pan",
            ContextMenuStrip = menu,
            Visible = true
        };

        _mouseHook.Install();

        _winEventProc = OnWinEvent;
        _winEventHook_System_Minimize = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _winEventProc,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _winEventHook_System_Foreground = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _winEventHook_Object_Destroy = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_DESTROY,
            NativeMethods.EVENT_OBJECT_DESTROY,
            IntPtr.Zero,
            _winEventProc,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        _winEventHook_Object_LocationChange = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventProc,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_canvas.IsTransformed) return;

        switch (eventType)
        {
            case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
            case NativeMethods.EVENT_OBJECT_DESTROY:
                _lastWindowLostTick = Environment.TickCount64;
                break;

            case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                _wm.ReprojectWindow(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                // Window got focus — if it's off-screen, center camera on it.
                // Skip if a window was just minimized — the OS is auto-focusing
                // the next window in Z-order, not the user switching.
                if (Environment.TickCount64 - _lastWindowLostTick < ForegroundSuppressionMs)
                    break;

                if (_canvas.HasWindow(hwnd))
                {
                    var screen = Screen.PrimaryScreen!.WorkingArea;
                    if (!_canvas.IsWindowOnScreen(hwnd, screen.Width, screen.Height))
                    {
                        var world = _canvas.Windows[hwnd];
                        _canvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
                        _wm.Reproject(updateDpi: true);
                        _minimap.NotifyCanvasChanged();
                    }
                }
                break;

            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                // Only top-level window moves, not child controls
                if (idObject == NativeMethods.OBJID_WINDOW && _canvas.HasWindow(hwnd))
                    _wm.ReconcileWindow(hwnd);
                break;
        }
    }

    private void OnMoveTick(object? sender, EventArgs e)
    {
        // Check for virtual desktop switch
        if (_vds.CheckDesktopChanged())
            OnDesktopSwitched();

        bool moved = false;
        bool zoomed = false;

        if (_mouseHook.TryDrainDelta(out int dx, out int dy))
        {
            _canvas.Pan(dx, dy);
            _inertia.RecordDelta(dx, dy);
            moved = true;
        }

        if (_mouseHook.TryDrainDragEnded())
            _inertia.Release();

        if (_mouseHook.TryDrainZoom(out int scrollDelta, out int cx, out int cy))
        {
            _inertia.Cancel();
            _wm.Reconcile();
            _canvas.ZoomAt(scrollDelta, cx, cy);
            moved = true;
            zoomed = true;
        }

        if (moved)
        {
            _wm.Reproject(updateDpi: zoomed);
            _minimap.NotifyCanvasChanged();
        }

        if (_canvas.IsTransformed && ++_reconcileCounter >= 30)
        {
            _reconcileCounter = 0;
            _wm.Reconcile();
            _wm.RemoveStale();
        }
    }

    private void OnDesktopSwitched()
    {
        _inertia.Cancel();

        // Save current canvas state for the previous desktop
        // (we need to find the previous ID — it was stored before CheckDesktopChanged updated it)
        // Since CheckDesktopChanged already updated _vds.CurrentDesktopId to the NEW one,
        // we save under whatever key we last stored. Use a tracking field.
        if (_lastDesktopId != Guid.Empty)
        {
            _wm.Reset();
            _desktopStates[_lastDesktopId] = _canvas.SaveState();
        }

        _lastDesktopId = _vds.CurrentDesktopId;

        // Load state for new desktop, or start fresh
        if (_desktopStates.TryGetValue(_lastDesktopId, out var state))
            _canvas.LoadState(state);

        // Always reproject to discover windows and apply state
        _wm.Reproject(updateDpi: true);
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
        _moveTimer.Stop();
        _moveTimer.Dispose();
        if (_winEventHook_System_Minimize != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_winEventHook_System_Minimize);
        if (_winEventHook_System_Foreground != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_winEventHook_System_Foreground);
        if (_winEventHook_Object_Destroy != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_winEventHook_Object_Destroy);
        if (_winEventHook_Object_LocationChange != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_winEventHook_Object_LocationChange);
        _inertia.Cancel();
        _search.Close();
        _search.Dispose();
        _hotkeyWindow.Dispose();
        _minimap.Close();
        _minimap.Dispose();
        _wm.Reset();
        _mouseHook.Dispose();
        _inertia.Dispose();
        _vds.Dispose();
        _sharedMem.Dispose();
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
