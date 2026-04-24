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
    private readonly IWindowApi _win32;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _signal = new(false);
    // Held while the worker is inside _win32.BatchMove. ClearPending grabs
    // this briefly after cancelling so the follow-up sync BatchMove can't
    // race the in-flight worker batch on the same HWNDs.
    private readonly object _processLock = new();
    // Doubles as the shutdown signal AND the in-flight-batch canceller.
    // ClearPending cancels + replaces with a fresh CTS so the next batch has
    // a live token; Dispose cancels without replacing.
    private CancellationTokenSource _cts = new();
    private volatile bool _disposed;
    private Job? _pending;

    private sealed class Job
    {
        public required List<BatchMoveItem> Items;
        public bool IsTransient;
        public bool IsAsync;
    }

    public ProjectionWorker(IWindowApi win32)
    {
        _win32 = win32;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Projection"
        };
        _thread.Start();
    }

    /// <summary>UI thread: hand off the latest batch. Overwrites any earlier pending batch.</summary>
    public void Schedule(
        List<BatchMoveItem> items,
        bool isAsync,
        bool isTransient)
    {
        Volatile.Write(ref _pending, new Job { Items = items, IsAsync = isAsync, IsTransient = isTransient });
        _signal.Set();
    }

    /// <summary>
    /// Drop any queued batch and cancel the in-flight one if any. The
    /// in-flight BatchMove exits between items instead of finalizing -
    /// callers that follow up with their own sync BatchMove get to run
    /// without waiting for the full batch to complete.
    /// </summary>
    public void ClearPending()
    {
        var prev = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        prev.Cancel();
        prev.Dispose();
        lock (_processLock)
        {
            Interlocked.Exchange(ref _pending, null);
        }
    }

    private void Loop()
    {
        while (!_disposed)
        {
            CancellationTokenSource cts = _cts;
            try
            {
                _signal.Wait(cts.Token);
            }
            catch (OperationCanceledException)
            {
                continue; // _cts was swapped or disposed — re-read next iteration.
            }
            _signal.Reset();
            if (_disposed) break;
            lock (_processLock)
            {
                Job? job = Interlocked.Exchange(ref _pending, null);
                if (job != null && !cts.IsCancellationRequested && !_disposed)
                    _win32.BatchMove(job.Items, isAsync: job.IsAsync, isTransient: job.IsTransient, ct: cts.Token);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(1));
        _signal.Dispose();
        _cts.Dispose();
    }
}
