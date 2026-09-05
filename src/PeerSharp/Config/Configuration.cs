using PeerSharp.Internals.Framework;

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
    /// Local address to bind network traffic to. <see langword="null"/> uses the operating system's
    /// default routing and listens on all available interfaces.
    /// </summary>
    /// <remarks>
    /// Set this before initializing the engine. When set, socket creation fails rather than falling
    /// back to an unbound socket if the address is unavailable; this allows a host to enforce a VPN
    /// interface as a kill switch. The address family also limits connections to that family.
    /// Operating-system DNS resolution is outside this socket binding and follows the host's resolver
    /// policy.
    /// </remarks>
    public System.Net.IPAddress? BindAddress { get; set; }

    /// <summary>
    /// Maximum number of connection attempts per second.
    /// Default is 18 (aligned with libtransmission). Prevents the client from being flagged as a port scanner.
    /// </summary>
    public int ConnectionsPerSecond { get; set; } = 18;

    /// <summary>
    /// Whether more than one peer connection may share a single IP address. On by default, which is
    /// the opposite of libtorrent's <c>allow_multiple_connections_per_ip</c>.
    ///
    /// <para>
    /// The case for restricting it is that one host can otherwise hold as many slots as it has source
    /// ports. The case against is that carrier-grade NAT puts large numbers of unrelated subscribers
    /// behind one address, and refusing them costs real peers - as does running two clients on one
    /// machine. libtorrent's default was set when shared addresses were rarer than they are now, and
    /// the abuse it guards against is already bounded by the overall connection limit.
    /// </para>
    ///
    /// <para>
    /// Turning it off has a sharp edge worth knowing about: the check runs when a connection is
    /// accepted, before any handshake, so a peer reconnecting from a new source port is refused
    /// rather than recognised as the same peer and allowed to replace its own stale connection.
    /// </para>
    /// </summary>
    public bool AllowMultipleConnectionsPerIp { get; set; } = true;

    /// <summary>
    /// How many connections one IP address may hold on a single torrent. Zero, the default, means no
    /// limit. Ignored when <see cref="AllowMultipleConnectionsPerIp"/> is <see langword="false"/>,
    /// which is the stricter policy.
    ///
    /// <para>
    /// This is the middle ground the flag above lacks. Allowing many connections per address is the
    /// right default because of carrier-grade NAT, but on a host you control - a seedbox, or an
    /// engine facing a swarm you do not trust - "many" should not have to mean "all of them": one
    /// host cycling source ports can otherwise hold every slot a torrent has, and
    /// <see cref="MaxConnections"/> bounds only how much of the engine that costs, not how much of
    /// one swarm.
    /// </para>
    ///
    /// <para>
    /// Off by default, because the number that is safe depends on the deployment and a wrong guess
    /// refuses real peers. What this counts is live connection <em>registrations</em>, and a single
    /// logical peer can briefly hold more than one: a dial may try uTP and TCP, a handshake in
    /// progress is already registered, and a reconnect overlaps the connection it replaces. Anywhere
    /// peers genuinely share an address the count therefore sits well above the peer count - most
    /// sharply on loopback, where every local engine is <c>127.0.0.1</c> and a swarm of twenty-four
    /// leechers is twenty-four connections from one address before any of that. Set it from what the
    /// deployment looks like: a public seedbox can afford a small number, a client behind CGNAT
    /// should leave it off.
    /// </para>
    /// </summary>
    public int MaxConnectionsPerIp { get; set; }

    /// <summary>
    /// How often, at most, one peer is told about swarm changes over ut_pex (BEP 11).
    ///
    /// <para>
    /// Sixty seconds by default, matching libtorrent. Sending faster than peers expect is what gets a
    /// client throttled or ignored, so raising the rate is rarely useful; the reason this is adjustable
    /// at all is that a minute is a long time to wait to observe an exchange, in a test or otherwise.
    /// Peer exchange is skipped entirely for private torrents regardless of this value.
    /// </para>
    /// </summary>
    public TimeSpan PexInterval { get; set; } = TimeSpan.FromSeconds(60);

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
    /// Applied when the torrent's peer manager is created; changing it does not resize an existing queue.
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
    /// Maximum number of pending (half-open) outgoing connections. Default is 200.
    ///
    /// <para>
    /// This was briefly lowered to 50, on the theory that the ~150 half-open connections the client
    /// settles at were exhausting a home router's connection tracking table and getting working
    /// connections evicted. The evidence for it was a correlation: a download was unstable while
    /// half-open sat at 125-153 and steady once it fell to 38. Three things then contradicted it.
    /// libtorrent does not limit half-open connections at all - its half_open_limit is deprecated, and
    /// 30 attempts a second against a 15 second connect timeout implies more of them than this. The
    /// median lifetime of a handshaked connection got worse after the cap, not better, which is the
    /// opposite of what relieving that pressure would do. And once closes recorded which method caused
    /// them, every one turned out to be the connect path failing or the peer hanging up - never
    /// anything on this side dropping a working connection.
    /// </para>
    ///
    /// <para>
    /// The correlation had a simpler reading: half-open is high while the client is still hunting for
    /// peers, and the download is slow while the client is still hunting for peers. One cause, two
    /// symptoms, no arrow between them. Meanwhile the cap did measurable harm - it bound 85% of the
    /// time through the ramp, and in a swarm where 89% of advertised addresses never answer, dialling
    /// is exactly what must not be throttled: finding twenty live peers takes around 170 attempts, which
    /// is eleven seconds at 150 half-open and thirty-four at 50.
    /// </para>
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
    /// Use <see cref="UtpSpeculativeTimeoutMs"/> and <see cref="UtpFallbackTimeoutMs"/> to tune the behavior.
    /// </summary>
    public bool PreferUtp { get; set; } = true;


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
    /// The TCP port to listen on for incoming connections. Set to 0 for an OS-assigned port.
    /// Default is 6881.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 6881 is the first of the range BitTorrent has used since the original client, and the
    /// default in libtorrent, qBittorrent and Deluge. Prefer a port below 49152. The range above it
    /// is the dynamic range, which the OS hands out to outbound connections and which Windows
    /// reserves blocks of for Hyper-V, WSL and Docker; a bind inside one of those reserved blocks
    /// fails with a permission error even though nothing is listening, and the blocks move between
    /// reboots. An earlier default of 55125 sat in that range.
    /// </para>
    /// <para>
    /// If the port cannot be bound the engine tries the next few and then falls back to an
    /// OS-assigned one rather than refusing to start, so a busy or reserved port costs inbound
    /// reachability at worst. Set an explicit port when forwarding one through a router.
    /// </para>
    /// </remarks>
    public ushort TcpPort { get; set; } = 6881;

    /// <summary>
    /// The UDP port to listen on for DHT, uTP, and LSD. Set to 0 for an OS-assigned port.
    /// Default is 6881, matching <see cref="TcpPort"/>; see its remarks for how the port is chosen.
    /// </summary>
    public ushort UdpPort { get; set; } = 6881;

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
    /// How long to spend (ms) on a transport that has another one behind it, before falling back.
    /// Default is 3000ms to keep the connection pool responsive.
    ///
    /// <para>
    /// Applies to whichever transport is tried first, not only uTP. The whole connection attempt shares
    /// one budget, so an unbounded first attempt can exhaust it and leave the fallback with the
    /// <see cref="MinConnectionTimeoutMs"/> floor - which is not long enough to reach most peers, and
    /// makes the fallback fail almost every time it is needed.
    /// </para>
    /// </summary>
    public int UtpFallbackTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// How long a uTP attempt may take when nothing is known about whether the peer speaks it, in
    /// milliseconds. Default is 1,000.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every peer is assumed to support uTP until something says otherwise, which is what gets LEDBAT
    /// used at all. On a public swarm that assumption is wrong about three times in four, and each
    /// wrong guess is charged in full to the connection budget before TCP is even tried.
    /// </para>
    /// <para>
    /// Measured against the Debian swarm, half the successful uTP connections completed within 88ms
    /// and 99% within 926ms; exactly one of 139 needed longer than this. <see cref="UtpFallbackTimeoutMs"/>
    /// still applies once a peer has actually answered over uTP, so the peers this could cut short
    /// are only ever the ones nothing is known about.
    /// </para>
    /// </remarks>
    public int UtpSpeculativeTimeoutMs { get; set; } = 1000;

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
    /// LEDBAT's target one-way queuing delay, in microseconds. Default is 100,000 (100 ms), the value
    /// in BEP 29 and libutp; clamped to 1,000 - 1,000,000. Read while the connection runs.
    /// </summary>
    /// <remarks>
    /// This is the whole point of uTP: the controller grows its window while measured queuing delay
    /// is under the target and shrinks it above, so uTP backs off before TCP does and leaves the link
    /// to interactive traffic. Raising it makes uTP more aggressive and less yielding - a LAN or a
    /// dedicated line may want that, and a shared home connection is the case the default is chosen
    /// for. Lowering it yields sooner, at the cost of throughput.
    /// </remarks>
    public int UtpTargetDelayMicroseconds { get; set; } = 100000;

    /// <summary>
    /// The most the congestion window may grow in one round trip, in bytes. Default is 3,000, as in
    /// libutp; clamped to 100 - 1,000,000. Read while the connection runs.
    /// </summary>
    /// <remarks>
    /// LEDBAT's gain. It bounds how fast the window opens when the path looks idle, which is what
    /// keeps a delay-based controller from overshooting into the very queue it is measuring.
    /// </remarks>
    public int UtpMaxWindowIncreaseBytesPerRtt { get; set; } = 3000;

    /// <summary>
    /// The shortest interval between two loss-driven cuts of the congestion window, in milliseconds.
    /// Default is 100, matching libtorrent's <c>cwnd_reduce_timer</c>; clamped to 10 - 60,000. Read
    /// while the connection runs.
    /// </summary>
    /// <remarks>
    /// One lost packet is normally reported by several duplicate acknowledgements. Without an
    /// interval the window is halved once per report rather than once per loss, which takes it to
    /// the floor on the first burst.
    /// </remarks>
    public int UtpLossWindowCutIntervalMs { get; set; } = 100;

    /// <summary>
    /// How many times a uTP handshake is retried before the connection is abandoned. Default is 2;
    /// clamped to 0 - 10. Read while the connection runs.
    /// </summary>
    /// <remarks>
    /// Most addresses a swarm hands out sit behind a NAT that never answers, so this is a budget
    /// spent mostly on peers that will never reply. Each retry doubles the wait before the next.
    /// </remarks>
    public int UtpMaxSynRetries { get; set; } = 2;

    /// <summary>
    /// Cooldown (seconds) between uTP slow penalties for the same peer.
    /// </summary>
    public int UtpSlowPenaltyCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Penalty duration (seconds) when uTP is consistently slow.
    /// </summary>
    public int UtpSlowPenaltySeconds { get; set; } = 90;

    /// <summary>
    /// How many uTP dials may fail with none having succeeded before this client stops guessing that
    /// peers speak uTP. Default is 8; zero or less disables the guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces a fixed warm-up period that put every peer on TCP for the first thirty seconds of a
    /// torrent. That covered exactly the wrong window - the tracker's opening batch, whose
    /// connections are the ones that last - and it answered from a clock rather than from evidence:
    /// a machine whose UDP works waited anyway, and one whose UDP is blocked carried on regardless
    /// once the clock ran out.
    /// </para>
    /// <para>
    /// This asks the network instead. Leading with uTP is itself the probe, and on a path that
    /// drops UDP the first several dials say so; the global penalty then holds uTP back for a while
    /// rather than paying a capped attempt per peer. One success clears the count, because the
    /// question is whether uTP works here at all.
    /// </para>
    /// </remarks>
    public int UtpColdStartFailureLimit { get; set; } = 8;
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
    public IReadOnlyList<DhtBootstrapNode> BootstrapNodes { get; set; } =
    [
        new("router.bittorrent.com", 6881),
        new("dht.transmissionbt.com", 6881),
        new("router.utorrent.com", 6881)
    ];

    /// <summary>Whether DHT is enabled for peer discovery.</summary>
    public bool Enabled { get; set; } = true;


    /// <summary>
    /// BEP 43: run as a read-only DHT node - one that queries the network but does not serve it.
    ///
    /// <para>
    /// Off by default, because an ordinary client should be a full participant. Turn it on for a node
    /// that is transient, unreachable behind a NAT, or exists only to crawl (see
    /// <c>DiscoverInfoHashesAsync</c>): such a node in someone's routing table is dead weight, and
    /// flagging it keeps it out of them.
    /// </para>
    ///
    /// <para>
    /// The mode has teeth. Queries carry <c>ro=1</c> so other nodes leave us out of their tables, and
    /// incoming queries go unanswered - which means no peers are served, no BEP 44 items are stored, and
    /// <see cref="AnswerInfoHashSampling"/> becomes moot. Announcing a torrent from a read-only node
    /// still works, but other peers will find it harder to find us.
    /// </para>
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Whether to answer BEP 51 <c>sample_infohashes</c> queries, which let indexers enumerate the
    /// info-hashes this node holds peers for.
    ///
    /// <para>
    /// On by default: the same information is already obtainable by asking us <c>get_peers</c>, and
    /// answering makes us a useful participant rather than a dead end. Turn it off to reply
    /// <c>204 Method Unknown</c> instead, as every pre-BEP 51 node does.
    /// </para>
    /// </summary>
    public bool AnswerInfoHashSampling { get; set; } = true;

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
    private long _maxDiskReadSpeed;
    private long _maxDiskWriteSpeed;

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
    public int ReadAheadBlocks { get; set; } = 16;

    /// <summary>
    /// Global disk read speed limit in bytes per second. 0 for unlimited. Negative values are rejected.
    /// </summary>
    public long MaxDiskReadSpeed
    {
        get => _maxDiskReadSpeed;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxDiskReadSpeed = value;
        }
    }

    /// <summary>
    /// Global disk write speed limit in bytes per second. 0 for unlimited. Negative values are rejected.
    /// </summary>
    public long MaxDiskWriteSpeed
    {
        get => _maxDiskWriteSpeed;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxDiskWriteSpeed = value;
        }
    }
}

