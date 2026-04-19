using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ForegroundCoordinatorTests
{
    private sealed class Harness
    {
        public Canvas Canvas = new();
        public FakeWindowApi Api = new();
        public FakeAppConfig Config = new();
        public FakeClock Clock = new();
        public FakeScreens Screens = new();
        public FakeInputRouter Input = new();
        public FakeOverviewController Overview = new();
        public WindowManager Wm = null!;
        public ForegroundCoordinator Foreground = null!;

        public Harness()
        {
            Wm = new WindowManager(Canvas, Api, new DllInjector(), Config, Input, Clock);
            _ = new OverviewInputs(Overview, Input, Canvas);
            Foreground = new ForegroundCoordinator(Canvas, Overview, Input, Clock, Screens);
        }
    }

    // ==================== MOUSE / DRAG ====================

    [Fact]
    public void DragStarted_TransitionsOverviewToPanning()
    {
        var h = new Harness();
        h.Input.RaiseDragStarted();

        Assert.Equal(OverviewMode.Panning, h.Overview.CurrentMode);
    }

    [Fact]
    public void OverviewPanningMode_RegistersExtraPanSurfaces()
    {
        var h = new Harness();
        h.Overview.Monitors.Add((IntPtr)42);

        h.Input.RaiseDragStarted();

        Assert.Equal(1, h.Input.SetExtraCalls);
        Assert.Single(h.Input.ExtraPanSurfaces);
        Assert.Equal((IntPtr)42, h.Input.ExtraPanSurfaces[0]);
    }

    [Fact]
    public void OverviewLeavingPanning_ClearsExtraPanSurfaces()
    {
        var h = new Harness();
        h.Input.RaiseDragStarted();
        Assert.Equal(1, h.Input.SetExtraCalls);

        h.Overview.TransitionTo(OverviewMode.Hidden);

        Assert.Equal(1, h.Input.ClearExtraCalls);
    }

    [Fact]
    public void MouseButtonDown_DuringPanning_HidesOverview()
    {
        var h = new Harness();
        h.Input.RaiseDragStarted();
        Assert.Equal(OverviewMode.Panning, h.Overview.CurrentMode);

        h.Input.RaiseButtonDown();

        Assert.Equal(OverviewMode.Hidden, h.Overview.CurrentMode);
    }

    [Fact]
    public void MouseButtonDown_NotInPanning_DoesNothing()
    {
        var h = new Harness();
        Assert.Equal(OverviewMode.Hidden, h.Overview.CurrentMode);

        h.Input.RaiseButtonDown();

        // No spurious transition — single Hidden->Hidden noop wouldn't be recorded
        Assert.Empty(h.Overview.Transitions);
    }

    // ==================== CANVAS INPUT DRAIN ====================

    [Fact]
    public void InputAvailable_DrainPanDelta_PansCanvasAndRecordsForInertia()
    {
        var h = new Harness();
        h.Input.QueuePanAndRaise(dx: 10, dy: 5);

        Assert.Equal(-10, h.Canvas.CamX);
        Assert.Equal(-5, h.Canvas.CamY);
        Assert.Single(h.Overview.RecordedDeltas);
        Assert.Equal((10, 5), h.Overview.RecordedDeltas[0]);
    }

    [Fact]
    public void InputAvailable_DragEnded_ReleasesInertia()
    {
        var h = new Harness();
        h.Input.PendingDragEnded = true;
        h.Input.RaiseInputAvailable();

        Assert.Equal(1, h.Overview.ReleaseInertiaCalls);
    }

    [Fact]
    public void InputAvailable_Zoom_TransitionsToZooming()
    {
        var h = new Harness();
        h.Input.PendingZoom = true;
        h.Input.RaiseInputAvailable();

        Assert.Equal(OverviewMode.Zooming, h.Overview.CurrentMode);
    }

    [Fact]
    public void InputAvailable_Zoom_WhileZooming_HidesOverview()
    {
        var h = new Harness();
        h.Overview.TransitionTo(OverviewMode.Zooming);

        h.Input.PendingZoom = true;
        h.Input.RaiseInputAvailable();

        Assert.Equal(OverviewMode.Hidden, h.Overview.CurrentMode);
    }

    // ==================== HOTKEYS ====================

    [Fact]
    public void OverviewHotkey_FromHidden_OpensZooming()
    {
        var h = new Harness();
        h.Input.RaiseOverviewHotkey();
        Assert.Equal(OverviewMode.Zooming, h.Overview.CurrentMode);
    }

    [Fact]
    public void OverviewHotkey_FromZooming_Hides()
    {
        var h = new Harness();
        h.Overview.TransitionTo(OverviewMode.Zooming);
        h.Input.RaiseOverviewHotkey();
        Assert.Equal(OverviewMode.Hidden, h.Overview.CurrentMode);
    }

    // ==================== WINDOW LIFECYCLE ====================

    [Fact]
    public void WindowMinimized_TrackedWindow_CollapsesInCanvas()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);

        h.Input.RaiseWindowMinimized((IntPtr)1);

        Assert.True(h.Canvas.IsCollapsed((IntPtr)1));
    }

    [Fact]
    public void WindowMinimized_StampsLastWindowLostTick()
    {
        var h = new Harness();
        h.Clock.Now = 12345;

        h.Input.RaiseWindowMinimized((IntPtr)1);

        // Foreground suppression depends on this tick; verify by checking
        // that an immediate WindowFocused does NOT recenter.
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        var camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);
        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowRestored_ExpandsCanvasAndReprojects()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        h.Canvas.CollapseWindow((IntPtr)1);
        h.Api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        h.Input.RaiseWindowRestored((IntPtr)1);

        Assert.False(h.Canvas.IsCollapsed((IntPtr)1));
        // Two reprojects: one from CollapseChanged (Minimized->Normal), one
        // from the explicit OnWindowRestored call. Both target the same world.
        Assert.NotEmpty(h.Api.SetPositionCalls);
        Assert.All(h.Api.SetPositionCalls, c => Assert.Equal(100, c.x));
    }

    [Fact]
    public void WindowDestroyed_RemovesFromCanvas()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);

        h.Input.RaiseWindowDestroyed((IntPtr)1);

        Assert.False(h.Canvas.HasWindow((IntPtr)1));
    }

    [Fact]
    public void WindowMoved_TrackedWindow_TriggersReconcile()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        h.Api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        h.Wm.Reproject(); // establishes _lastScreen baseline

        // User dragged the window manually
        h.Api.Windows[(IntPtr)1].X = 400;
        h.Api.Windows[(IntPtr)1].Y = 250;

        h.Input.RaiseWindowMoved((IntPtr)1);

        var world = h.Canvas.Windows[(IntPtr)1];
        Assert.Equal(400, world.X);
        Assert.Equal(250, world.Y);
    }

    [Fact]
    public void AltTabStarted_SuspendsGreedyDraw()
    {
        var h = new Harness();
        Assert.False(h.Wm.SuspendGreedyDraw);

        h.Input.RaiseAltTabStarted();

        Assert.True(h.Wm.SuspendGreedyDraw);
    }

    [Fact]
    public void AltTabEnded_RestoresGreedyDraw()
    {
        var h = new Harness();
        h.Input.RaiseAltTabStarted();
        h.Input.RaiseAltTabEnded();
        Assert.False(h.Wm.SuspendGreedyDraw);
    }

    // ==================== FOREGROUND SUPPRESSION ====================

    [Fact]
    public void WindowFocused_OffScreenWindow_RecentersCamera()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        // No prior minimize/destroy/overlay close — clock at tick 10000
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

    // ==================== CAMERA / MINIMAP COUPLING ====================

    [Fact]
    public void CanvasPan_SyncsOverviewCamera()
    {
        var h = new Harness();
        h.Canvas.Pan(10, 10);

        Assert.Equal(1, h.Overview.SyncCameraCalls);
    }

    [Fact]
    public void CanvasCommit_TriggersReproject()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 200, 400, 300);
        h.Api.AddWindow((IntPtr)1, 0, 0, 400, 300);

        h.Canvas.Commit();

        Assert.Single(h.Api.LastBatch);
    }

    [Fact]
    public void CanvasPan_WithinReprojectThrottle_OnlyReprojectsOnce()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 200, 400, 300);
        h.Api.AddWindow((IntPtr)1, 0, 0, 400, 300);

        h.Clock.Now = 1000;
        h.Canvas.Pan(1, 0); // first pan -> reproject
        int firstBatch = h.Api.LastBatch.Count;
        h.Api.LastBatch.Clear();

        // Within 200ms throttle
        h.Clock.Now = 1100;
        h.Canvas.Pan(1, 0);

        Assert.Equal(1, firstBatch);
        Assert.Empty(h.Api.LastBatch);
    }

    [Fact]
    public void CanvasPan_PastReprojectThrottle_ReprojectsAgain()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 200, 400, 300);
        h.Api.AddWindow((IntPtr)1, 0, 0, 400, 300);

        h.Clock.Now = 1000;
        h.Canvas.Pan(1, 0);
        h.Api.LastBatch.Clear();

        h.Clock.Now = 1300; // past 200ms
        h.Canvas.Pan(1, 0);

        Assert.Single(h.Api.LastBatch);
    }
}
