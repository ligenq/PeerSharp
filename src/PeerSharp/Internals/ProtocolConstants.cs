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
    /// Idle timeout before closing connection (2 minutes).
    /// </summary>
    public const int IdleTimeoutMs = 120000;

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
    /// Reduced from 2000 to prevent:
    /// - Massive request backlogs causing stalls when peers choke
    /// - Long recovery times after peer state changes
    /// - Memory pressure from thousands of pending requests
    /// 250 blocks is sufficient for 1 Gbps with 30ms RTT.
    /// </summary>
    public const int MaxPipelineDepth = 128;

    /// <summary>
    /// Minimum pipeline depth for request pipelining (8 blocks = 128KB in-flight).
    /// </summary>
    public const int MinPipelineDepth = 8;

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
    /// Client version for BEP 20 peer ID.
    /// Format: XXYY where XX = major, YY = minor (e.g., "0100" = 1.0.0)
    /// </summary>
    public const string ClientVersion = "0100";

    /// <summary>
    /// Generates a BEP 20 compliant peer ID using Azureus-style format.
    /// Format: -XX0000-xxxxxxxxxxxx (20 bytes total)
    /// - First 8 bytes: "-MT0100-" (client identifier and version)
    /// - Last 12 bytes: Random bytes for uniqueness
    /// </summary>
    public static byte[] GeneratePeerId()
    {
        byte[] peerId = new byte[20];

        // Azureus-style format: -XXYYYY- where XX is client ID, YYYY is version
        // Example: -PS0100- for PeerSharp version 1.0.0
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
