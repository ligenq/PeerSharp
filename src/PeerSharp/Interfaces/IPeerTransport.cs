namespace PeerSharp.Interfaces;

/// <summary>
/// Optional peer transport that supplies peer connections to a torrent. Extensions such as
/// PeerSharp.WebTorrent register their own transport via <see cref="ITorrent.RegisterPeerTransport"/>;
/// the torrent then drives its lifetime alongside its built-in transports.
/// </summary>
public interface IPeerTransport : IAsyncDisposable
{
    /// <summary>
    /// Starts the transport. Called by the torrent when it transitions to active.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the transport. Called by the torrent when it transitions to stopped.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
