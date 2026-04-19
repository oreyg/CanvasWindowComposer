using System;

namespace CanvasDesktop;

/// <summary>
/// Abstracts the monotonic millisecond tick source so timing-dependent
/// logic (inertia decay, reload debounce, foreground suppression) can be
/// driven deterministically in tests.
/// </summary>
internal interface IClock
{
    long TickCount64 { get; }
}

internal sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public long TickCount64
    {
        get { return Environment.TickCount64; }
    }
}
