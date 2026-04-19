using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewStateTests
{
    [Fact]
    public void NewState_StartsHidden()
    {
        var s = new OverviewState();
        Assert.Equal(OverviewMode.Hidden, s.CurrentMode);
        Assert.Equal(OverviewState.HiddenConfig, s.CurrentConfig);
    }

    [Fact]
    public void SetMode_ToSameMode_ReturnsFalse()
    {
        var s = new OverviewState();
        Assert.False(s.SetMode(OverviewMode.Hidden));
    }

    [Fact]
    public void SetMode_ToDifferentMode_ReturnsTrue()
    {
        var s = new OverviewState();
        Assert.True(s.SetMode(OverviewMode.Panning));
        Assert.Equal(OverviewMode.Panning, s.CurrentMode);
    }

    [Fact]
    public void SetMode_PanningSelectsPanningConfig()
    {
        var s = new OverviewState();
        s.SetMode(OverviewMode.Panning);
        Assert.Equal(OverviewState.PanningConfig, s.CurrentConfig);
    }

    [Fact]
    public void SetMode_ZoomingSelectsZoomingConfig()
    {
        var s = new OverviewState();
        s.SetMode(OverviewMode.Zooming);
        Assert.Equal(OverviewState.ZoomingConfig, s.CurrentConfig);
    }

    [Fact]
    public void SetMode_HiddenAfterPanning_RestoresHiddenConfig()
    {
        var s = new OverviewState();
        s.SetMode(OverviewMode.Panning);
        s.SetMode(OverviewMode.Hidden);
        Assert.Equal(OverviewState.HiddenConfig, s.CurrentConfig);
    }

    [Fact]
    public void Configs_HavePolicyDifferences()
    {
        Assert.False(OverviewState.HiddenConfig.GridVisible);
        Assert.True(OverviewState.ZoomingConfig.GridVisible);
        Assert.True(OverviewState.PanningConfig.InertiaAllowed);
        Assert.False(OverviewState.ZoomingConfig.InertiaAllowed);
        Assert.False(OverviewState.PanningConfig.InputEnabled);
        Assert.True(OverviewState.ZoomingConfig.InputEnabled);
        Assert.True(OverviewState.PanningConfig.TaskbarVisible);
        Assert.False(OverviewState.ZoomingConfig.TaskbarVisible);
    }

    [Fact]
    public void ConfigFor_ReturnsExpectedConfig()
    {
        Assert.Equal(OverviewState.HiddenConfig, OverviewState.ConfigFor(OverviewMode.Hidden));
        Assert.Equal(OverviewState.PanningConfig, OverviewState.ConfigFor(OverviewMode.Panning));
        Assert.Equal(OverviewState.ZoomingConfig, OverviewState.ConfigFor(OverviewMode.Zooming));
    }
}