/// <summary>
/// Settings for network proxy.
/// </summary>
public sealed class ProxySettings
{
    /// <summary>
    /// Reports which UDP features the engine's proxy policy permits, without changing settings.
    /// </summary>
    /// <remarks>
    /// DHT uses a configured proxy. Peer and tracker traffic follow their respective proxy flags.
    /// A SOCKS5 server must still support UDP association for a permitted connection to succeed.
    /// </remarks>
    public UdpProxyCapabilities GetUdpCapabilities() => new(
        Internals.Network.UdpProxyPolicy.Decide(this, proxyTraffic: true) != Internals.Network.UdpProxyPolicy.Decision.Refuse,
        Internals.Network.UdpProxyPolicy.Decide(this, ProxyPeers) != Internals.Network.UdpProxyPolicy.Decision.Refuse,
        Internals.Network.UdpProxyPolicy.Decide(this, ProxyTrackers) != Internals.Network.UdpProxyPolicy.Decision.Refuse);

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

    /// <summary>Determines whether <paramref name="obj"/> is an equivalent proxy configuration.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> if every proxy setting matches.</returns>
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

    /// <summary>Returns a hash code derived from every proxy setting.</summary>
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
    private readonly List<IConcurrencyLimitListener> _concurrencyListeners = [];
    private readonly Lock _concurrencyListenerLock = new();
    private long _activePieceByteBudget = 32L * 1024 * 1024;
    private int _maxConcurrentPieceHashing = 8;
    private int _maxConcurrentPieceWrites = 8;
    private long _maxDownloadSpeed;
    private long _maxUploadSpeed;

