using System;
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
    private bool _enabled = true;
    private int _refreshCounter;
    private IntPtr _winEventHook;
    private readonly NativeMethods.WinEventDelegate _winEventProc;

    public TrayApp()
    {
        _sharedMem = new ZoomSharedMemory();
        _injector = new DllInjector();
        WindowMover.SetDpiHookResources(_injector, _sharedMem);

        _inertia = new InertiaEngine();
        _mouseHook = new MouseHook();
        _mouseHook.DragStarted += () =>
        {
            _inertia.Cancel();
            WindowMover.BeginMove();
        };

        // Timer runs at ~60fps and drains accumulated deltas from the hook.
        // This keeps heavy work (EnumWindows + DeferWindowPos) OFF the hook callback.
        _moveTimer = new Timer { Interval = 16 };
        _moveTimer.Tick += OnMoveTick;
        _moveTimer.Start();

        var toggleItem = new ToolStripMenuItem("Enabled", null, OnToggle) { Checked = true };
        var resetZoomItem = new ToolStripMenuItem("Reset Zoom", null, (_, _) => WindowMover.ResetZoom());
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

        // Listen for window restore events so zoom adjusts immediately
        _winEventProc = OnWinEvent;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _winEventProc,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_MINIMIZEEND && WindowMover.IsZoomActive)
            WindowMover.ZoomWindow(hwnd);
    }

    private void OnMoveTick(object? sender, EventArgs e)
    {
        // Drain accumulated drag delta
        if (_mouseHook.TryDrainDelta(out int dx, out int dy))
        {
            WindowMover.ApplyDelta(dx, dy);
            _inertia.RecordDelta(dx, dy);
        }

        if (_mouseHook.TryDrainDragEnded())
        {
            WindowMover.EndMove();
            _inertia.Release();
        }

        // Drain zoom scroll
        if (_mouseHook.TryDrainZoom(out int scrollDelta, out int cx, out int cy))
        {
            WindowMover.ApplyZoom(scrollDelta, cx, cy);
            _refreshCounter = 0;
        }

        // Periodically scan for new windows only (~500ms)
        // Restored windows are handled instantly via WinEvent hook.
        // Existing windows are never touched — respects manual moves.
        if (WindowMover.IsZoomActive && ++_refreshCounter >= 30)
        {
            _refreshCounter = 0;
            WindowMover.ScanNewWindows();
        }
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
        if (_winEventHook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_winEventHook);
        _inertia.Cancel();
        WindowMover.ResetZoom(); // eject hooks + restore windows before exit
        _mouseHook.Dispose();
        _inertia.Dispose();
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
        catch { /* don't crash on log failure */ }
    }
}
