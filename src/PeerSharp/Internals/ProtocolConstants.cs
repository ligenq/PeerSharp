namespace PeerSharp.Internals;

/// <summary>
/// Centralized constants for BitTorrent protocol parameters.
/// Using named constants improves code readability and maintainability.
/// </summary>
internal static class ProtocolConstants
{
    #region Block and Message Sizes

    /// <summary>
    /// Standard block size for piece transfers (16KB).
    /// This is the de facto standard in BitTorrent clients.
    /// </summary>
    public const int BlockSize = 16 * 1024; // 16KB

    /// <summary>
    /// Download batch size for bandwidth-limited reads (256KB).
    /// </summary>
    public const int DownloadBatchSize = 256 * 1024;

    /// <summary>
    /// Maximum message size to prevent DoS attacks (2MB).
    /// </summary>
    public const int MaxMessageSize = 2 * 1024 * 1024; // 2MB

    /// <summary>
    /// Metadata piece size for BEP-9 ut_metadata extension (16KB).
    /// </summary>
    public const int MetadataPieceSize = 16 * 1024; // 16KB

    // 256KB

    /// <summary>
    /// Upload batch size for bandwidth-limited writes (64KB).
    /// </summary>
    public const int UploadBatchSize = 64 * 1024; // 64KB

    /// <summary>
    /// How many block requests we will hold from one peer before rejecting the rest, and the value we
    /// advertise as <c>reqq</c> in the BEP 10 extended handshake. These must be the same number: the
    /// point of advertising is to tell the peer what we will actually accept.
    ///
    /// <para>
    /// A peer that is not told assumes something. Transmission assumes 500 and libtorrent advertises
    /// 2000, so staying silent means most peers send more than we will take and we reject the excess -
    /// a wasted round trip per rejected request, against every client rather than any particular one.
    /// </para>
    ///
    /// <para>
    /// The depth also bounds how much work one peer can have outstanding with us, which is what limits
    /// throughput against a peer that only refills its request window on a timer - Transmission 4.1.3
    /// does so every 500ms, so at 250 we answered a batch in about 20ms and then sat idle for the rest
    /// of the pulse. Raising this to 2000 took seeding to Transmission from 7.4 to 31.6 MiB/s.
    /// </para>
    ///
    /// <para>
    /// It is cheap, which is not obvious and was worth measuring: the queue is a bounded channel of
    /// 12-byte descriptors and block data is read lazily as each is served, so depth buys descriptors
    /// rather than buffers - about 23 KiB per peer here, or 4 MiB across two hundred of them. In-flight
    /// block data is bounded by the send queue instead, independently of this. <c>ManyPeerSoakTests</c>
    /// is the harness for re-checking that if this changes again; at 24 peers, 250 and 2000 are
    /// indistinguishable in both throughput and heap.
    /// </para>
    /// </summary>
    public const int MaxOutstandingRequestsPerPeer = 2000;

    #endregion Block and Message Sizes

    #region Connection Timeouts

    /// <summary>
    /// Timeout for individual block requests (8 seconds).
    /// Reduced from 10s for faster recovery from unresponsive peers.
    /// </summary>
    public const int BlockRequestTimeoutMs = 8000;

    /// <summary>
    /// Timeout for establishing TCP/uTP connections (10 seconds).
    /// </summary>
    public const int ConnectionTimeoutMs = 10000;

    /// <summary>
    /// Timeout for first read during handshake (5 seconds).
    /// Shorter than regular timeout to fail fast on dead connections.
    /// </summary>
    public const int FirstReadTimeoutMs = 5000;

    /// <summary>
    /// Timeout for handshake operations (10 seconds).
    /// </summary>
    public const int HandshakeTimeoutMs = 10000;

    /// <summary>
    /// HTTP tracker request timeout (15 seconds).
    /// </summary>
    public const int HttpTrackerTimeoutSeconds = 15;

    /// <summary>
    /// Idle timeout before closing connection (2 minutes). Matches libtorrent's peer_timeout.
    /// </summary>
    public const int IdleTimeoutMs = 120000;