    /// <summary>Bandwidth allocation update interval in milliseconds.</summary>
    public int BandwidthUpdateIntervalMs { get; set; } = 10;

    /// <summary>Estimated bandwidth for startup pipeline calculation (bytes/sec).</summary>
    public int EstimatedBandwidthBytesPerSec { get; set; } = 12500000;

    /// <summary>Estimated round-trip time for startup pipeline calculation (ms).</summary>
    public int EstimatedRttMs { get; set; } = 50;

    /// <summary>
    /// Seconds of work to keep queued on each peer, which is what sets the request pipeline depth.
    /// </summary>
    /// <remarks>
    /// Depth is this many seconds multiplied by the peer's measured download rate, so a fast peer
    /// earns a deep queue and a slow one does not. Raising it costs a request record per outstanding
    /// block and risks asking for more than a peer will serve before it is choked; lowering it below
    /// the round trip leaves the peer idle between requests. Matches libtorrent's
    /// <c>request_queue_time</c>.
    /// </remarks>
    public int RequestQueueTimeSeconds { get; set; } = 3;

    /// <summary>Initial request pipeline depth for new peer connections.</summary>
    public int InitialPipelineDepth { get; set; } = 16;

    /// <summary>Maximum concurrent web-seed piece downloads per torrent, clamped to 1-64. Read while running; lowering it lets existing requests finish.</summary>
    public int WebSeedMaxConnections { get; set; } = 2;

