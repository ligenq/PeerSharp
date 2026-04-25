namespace PeerSharp.Config;

/// <summary>
/// BitTorrent protocol encryption modes (Message Stream Encryption / Protocol Encryption).
/// </summary>
/// <remarks>
/// <b>Note on Privacy:</b> Protocol encryption is primarily used for <b>obfuscation</b> to prevent
/// traffic shaping and throttling by ISPs. It makes it harder for Deep Packet Inspection (DPI)
/// to identify traffic as BitTorrent.
/// It does <b>not</b> provide anonymity, nor does it hide your IP address or the fact that
/// you are part of a swarm from other peers (including copyright monitors).
/// For true privacy and anonymity, use a VPN or a Proxy.
/// </remarks>
public enum Encryption
{
    /// <summary>Do not use encryption. High compatibility, but easily detected by ISPs.</summary>
    Refuse,

    /// <summary>Support encryption but accept plaintext connections. Best balance of speed and compatibility.</summary>
    Allow,

    /// <summary>Only allow encrypted connections. Hardest to detect, but may significantly reduce the number of available peers.</summary>
    Require
}

/// <summary>
/// Specifies the type of proxy to use.
/// </summary>
public enum ProxyType
{
    /// <summary>No proxy used.</summary>
    None,

    /// <summary>SOCKS5 proxy.</summary>
    Socks5,

    /// <summary>HTTP proxy.</summary>
    Http
}

