namespace PeerSharp.Internals.Peers;

/// <summary>
/// A global governor for peer connections, inspired by libtransmission's central session management.
/// Ensures that global resource limits are respected across all active torrents.
/// </summary>
internal interface IConnectionGovernor
{
    /// <summary>
    /// Current number of active connections.
    /// </summary>
    int ActiveConnections { get; }

    /// <summary>
    /// Current number of pending connections.
    /// </summary>
    int PendingConnections { get; }

    /// <summary>
    /// Releases a previously acquired connection slot.
    /// </summary>
    void ReleaseConnectionSlot();

    /// <summary>
    /// Releases a previously acquired pending connection slot.
    /// </summary>
    void ReleasePendingSlot();

    /// <summary>
    /// Attempts to acquire a slot for a new connection.
    /// </summary>
    /// <returns>True if a slot was acquired, false if the global limit is reached.</returns>
    bool TryAcquireConnectionSlot();

    /// <summary>
    /// Attempts to acquire a slot for a new pending (half-open) connection.
    /// </summary>
    /// <returns>True if a slot was acquired, false if the global limit is reached.</returns>
    bool TryAcquirePendingSlot();
}