    /// <summary>Maximum concurrent piece downloads from one web-seed URL, clamped to 1-64. Also bounded by WebSeedMaxConnections.</summary>
    public int WebSeedMaxConnectionsPerSource { get; set; } = 2;

    /// <summary>Piece processing worker count and queue capacity, clamped to 4-256. Applied when the torrent's transfer is created.</summary>
    public int MaxConcurrentPieceProcessing { get; set; } = 16;

    /// <summary>Maximum concurrent piece hash verification operations (1-256). Changes apply to existing transfers; running work drains normally.</summary>
    public int MaxConcurrentPieceHashing
    {
        get => Volatile.Read(ref _maxConcurrentPieceHashing);
        set
        {
            Volatile.Write(ref _maxConcurrentPieceHashing, value);
            NotifyConcurrencyLimitsChanged();
        }
    }

    /// <summary>Maximum concurrent piece write operations (1-128). Changes apply to existing transfers; running work drains normally.</summary>
    public int MaxConcurrentPieceWrites
    {
        get => Volatile.Read(ref _maxConcurrentPieceWrites);
        set
        {
            Volatile.Write(ref _maxConcurrentPieceWrites, value);
            NotifyConcurrencyLimitsChanged();
        }
    }

    /// <summary>
    /// Bytes of piece data allowed to be in progress at once, which is what sets how many pieces the
    /// picker keeps open. Default is 32 MiB, clamped to 1 MiB - 4 GiB.
    /// </summary>
    /// <remarks>
    /// A fixed piece count cannot serve both ends of the range it has to cover. Thirty-two pieces is
    /// 8 MiB of exposure on a 256 KiB-piece torrent and 512 MiB on a 16 MiB-piece one - the first
    /// too few to keep a fast swarm busy, the second more unverified data in flight than most callers
    /// would choose. A byte budget divided by the piece size gives the same exposure either way.
    /// </remarks>
    public long ActivePieceByteBudget
    {
        get => _activePieceByteBudget;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _activePieceByteBudget = value;
        }
    }

    /// <summary>
    /// Hard ceiling on pieces open at once, whatever the byte budget works out to. Default is 256,
    /// clamped to <see cref="MinActivePieces"/> - 4096.
    /// </summary>
    public int MaxActivePieces { get; set; } = 256;

    /// <summary>
    /// Floor on pieces open at once. Default is 8, clamped to 1 - 4096. A torrent with pieces larger
    /// than the whole budget still gets this many, or endgame has nothing to work with.
    /// </summary>
    public int MinActivePieces { get; set; } = 8;

    /// <summary>
    /// Pieces of headroom kept open per unchoked peer, so capacity tracks the demand actually
    /// present rather than the budget alone. Default is 2, clamped to 1 - 64.
    /// </summary>
    public int ActivePiecesPerPeer { get; set; } = 2;

    /// <summary>
    /// Multiplier on a peer's smoothed round trip for the soft block timeout - the point at which the
    /// block is offered to another peer while the original request stands. Default is 6, clamped to
    /// 1 - 100.
    /// </summary>
    public int BlockTimeoutRttMultiplier { get; set; } = 6;

    /// <summary>
    /// Multiplier on a peer's smoothed round trip for the hard block timeout - the point at which the
    /// request is abandoned and the peer takes a strike. Default is 10, clamped to 1 - 100.
    /// </summary>
    public int BlockHardTimeoutRttMultiplier { get; set; } = 10;

    /// <summary>
    /// Multiplier on the observed variance of a peer's response times, added to the round-trip term
    /// of both block timeouts. Default is 4 (RFC 6298's <c>K</c>), clamped to 0 - 32; zero restores
    /// the older mean-only behaviour.
    /// </summary>
    /// <remarks>
    /// A peer whose replies average 200 ms but range from 50 ms to 2 s is not the same peer as one
    /// that answers in a steady 200 ms, and a multiple of the mean alone cannot tell them apart: the
    /// bound is either too tight for the jittery peer, which then collects timeouts it did not earn,
    /// or too loose for the steady one, which is given seconds to answer a request it would have
    /// dropped. This is the same term libtorrent adds in <c>peer_connection.cpp</c>.
    /// </remarks>
    public int BlockTimeoutVarianceMultiplier { get; set; } = 4;

    /// <summary>Lower bound on the soft block timeout, in milliseconds. Default 3000, clamped 100 - 600000.</summary>
    public int MinBlockTimeoutMs { get; set; } = 3000;

    /// <summary>Upper bound on the soft block timeout, in milliseconds. Default 15000, clamped to at least <see cref="MinBlockTimeoutMs"/>.</summary>
    public int MaxBlockTimeoutMs { get; set; } = 15000;

    /// <summary>Lower bound on the hard block timeout, in milliseconds. Default 5000, clamped 100 - 600000.</summary>
    public int MinBlockHardTimeoutMs { get; set; } = 5000;

    /// <summary>Upper bound on the hard block timeout, in milliseconds. Default 30000, clamped to at least <see cref="MinBlockHardTimeoutMs"/>.</summary>
    public int MaxBlockHardTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Maximum outstanding requests per peer to cap pipeline growth. Read on each scheduling pass,
    /// also bounded by the peer's advertised request capacity. Values below one are treated as one.
    /// Lowering the limit lets in-flight requests drain. Matches libtorrent's <c>max_out_request_queue</c>.
    /// </summary>
    public int MaxRequestsPerPeer { get; set; } = 500;

    /// <summary>
    /// Maximum number of distinct metadata pieces requested in parallel (ut_metadata).
    ///
    /// <para>
    /// Default is 32, the most the pipeline is clamped to, which covers metadata up to 512 KiB in a
    /// single round. It was 8: enough for the 128 KiB that most torrents carry, but Ubuntu's is 254 KiB
    /// and needed two rounds, and a round costs a full request-response across the internet whether it
    /// carries one piece or thirty. Metadata is at most a few hundred KiB in total and is the only
    /// thing a magnet can do until it arrives, so there is nothing to be gained by rationing it.
    /// </para>
    /// </summary>
    public int MetadataRequestPipeline { get; set; } = 32;

    /// <summary>
    /// How long an individual peer-owned metadata request may remain unanswered. Default is 1.
    ///
    /// <para>
    /// A metadata piece is at most 16 KiB and a magnet cannot start without it, so this trades a little
    /// duplicate traffic for latency. It was 10, which meant one unresponsive peer held a magnet up for
    /// ten seconds at a time - measured against real swarms, three torrents took between thirty-six
    /// seconds and never to acquire metadata while over a hundred willing peers stayed connected
    /// throughout.
    /// </para>
    ///
    /// <para>
    /// Three was then chosen to match libtorrent, but libtorrent applies it as a per-peer throttle - it
    /// will not re-ask the same peer for the same piece inside three seconds, while remaining free to
    /// ask a different one immediately. Here it is the interval between rounds, which makes it the clock
    /// the whole download runs on. Traced against a real magnet, every piece arrived 100-400 ms after a
    /// round fired and no peer ever sent a reject: peers are either prompt or silent, so the wait buys
    /// nothing and the rounds, at 3 seconds plus a 1 second tick, were costing four seconds each. Ubuntu
    /// took 50 seconds to collect sixteen pieces with 143 willing peers connected throughout.
    /// </para>
    /// </summary>
    public int MetadataRequestTimeoutSeconds { get; set; } = 1;

    /// <summary>
    /// How many times the same metadata piece is asked of the same peer before that pair is set aside
    /// in favour of an untried one. Default is 5.
    ///
    /// <para>
    /// This orders who gets asked; it is not a hard ceiling on the total. When every peer has been set
    /// aside for every missing piece the budgets are restored rather than leaving a magnet with willing
    /// peers connected and nothing scheduled - a timeout says a peer was slow, which a slow link
    /// produces just as readily as an unwilling peer, so it is not evidence enough to stop for good.
    /// An explicit reject is, and survives the restoration.
    /// </para>
    ///
    /// <para>
    /// Restoration therefore costs at most one further round per piece per peer before the pairs are
    /// set aside again, and cannot run more often than <see cref="MetadataRequestTimeoutSeconds"/>,
    /// since the restored requests must time out before the budgets can be spent again.
    /// </para>
    /// </summary>
    public int MetadataMaxRequestAttempts { get; set; } = 5;

    /// <summary>
    /// Maximum peer-owned requests for the same metadata piece at once. Each eligible peer chooses a
    /// least-requested missing piece independently; this value bounds duplicate traffic. Default is 3.
    /// Values are clamped to 1-16 by the downloader.
    /// </summary>
    public int MetadataRequestRedundancy { get; set; } = 3;

    /// <summary>
    /// Maximum accepted ut_metadata size in bytes.
    /// Default is 8 MiB, which is far above normal torrent metadata while bounding peer-controlled allocations.
    /// </summary>
    public int MaxMetadataSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Global download speed limit in bytes per second. 0 for unlimited. Negative values are rejected.
    /// </summary>
    public long MaxDownloadSpeed
    {
        get => _maxDownloadSpeed;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxDownloadSpeed = value;
        }
    }

    /// <summary>
    /// Global upload speed limit in bytes per second. 0 for unlimited. Negative values are rejected.
    /// </summary>
    public long MaxUploadSpeed
    {
        get => _maxUploadSpeed;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxUploadSpeed = value;
        }
    }

    /// <summary>
    /// Registers a component that holds live concurrency limiters. The caller owns the registration
    /// and must pair it with <see cref="RemoveConcurrencyLimitListener"/> when it is disposed.
    /// </summary>
    internal void AddConcurrencyLimitListener(IConcurrencyLimitListener listener)
    {
        lock (_concurrencyListenerLock)
        {
            _concurrencyListeners.Add(listener);
        }
    }

    /// <summary>Deregisters a listener added by <see cref="AddConcurrencyLimitListener"/>.</summary>
    internal void RemoveConcurrencyLimitListener(IConcurrencyLimitListener listener)
    {
        lock (_concurrencyListenerLock)
        {
            _concurrencyListeners.Remove(listener);
        }
    }

    private void NotifyConcurrencyLimitsChanged()
    {
        IConcurrencyLimitListener[] listeners;
        lock (_concurrencyListenerLock)
        {
            if (_concurrencyListeners.Count == 0)
            {
                return;
            }

            listeners = [.. _concurrencyListeners];
        }

        // Outside the lock: a listener re-reads settings and takes its own locks, and a setter can be
        // called from any thread. Holding this one across that is how two settings writes deadlock.
        foreach (var listener in listeners)
        {
            listener.OnConcurrencyLimitsChanged();
        }
    }
}

