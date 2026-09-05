namespace PeerSharp.Internals.Framework;

/// <summary>A live concurrency limit. Lowering it lets current work drain without revoking permits.</summary>
internal sealed class AdjustableConcurrencyLimiter : IDisposable
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _changed = NewSignal();
    private int _limit;
    private int _active;
    private bool _disposed;

    public AdjustableConcurrencyLimiter(int limit) => _limit = Math.Max(1, limit);

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_active < _limit)
                {
                    _active++;
                    return;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void SetLimit(int limit)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _limit = Math.Max(1, limit);
            Pulse();
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_active == 0) throw new SemaphoreFullException();
            _active--;
            Pulse();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Pulse();
        }
    }

    private void Pulse()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
