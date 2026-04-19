namespace PeerSharp.Interfaces;

/// <summary>
/// Minimal host surface required by peer-transport adapters that attach an already-connected duplex stream.
/// </summary>
public interface IPeerTransportHost
{
    /// <summary>
    /// Gets the torrent info hash used for peer routing and transport-specific signaling.
    /// </summary>
    InfoHash Hash { get; }

    /// <summary>
    /// Gets the remaining bytes to download.
    /// </summary>
    long DataLeft { get; }

    /// <summary>
     /// Gets the local peer ID advertised to trackers and peers.
     /// </summary>
    ReadOnlyMemory<byte> PeerId { get; }

    /// <summary>
    /// Attaches an already-connected peer transport to the torrent.
    /// </summary>
    /// <param name="stream">The connected duplex stream.</param>
    /// <param name="initiator">True when the local side should send the BitTorrent handshake first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AttachPeerTransportAsync(Stream stream, bool initiator, CancellationToken cancellationToken = default);
}