/// <summary>
/// What the alert queue does when it reaches <see cref="AlertSettings.MaxQueueSize"/>.
/// </summary>
/// <remarks>
/// Either way the capacity is a real limit. Alerts the engine treats as critical - a torrent added,
/// removed, finished, errored, or its metadata resolved - are evicted last, but they are not exempt:
/// a queue that could be exceeded by critical alerts is not bounded at all, and the case that fills
/// it is exactly the case where a consumer has stopped reading.
/// </remarks>
public enum AlertOverflowPolicy
{
    /// <summary>
    /// Discard the oldest droppable alert to make room for the new one. Favours recency, which is
    /// what a consumer watching live state usually wants.
    /// </summary>
    DropOldest,

    /// <summary>
    /// Refuse the new alert and keep the backlog. Favours the earliest record of what happened, which
    /// is what a consumer auditing a sequence usually wants.
    /// </summary>
    DropNewest
}

/// <summary>
/// Settings for the engine's alert queue.
/// </summary>
public sealed class AlertSettings
{
    private int _maxQueueSize = 10000;

    /// <summary>
    /// Most alerts held for a consumer that has not read them yet. Default is 10,000; values below
    /// one are rejected. Read on each post, so lowering it takes effect on the next overflow.
    /// </summary>
    /// <remarks>
    /// Each queued alert holds a reference to whatever it describes, a torrent included, so the queue
    /// is the one place a consumer that stops calling
    /// <see cref="IAlerts.GetAlertsAsync"/> can cost the engine unbounded memory.
    /// </remarks>
    public int MaxQueueSize
    {
        get => Volatile.Read(ref _maxQueueSize);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            Volatile.Write(ref _maxQueueSize, value);
        }
    }

    /// <summary>Which alert gives way when the queue is full. Default is <see cref="AlertOverflowPolicy.DropOldest"/>.</summary>
    public AlertOverflowPolicy OverflowPolicy { get; set; } = AlertOverflowPolicy.DropOldest;
}

