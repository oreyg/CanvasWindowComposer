using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Thin shell around <see cref="MinimapRenderer"/>. Canvas events translate
/// to a single <c>UpdateSnapshot</c> on the UI thread; drawing and fade
/// timing run on the render thread.
/// </summary>
internal sealed class MinimapOverlay : Form
{
    private const int MapWidth = 240;
    private const int MapHeight = 160;
    private const int ScreenMargin = 20;
    private const int InnerPadding = 8;
    private const double MinimapOpacity = 0.75;

    // Fade: hold full for HoldMs, then linear ramp to 0 over FadeMs. Driven on
    // the render thread via OnFrameTick; mirrors the overview's inertia tick.
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
    private readonly Action _applyFadeOnUi;
    private bool _rendererInitialized;

    private long _touchTicks;
    private double _lastAppliedOpacity = -1;
    private int _fadeUpdateQueued;

    // Scratch buffer reused across snapshots, refilled per snapshot by
    // sorting Canvas.Windows by WorldRect.ZOrder descending (topmost first).
    private readonly List<WorldRect> _orderedWindows = new();
    private static readonly Comparison<WorldRect> ZOrderDescending = (a, b) => b.ZOrder.CompareTo(a.ZOrder);

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

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    public MinimapOverlay(Canvas canvas, IInputRouter input, DesktopStateCache desktops, IScreens? screens = null)
    {
        _canvas = canvas;
        _screens = screens ?? WinFormsScreens.Instance;
        _applyFadeOnUi = ApplyFadeOnUi;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(MapWidth + InnerPadding * 2, MapHeight + InnerPadding * 2);
        BackColor = Color.Black;
        Opacity = MinimapOpacity;

        PositionOnScreen();

        canvas.CameraChanged       += NotifyCanvasChanged;
        canvas.CollapseChanged     += _ => NotifyCanvasChanged();
        canvas.MaximizeChanged     += _ => NotifyCanvasChanged();
        canvas.FrontChanged        += _ => RefreshSnapshotIfVisible();
        input.DragStarted          += BringToFront;
        desktops.AfterStateLoaded  += NotifyCanvasChanged;
    }

    /// <summary>Called on canvas changes + by <see cref="DesktopStateCache"/> after restore.</summary>
    public void NotifyCanvasChanged()
    {
        PositionOnScreen();
        EnsureRendererInitialized();
        RebuildSnapshot();

        _touchTicks = Environment.TickCount64;
        if (!Visible)
        {
            Opacity = MinimapOpacity;
            _lastAppliedOpacity = MinimapOpacity;
            Show();
            _renderer.Start();
        }
    }

    /// <summary>
    /// Rebuild the z-order snapshot but leave visibility and fade timer alone.
    /// For events that shouldn't wake a hidden minimap (e.g. FrontChanged).
    /// </summary>
    private void RefreshSnapshotIfVisible()
    {
        if (!Visible) return;
        EnsureRendererInitialized();
        RebuildSnapshot();
    }

    private void RebuildSnapshot()
    {
        _orderedWindows.Clear();
        foreach (var kv in _canvas.Windows)
            if (kv.Value.State == CanvasDesktop.WindowState.Normal)
                _orderedWindows.Add(kv.Value);
        _orderedWindows.Sort(ZOrderDescending);

        var primary = _screens.PrimaryBounds;
        _renderer.UpdateSnapshot(
            _orderedWindows,
            _canvas.GetWorldExtents(),
            _canvas.GetViewport(primary.Width, primary.Height));
    }

    private void EnsureRendererInitialized()
    {
        if (_rendererInitialized) return;
        _ = Handle; // force HWND
        MinimapRenderer.CompileShaders();
        _rendererInitialized = _renderer.Initialize(Handle, Width, Height,
            mapOriginX: InnerPadding, mapOriginY: InnerPadding, mapW: MapWidth, mapH: MapHeight);
        if (_rendererInitialized)
        {
            _renderer.OnFrameTick = OnRendererFrameTick;
            _renderer.StartThread();
        }
    }

    /// <summary>
    /// Render-thread tick. Coalesces into a single pending BeginInvoke — the
    /// UI handler recomputes the target so it isn't stale if the UI was busy.
    /// </summary>
    private void OnRendererFrameTick()
    {
        if (!IsHandleCreated) return;
        if (Interlocked.Exchange(ref _fadeUpdateQueued, 1) != 0) return;
        BeginInvoke(_applyFadeOnUi);
    }

    private void ApplyFadeOnUi()
    {
        _fadeUpdateQueued = 0;

        long elapsed = Environment.TickCount64 - _touchTicks;
        double fadeProgress = Math.Clamp((elapsed - HoldMs) / (double)FadeMs, 0.0, 1.0);
        double target = MinimapOpacity * (1.0 - fadeProgress);

        if (target > 0.0 && Math.Abs(target - _lastAppliedOpacity) < OpacityQuantum) return;

        _lastAppliedOpacity = target;
        if (target <= 0.0)
        {
            if (Visible) Hide();
            _renderer.Stop();
        }
        else
        {
            Opacity = target;
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