    /// <summary>
    /// How long a connection may go without us sending anything before we send a keepalive.
    ///
    /// <para>
    /// Must stay comfortably below the two minutes other clients allow, or an otherwise healthy
    /// connection is dropped by the remote for looking dead. Transmission uses 100 seconds for the same
    /// reason and libtorrent's peer_timeout is 120, so this matches the stricter of the two.
    /// </para>
    /// </summary>
    public const int KeepAliveIntervalMs = 100000;

    /// <summary>
    /// How long a uTP connection may receive nothing before the transport tears it down.
    ///
    /// <para>
    /// Deliberately longer than <see cref="IdleTimeoutMs"/>. A transport that gives up sooner than the
    /// protocol riding on it pre-empts a decision that is not its to make: this was 60 seconds, so a
    /// quiet peer was killed at the transport before the peer layer's own two-minute policy applied,
    /// and well before a remote keepalive at 100 seconds could arrive.
    /// </para>
    /// </summary>
    public const int UtpInactivityTimeoutMs = 180000;

    /// <summary>
    /// Timeout for pending connection cleanup (10 seconds).
    /// </summary>
    public const int PendingConnectionTimeoutMs = 10000;

    /// <summary>
    /// Timeout for send queue operations (30 seconds).
    /// </summary>
    public const int SendQueueTimeoutMs = 30000;

    /// <summary>
    /// Timeout for subsequent reads during handshake (30 seconds).
    /// </summary>
    public const int SubsequentReadTimeoutMs = 30000;

    #endregion Connection Timeouts

    #region Rate Limiting

    /// <summary>
    /// Maximum messages per minute to prevent DoS (5000).
    /// </summary>
    public const int MaxMessagesPerMinute = 5000;

    /// <summary>
    /// Maximum RTT value for timeout calculations (30 seconds).
    /// </summary>
    public const int MaxRttMs = 30000;

    /// <summary>
    /// Maximum RTT value for smoothing (5 seconds).
    /// </summary>
    public const int MaxSmoothedRttMs = 5000;

    /// <summary>
    /// Minimum RTT value for timeout calculations (10ms).
    /// </summary>
    public const int MinRttMs = 10;

    /// <summary>
    /// Rate limiting window duration (1 minute).
    /// </summary>
    public const int RateLimitWindowMs = 60000;

    #endregion Rate Limiting

    #region Protocol Encryption (MSE/PE)

    /// <summary>
    /// Initial buffer size for encryption handshake (8KB).
    /// </summary>
    public const int EncryptionInitialBufferSize = 8192;

    /// <summary>
    /// Maximum buffer size for encryption handshake (16KB).
    /// </summary>
    public const int EncryptionMaxBufferSize = 16384;

    /// <summary>
    /// Maximum padding length per MSE spec (512 bytes).
    /// </summary>
    public const int EncryptionMaxPaddingLength = 512;

    /// <summary>
    /// RC4 discard count for MSE (1024 bytes).
    /// </summary>
    public const int RC4DiscardCount = 1024;

    #endregion Protocol Encryption (MSE/PE)

    #region DHT Constants

    /// <summary>
    /// DHT maintenance interval (60 seconds).
    /// </summary>
    public const int DhtMaintenanceIntervalMs = 60000;

    /// <summary>
    /// Maximum time a DHT node can be inactive before being replaced (15 minutes).
    /// </summary>
    public const int DhtNodeInactiveTimeoutMinutes = 15;

    /// <summary>
    /// DHT peer cache timeout (30 minutes).
    /// </summary>
    public const int DhtPeerCacheTimeoutMinutes = 30;

    /// <summary>
    /// DHT token secret rotation interval (10 minutes).
    /// </summary>
    public const int DhtSecretRotationMinutes = 10;

    /// <summary>
    /// DHT transaction timeout (2 minutes).
    /// </summary>
    public const int DhtTransactionTimeoutMinutes = 2;

    #endregion DHT Constants

    #region UDP Tracker Constants

    /// <summary>
    /// UDP tracker connection ID lifetime per BEP-15 (60 seconds).
    /// </summary>
    public const int UdpTrackerConnectionIdLifetimeSeconds = 60;

    /// <summary>
    /// UDP tracker receive timeout (15 seconds).
    /// </summary>
    public const int UdpTrackerReceiveTimeoutMs = 15000;

    #endregion UDP Tracker Constants

    #region Piece Selection

    /// <summary>
    /// Interval for refreshing piece selection (5 seconds).
    /// </summary>
    public const int PieceSelectionRefreshIntervalSeconds = 5;

