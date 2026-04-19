using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class CanvasTests
{
    // ==================== PROJECTIONS ====================

    [Fact]
    public void WorldToScreen_AtOrigin_ReturnsWorldCoords()
    {
        var canvas = new Canvas();
        var (x, y) = canvas.WorldToScreen(100, 200);
        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void WorldToScreen_NegativeCoords_ReturnsNegative()
    {
        var canvas = new Canvas();
        var (x, y) = canvas.WorldToScreen(-500, -300);
        Assert.Equal(-500, x);
        Assert.Equal(-300, y);
    }

    [Fact]
    public void WorldToScreen_AfterPan_OffsetsCorrectly()
    {
        var canvas = new Canvas();
        canvas.Pan(50, 30); // shifts camera by -50, -30
        var (x, y) = canvas.WorldToScreen(100, 200);
        Assert.Equal(150, x);
        Assert.Equal(230, y);
    }

    [Fact]
    public void ScreenToWorld_RoundtripsWithWorldToScreen()
    {
        var canvas = new Canvas();
        canvas.Pan(73, -41);

        double wx = 500, wy = 300;
        var (sx, sy) = canvas.WorldToScreen(wx, wy);
        var (wx2, wy2) = canvas.ScreenToWorld(sx, sy);

        Assert.InRange(wx2, wx - 1.5, wx + 1.5);
        Assert.InRange(wy2, wy - 1.5, wy + 1.5);
    }

    [Fact]
    public void WorldToScreenSize_ClampsMinimum()
    {
        var canvas = new Canvas();
        var (w, h) = canvas.WorldToScreenSize(10, 5);
        Assert.Equal(200, w); // min 200
        Assert.Equal(100, h); // min 100
    }

    [Fact]
    public void WorldToScreenSize_FractionalValues_Ceils()
    {
        var canvas = new Canvas();
        var (w, h) = canvas.WorldToScreenSize(800.3, 600.1);
        Assert.Equal(801, w);
        Assert.Equal(601, h);
    }

    [Fact]
    public void WorldToScreenSize_LargeValues_PassThrough()
    {
        var canvas = new Canvas();
        var (w, h) = canvas.WorldToScreenSize(800, 600);
        Assert.Equal(800, w);
        Assert.Equal(600, h);
    }

    [Fact]
    public void ScreenToWorldSize_RoundtripsWithWorldToScreenSize()
    {
        var canvas = new Canvas();
        double ww = 800, wh = 600;
        var (sw, sh) = canvas.WorldToScreenSize(ww, wh);
        var (ww2, wh2) = canvas.ScreenToWorldSize(sw, sh);
        Assert.InRange(ww2, ww - 1, ww + 1);
        Assert.InRange(wh2, wh - 1, wh + 1);
    }

    // ==================== CAMERA ====================

    [Fact]
    public void Pan_UpdatesCameraPosition()
    {
        var canvas = new Canvas();
        canvas.Pan(100, 50);
        Assert.Equal(-100, canvas.CamX);
        Assert.Equal(-50, canvas.CamY);
    }

    [Fact]
    public void Pan_NegativeValues_PansReverse()
    {
        var canvas = new Canvas();
        canvas.Pan(-60, -40);
        Assert.Equal(60, canvas.CamX);
        Assert.Equal(40, canvas.CamY);
    }

    [Fact]
    public void Pan_Accumulates()
    {
        var canvas = new Canvas();
        canvas.Pan(10, 20);
        canvas.Pan(30, 40);
        Assert.Equal(-40, canvas.CamX);
        Assert.Equal(-60, canvas.CamY);
    }

    [Fact]
    public void CenterOn_CentersRectOnScreen()
    {
        var canvas = new Canvas();
        canvas.CenterOn(1000, 2000, 400, 300, 1920, 1080);

        // The center of the world rect should map to the center of the screen
        var (sx, sy) = canvas.WorldToScreen(1000 + 200, 2000 + 150);
        Assert.InRange(sx, 960 - 1, 960 + 1);
        Assert.InRange(sy, 540 - 1, 540 + 1);
    }

    [Fact]
    public void CenterOn_TinyRect_CentersCorrectly()
    {
        var canvas = new Canvas();
        canvas.CenterOn(5000, 3000, 1, 1, 1920, 1080);
        var (sx, sy) = canvas.WorldToScreen(5000.5, 3000.5);
        Assert.InRange(sx, 960 - 1, 960 + 1);
        Assert.InRange(sy, 540 - 1, 540 + 1);
    }

    [Fact]
    public void CenterOn_AlreadyCentered_NearNoOp()
    {
        var canvas = new Canvas();
        // Place rect so its center is at screen center (960, 540)
        canvas.CenterOn(760, 390, 400, 300, 1920, 1080);
        double camX1 = canvas.CamX, camY1 = canvas.CamY;

        // CenterOn again with same rect
        canvas.CenterOn(760, 390, 400, 300, 1920, 1080);
        Assert.Equal(camX1, canvas.CamX);
        Assert.Equal(camY1, canvas.CamY);
    }

    [Fact]
    public void ResetCamera_RestoresOrigin()
    {
        var canvas = new Canvas();
        canvas.Pan(500, 300);
        canvas.ResetCamera();
        Assert.Equal(0, canvas.CamX);
        Assert.Equal(0, canvas.CamY);
        Assert.Equal(1.0, canvas.Zoom);
    }

    // ==================== STATE ====================

    [Fact]
    public void SaveState_RestoresExactly()
    {
        var canvas = new Canvas();
        canvas.Pan(100, 200);
        canvas.SetWindow((IntPtr)1, 10, 20, 800, 600);
        canvas.SetWindow((IntPtr)2, 500, 100, 400, 300);

        var state = canvas.SaveState();

        canvas.Pan(999, 999);
        canvas.ClearWindows();

        canvas.LoadState(state);

        Assert.Equal(-100, canvas.CamX);
        Assert.Equal(-200, canvas.CamY);
        Assert.Equal(2, canvas.Windows.Count);
        Assert.True(canvas.HasWindow((IntPtr)1));
        Assert.True(canvas.HasWindow((IntPtr)2));
    }

    [Fact]
    public void SaveState_RestoresWorldRectValues()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 123.4, 567.8, 800, 600);
        var state = canvas.SaveState();

        canvas.ClearWindows();
        canvas.LoadState(state);

        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(123.4, world.X);
        Assert.Equal(567.8, world.Y);
        Assert.Equal(800, world.W);
        Assert.Equal(600, world.H);
    }

    [Fact]
    public void LoadState_NullWindows_HandledGracefully()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 0, 0, 100, 100);

        var state = new CanvasState { CamX = 10, CamY = 20, Zoom = 1.0, Windows = null! };
        canvas.LoadState(state);

        Assert.Equal(10, canvas.CamX);
        Assert.Equal(20, canvas.CamY);
        Assert.Empty(canvas.Windows);
    }

    [Fact]
    public void LoadState_ClearsExistingWindows()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 0, 0, 100, 100);
        canvas.SetWindow((IntPtr)2, 0, 0, 100, 100);

        var state = canvas.SaveState();
        // State has windows 1 and 2

        canvas.SetWindow((IntPtr)3, 500, 500, 200, 200);
        // Canvas now has 1, 2, 3

        // Load state with only 1, 2 — window 3 should be gone
        canvas.LoadState(state);
        Assert.Equal(2, canvas.Windows.Count);
        Assert.True(canvas.HasWindow((IntPtr)1));
        Assert.True(canvas.HasWindow((IntPtr)2));
        Assert.False(canvas.HasWindow((IntPtr)3));
    }

    [Fact]
    public void SaveState_CreatesIndependentCopy()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 10, 20, 800, 600);

        var state = canvas.SaveState();
        canvas.SetWindow((IntPtr)1, 999, 999, 100, 100);

        // State should not be affected by subsequent changes
        Assert.Equal(10, state.Windows[(IntPtr)1].X);
    }

    // ==================== WINDOW MAP ====================

    [Fact]
    public void SetWindow_TracksWindow()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)42, 100, 200, 800, 600);

        Assert.True(canvas.HasWindow((IntPtr)42));
        var world = canvas.Windows[(IntPtr)42];
        Assert.Equal(100, world.X);
        Assert.Equal(200, world.Y);
        Assert.Equal(800, world.W);
        Assert.Equal(600, world.H);
    }

    [Fact]
    public void SetWindow_OverwritesExisting()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        canvas.SetWindow((IntPtr)1, 999, 888, 400, 300);

        Assert.Single(canvas.Windows);
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(999, world.X);
        Assert.Equal(888, world.Y);
        Assert.Equal(400, world.W);
        Assert.Equal(300, world.H);
    }

    [Fact]
    public void SetWindowFromScreen_ConvertsToWorld()
    {
        var canvas = new Canvas();
        canvas.Pan(50, 30);
        canvas.SetWindowFromScreen((IntPtr)1, 200, 100, 800, 600);

        var world = canvas.Windows[(IntPtr)1];
        var (expectedX, expectedY) = canvas.ScreenToWorld(200, 100);
        Assert.Equal(expectedX, world.X);
        Assert.Equal(expectedY, world.Y);
    }

    [Fact]
    public void SetWindowFromScreen_ConvertsSizeToWorld()
    {
        var canvas = new Canvas();
        canvas.Pan(50, 30);
        canvas.SetWindowFromScreen((IntPtr)1, 200, 100, 800, 600);

        var world = canvas.Windows[(IntPtr)1];
        var (expectedW, expectedH) = canvas.ScreenToWorldSize(800, 600);
        Assert.Equal(expectedW, world.W);
        Assert.Equal(expectedH, world.H);
    }

    [Fact]
    public void RemoveWindow_RemovesFromMap()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 0, 0, 100, 100);
        Assert.True(canvas.HasWindow((IntPtr)1));

        canvas.RemoveWindow((IntPtr)1);
        Assert.False(canvas.HasWindow((IntPtr)1));
    }

    [Fact]
    public void ClearWindows_RemovesAll()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 0, 0, 100, 100);
        canvas.SetWindow((IntPtr)2, 0, 0, 100, 100);

        canvas.ClearWindows();
        Assert.Empty(canvas.Windows);
    }

    // ==================== WORLD EXTENTS ====================

    [Fact]
    public void GetWorldExtents_EmptyCanvas_ReturnsNull()
    {
        var canvas = new Canvas();
        Assert.Null(canvas.GetWorldExtents());
    }

    [Fact]
    public void GetWorldExtents_SingleWindow_ReturnsBounds()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);

        var ext = canvas.GetWorldExtents();
        Assert.NotNull(ext);
        var (minX, minY, maxX, maxY) = ext.Value;
        Assert.Equal(100, minX);
        Assert.Equal(200, minY);
        Assert.Equal(900, maxX);
        Assert.Equal(800, maxY);
    }

    [Fact]
    public void GetWorldExtents_NegativeCoords_ReturnsCorrectBounds()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, -500, -300, 200, 150);

        var ext = canvas.GetWorldExtents();
        Assert.NotNull(ext);
        var (minX, minY, maxX, maxY) = ext.Value;
        Assert.Equal(-500, minX);
        Assert.Equal(-300, minY);
        Assert.Equal(-300, maxX);
        Assert.Equal(-150, maxY);
    }

    [Fact]
    public void GetWorldExtents_MultipleWindows_ReturnsUnion()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 0, 0, 100, 100);
        canvas.SetWindow((IntPtr)2, 500, 300, 200, 150);

        var ext = canvas.GetWorldExtents();
        Assert.NotNull(ext);
        var (minX, minY, maxX, maxY) = ext.Value;
        Assert.Equal(0, minX);
        Assert.Equal(0, minY);
        Assert.Equal(700, maxX);
        Assert.Equal(450, maxY);
    }

    // ==================== ON-SCREEN CHECK ====================

    [Fact]
    public void IsWindowOnScreen_VisibleWindow_ReturnsTrue()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        Assert.True(canvas.IsWindowOnScreen((IntPtr)1, 1920, 1080));
    }

    [Fact]
    public void IsWindowOnScreen_OffScreenWindow_ReturnsFalse()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 3000, 3000, 400, 300);
        Assert.False(canvas.IsWindowOnScreen((IntPtr)1, 1920, 1080));
    }

    [Fact]
    public void IsWindowOnScreen_PartiallyVisible_ReturnsTrue()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, -200, -100, 400, 300);
        Assert.True(canvas.IsWindowOnScreen((IntPtr)1, 1920, 1080));
    }

    [Fact]
    public void IsWindowOnScreen_AtRightEdge_ReturnsFalse()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 1920, 500, 400, 300);
        Assert.False(canvas.IsWindowOnScreen((IntPtr)1, 1920, 1080));
    }

    [Fact]
    public void IsWindowOnScreen_JustInsideRightEdge_ReturnsTrue()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 1919, 500, 400, 300);
        Assert.True(canvas.IsWindowOnScreen((IntPtr)1, 1920, 1080));
    }

    [Fact]
    public void IsWindowOnScreen_UnknownWindow_ReturnsFalse()
    {
        var canvas = new Canvas();
        Assert.False(canvas.IsWindowOnScreen((IntPtr)99, 1920, 1080));
    }

    // ==================== VIEWPORT ====================

    [Fact]
    public void GetViewport_ReturnsCorrectDimensions()
    {
        var canvas = new Canvas();
        canvas.Pan(100, 50);

        var (x, y, w, h) = canvas.GetViewport(1920, 1080);
        Assert.Equal(canvas.CamX, x);
        Assert.Equal(canvas.CamY, y);
        Assert.Equal(1920.0, w);
        Assert.Equal(1080.0, h);
    }

    [Fact]
    public void GetViewport_AtOrigin_StartsAtZero()
    {
        var canvas = new Canvas();
        var (x, y, w, h) = canvas.GetViewport(1920, 1080);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(1920.0, w);
        Assert.Equal(1080.0, h);
    }

    // ==================== COLLAPSED ====================

    [Fact]
    public void CollapseWindow_MarksAsCollapsed()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        canvas.CollapseWindow((IntPtr)1);
        Assert.True(canvas.IsCollapsed((IntPtr)1));
    }

    [Fact]
    public void ExpandWindow_ClearsCollapsed()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        canvas.CollapseWindow((IntPtr)1);
        canvas.ExpandWindow((IntPtr)1);
        Assert.False(canvas.IsCollapsed((IntPtr)1));
    }

    [Fact]
    public void RemoveWindow_ClearsCollapsed()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        canvas.CollapseWindow((IntPtr)1);
        canvas.RemoveWindow((IntPtr)1);
        Assert.False(canvas.IsCollapsed((IntPtr)1));
    }

    [Fact]
    public void ClearWindows_ClearsCollapsed()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        canvas.CollapseWindow((IntPtr)1);
        canvas.ClearWindows();
        Assert.False(canvas.IsCollapsed((IntPtr)1));
    }

    [Fact]
    public void GetWorldExtents_ExcludesCollapsedWindows()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 0, 0, 100, 100);
        canvas.SetWindow((IntPtr)2, 5000, 5000, 200, 200);
        canvas.CollapseWindow((IntPtr)2);

        var ext = canvas.GetWorldExtents();
        Assert.NotNull(ext);
        var (_, _, maxX, maxY) = ext.Value;
        Assert.Equal(100, maxX);
        Assert.Equal(100, maxY);
    }

    [Fact]
    public void GetWorldExtents_AllCollapsed_ReturnsNull()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 0, 0, 100, 100);
        canvas.CollapseWindow((IntPtr)1);

        Assert.Null(canvas.GetWorldExtents());
    }

    [Fact]
    public void SaveState_PersistsCollapsed()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        canvas.SetWindow((IntPtr)2, 500, 100, 400, 300);
        canvas.CollapseWindow((IntPtr)1);

        var state = canvas.SaveState();

        canvas.ExpandWindow((IntPtr)1);
        canvas.LoadState(state);

        Assert.True(canvas.IsCollapsed((IntPtr)1));
        Assert.False(canvas.IsCollapsed((IntPtr)2));
    }

    [Fact]
    public void LoadState_ClearsOldCollapsed()
    {
        var canvas = new Canvas();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        canvas.CollapseWindow((IntPtr)1);

        // Load state that has no collapsed windows
        var state = new CanvasState
        {
            CamX = 0, CamY = 0, Zoom = 1.0,
            Windows = new() { [(IntPtr)1] = new WorldRect { X = 100, Y = 200, W = 800, H = 600 } }
        };
        canvas.LoadState(state);

        Assert.False(canvas.IsCollapsed((IntPtr)1));
    }
}
