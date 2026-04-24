using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class TrayApp : ApplicationContext
{
    private const int ReconcileTimerIntervalMs = 500;
    private const int TrayIconSizePx = 32;
    private const float IconLineWidth = 2f;
    private const int IconArrowLength = 10;
    private const int IconArrowHead = 3;

    private readonly IClock _clock;
    private readonly IScreens _screens;
    private readonly AppConfig _config;
    private readonly NotifyIcon _trayIcon;
    private readonly Timer _bgTimer;
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly VirtualDesktopService _vds;
    private readonly MinimapOverlay _minimap;
    private readonly SearchOverlay _search;
    private readonly OverviewManager _overview;
    private readonly Win32InputRouter _input;
    private readonly DesktopStateCache _desktops;
    private readonly ForegroundCoordinator _foreground;
    private bool _enabled = true;

    public TrayApp(IClock? clock = null, IScreens? screens = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _screens = screens ?? WinFormsScreens.Instance;
        _config = new AppConfig(_clock);
        _config.Load();
        _config.StartObservingChanges();
        OverviewRenderer.CompileShaders();
        MinimapRenderer.CompileShaders();

        var winApi = new Win32WindowApi(_screens);
        _vds = new VirtualDesktopService();
        _canvas = new Canvas();
        _input = new Win32InputRouter(_config);
        _wm = new WindowManager(_canvas, winApi, _config, _input, _clock, _vds, useAsyncProjection: true);
        _overview = new OverviewManager(_canvas, _wm, winApi, _input, _screens);
        _overview.Warmup();
        _foreground = new ForegroundCoordinator(_canvas, _overview, _input, _clock, _screens);
        _desktops = new DesktopStateCache(_canvas, _wm, _overview, _vds);

        // Constructed last so they can self-subscribe to canvas/input/desktops events.
        _minimap = new MinimapOverlay(_canvas, _input, _desktops, _screens);
        _search = new SearchOverlay(_canvas, _wm, winApi, _input, _screens);

        _bgTimer = new Timer { Interval = ReconcileTimerIntervalMs };
        _bgTimer.Tick += OnBgTick;
        _bgTimer.Start();

        var toggleItem = new ToolStripMenuItem("Enabled", null, OnToggle) { Checked = true };
        var refreshItem = new ToolStripMenuItem("Refresh", null, OnRefresh);
        var openConfigItem = new ToolStripMenuItem("Open Config Directory", null,
            (_, _) => System.Diagnostics.Process.Start("explorer.exe", AppConfig.ConfigDir));
        var exitItem = new ToolStripMenuItem("Exit", null, OnExit);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel("Canvas Desktop") { Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggleItem);
        menu.Items.Add(refreshItem);
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
    }

    private void OnBgTick(object? sender, EventArgs e)
    {
        _wm.Tick();
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        _enabled = !_enabled;
        _input.Enabled = _enabled;
        if (!_enabled)
            _overview.CancelInertia();

        if (sender is ToolStripMenuItem item)
            item.Checked = _enabled;

        _trayIcon.Text = _enabled
            ? "Canvas Desktop - Middle-click drag to pan"
            : "Canvas Desktop - Disabled";
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        _wm.RefreshAllWindows();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _bgTimer.Stop();
        _bgTimer.Dispose();
        _input.Dispose();
        _wm.Reset();
        _wm.Dispose();
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