    /// <summary>
    /// Request stall detection threshold (8 seconds).
    /// </summary>
    public const int RequestStallThresholdMs = 8000;

    #endregion Piece Selection

    #region Speed Stability (Gigabit Optimization)

    /// <summary>
    /// Threshold for gradual unchoking - peers performing at this percentage
    /// or better of the top peer's speed are kept unchoked.
    /// Prevents sudden disconnection of productive peers.
    /// </summary>
    public const double GradualUnchokeThreshold = 0.5;

    /// <summary>
    /// Maximum pipeline depth for request pipelining (250 blocks = 4MB in-flight).
    /// Matches libtorrent's <c>max_out_request_queue</c>. The cost of a deep queue is one small
    /// record per outstanding block on this side - the data is buffered by the sender - against the
    /// risk of having asked for more than a peer serves before choking us.
    /// </summary>
    public const int MaxPipelineDepth = PeerSharp.Internals.Peers.PipelineDepthCalculator.MaxPipeline;

    /// <summary>
    /// Minimum pipeline depth, matching libtorrent's <c>min_request_queue</c>.
    /// </summary>
    public const int MinPipelineDepth = PeerSharp.Internals.Peers.PipelineDepthCalculator.MinPipeline;

    /// <summary>
    /// Soft timeout before duplicating a request to another peer (5 seconds).
    /// Increased from 3 seconds to prevent premature duplication that causes
    /// traffic bursts and bandwidth waste on high-latency connections.
    /// </summary>
    public const int SoftTimeoutMs = 5000;

    /// <summary>
    /// Interval for unchoking algorithm (30 seconds).
    /// Increased from 10 seconds to prevent frequent peer set changes
    /// that cause speed oscillations and disrupt stable connections.
    /// </summary>
    public const int UnchokeIntervalSeconds = 30;

    // 50%

    #endregion Speed Stability (Gigabit Optimization)

    #region Buffer Sizes

    /// <summary>
    /// Default read buffer size (4KB).
    /// </summary>
    public const int DefaultReadBufferSize = 4096;

    /// <summary>
    /// uTP window size advertisement (1MB).
    /// </summary>
    public const int UtpWindowSize = 1024 * 1024;

    #endregion Buffer Sizes

    #region BEP 20 - Peer ID Conventions

    /// <summary>
    /// Client identifier for BEP 20 peer ID (Azureus-style).
    /// "PS" = PeerSharp
    /// </summary>
    public const string ClientId = "PS";

    /// <summary>
    /// Client version for the BEP 20 peer ID and the HTTP user agent.
    /// </summary>
    /// <remarks>
    /// Format: XXYY, two digits of major and two of minor, so 4.0 is "0400". This is what every peer,
    /// tracker and web seed sees, and it is the one version string that is not derived from the
    /// package - it sat at "0100" through three major releases because nothing checked.
    /// <c>ProtocolVersionTests</c> now compares it against the assembly version, so bumping one
    /// without the other fails the build's tests rather than shipping.
    /// </remarks>
    public const string ClientVersion = "0400";

    /// <summary>
    /// Generates a BEP 20 compliant peer ID using Azureus-style format.
    /// Format: -XXYYYY-xxxxxxxxxxxx (20 bytes total)
    /// - First 8 bytes: "-PS0400-" (client identifier and version)
    /// - Last 12 bytes: Random bytes for uniqueness
    /// </summary>
    public static byte[] GeneratePeerId()
    {
        byte[] peerId = new byte[20];

        // Azureus-style format: -XXYYYY- where XX is client ID, YYYY is version
        // Example: -PS0400- for PeerSharp 4.0
        peerId[0] = (byte)'-';
        peerId[1] = (byte)ClientId[0];
        peerId[2] = (byte)ClientId[1];
        peerId[3] = (byte)ClientVersion[0];
        peerId[4] = (byte)ClientVersion[1];
        peerId[5] = (byte)ClientVersion[2];
        peerId[6] = (byte)ClientVersion[3];
        peerId[7] = (byte)'-';

        // Fill remaining 12 bytes with random data
        Random.Shared.NextBytes(peerId.AsSpan(8, 12));

        return peerId;
    }

    #endregion BEP 20 - Peer ID Conventions
}
