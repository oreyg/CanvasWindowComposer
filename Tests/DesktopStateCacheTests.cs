using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class DesktopStateCacheTests
{
    private sealed class Harness
    {
        public Canvas Canvas = new();
        public FakeWindowApi Api = new();
        public FakeAppConfig Config = new();
        public FakeClock Clock = new();
        public FakeInputRouter Input = new();
        public FakeOverviewController Overview = new();
        public FakeVirtualDesktops Vds = new();
        public WindowManager Wm = null!;
        public DesktopStateCache Cache = null!;

        public Harness(Guid? initialDesktop = null)
        {
            Vds.CurrentDesktopId = initialDesktop ?? Guid.Empty;
            Wm = new WindowManager(Canvas, Api, new DllInjector(), Config, Input, Clock);
            Cache = new DesktopStateCache(Canvas, Wm, Overview, Vds);
        }
    }

    private static readonly Guid GuidA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid GuidB = new("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void Switch_FromEmptyInitial_DoesNotResetWmOrSaveState()
    {
        var h = new Harness(initialDesktop: Guid.Empty);
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        h.Api.AddWindow((IntPtr)1, 100, 100, 800, 600);

        h.Vds.SwitchTo(GuidA);

        // Initial _lastDesktopId was Empty, so Reset shouldn't have run; the
        // window stays in canvas.
        Assert.True(h.Canvas.HasWindow((IntPtr)1));
    }

    [Fact]
    public void Switch_CancelsOverviewInertia()
    {
        var h = new Harness(initialDesktop: GuidA);

        h.Vds.SwitchTo(GuidB);

        Assert.Equal(1, h.Overview.CancelInertiaCalls);
    }

    [Fact]
    public void Switch_FiresAfterStateLoaded()
    {
        var h = new Harness(initialDesktop: GuidA);
        int fired = 0;
        h.Cache.AfterStateLoaded += () => fired++;

        h.Vds.SwitchTo(GuidB);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void SwitchAndReturn_RestoresPanAndZoom()
    {
        var h = new Harness(initialDesktop: GuidA);
        // Pan canvas while on desktop A
        h.Canvas.Pan(50, 30); // CamX=-50, CamY=-30
        double camAx = h.Canvas.CamX;
        double camAy = h.Canvas.CamY;

        // Switch to B then back to A — A's camera should be restored.
        h.Vds.SwitchTo(GuidB);
        h.Vds.SwitchTo(GuidA);

        Assert.Equal(camAx, h.Canvas.CamX);
        Assert.Equal(camAy, h.Canvas.CamY);
    }

    [Fact]
    public void SwitchToNewDesktop_ResetsCanvasCamera()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Canvas.Pan(50, 30);

        // First-time visit to GuidB — no saved state, so camera resets via WM.Reset.
        h.Vds.SwitchTo(GuidB);

        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal(0, h.Canvas.CamY);
        Assert.Equal(1.0, h.Canvas.Zoom);
    }

    [Fact]
    public void SwitchAndReturn_KeepsIndependentStatesPerDesktop()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Canvas.Pan(100, 0);  // A: CamX = -100

        h.Vds.SwitchTo(GuidB);
        h.Canvas.Pan(0, 200); // B: CamY = -200 (CamX 0 from reset)

        h.Vds.SwitchTo(GuidA);
        Assert.Equal(-100, h.Canvas.CamX);
        Assert.Equal(0, h.Canvas.CamY);

        h.Vds.SwitchTo(GuidB);
        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal(-200, h.Canvas.CamY);
    }

    [Fact]
    public void Switch_ResetsWindowManagerState()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        h.Api.AddWindow((IntPtr)1, 100, 100, 400, 300);

        h.Vds.SwitchTo(GuidB);

        // WM.Reset clears the canvas window map (each desktop's windows are
        // re-discovered after the switch).
        Assert.Empty(h.Canvas.Windows);
    }
}
