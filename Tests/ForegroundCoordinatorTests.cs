using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ForegroundCoordinatorTests
{
    private sealed class Harness
    {
        public Canvas Canvas = new();
        public FakeClock Clock = new();
        public FakeScreens Screens = new();
        public FakeInputRouter Input = new();
        public FakeOverviewController Overview = new();
        public ForegroundCoordinator Foreground = null!;

        public Harness()
        {
            Foreground = new ForegroundCoordinator(Canvas, Overview, Input, Clock, Screens);
        }
    }

    [Fact]
    public void WindowMinimized_StampsLastWindowLostTick()
    {
        var h = new Harness();
        h.Clock.Now = 12345;
        h.Input.RaiseWindowMinimized((IntPtr)1);

        // Verify the stamp was applied: an immediate WindowFocused on an
        // off-screen tracked window should NOT recenter.
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);
        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_OffScreenWindow_RecentersCamera()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Input.RaiseWindowFocused((IntPtr)1);

        // Camera should now be centered roughly on the window
        var (sx, sy) = h.Canvas.WorldToScreen(5200, 5150);
        Assert.InRange(sx, 1920 / 2 - 2, 1920 / 2 + 2);
        Assert.InRange(sy, 1040 / 2 - 2, 1040 / 2 + 2);
    }

    [Fact]
    public void WindowFocused_OnScreenWindow_DoesNotRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);

        h.Clock.Now = 10000;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_WithinSuppressionWindowAfterDestroy_DoesNotRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Input.RaiseWindowDestroyed((IntPtr)1);

        // Within 500ms — focused event should be suppressed
        h.Clock.Now = 10499;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_AfterSuppressionWindowExpires_RecentersAgain()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Input.RaiseWindowDestroyed((IntPtr)1);

        // Past 500ms suppression window — focus should recenter
        h.Clock.Now = 10501;
        h.Input.RaiseWindowFocused((IntPtr)2);

        var (sx, _) = h.Canvas.WorldToScreen(5200, 5150);
        Assert.InRange(sx, 1920 / 2 - 2, 1920 / 2 + 2);
    }

    [Fact]
    public void OverviewClose_StampsOverlayClosedTick_SuppressesNextFocus()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Overview.TransitionTo(OverviewMode.Zooming);
        h.Overview.TransitionTo(OverviewMode.Hidden);

        h.Clock.Now = 10100;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }
}
