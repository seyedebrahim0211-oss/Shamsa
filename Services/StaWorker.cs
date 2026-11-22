using System.Collections.Concurrent;

namespace Shamsa.Services;

/// <summary>
/// Dedicated single-threaded apartment (STA) worker for Shell.Application COM and UI Automation.
/// Calling STA COM servers from MTA ThreadPool threads forces cross-apartment marshaling and stalls;
/// running them on a persistent STA thread is 3x-5x faster and prevents thread-pool starvation.
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(boundedCapacity: 8);
    private readonly Thread _thread;
    private volatile bool _disposed;

    public StaWorker(string name = "Shamsa.StaWorker")
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = name
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            tcs.TrySetCanceled();
            return tcs.Task;
        }

        // Drop stale queued items if the worker is momentarily busy so we always process the latest cursor pos
        while (_queue.Count >= 4 && _queue.TryTake(out _)) { }

        if (!_queue.TryAdd(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetResult(default!);
        }

        return tcs.Task;
    }

    private void RunLoop()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;
                try { work(); }
                catch (Exception ex) { AppLog.Warn("StaWorker item failed: " + ex.Message); }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
    }
}