/// <summary>
/// Settings for peer-to-peer network connections.
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>
    /// Maximum number of connection attempts per second.
    /// Default is 18 (aligned with libtransmission). Prevents the client from being flagged as a port scanner.
    /// </summary>
    public int ConnectionsPerSecond { get; set; } = 18;

    /// <summary>
    /// Enable adaptive connection timeouts based on network performance. Default is true.
    /// Dynamically adjusts how long to wait for peers based on latency and success rate.
    /// </summary>
    public bool EnableAdaptiveTimeouts { get; set; } = true;

    /// <summary>
    /// Whether to enable Local Peer Discovery (LSD/BEP 14). Default is false.
    /// Enable this if your users are likely to be on shared local networks (offices, dorms, home LANs)
    /// to allow high-speed sharing without consuming internet bandwidth.
    /// </summary>
    public bool EnableLsd { get; set; } = false;

    /// <summary>
    /// Allow incoming TCP connections. Default is true.
    /// Disabling this will prevent being a seed for peers that cannot initiate connections.
    /// </summary>
    public bool EnableTcpIn { get; set; } = true;

    /// <summary>
    /// Allow outgoing TCP connections. Default is true.
    /// </summary>
    public bool EnableTcpOut { get; set; } = true;

    /// <summary>
    /// Allow incoming uTP (UDP) connections. Default is true.
    /// uTP is recommended as it handles network congestion better than TCP.
    /// </summary>
    public bool EnableUtpIn { get; set; } = true;

    /// <summary>
    /// Allow outgoing uTP (UDP) connections. Default is true.
    /// </summary>
    public bool EnableUtpOut { get; set; } = true;

    /// <summary>
    /// Allow HTTP/HTTPS web seeds from torrent metadata. Default is true.
    /// Disable this when validating pure peer-to-peer/WebTorrent download behavior.
    /// </summary>
    public bool EnableWebSeeds { get; set; } = true;

    /// <summary>
    /// BitTorrent protocol encryption mode. Default is Allow.
    /// <para>
    /// This is used for <b>obfuscation</b> to bypass ISP throttling. It does not provide
    /// security against swarm monitoring.
    /// </para>
    /// </summary>
    public Encryption Encryption { get; set; } = Encryption.Allow;

    /// <summary>Initial connection timeout in milliseconds for new peers. Default is 10000.</summary>
    public int InitialConnectionTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Maximum number of connection attempts to queue before dropping new requests.
    /// Default is 2000. Increase this if you see "Connection queue full" logs during high activity (e.g. DHT/PEX bursts).
    /// </summary>
    public int MaxConnectionQueueSize { get; set; } = 2000;

    /// <summary>
    /// Maximum simultaneous connections across all torrents.
    /// Default is 200 (aligned with libtransmission).
    /// Higher values can improve speeds but might overwhelm some consumer routers or OS resources.
    /// </summary>
    public uint MaxConnections { get; set; } = 200;

    /// <summary>Maximum connection timeout in milliseconds. Default is 30000.</summary>
    public int MaxConnectionTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Maximum number of holepunch rendezvous attempts per minute.
    /// Default is 20. Used for NAT traversal via BEP 55.
    /// </summary>
    public int MaxHolepunchPerMinute { get; set; } = 20;

    /// <summary>
    /// Maximum number of connected peers per individual torrent.
    /// Default is 50 (aligned with libtransmission). For most swarms, 50-200 is sufficient for maximum speed.
    /// </summary>
    public int MaxPeersPerTorrent { get; set; } = 50;

    /// <summary>
    /// Maximum number of pending (half-open) outgoing connections.
    /// Default is 200. Limits the resources consumed while trying to find active peers.
    /// </summary>
    public int MaxPendingConnections { get; set; } = 200;

    /// <summary>Minimum connection timeout in milliseconds. Default is 1000.</summary>
    public int MinConnectionTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Whether to attempt NAT-PMP port mapping. Default is false.
    /// NAT-PMP is a newer, simpler protocol supported by Apple and modern prosumer routers.
    /// It is generally considered safer than UPnP.
    /// </summary>
    public bool NatPmpPortMapping { get; set; } = false;

    /// <summary>
    /// Prefer uTP over TCP when both are available. Default is true.
    /// This helps maintain internet responsiveness for other applications during high-speed downloads.
    /// Note: preference is advisory; the client may start with TCP for unknown peers or fall back to TCP if uTP stalls.
    /// Use <see cref="PreferUtpRatioPercent"/> and <see cref="UtpFallbackTimeoutMs"/> to tune the behavior.
    /// </summary>
    public bool PreferUtp { get; set; } = true;

    /// <summary>
    /// Target percentage of outgoing connections that should use uTP when PreferUtp is enabled.
    /// Default is 70 (meaning ~70% uTP / 30% TCP for stability).
    /// </summary>
    public int PreferUtpRatioPercent { get; set; } = 70;

    /// <summary>
    /// Enables TCP_NODELAY (Nagle off) for peer connections. Default is true.
    /// This reduces latency for request/response cycles and generally improves throughput.
    /// </summary>
    public bool TcpNoDelay { get; set; } = true;

    /// <summary>
    /// TCP receive buffer size in bytes. Set to 0 to keep OS defaults.
    /// Default is 0 (OS auto-tuning) to improve memory usage.
    /// </summary>
    public int TcpReceiveBufferBytes { get; set; } = 0;

    /// <summary>
    /// TCP send buffer size in bytes. Set to 0 to keep OS defaults.
    /// Default is 0 (OS auto-tuning) to improve memory usage.
    /// </summary>
    public int TcpSendBufferBytes { get; set; } = 0;

    /// <summary>
    /// Max connection attempts per second once speed is stable.
    /// Default is 4 to reduce churn and oscillations.
    /// </summary>
    public int StableConnectionsPerSecond { get; set; } = 4;

    /// <summary>
    /// Rechoke interval in seconds. Default is 10 (aligned with libtransmission).
    /// Shorter intervals improve responsiveness during startup and peer churn.
    /// </summary>
    public int RechokeIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Base cooldown in seconds between connection attempts to the same peer after a failure.
    /// </summary>
    public int PeerReconnectBaseSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum cooldown in seconds between connection attempts to the same peer after repeated failures.
    /// </summary>
    public int PeerReconnectMaxSeconds { get; set; } = 300;

    /// <summary>
    /// Random jitter in milliseconds added to connection cooldown to avoid thundering herd.
    /// </summary>
    public int PeerReconnectJitterMs { get; set; } = 2000;

    /// <summary>Minimum peers connected before slow-peer pruning is applied.</summary>
    public int SlowPeerMinConnectedPeers { get; set; } = 8;

    /// <summary>Minimum sustained download speed in bytes/sec before a peer is considered slow.</summary>
    public int SlowPeerMinDownloadSpeedBytesPerSec { get; set; } = 30 * 1024;

    /// <summary>Minimum sustained upload speed in bytes/sec before a peer is considered slow while seeding.</summary>
    public int SlowPeerMinUploadSpeedBytesPerSec { get; set; } = 30 * 1024;

    /// <summary>Grace period in seconds before disconnecting a slow peer.</summary>
    public int SlowPeerGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Optimistic unchoke rotation interval in seconds. Default is 30.
    /// </summary>
    public int OptimisticUnchokeIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Minimum number of upload slots per torrent. Default is 4.
    /// </summary>
    public int UploadSlotsMin { get; set; } = 4;

    /// <summary>
    /// Maximum number of upload slots per torrent. Default is 8.
    /// </summary>
    public int UploadSlotsMax { get; set; } = 8;

    /// <summary>
    /// Target upload rate per slot (bytes/sec) used to scale slot count.
    /// Default is 64000 (~64 KB/s).
    /// </summary>
    public int TargetUploadPerSlotBytesPerSec { get; set; } = 64000;

    /// <summary>
    /// How long (seconds) speed must remain above StableSpeedThresholdBytesPerSec to be considered stable.
    /// </summary>
    public int StableSpeedSeconds { get; set; } = 20;

    /// <summary>
    /// Download speed threshold (bytes/sec) considered "stable" for connection throttling.
    /// Default is 2MB/s.
    /// </summary>
    public int StableSpeedThresholdBytesPerSec { get; set; } = 2_000_000;

    /// <summary>
    /// The TCP port to listen on for incoming connections. Set to 0 for OS-assigned port.
    /// Default is 55125. Use a value in the range 49152-65535 to avoid conflicts with system services.
    /// </summary>
    public ushort TcpPort { get; set; } = 55125;

    /// <summary>
    /// The UDP port to listen on for DHT, uTP, and LSD. Set to 0 for OS-assigned port.
    /// Default is 55125. It is recommended to use the same value as TcpPort for simplicity.
    /// </summary>
    public ushort UdpPort { get; set; } = 55125;

    /// <summary>
    /// Whether to attempt UPnP port mapping. Default is false.
    /// Enable this if you want the client to automatically open ports on compatible routers.
    /// Note: UPnP is widely supported but has known security vulnerabilities in some implementations.
    /// </summary>
    public bool UpnpPortMapping { get; set; } = false;

    /// <summary>
    /// Grace period (seconds) before evaluating uTP performance after connection.
    /// </summary>
    public int UtpDegradeGraceSeconds { get; set; } = 20;

    /// <summary>
    /// Minimum download speed (bytes/sec) expected for uTP connections before penalizing.
    /// Default is 50000 bytes/sec (~50 KB/s).
    /// </summary>
    public int UtpDegradeMinDownloadSpeedBytesPerSec { get; set; } = 50000;

    /// <summary>
    /// Number of consecutive uTP failures before marking the peer as uTP-unsupported.
    /// Default is 3.
    /// </summary>
    public int UtpFailureHardLimit { get; set; } = 3;

    /// <summary>
    /// When PreferUtp is enabled, how long to wait (ms) before falling back to TCP if uTP stalls.
    /// Default is 3000ms to keep the connection pool responsive.
    /// </summary>
    public int UtpFallbackTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Base penalty duration (seconds) when uTP fails to connect.
    /// Penalty time backs off exponentially up to UtpPenaltyMaxSeconds.
    /// </summary>
    public int UtpPenaltyBaseSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum penalty duration (seconds) when uTP repeatedly fails.
    /// </summary>
    public int UtpPenaltyMaxSeconds { get; set; } = 600;

    /// <summary>
    /// Cooldown (seconds) between uTP slow penalties for the same peer.
    /// </summary>
    public int UtpSlowPenaltyCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Penalty duration (seconds) when uTP is consistently slow.
    /// </summary>
    public int UtpSlowPenaltySeconds { get; set; } = 90;

    /// <summary>
    /// Startup warmup period (seconds) during which new outgoing connections prefer TCP.
    /// uTP is allowed during warmup only for peers with a known uTP hint.
    /// </summary>
    public int UtpWarmupSeconds { get; set; } = 30;
}

