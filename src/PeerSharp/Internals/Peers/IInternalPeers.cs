namespace PeerSharp.Internals.Peers;

/// <summary>
/// Internal interface for peer management.
/// </summary>
internal interface IInternalPeers : IPeers
{
    /// <summary>
    /// Handles an incoming TCP connection.
    /// </summary>
    /// <param name="client">The connected TCP client.</param>
    /// <param name="handshake">The received handshake data.</param>
    Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake);

    /// <summary>
    /// Handles an incoming TCP connection with established encryption.
    /// </summary>
    Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake, ProtocolEncryption encryption);

    /// <summary>
    /// Handles an incoming stream connection (e.g., uTP).
    /// </summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="handshake">The received handshake data.</param>
    /// <param name="remote">Optional remote endpoint info.</param>
    Task AddIncomingPeerAsync(Stream stream, byte[] handshake, System.Net.IPEndPoint? remote = null);

    /// <summary>
    /// Attaches an already-connected duplex stream as a BitTorrent peer.
    /// </summary>
    /// <param name="stream">The connected peer stream.</param>
    /// <param name="initiator">True if the local side should initiate the BitTorrent handshake.</param>
    /// <param name="remote">Optional remote endpoint info.</param>
    /// <param name="sourceKind">The source of the peer connection.</param>
    Task AddConnectedPeerAsync(Stream stream, bool initiator, System.Net.IPEndPoint? remote = null, PeerSourceKind sourceKind = PeerSourceKind.Unknown);

    /// <summary>
    /// Adds a list of peers to the peer manager.
    /// </summary>
    /// <param name="peers">List of peer endpoints.</param>
    /// <param name="sourceKind">Source of the peers.</param>
    /// <param name="source">Specific peer that provided these peers.</param>
    void AddPeers(IEnumerable<System.Net.IPEndPoint> peers, PeerSourceKind sourceKind = PeerSourceKind.Unknown, PeerCommunication? source = null);

    /// <summary>
    /// Gets a list of all currently connected peer communications.
    /// </summary>
    IEnumerable<PeerCommunication> GetConnectedPeersInternal();
}
