namespace PeerSharp.Core;

internal struct AtomicDisposal
{
    private int _disposed;

    public bool MarkDisposed()
    {
        return Interlocked.Exchange(ref _disposed, 1) == 0;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public void ThrowIfDisposed(object instance) => ObjectDisposedException.ThrowIf(IsDisposed, instance);
}
