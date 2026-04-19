using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

/// <summary>
/// Tests for WindowManager's self-subscriptions to Canvas + IInputRouter
/// events. The canonical reproject/clip/reconcile tests live in
/// <see cref="WindowManagerTests"/>; this file focuses on the wiring path.
/// </summary>
public class WindowManagerEventTests
{
    private sealed class Harness
    {
        public Canvas Canvas = new();
        public FakeWindowApi Api = new();
        public FakeAppConfig Config = new();
        public FakeClock Clock = new();
        public FakeInputRouter Input = new();
        public WindowManager Wm = null!;

        public Harness()
        {
            Wm = new WindowManager(Canvas, Api, new DllInjector(), Config, Input, Clock);
        }
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
    public void WindowRestored_ExpandsCanvasAndReprojects()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        h.Canvas.CollapseWindow((IntPtr)1);
        h.Api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        h.Input.RaiseWindowRestored((IntPtr)1);

        Assert.False(h.Canvas.IsCollapsed((IntPtr)1));
        // Two reprojects: one from CollapseChanged (Minimized->Normal), one
        // from the explicit OnWindowRestored handler. Both target the same world.
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

    // ==================== ALT-TAB ====================

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

    // ==================== CANVAS COMMIT + REPROJECT THROTTLE ====================

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
