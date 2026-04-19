using System;
using System.Collections.Generic;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// Offloads the SetWindowPos phase of reprojection to a dedicated thread so the
/// UI thread isn't blocked waiting on slow-drawing windows to ack their moves.
///
/// Single-producer / single-consumer: UI thread builds a batch and calls
/// Schedule; the worker thread consumes the latest scheduled batch via a
/// volatile reference swap and applies it with BatchMove(sync). Newer batches
/// replace older ones that haven't been consumed yet, so the worker always
/// converges on the most recent canvas state.
/// </summary>
internal sealed class ProjectionWorker : IDisposable
{
    private readonly IWindowApi _pos;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _signal = new(false);
    private volatile bool _alive = true;
    private Job? _pending;

    private sealed class Job
    {
        public required List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)> Items;
        public bool IsTransient;
        public bool IsAsync;
    }

    public ProjectionWorker(IWindowApi pos)
    {
        _pos = pos;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Projection"
        };
        _thread.Start();
    }

    /// <summary>UI thread: hand off the latest batch. Overwrites any earlier pending batch.</summary>
    public void Schedule(
        List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)> items,
        bool isAsync,
        bool isTransient)
    {
        Volatile.Write(ref _pending, new Job { Items = items, IsAsync = isAsync, IsTransient = isTransient });
        _signal.Set();
    }

    /// <summary>Drop any batch that hasn't been applied yet (used before sync Reset).</summary>
    public void ClearPending()
    {
        Interlocked.Exchange(ref _pending, null);
    }

    private void Loop()
    {
        while (_alive)
        {
            _signal.Wait();
            _signal.Reset();
            if (!_alive) break;

            Job? job = Interlocked.Exchange(ref _pending, null);
            if (job != null)
                _pos.BatchMove(job.Items, isAsync: job.IsAsync, isTransient: job.IsTransient);
        }
    }

    public void Dispose()
    {
        _alive = false;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        _signal.Dispose();
    }
}
