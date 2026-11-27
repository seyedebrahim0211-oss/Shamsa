using System.Collections.Concurrent;

namespace Shamsa.Services;

/// <summary>Dedicated STA for Shell COM. When work is superseded its caller still receives a result.</summary>
public sealed class StaWorker : IDisposable
{
    private sealed record Work(Action Run, Action Skip);
    private readonly BlockingCollection<Work> _queue = new(boundedCapacity: 8);
    private readonly object _gate = new();
    private bool _disposed;

    public StaWorker(string name = "Shamsa.StaWorker")
    {
        var thread = new Thread(RunLoop) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed) return Task.FromCanceled<T>(new CancellationToken(true));
            while (_queue.Count >= 4 && _queue.TryTake(out var stale)) stale.Skip();
            var work = new Work(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, () => tcs.TrySetResult(default!));
            if (!_queue.TryAdd(work)) work.Skip();
        }
        return tcs.Task;
    }

    private void RunLoop()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work.Run();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            while (_queue.TryTake(out var pending)) pending.Skip();
        }
    }
}
