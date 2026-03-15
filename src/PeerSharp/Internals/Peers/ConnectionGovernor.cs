namespace PeerSharp.Internals.Peers;

internal sealed class ConnectionGovernor : IConnectionGovernor
{
    private readonly Settings _settings;
    private int _activeConnections;
    private int _pendingConnections;

    public ConnectionGovernor(Settings settings)
    {
        _settings = settings;
    }

    public int ActiveConnections => Interlocked.CompareExchange(ref _activeConnections, 0, 0);
    public int PendingConnections => Interlocked.CompareExchange(ref _pendingConnections, 0, 0);

    public void ReleaseConnectionSlot()
    {
        int newVal = Interlocked.Decrement(ref _activeConnections);
        if (newVal < 0)
        {
            Interlocked.Exchange(ref _activeConnections, 0);
        }
    }

    public void ReleasePendingSlot()
    {
        int newVal = Interlocked.Decrement(ref _pendingConnections);
        if (newVal < 0)
        {
            Interlocked.Exchange(ref _pendingConnections, 0);
        }
    }

    public bool TryAcquireConnectionSlot()
    {
        int current = Interlocked.CompareExchange(ref _activeConnections, 0, 0);
        if (current >= _settings.Connection.MaxConnections)
        {
            return false;
        }

        Interlocked.Increment(ref _activeConnections);
        return true;
    }

    public bool TryAcquirePendingSlot()
    {
        int current = Interlocked.CompareExchange(ref _pendingConnections, 0, 0);
        if (current >= _settings.Connection.MaxPendingConnections)
        {
            return false;
        }

        Interlocked.Increment(ref _pendingConnections);
        return true;
    }
}
