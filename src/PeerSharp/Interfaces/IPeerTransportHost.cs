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
    /// Gets the total bytes downloaded since the host was started. Mirrors
    /// <c>Torrent.TotalDownloaded</c> and is used to compute per-session deltas for tracker
    /// announces.
    /// </summary>
    long DataDownloaded { get; }

    /// <summary>
    /// Gets the total bytes uploaded since the host was started. Mirrors
    /// <c>Torrent.TotalUploaded</c> and is used to compute per-session deltas for tracker
    /// announces.
    /// </summary>
    long DataUploaded { get; }

    /// <summary>
    /// Gets the local peer ID advertised to trackers and peers.
    /// </summary>
    ReadOnlyMemory<byte> PeerId { get; }

    /// <summary>
    /// Attaches an already-connected peer transport to the torrent.
    /// </summary>
    /// <param name="stream">
    /// The connected duplex stream. The torrent takes ownership of it: it is closed if the
    /// connection is rejected (peer limits, blocklist, forced proxy) or if the attach is
    /// cancelled.
    /// </param>
    /// <param name="initiator">True when the local side should send the BitTorrent handshake first.</param>
    /// <param name="cancellationToken">
    /// Cancels the attach itself. Once the peer has been handed the stream its handshake is
    /// governed by the peer connection's own lifetime, which ends when the torrent stops.
    /// </param>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the token is cancelled before the peer takes ownership of the stream. The
    /// stream is closed in that case.
    /// </exception>
    Task AttachPeerTransportAsync(Stream stream, bool initiator, CancellationToken cancellationToken = default);
}
