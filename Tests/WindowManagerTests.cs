using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowManagerTests
{
    private static (Canvas canvas, FakeWindowApi api, WindowManager wm) Create(FakeAppConfig? config = null)
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var wm = new WindowManager(canvas, api, config ?? new FakeAppConfig(), new FakeInputRouter(), new FakeClock());
        return (canvas, api, wm);
    }

    // ==================== REPROJECT ====================

    [Fact]
    public void Reproject_ProjectsOnScreenWindowsToBatch()
    {
        var (canvas, api, wm) = Create();

        // Place window at world (100, 200) — on-screen with default 1920x1080
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        Assert.Single(api.LastBatch);
        var item = api.LastBatch[0];
        Assert.Equal((IntPtr)1, item.HWnd);
        Assert.Equal(100, item.Rect.X);
        Assert.Equal(200, item.Rect.Y);
    }

    [Fact]
    public void Reproject_ClipsOffScreenWindows()
    {
        var (canvas, api, wm) = Create();

        // Place window far off-screen
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        Assert.Contains((IntPtr)1, api.ClippedWindows);
    }

    [Fact]
    public void Reproject_UnclipsWindowThatMovesOnScreen()
    {
        var (canvas, api, wm) = Create();

        // Start off-screen, get clipped
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        // Move on-screen
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        wm.Reproject();
        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
    }

    [Fact]
    public void Reproject_SkipsMaximizedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.MaximizeWindow((IntPtr)1);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600,
            style: (int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MAXIMIZE);

        wm.Reproject();

        Assert.Empty(api.LastBatch);
    }

    [Fact]
    public void Reproject_SkipsMinimizedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.CollapseWindow((IntPtr)1); // canvas state drives Reproject now
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        Assert.Empty(api.LastBatch);
    }

    [Fact]
    public void Reproject_MultipleWindows_BatchesAll()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetWindow((IntPtr)2, 600, 200, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);

        wm.Reproject();

        Assert.Equal(2, api.LastBatch.Count);
    }

    // ==================== RECONCILE ====================

    [Fact]
    public void ReconcileWindow_DetectsManualMove()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 100, 200, 800, 600);

        // Reproject to establish _lastScreen
        wm.Reproject();

        // Simulate user dragging the window to a new position
        api.Windows[(IntPtr)1].X = 300;
        api.Windows[(IntPtr)1].Y = 400;

        wm.ReconcileWindow((IntPtr)1);

        // Canvas should reflect the new position
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(300, world.X);
        Assert.Equal(400, world.Y);
    }

    [Fact]
    public void ReconcileWindow_IgnoresSmallMoves()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 100, 200, 800, 600);
        wm.Reproject();

        // Move by 1px — within 2px threshold
        api.Windows[(IntPtr)1].X = 101;

        wm.ReconcileWindow((IntPtr)1);

        // Canvas should NOT have changed
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(100, world.X);
    }

    [Fact]
    public void ReconcileWindow_IgnoresClippedWindows()
    {
        var (canvas, api, wm) = Create();

        // Place off-screen to get clipped
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        // Move the clipped window
        api.Windows[(IntPtr)1].X = 9999;

        wm.ReconcileWindow((IntPtr)1);

        // Canvas should NOT reflect the move (clipped windows are ignored)
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(5000, world.X);
    }

    // ==================== REMOVE STALE ====================

    [Fact]
    public void RemoveStale_RemovesInvisibleWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)2, 500, 200, 400, 300);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)2, 500, 200, 400, 300);

        // Window 1 disappears
        api.Windows[(IntPtr)1].Visible = false;

        wm.RemoveStale();

        Assert.False(canvas.HasWindow((IntPtr)1));
        Assert.True(canvas.HasWindow((IntPtr)2));
    }

    [Fact]
    public void RemoveStale_KeepsVisibleWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);

        wm.RemoveStale();

        Assert.True(canvas.HasWindow((IntPtr)1));
    }

    // ==================== DISCOVER NEW WINDOWS ====================

    [Fact]
    public void DiscoverNewWindows_RegistersNewManageableWindows()
    {
        var (canvas, api, wm) = Create();

        // Window exists in the system but not in canvas
        api.AddWindow((IntPtr)1, 200, 300, 800, 600, pid: 999);

        wm.DiscoverNewWindows();

        Assert.True(canvas.HasWindow((IntPtr)1));
    }

    [Fact]
    public void DiscoverNewWindows_SkipsAlreadyTrackedWindows()
    {
        var (canvas, api, wm) = Create();

        // Pre-tracked at world (100, 100); the API has it at a different
        // screen rect. If discover wrongly registered it again, SetWindowFromScreen
        // would overwrite the world coords with the API's rect.
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)1, 999, 888, 800, 600, pid: 999);

        wm.DiscoverNewWindows();

        Assert.Single(canvas.Windows);
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(100, world.X);
        Assert.Equal(100, world.Y);
    }

    [Fact]
    public void DiscoverNewWindows_SkipsNonManageableWindows()
    {
        var (canvas, api, wm) = Create();

        api.AddWindow((IntPtr)1, 200, 300, 800, 600, pid: 999, manageable: false);

        wm.DiscoverNewWindows();

        Assert.False(canvas.HasWindow((IntPtr)1));
    }

    // ==================== RESET ====================

    [Fact]
    public void Reset_UnclipsAllAndClearsCanvas()
    {
        var (canvas, api, wm) = Create();

        // Place off-screen to get clipped
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        wm.Reset();

        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
        Assert.Empty(canvas.Windows);
    }

    [Fact]
    public void Reset_RestoresWorldPositionsViaBatch()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 300, 400, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reset();

        // Batch should contain world coordinates
        Assert.Single(api.LastBatch);
        var item = api.LastBatch[0];
        Assert.Equal(300, item.Rect.X);
        Assert.Equal(400, item.Rect.Y);
        Assert.Equal(800, item.Rect.W);
        Assert.Equal(600, item.Rect.H);
    }

    // ==================== UNCLIP / RECLIP ====================

    [Fact]
    public void UnclipAll_RestoresAllClippedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        canvas.SetWindow((IntPtr)2, 6000, 6000, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);
        wm.Reproject();

        Assert.Equal(2, api.ClippedWindows.Count);

        wm.UnclipAll();

        Assert.Empty(api.ClippedWindows);
    }

    [Fact]
    public void ReclipAll_ReclipsAfterUnclip()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        wm.Reproject();
        Assert.Single(api.ClippedWindows);

        wm.UnclipAll();
        Assert.Empty(api.ClippedWindows);

        wm.ReclipAll();
        Assert.Single(api.ClippedWindows);
    }

    // ==================== COLLAPSED ====================

    [Fact]
    public void Reproject_SkipsCollapsedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetWindow((IntPtr)2, 600, 200, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);

        canvas.CollapseWindow((IntPtr)1);

        wm.Reproject();

        Assert.Single(api.LastBatch);
        Assert.Equal((IntPtr)2, api.LastBatch[0].HWnd);
    }

    // ==================== APP CONFIG FLAGS ====================

    [Fact]
    public void Reproject_WhenDisableGreedyDraw_DoesNotClipOffScreen()
    {
        var cfg = new FakeAppConfig { DisableGreedyDraw = true };
        var (canvas, api, wm) = Create(cfg);

        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        // Greedy draw is disabled — window should be batched at its world coords,
        // never clipped.
        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
        Assert.Single(api.LastBatch);
        Assert.Equal(5000, api.LastBatch[0].Rect.X);
    }

    [Fact]
    public void Reproject_WhenSuspendGreedyDraw_DoesNotClipOffScreen()
    {
        var cfg = new FakeAppConfig(); // DisableGreedyDraw = false
        var (canvas, api, wm) = Create(cfg);

        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.SuspendGreedyDraw = true;
        wm.Reproject();

        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
    }

    // ==================== REPROJECT WINDOW ====================

    [Fact]
    public void ReprojectWindow_SetsPositionDirectly()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 200, 300, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.ReprojectWindow((IntPtr)1);

        Assert.Single(api.SetPositionCalls);
        var call = api.SetPositionCalls[0];
        Assert.Equal((IntPtr)1, call.hWnd);
        Assert.Equal(200, call.x);
        Assert.Equal(300, call.y);
    }

    [Fact]
    public void ReprojectWindow_RegistersUnknownWindow()
    {
        var (canvas, api, wm) = Create();

        // Window not in canvas, but exists in system
        api.AddWindow((IntPtr)1, 400, 500, 800, 600, pid: 999);

        wm.ReprojectWindow((IntPtr)1);

        Assert.True(canvas.HasWindow((IntPtr)1));
    }
}