/// <summary>
/// Represents a DHT bootstrap node for initial network discovery.
/// </summary>
/// <param name="Host">The hostname or IP address of the bootstrap node.</param>
/// <param name="Port">The UDP port of the bootstrap node.</param>
public sealed record DhtBootstrapNode(string Host, ushort Port);

/// <summary>
/// Settings for the Distributed Hash Table (DHT) network.
/// </summary>
public sealed class DhtSettings
{
    /// <summary>
    /// DHT bootstrap nodes for initial network discovery.
    /// </summary>
    public IReadOnlyList<DhtBootstrapNode> BootstrapNodes { get; set; } = new List<DhtBootstrapNode>
    {
        new("router.bittorrent.com", 6881),
        new("dht.transmissionbt.com", 6881),
        new("router.utorrent.com", 6881)
    };

    /// <summary>Whether DHT is enabled for peer discovery.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Initial DHT state (Node ID and routing table) to restore.
    /// </summary>
    public DhtState? InitialState { get; set; }
}

/// <summary>
/// Settings for file management and GeoIP.
/// </summary>
public sealed class FilesSettings
{
    /// <summary>
    /// Gets or sets the default directory for downloading torrent files.
    /// This is used if no specific download path is provided when adding a torrent.
    /// </summary>
    public string DefaultDownloadPath { get; set; } = string.Empty;

