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
        ReleaseSlot(ref _activeConnections);
    }

    public void ReleasePendingSlot()
    {
        ReleaseSlot(ref _pendingConnections);
    }

    public bool TryAcquireConnectionSlot()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeConnections);
            if (current >= _settings.Connection.MaxConnections)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeConnections, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public bool TryAcquirePendingSlot()
    {
        while (true)
        {
            int current = Volatile.Read(ref _pendingConnections);
            if (current >= _settings.Connection.MaxPendingConnections)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pendingConnections, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private static void ReleaseSlot(ref int slots)
    {
        while (true)
        {
            int current = Volatile.Read(ref slots);
            if (current == 0 || Interlocked.CompareExchange(ref slots, current - 1, current) == current)
            {
                return;
            }
        }
    }
}
