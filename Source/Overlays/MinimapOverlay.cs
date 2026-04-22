using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Thin shell around <see cref="MinimapRenderer"/>. Canvas events translate
/// to a single <c>UpdateSnapshot</c> on the UI thread; everything else —
/// drawing and fade animation — runs on the render thread.
/// </summary>
internal sealed class MinimapOverlay : Form
{
    private const int MapWidth = 240;
    private const int MapHeight = 160;
    private const int ScreenMargin = 20;
    private const int InnerPadding = 8;
    private const double MinimapOpacity = 0.75;

    // Fade timing — driven on the render thread by OnFrameTick, mirroring the
    // overview's inertia pattern (render thread computes, UI thread applies).
    private const long HoldMs = 2000;
    private const long FadeMs = 500;
    private const double OpacityQuantum = 0.02;

    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Canvas _canvas;
    private readonly IScreens _screens;
    private readonly MinimapRenderer _renderer = new();
    private bool _rendererInitialized;

    // Fade state read on render thread, written on UI thread.
    private long _touchTicks;
    // Last value we pushed to Form.Opacity — used so OnFrameTick only
    // BeginInvokes when the value changes by more than a quantum.
    private double _lastAppliedOpacity = -1;
    private int _fadeUpdateQueued;

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

    public MinimapOverlay(Canvas canvas, IInputRouter input, DesktopStateCache desktops, IScreens? screens = null)
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

        PositionOnScreen();

        canvas.CameraChanged    += NotifyCanvasChanged;
        canvas.CollapseChanged  += OnWindowStateChanged;
        canvas.MaximizeChanged  += OnWindowStateChanged;
        input.DragStarted       += BringToFront;
        desktops.AfterStateLoaded += ShowBriefly;
    }

    private void EnsureRendererInitialized()
    {
        if (_rendererInitialized) return;
        _ = Handle;
        MinimapRenderer.CompileShaders();
        _rendererInitialized = _renderer.Initialize(Handle, Width, Height);
        if (_rendererInitialized)
        {
            _renderer.OnFrameTick = OnRendererFrameTick;
            _renderer.StartThread();
        }
    }

    private void OnWindowStateChanged(IntPtr hWnd)
    {
        NotifyCanvasChanged();
    }

    /// <summary>Force-show the minimap briefly (e.g., on desktop switch).</summary>
    public void ShowBriefly()
    {
        NotifyCanvasChanged();
    }

    /// <summary>Push a snapshot to the renderer and touch the fade timer.</summary>
    public void NotifyCanvasChanged()
    {
        PositionOnScreen();
        EnsureRendererInitialized();

        PushSnapshot();

        _touchTicks = Environment.TickCount64;
        if (!Visible)
        {
            Opacity = MinimapOpacity;
            _lastAppliedOpacity = MinimapOpacity;
            Show();
            _renderer.Start();
        }
    }

    private void PushSnapshot()
    {
        var primary = _screens.PrimaryBounds;
        var viewport = _canvas.GetViewport(primary.Width, primary.Height);
        var extents = _canvas.GetWorldExtents();

        _renderer.UpdateSnapshot(
            _canvas.Windows,
            extents,
            viewport,
            mapOriginX: InnerPadding,
            mapOriginY: InnerPadding,
            mapW: MapWidth,
            mapH: MapHeight);
    }

    /// <summary>
    /// Fires on the renderer's thread after each Present. Computes the fade
    /// alpha from elapsed time since the last touch and queues a single UI
    /// update when the value drifts past a quantum.
    /// </summary>
    private void OnRendererFrameTick()
    {
        long elapsed = Environment.TickCount64 - _touchTicks;
        double target;
        if (elapsed < HoldMs)
        {
            target = MinimapOpacity;
        }
        else if (elapsed < HoldMs + FadeMs)
        {
            double t = (elapsed - HoldMs) / (double)FadeMs;
            target = MinimapOpacity * (1.0 - t);
        }
        else
        {
            target = 0.0;
        }

        if (Math.Abs(target - _lastAppliedOpacity) < OpacityQuantum && target > 0.0)
            return;

        // Coalesce: only one pending BeginInvoke at a time.
        if (System.Threading.Interlocked.Exchange(ref _fadeUpdateQueued, 1) != 0)
            return;
        if (!IsHandleCreated) { _fadeUpdateQueued = 0; return; }

        BeginInvoke(() =>
        {
            _fadeUpdateQueued = 0;
            ApplyFadeOpacity(target);
        });
    }

    private void ApplyFadeOpacity(double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        _lastAppliedOpacity = value;
        if (value <= 0.0)
        {
            if (Visible) Hide();
            _renderer.Stop();
            return;
        }
        Opacity = value;
    }

    private void PositionOnScreen()
    {
        var screen = _screens.PrimaryWorkingArea;
        Location = new Point(
            screen.Right - Width - ScreenMargin,
            screen.Bottom - Height - ScreenMargin
        );
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        _renderer.Dispose();
        base.OnFormClosing(e);
    }
}
