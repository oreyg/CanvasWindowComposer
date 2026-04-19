using System.Collections.Generic;

namespace CanvasDesktop.Tests;

internal sealed class FakeScreens : IScreens
{
    public List<ScreenRect> Bounds = new() { new ScreenRect(0, 0, 1920, 1080) };
    public List<ScreenRect> WorkingAreas = new() { new ScreenRect(0, 0, 1920, 1040) };
    public ScreenRect Virtual = new(0, 0, 1920, 1080);
    public ScreenRect Primary = new(0, 0, 1920, 1080);
    public ScreenRect PrimaryWa = new(0, 0, 1920, 1040);

    public IReadOnlyList<ScreenRect> AllBounds { get { return Bounds; } }
    public IReadOnlyList<ScreenRect> AllWorkingAreas { get { return WorkingAreas; } }
    public ScreenRect VirtualScreen { get { return Virtual; } }
    public ScreenRect PrimaryBounds { get { return Primary; } }
    public ScreenRect PrimaryWorkingArea { get { return PrimaryWa; } }
}