    /// <summary>
    /// Enables sparse file allocation when supported by the filesystem.
    /// </summary>
    public bool EnableSparseFiles { get; set; } = true;

    /// <summary>
    /// Block cache size in bytes for disk reads/writes.
    /// </summary>
    public int BlockCacheSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Enables sequential read-ahead in the block cache.
    /// </summary>
    public bool EnableReadAhead { get; set; } = true;

    /// <summary>
    /// Number of 16KiB blocks to prefetch when sequential reads are detected.
    /// </summary>
    public int ReadAheadBlocks { get; set; } = 4;

    /// <summary>Global disk read speed limit in bytes per second. 0 for unlimited.</summary>
    public uint MaxDiskReadSpeed { get; set; } = 0;

    /// <summary>Global disk write speed limit in bytes per second. 0 for unlimited.</summary>
    public uint MaxDiskWriteSpeed { get; set; } = 0;
}

/// <summary>
/// Settings for network proxy.
/// </summary>
public sealed class ProxySettings
{
    /// <summary>
    /// If true, the client will only connect via proxy.
    /// Direct connections will be disabled, and incoming connections might be blocked.
    /// </summary>
    public bool ForceProxy { get; set; } = false;

    /// <summary>Proxy server hostname or IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Password for proxy authentication (optional).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Proxy server port.</summary>
    public ushort Port { get; set; } = 0;

    /// <summary>Whether to use proxy for peer connections.</summary>
    public bool ProxyPeers { get; set; } = true;

    /// <summary>Whether to use proxy for tracker connections.</summary>
    public bool ProxyTrackers { get; set; } = true;

    /// <summary>Type of proxy to use.</summary>
    public ProxyType Type { get; set; } = ProxyType.None;

    /// <summary>Username for proxy authentication (optional).</summary>
    public string Username { get; set; } = string.Empty;

