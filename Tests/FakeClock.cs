namespace CanvasDesktop.Tests;

internal sealed class FakeClock : IClock
{
    public long Now;

    public FakeClock(long start = 0)
    {
        Now = start;
    }

    public long TickCount64
    {
        get { return Now; }
    }

    public void Advance(long ms)
    {
        Now += ms;
    }
}
