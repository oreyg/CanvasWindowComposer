using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewCameraTests
{
    private static OverviewCamera MakeCamera()
    {
        var screens = new FakeScreens();
        screens.Virtual = new ScreenRect(0, 0, 1920, 1080);
        return new OverviewCamera(screens);
    }

    [Fact]
    public void NewCamera_StartsAtOriginUnzoomed()
    {
        var cam = MakeCamera();
        Assert.Equal(0, cam.X);
        Assert.Equal(0, cam.Y);
        Assert.Equal(1.0, cam.Zoom);
    }

    [Fact]
    public void SetTo_AssignsAllThree()
    {
        var cam = MakeCamera();
        cam.SetTo(100, 200, 0.5);
        Assert.Equal(100, cam.X);
        Assert.Equal(200, cam.Y);
        Assert.Equal(0.5, cam.Zoom);
    }

    [Fact]
    public void SyncFrom_CopiesCanvasState()
    {
        var canvas = new Canvas();
        canvas.Pan(50, 30); // shifts cam by -50, -30

        var cam = MakeCamera();
        cam.SyncFrom(canvas);
        Assert.Equal(canvas.CamX, cam.X);
        Assert.Equal(canvas.CamY, cam.Y);
        Assert.Equal(canvas.Zoom, cam.Zoom);
    }

    [Fact]
    public void PanByVirtual_AtZoom1_TranslatesByPixels()
    {
        var cam = MakeCamera();
        cam.PanByVirtual(40, -20);
        Assert.Equal(-40, cam.X);
        Assert.Equal(20, cam.Y);
    }

    [Fact]
    public void PanByVirtual_AtZoomHalf_DoublesEffectiveDelta()
    {
        var cam = MakeCamera();
        cam.SetTo(0, 0, 0.5);
        cam.PanByVirtual(40, 0);
        // dx / zoom = 40 / 0.5 = 80
        Assert.Equal(-80, cam.X);
    }

    [Fact]
    public void ZoomToCursor_ChangesZoomAndKeepsCursorFixed()
    {
        var cam = MakeCamera();
        cam.SetTo(0, 0, 0.5);

        // Pre-zoom: world point under cursor (100, 100)
        var (preWx, preWy) = cam.WorldFromVirtual(100, 100);

        bool changed = cam.ZoomToCursor(100, 100, 1);
        Assert.True(changed);

        // Post-zoom: world point under same cursor pixel should be unchanged
        var (postWx, postWy) = cam.WorldFromVirtual(100, 100);
        Assert.InRange(Math.Abs(preWx - postWx), 0, 1e-9);
        Assert.InRange(Math.Abs(preWy - postWy), 0, 1e-9);
    }

    [Fact]
    public void ZoomToCursor_ClampsToZoomMin()
    {
        var cam = MakeCamera();
        cam.SetTo(0, 0, OverviewCamera.ZoomMin);
        bool changed = cam.ZoomToCursor(0, 0, -100);
        Assert.False(changed);
        Assert.Equal(OverviewCamera.ZoomMin, cam.Zoom);
    }

    [Fact]
    public void ZoomToCursor_ClampsToZoomMax()
    {
        var cam = MakeCamera();
        cam.SetTo(0, 0, OverviewCamera.ZoomMax);
        bool changed = cam.ZoomToCursor(0, 0, 100);
        Assert.False(changed);
        Assert.Equal(OverviewCamera.ZoomMax, cam.Zoom);
    }

    [Fact]
    public void CenterOnWorld_PutsRectCenterAtScreenCenter()
    {
        var cam = MakeCamera();
        cam.CenterOnWorld(1000, 2000, 400, 300);

        // The center of the world rect (1200, 2150) maps to which virtual pixel?
        // virtual = (world - cam) * zoom
        int vx = (int)((1200 - cam.X) * cam.Zoom);
        int vy = (int)((2150 - cam.Y) * cam.Zoom);
        Assert.InRange(vx, 1920 / 2 - 1, 1920 / 2 + 1);
        Assert.InRange(vy, 1080 / 2 - 1, 1080 / 2 + 1);
    }

    [Fact]
    public void WorldFromVirtual_RoundtripsThroughCamera()
    {
        var cam = MakeCamera();
        cam.SetTo(123, -45, 0.75);
        var (wx, wy) = cam.WorldFromVirtual(200, 100);
        // Inverse: vx = (wx - X) * Zoom
        int vxBack = (int)((wx - cam.X) * cam.Zoom);
        int vyBack = (int)((wy - cam.Y) * cam.Zoom);
        Assert.InRange(vxBack, 199, 201);
        Assert.InRange(vyBack, 99, 101);
    }

    [Fact]
    public void ViewportCamera_AtZoom1_EqualsCameraPosition()
    {
        var cam = MakeCamera();
        cam.SetTo(123, 456, 1.0);
        var (vx, vy) = cam.ViewportCamera;
        Assert.Equal(123, vx);
        Assert.Equal(456, vy);
    }

    [Fact]
    public void ViewportCamera_AtZoomHalf_OffsetsByHalfVirtualScreen()
    {
        var cam = MakeCamera();
        cam.SetTo(0, 0, 0.5);
        var (vx, vy) = cam.ViewportCamera;
        // ox = 1920 * (2 - 1) / 2 = 960
        // oy = 1080 * (2 - 1) / 2 = 540
        Assert.Equal(960, vx);
        Assert.Equal(540, vy);
    }
}
