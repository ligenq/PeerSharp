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
    /// Offers peers to connect to, in addition to whatever discovery finds.
    ///
    /// <para>
    /// This is the "add peer" every desktop client exposes, and the only way to reach a peer that no
    /// tracker, DHT node or exchange has mentioned - a machine on the same LAN, a known seedbox, or a
    /// second instance during testing.
    /// </para>
    ///
    /// <para>
    /// Offered rather than forced: an address still has to pass the blocklist, the connection limits
    /// and the same duplicate checks as any other candidate, and it joins the ordinary connection
    /// queue rather than pre-empting it. The return value is how many were accepted as new candidates,
    /// not how many connected.
    /// </para>
    /// </summary>
    /// <param name="endpoints">Peer addresses to try.</param>
    /// <returns>How many were accepted as new candidates.</returns>
    int Add(IEnumerable<System.Net.IPEndPoint> endpoints);

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