/// <summary>
/// Configuration settings for the BitTorrent client.
/// </summary>
/// <remarks>
/// <para><b>Runtime changes:</b> scheduling limits such as request depth, connection timeout bounds,
/// hash/write concurrency and web-seed concurrency apply to existing transfers. Existing work is
/// allowed to finish when a concurrency limit is lowered. Other settings that create resources
/// (socket bindings, queue capacities, storage caches, worker counts and session persistence) must
/// be set before those resources are created; property writes do not recreate them.</para>
/// <para>There is no atomicity <i>across</i> properties: the engine may briefly observe a mix
/// of old and new values while several properties are being changed, so related settings
/// (e.g. a speed limit and its slot count) should be treated as eventually consistent rather
/// than as one transaction.</para>
/// <para>The sub-setting objects (<see cref="Connection"/>, <see cref="Transfer"/> and the rest) are
/// get-only by design. Running components hold a reference to the instance they were given, so
/// replacing one would leave them reading the object nobody can see any more - the exact staleness
/// the live re-reads above exist to avoid. Mutate their properties instead; in an object initializer
/// that is <c>Connection = { EnableUtpIn = true }</c>.</para>
/// </remarks>
public sealed class Settings
{
    /// <summary>Settings for the engine's alert queue.</summary>
    public AlertSettings Alerts { get; } = new();

