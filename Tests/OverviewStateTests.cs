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
    public void SetMode_ToDifferentMode_ReturnsTrueAndUpdatesConfig()
    {
        var s = new OverviewState();
        Assert.True(s.SetMode(OverviewMode.Panning));
        Assert.Equal(OverviewMode.Panning, s.CurrentMode);
        Assert.Equal(OverviewState.PanningConfig, s.CurrentConfig);
    }

    [Fact]
    public void ConfigFor_ReturnsExpectedConfig()
    {
        Assert.Equal(OverviewState.HiddenConfig, OverviewState.ConfigFor(OverviewMode.Hidden));
        Assert.Equal(OverviewState.PanningConfig, OverviewState.ConfigFor(OverviewMode.Panning));
        Assert.Equal(OverviewState.ZoomingConfig, OverviewState.ConfigFor(OverviewMode.Zooming));
    }
}
