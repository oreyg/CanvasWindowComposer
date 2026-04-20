using System.Threading;

namespace CanvasDesktop;

internal enum MouseEventType : byte
{
    DragStarted,
    DragEnded,
    Pan,
    ButtonDown,
    Zoom,
}

internal readonly struct MouseEvent
{
    public readonly MouseEventType Type;
    public readonly int Dx;
    public readonly int Dy;

    public MouseEvent(MouseEventType type, int dx = 0, int dy = 0)
    {
        Type = type;
        Dx = dx;
        Dy = dy;
    }
}

/// <summary>
/// Single-producer / single-consumer ring buffer.
/// Producer = raw input polling thread. Consumer = UI thread drain.
/// Fixed capacity (must be power of two); silently drops on overflow.
/// </summary>
internal sealed class MouseEventRingBuffer
{
    private readonly MouseEvent[] _buffer;
    private readonly int _mask;
    private int _writeIndex;
    private int _readIndex;

    public MouseEventRingBuffer(int capacityPow2 = 256)
    {
        if ((capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new System.ArgumentException("capacity must be a power of two", nameof(capacityPow2));
        _buffer = new MouseEvent[capacityPow2];
        _mask = capacityPow2 - 1;
    }

    /// <summary>Producer: append an event. Returns false if full (event dropped).</summary>
    public bool TryEnqueue(in MouseEvent evt)
    {
        int w = _writeIndex;
        int next = (w + 1) & _mask;
        if (next == Volatile.Read(ref _readIndex))
            return false;
        _buffer[w] = evt;
        Volatile.Write(ref _writeIndex, next);
        return true;
    }

    /// <summary>Consumer: pull the next event. Returns false if empty.</summary>
    public bool TryDequeue(out MouseEvent evt)
    {
        int r = _readIndex;
        if (r == Volatile.Read(ref _writeIndex))
        {
            evt = default;
            return false;
        }
        evt = _buffer[r];
        Volatile.Write(ref _readIndex, (r + 1) & _mask);
        return true;
    }
}
