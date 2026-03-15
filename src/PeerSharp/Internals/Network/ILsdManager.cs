namespace PeerSharp.Internals.Network;

/// <summary>
/// Interface for Local Peer Discovery (LSD/BEP 14).
/// </summary>
internal interface ILsdManager : IAsyncDisposable
{
    /// <summary>
    /// Manually triggers an announcement for a specific torrent.
    /// </summary>
    Task AnnounceAsync(InfoHash infoHash, CancellationToken token = default);

    /// <summary>
    /// Starts the LSD manager.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the LSD manager.
    /// </summary>
    void Stop();
}
