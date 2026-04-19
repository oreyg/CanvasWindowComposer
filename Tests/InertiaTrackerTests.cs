using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class InertiaTrackerTests
{
    [Fact]
    public void Release_NoSamples_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        Assert.False(tracker.Release());
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Release_SingleSample_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        tracker.RecordDelta(10, 0);
        Assert.False(tracker.Release());
    }

    [Fact]
    public void Release_FastSamples_StartsInertia()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // 10px every 16ms → ~0.625 px/ms, well above 0.02 stop threshold
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(10, 5);
            clock.Advance(16);
        }

        Assert.True(tracker.Release());
        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void Release_StaleSamplesOutsideWindow_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        tracker.RecordDelta(10, 10);
        tracker.RecordDelta(10, 10);
        // Velocity window is 100ms; jumping forward 500ms drops both samples.
        clock.Advance(500);

        Assert.False(tracker.Release());
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Release_SlowSamples_BelowThreshold_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // 1px every 100ms → 0.01 px/ms, below 0.02 stop threshold
        tracker.RecordDelta(1, 0);
        clock.Advance(100);
        tracker.RecordDelta(1, 0);
        clock.Advance(100);

        Assert.False(tracker.Release());
    }

    [Fact]
    public void Tick_BeforeRelease_ReturnsZeroAndInactive()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        var (dx, dy, stopped) = tracker.Tick();
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
        Assert.False(stopped);
    }

    [Fact]
    public void Tick_DecaysVelocityOverTime()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // Build up strong velocity: 30px/16ms ~= 1.875 px/ms
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(30, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());

        clock.Advance(16);
        var first = tracker.Tick();

        clock.Advance(16);
        var second = tracker.Tick();

        // Decay is multiplicative — second tick must move less than the first.
        Assert.True(Math.Abs(second.dx) < Math.Abs(first.dx),
            $"expected decay: first={first.dx} second={second.dx}");
    }

    [Fact]
    public void Tick_EventuallyStops()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(20, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());

        bool stopped = false;
        for (int i = 0; i < 1000 && !stopped; i++)
        {
            clock.Advance(16);
            stopped = tracker.Tick().stopped;
        }

        Assert.True(stopped, "inertia never decayed below stop threshold");
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Cancel_StopsInertiaImmediately()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(20, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());
        Assert.True(tracker.IsActive);

        tracker.Cancel();

        Assert.False(tracker.IsActive);
        var (dx, dy, _) = tracker.Tick();
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void RecordDelta_BeyondSampleWindow_TrimsOldest()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // Push 30 samples — old ones beyond SampleWindow=15 get dropped, but the
        // logic still releases on the recent ones.
        for (int i = 0; i < 30; i++)
        {
            tracker.RecordDelta(15, 0);
            clock.Advance(8);
        }

        Assert.True(tracker.Release());
    }
}
