namespace CanvasDesktop.Tests;

internal sealed class FakeAppConfig : IAppConfig
{
    public bool DisableSearch { get; set; }
    public bool DisableAltPan { get; set; }
    public bool DisableGreedyDraw { get; set; }
    public bool ShowScreenFixedWindowsDuringPan { get; set; } = true;
    public bool DisableMouseCurve { get; set; }
    public bool DisableZoomHotkey { get; set; }
}
