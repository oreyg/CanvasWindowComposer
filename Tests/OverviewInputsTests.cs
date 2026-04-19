using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewInputsTests
{
    private sealed class Harness
    {
        public Canvas Canvas = new();
        public FakeInputRouter Input = new();
        public FakeOverviewController Overview = new();

        public Harness()
        {
            _ = new OverviewInputs(Overview, Input, Canvas);
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

    // ==================== CAMERA SYNC ====================

    [Fact]
    public void CanvasPan_SyncsOverviewCamera()
    {
        var h = new Harness();
        h.Canvas.Pan(10, 10);

        Assert.Equal(1, h.Overview.SyncCameraCalls);
    }
}
