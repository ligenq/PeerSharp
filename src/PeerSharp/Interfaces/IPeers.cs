namespace PeerSharp.Interfaces;

/// <summary>
/// Provides access to peer connections and peer management for a torrent.
/// </summary>
public interface IPeers
{
    /// <summary>
    /// Gets the number of currently connected peers.
    /// </summary>
    int ConnectedCount { get; }

    /// <summary>
    /// Gets a snapshot of all currently connected peers and their details.
    /// </summary>
    /// <returns>Read-only list of peer information.</returns>
    IReadOnlyList<PeerInfo> GetConnectedPeers();

    /// <summary>
    /// Gets a snapshot of piece availability across all connected peers.
    /// The returned array has one element per piece, where the value is the
    /// number of peers that have that piece.
    /// </summary>
    /// <returns>An array of availability counts.</returns>
    int[] GetPieceAvailability();
}