    public override bool Equals(object? obj)
    {
        if (obj is not ProxySettings other)
        {
            return false;
        }

        return Type == other.Type &&
               Host == other.Host &&
               Port == other.Port &&
               Username == other.Username &&
               Password == other.Password &&
               ProxyTrackers == other.ProxyTrackers &&
               ProxyPeers == other.ProxyPeers &&
               ForceProxy == other.ForceProxy;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        hash.Add(Host);
        hash.Add(Port);
        hash.Add(Username);
        hash.Add(Password);
        hash.Add(ProxyTrackers);
        hash.Add(ProxyPeers);
        hash.Add(ForceProxy);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Settings for queue management and auto-stop behavior.
/// </summary>
public sealed class QueueSettings
{
    /// <summary>
    /// Whether queue management is enabled. Default is false.
    /// When enabled, the engine will auto-start/stop torrents to respect limits.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether to enforce ratio/seed-time auto-stop rules. Default is true.
    /// </summary>
    public bool EnforceAutoStop { get; set; } = true;

    /// <summary>
    /// Maximum number of active downloading torrents. 0 means unlimited.
    /// Default is 3.
    /// </summary>
    public int MaxActiveDownloads { get; set; } = 3;

    /// <summary>
    /// Maximum number of active seeding torrents. 0 means unlimited.
    /// Default is 2.
    /// </summary>
    public int MaxActiveSeeds { get; set; } = 2;

    /// <summary>
    /// Queue evaluation interval in seconds. Default is 5.
    /// </summary>
    public int RecheckIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// Settings for session persistence (auto-save/load of torrent state).
/// This feature is completely optional and disabled by default.
/// </summary>
public sealed class SessionSettings
{
    /// <summary>
    /// Interval in seconds for periodic auto-save of torrent state.
    /// Set to 0 to only save on torrent changes and shutdown.
    /// Default is 60 (1 minute).
    /// </summary>
    public int AutoSaveIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whether to enable automatic session persistence. Default is false.
    /// When enabled, torrents and their resume data are saved to disk and restored on startup.
    /// <para>
    /// <b>Important:</b> When enabled, <see cref="SessionPath"/> must be set to a valid directory path.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum interval in seconds between saves triggered by piece completion.
    /// Prevents excessive disk I/O during fast downloads. Default is 30.
    /// </summary>
    public int MinSaveIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to save resume data when pieces complete (enables granular recovery).
    /// When false, only saves on stop/shutdown. Default is true.
    /// </summary>
    public bool SaveOnPieceCompletion { get; set; } = true;

    /// <summary>
    /// Directory path for storing session data (torrents, resume data).
    /// <para>
    /// <b>Required</b> when <see cref="Enabled"/> is true. The library does not assume
    /// any default location - the application must explicitly specify where to store session data.
    /// </para>
    /// </summary>
    public string SessionPath { get; set; } = string.Empty;
}

/// <summary>
/// Settings for data transfer and bandwidth management.
/// </summary>
public sealed class TransferSettings
{
    /// <summary>Bandwidth allocation update interval in milliseconds.</summary>
    public int BandwidthUpdateIntervalMs { get; set; } = 10;

    /// <summary>Estimated bandwidth for startup pipeline calculation (bytes/sec).</summary>
    public int EstimatedBandwidthBytesPerSec { get; set; } = 12500000;

    /// <summary>Estimated round-trip time for startup pipeline calculation (ms).</summary>
    public int EstimatedRttMs { get; set; } = 50;

    /// <summary>Initial request pipeline depth for new peer connections.</summary>
    public int InitialPipelineDepth { get; set; } = 16;

    /// <summary>Maximum concurrent piece hash/write operations.</summary>
    public int MaxConcurrentPieceProcessing { get; set; } = 16;

    /// <summary>Maximum concurrent piece hash verification operations.</summary>
    public int MaxConcurrentPieceHashing { get; set; } = 8;

    /// <summary>Maximum concurrent piece write operations.</summary>
    public int MaxConcurrentPieceWrites { get; set; } = 8;

    /// <summary>
    /// Maximum outstanding requests per peer to cap pipeline growth.
    /// </summary>
    public int MaxRequestsPerPeer { get; set; } = 128;

    /// <summary>
    /// Number of parallel metadata piece requests allowed (ut_metadata).
    /// Default is 8 for faster magnet startup.
    /// </summary>
    public int MetadataRequestPipeline { get; set; } = 8;

    /// <summary>
    /// Timeout for metadata piece requests in seconds.
    /// Default is 10.
    /// </summary>
    public int MetadataRequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum retry attempts per metadata piece request.
    /// Default is 5.
    /// </summary>
    public int MetadataMaxRequestAttempts { get; set; } = 5;

    /// <summary>Global download speed limit in bytes per second. 0 for unlimited.</summary>
    public uint MaxDownloadSpeed { get; set; } = 0;

    /// <summary>Global upload speed limit in bytes per second. 0 for unlimited.</summary>
    public uint MaxUploadSpeed { get; set; } = 0;
}

/// <summary>
/// Configuration settings for the BitTorrent client.
/// </summary>
public sealed class Settings
{
    /// <summary>Settings for peer-to-peer network connections.</summary>
    public ConnectionSettings Connection { get; set; } = new();

    /// <summary>Settings for Distributed Hash Table (DHT).</summary>
    public DhtSettings Dht { get; set; } = new();

    /// <summary>Settings for file management and storage.</summary>
    public FilesSettings Files { get; set; } = new();

    /// <summary>Maximum number of unique known peers to keep in cache.</summary>
    public int MaxKnownPeersCache { get; set; } = 2000;

    /// <summary>Maximum number of peers to request from a tracker in one announce.</summary>
    public uint MaxPeersPerTrackerRequest { get; set; } = 200;

    /// <summary>The client's unique 20-byte Peer ID (BEP 20).</summary>
    public byte[] PeerId { get; set; } = new byte[20];

    /// <summary>Settings for network proxy.</summary>
    public ProxySettings Proxy { get; set; } = new();

    /// <summary>Settings for queue management and auto-stop rules.</summary>
    public QueueSettings Queue { get; set; } = new();

    /// <summary>Settings for session persistence (optional, disabled by default).</summary>
    public SessionSettings Session { get; set; } = new();

    /// <summary>Settings for data transfer.</summary>
    public TransferSettings Transfer { get; set; } = new();
}