    /// <summary>Settings for peer-to-peer network connections.</summary>
    public ConnectionSettings Connection { get; } = new();

    /// <summary>Settings for Distributed Hash Table (DHT).</summary>
    public DhtSettings Dht { get; } = new();

    /// <summary>Settings for file management and storage.</summary>
    public FilesSettings Files { get; } = new();

    /// <summary>Maximum number of unique known peers to keep in cache.</summary>
    public int MaxKnownPeersCache { get; set; } = 2000;

    /// <summary>Maximum number of peers to request from a tracker in one announce.</summary>
    public uint MaxPeersPerTrackerRequest { get; set; } = 200;

    /// <summary>
    /// Whether UDP announces carry the tracker URL's path and query as BEP 41 options.
    ///
    /// <para>
    /// On by default, because without it a UDP announce conveys only a host and port: a tracker that
    /// authenticates on a passkey in the URL cannot identify the announce, and the failure is silent -
    /// a working socket and no peers. Sending the options makes such trackers work with no
    /// configuration.
    /// </para>
    ///
    /// <para>
    /// The cost is that announces grow past the 98 bytes of BEP 15 alone. That is the extension point
    /// BEP 41 defines, and implementations read fixed offsets, so trailing bytes are ignored by
    /// trackers that do not support it. Turn this off if a particular tracker rejects the longer
    /// packet.
    /// </para>
    /// </summary>
    public bool SendUdpTrackerUrlData { get; set; } = true;

    /// <summary>The client's unique 20-byte Peer ID (BEP 20).</summary>
    public byte[] PeerId { get; set; } = new byte[20];

    /// <summary>Settings for network proxy.</summary>
    public ProxySettings Proxy { get; } = new();

    /// <summary>Settings for queue management and auto-stop rules.</summary>
    public QueueSettings Queue { get; } = new();

    /// <summary>Settings for session persistence (optional, disabled by default).</summary>
    public SessionSettings Session { get; } = new();

    /// <summary>Settings for data transfer.</summary>
    public TransferSettings Transfer { get; } = new();
}
