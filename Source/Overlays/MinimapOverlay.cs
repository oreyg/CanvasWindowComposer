using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Semi-transparent minimap overlay that appears in the bottom-right corner
/// when the canvas is transformed. Shows all windows as rectangles and
/// the camera viewport.
/// </summary>
internal sealed class MinimapOverlay : Form
{
    private const int MapWidth = 240;
    private const int MapHeight = 160;
    private const int ScreenMargin = 20;
    private const int InnerPadding = 8;
    private const double ExtentsPadding = 0.10; // 10% padding around extents
    private const int FadeDelayMs = 2000;
    private const int FadeTickIntervalMs = 100;
    private const double FadeOpacityStep = 0.15;
    private const double FadeOpacityThreshold = 0.05;
    private const double MinimapOpacity = 0.75;
    private const double MinimapBorderPx = 2.0;
    private const int MinRectSizePx = 2;

    private readonly Canvas _canvas;
    private readonly IScreens _screens;
    private readonly Timer _fadeTimer;
    private int _fadeTicksRemaining;

    // WS_EX flags for click-through, topmost, tool window
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                        | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public MinimapOverlay(Canvas canvas, IScreens? screens = null)
    {
        _canvas = canvas;
        _screens = screens ?? WinFormsScreens.Instance;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(MapWidth + InnerPadding * 2, MapHeight + InnerPadding * 2);
        BackColor = Color.Black;
        Opacity = MinimapOpacity;
        DoubleBuffered = true;

        PositionOnScreen();

        _fadeTimer = new Timer { Interval = FadeTickIntervalMs };
        _fadeTimer.Tick += OnFadeTick;
    }

    /// <summary>Force-show the minimap briefly (e.g., on desktop switch).</summary>
    public void ShowBriefly()
    {
        PositionOnScreen();
        if (!Visible) Show();
        Invalidate();
        Update();

        _fadeTicksRemaining = FadeDelayMs / 100;
        Opacity = MinimapOpacity;
        _fadeTimer.Start();
    }

    /// <summary>Call when the canvas changes. Shows the minimap and resets the fade timer.</summary>
    public void NotifyCanvasChanged()
    {
        PositionOnScreen();
        if (!Visible) Show();
        Invalidate();
        Update(); // force immediate repaint — Invalidate alone gets starved by input messages

        // Reset fade timer
        _fadeTicksRemaining = FadeDelayMs / 100;
        Opacity = MinimapOpacity;
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        _fadeTicksRemaining--;

        if (_fadeTicksRemaining <= 0)
        {
            // Fade out over ~500ms (5 ticks)
            Opacity -= FadeOpacityStep;
            if (Opacity <= FadeOpacityThreshold)
            {
                _fadeTimer.Stop();
                Hide();
            }
        }
    }

    private void PositionOnScreen()
    {
        var screen = _screens.PrimaryWorkingArea;
        Location = new Point(
            screen.Right - Width - ScreenMargin,
            screen.Bottom - Height - ScreenMargin
        );
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(30, 30, 30));

        var extents = _canvas.GetWorldExtents();
        if (extents == null) return;

        var (minX, minY, maxX, maxY) = extents.Value;

        // Include the viewport in the extents
        var screen = _screens.PrimaryBounds;
        var viewport = _canvas.GetViewport(screen.Width, screen.Height);
        minX = Math.Min(minX, viewport.x);
        minY = Math.Min(minY, viewport.y);
        maxX = Math.Max(maxX, viewport.x + viewport.w);
        maxY = Math.Max(maxY, viewport.y + viewport.h);

        // Add 10% padding
        double worldW = maxX - minX;
        double worldH = maxY - minY;
        double padX = worldW * ExtentsPadding;
        double padY = worldH * ExtentsPadding;
        minX -= padX; minY -= padY;
        maxX += padX; maxY += padY;
        worldW = maxX - minX;
        worldH = maxY - minY;

        if (worldW < 1 || worldH < 1) return;

        // Compute scale to fit into minimap area
        double scaleX = (MapWidth - MinimapBorderPx) / worldW;
        double scaleY = (MapHeight - MinimapBorderPx) / worldH;
        double scale = Math.Min(scaleX, scaleY);

        // Center in minimap
        double drawW = worldW * scale;
        double drawH = worldH * scale;
        double offsetX = InnerPadding + (MapWidth - drawW) / 2;
        double offsetY = InnerPadding + (MapHeight - drawH) / 2;

        // Draw window rects
        using var windowBrush = new SolidBrush(Color.FromArgb(100, 80, 160, 255));
        using var windowPen = new Pen(Color.FromArgb(180, 100, 180, 255), 1f);

        foreach (var (_, world) in _canvas.Windows)
        {
            if (world.State != CanvasDesktop.WindowState.Normal) continue;
            float rx = (float)(offsetX + (world.X - minX) * scale);
            float ry = (float)(offsetY + (world.Y - minY) * scale);
            float rw = (float)(world.W * scale);
            float rh = (float)(world.H * scale);

            if (rw < MinRectSizePx) rw = MinRectSizePx;
            if (rh < MinRectSizePx) rh = MinRectSizePx;

            g.FillRectangle(windowBrush, rx, ry, rw, rh);
            g.DrawRectangle(windowPen, rx, ry, rw, rh);
        }

        // Draw viewport
        using var viewportPen = new Pen(Color.FromArgb(200, 255, 200, 50), 1.5f);
        float vx = (float)(offsetX + (viewport.x - minX) * scale);
        float vy = (float)(offsetY + (viewport.y - minY) * scale);
        float vw = (float)(viewport.w * scale);
        float vh = (float)(viewport.h * scale);
        g.DrawRectangle(viewportPen, vx, vy, vw, vh);

        // Draw border
        using var borderPen = new Pen(Color.FromArgb(100, 255, 255, 255), 1f);
        g.DrawRectangle(borderPen, InnerPadding, InnerPadding, MapWidth - 1, MapHeight - 1);
    }
}
