using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Peers;

/*
 * THREAD-SAFETY GUIDELINES FOR THIS FILE:
 *
 * Synchronization Strategy:
 *
 * 1. ConcurrentDictionary: Primary data structures (_connectedPeers, _connectedEndpoints, etc.)
 *    - Thread-safe for individual operations
 *    - Compound operations (check-then-add) need careful ordering
 *
 * 2. Interlocked: For _connectedPeersCount counter
 *    - Always update atomically with Increment/Decrement
 *
 * 3. Channel<T>: For connection queue (_connectionQueue)
 *    - Bounded with backpressure to prevent resource exhaustion
 *    - Single reader pattern for ProcessConnectionQueueAsync
 *
 * KEY INVARIANTS:
 * - _connectedEndpoints is the authoritative duplicate-connection gate: a connection may only
 *   be registered in _connectedPeers if its endpoint was successfully added to _connectedEndpoints
 *   first (TryAdd), and only the owning PeerCommunication may remove its entry on disconnect.
 * - All endpoint keys are normalized (IPv4-mapped IPv6 -> plain IPv4); PeerCommunication.RemoteEndPoint
 *   normalizes on assignment.
 * - _connectedPeersCount reflects _connectedPeers.Count
 * - Connection queue enforces rate limiting (5 connections/second)
 *
 * POLICY COLLABORATORS:
 * - PeerChoker owns upload-slot and optimistic-unchoke policy.
 * - PeerExchangeCoordinator owns BEP 11 payload construction and fan-out.
 * - PeerHealthMonitor owns idle and slow-peer disconnect policy.
 */

internal class PeerManager : IInternalPeers, IPeerListener, IAsyncDisposable
{
    private const int AllowedFastSetSize = 10;
    // Periodic task intervals
    private const int MainLoopIntervalMs = 1000;

    private const int PendingConnectionTimeoutMs = 10000;
    /// <summary>
    /// Floor for how often peer exchange runs. The configured interval is honoured above this; the
    /// tick loop cannot notice anything finer than a second.
    /// </summary>
    private const int MinPexIntervalSeconds = 1;

    // 1 second base loop
    private const int WatchdogIntervalSeconds = 5;


    // Track active connection attempts for clean shutdown
    private readonly ConcurrentDictionary<Task, byte> _activeConnectionTasks = new();

    // Maps each connected remote endpoint to the PeerCommunication that owns it.
    // Authoritative duplicate gate - see thread-safety notes at the top of this file.
    private readonly ConcurrentDictionary<IPEndPoint, PeerCommunication> _connectedEndpoints = new();
    private readonly Lock _connectedEndpointRegistrationLock = new();

    // Maps each connected remote peer id (hex) to the PeerCommunication that owns it.
    // Endpoints alone cannot correlate an incoming connection (peer's ephemeral source port)
    // with an outgoing one (peer's listen port), so this second gate dedups by identity once
    // the handshake reveals the remote peer id.
    private readonly ConcurrentDictionary<string, PeerCommunication> _connectedPeerIds = new();
    private readonly ConcurrentDictionary<PeerCommunication, byte> _connectedPeers = new();
    private readonly ConcurrentDictionary<PeerCommunication, byte> _connectingPeers = new();

    // Connection throttling: rate limiter to prevent burst connections
    private readonly Channel<ConnectionRequest> _connectionQueue;

    private readonly IGeoIpService _geoIp;
    private readonly IConnectionGovernor _governor;
    private readonly ConcurrentDictionary<IPEndPoint, PeerHistory> _knownPeersCache = new();
    private readonly ILogger<PeerManager> _logger;
    private readonly IPeerCommunicationFactory _peerFactory;
    private readonly ConcurrentDictionary<IPEndPoint, PeerCommunication> _peerSources = new();

    // Value is timestamp (Environment.TickCount64) when connection was initiated
    private readonly ConcurrentDictionary<IPEndPoint, long> _pendingConnections = new();

    private readonly Settings _settings;
    private readonly DateTimeOffset _startTime;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private readonly PeerChoker _choker;
    private readonly PeerExchangeCoordinator _peerExchange;
    private readonly PeerHealthMonitor _peerHealth;
    private readonly PeerManagerFailureTracker _failureTracker = new();

    // Track pending connections separately
    // O(1) lookup for connected endpoints
    private int _connectedPeersCount = 0;
    private int _knownPeersCacheCount = 0;

    private int _connectingPeersCount = 0;
    private Task? _connectionQueueTask;
    private AtomicDisposal _disposal = new();
    // Stored as UTC ticks so cross-thread access is a single 64-bit read/write via Volatile:
    // the main loop writes the penalty window while connection tasks read it concurrently,
    // and a raw 16-byte DateTimeOffset field could tear.
    private long _globalUtpPenaltyUntilUtcTicks = DateTimeOffset.MinValue.UtcTicks;

    private int _holepunchCount = 0;

    private long _holepunchWindowStart = Environment.TickCount64;

    /// <summary>
    /// How many rendezvous have been refused in the current window. Exists so the limit can report
    /// itself once rather than once per refusal: relays keep asking after it is reached, which is the
    /// normal case, so a line each turned a limit that was working into several hundred warnings.
    /// </summary>
    private int _holepunchRefused = 0;

    private int _lastAggregateSpeed = 0;


    private DateTimeOffset _lastSpeedLog = DateTimeOffset.MinValue;

    private CancellationTokenSource? _mainLoopCts;

    private Task? _mainLoopTask;

    private int _peakSpeed = 0;

    private DateTimeOffset _stableSpeedSince = DateTimeOffset.MinValue;

    public PeerManager(Torrent torrent, IGeoIpService geoIp, IPeerCommunicationFactory peerFactory, TimeProvider timeProvider, IConnectionGovernor governor)
        : this(torrent, geoIp, peerFactory, timeProvider, governor, NullLogger<PeerManager>.Instance)
    {
    }

    internal PeerManager(Torrent torrent, IGeoIpService geoIp, IPeerCommunicationFactory peerFactory, TimeProvider timeProvider, IConnectionGovernor governor, ILogger<PeerManager> logger)
    {
        _torrent = torrent;
        _logger = logger;
        _settings = torrent.Settings;
        _governor = governor;

        _geoIp = geoIp;
        _peerFactory = peerFactory;
        _timeProvider = timeProvider;
        _startTime = _timeProvider.GetUtcNow();
        _choker = new PeerChoker(_torrent, _timeProvider, _logger);
        _peerExchange = new PeerExchangeCoordinator(_torrent, _knownPeersCache, _logger);
        _peerHealth = new PeerHealthMonitor(_torrent, _logger);

        // Initialize adaptive timeout based on settings
        var connSettings = _settings.Connection;
        AdaptiveTimeout = new AdaptiveTimeout(
            minTimeoutMs: connSettings.MinConnectionTimeoutMs,
            maxTimeoutMs: connSettings.MaxConnectionTimeoutMs,
            initialTimeoutMs: connSettings.InitialConnectionTimeoutMs,
            timeProvider: _timeProvider);

        // Initialize connection throttling with Wait mode to prevent silent data loss
        _connectionQueue = Channel.CreateBounded<ConnectionRequest>(new BoundedChannelOptions(Math.Max(100, connSettings.MaxConnectionQueueSize))
        {
            FullMode = BoundedChannelFullMode.Wait, // Wait instead of dropping - prevents silent connection loss
            SingleReader = true // Optimization: only one reader (ProcessConnectionQueueAsync)
        });
    }

    /// <summary>
    /// Adaptive timeout manager for connection timeouts.
    /// Adjusts timeouts based on observed network conditions.
    /// </summary>
    public AdaptiveTimeout AdaptiveTimeout { get; }

    private readonly record struct ConnectionRequest(string Ip, int Port, bool ForceUtp);
    public int ConnectedCount => _connectedPeersCount;

    /// <inheritdoc />
    public int Add(IEnumerable<IPEndPoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Counted before and after rather than reported by AddPeersInternal, because "accepted" means
        // "is now a candidate we did not already know", which is what the caller can act on. A peer
        // already in the list, blocked, or filtered out is not a failure worth distinguishing.
        int before = _knownPeersCache.Count;
        AddPeers(endpoints, PeerSourceKind.Manual);
        return Math.Max(0, _knownPeersCache.Count - before);
    }

    public Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake)
    {
        return AddIncomingTcpPeerCoreAsync(client, handshake, encryption: null);
    }

    public Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake, ProtocolEncryption encryption)
    {
        return AddIncomingTcpPeerCoreAsync(client, handshake, encryption);
    }

    public async Task AddIncomingPeerAsync(
        Stream stream,
        byte[] handshake,
        IPEndPoint? remote = null,
        ProtocolEncryption? encryption = null)
    {
        remote = NetworkUtils.NormalizeEndPoint(remote);

        // Reject if force proxy is enabled (incoming connections are not proxied)
        if (_torrent.Settings.Proxy.ForceProxy && _torrent.Settings.Proxy.Type != ProxyType.None)
        {
            _logger.LogDebug("Rejecting incoming uTP connection - ForceProxy is enabled");
            stream.Close();
            return;
        }

        // Check blocklist first
        if (_torrent.Blocklist?.IsBlocked(remote) == true)
        {
            _logger.LogDebug("Blocked incoming connection from {Remote} (blocklist)", remote);
            stream.Close();
            return;
        }

        // Calculate priority early for BEP 40 decisions
        uint incomingPriority = remote != null
            ? PeerPriority.Calculate(remote.Address, _torrent.Hash.ToArray())
            : 0;

        // Check connection limits for incoming connections
        int currentConnections = Interlocked.CompareExchange(ref _connectedPeersCount, 0, 0);
        if (currentConnections >= _settings.Connection.MaxPeersPerTorrent)
        {
            // BEP 40: Try to replace lowest priority peer if incoming has higher priority
            var lowestPriorityPeer = TryGetLowestPriorityPeer();
            if (lowestPriorityPeer != null && incomingPriority > lowestPriorityPeer.Priority)
            {
                _logger.LogDebug("BEP 40: Disconnecting low-priority peer {LowestPeer} (priority={LowestPriority}) for higher-priority incoming peer (priority={IncomingPriority})", lowestPriorityPeer.RemoteEndPoint, lowestPriorityPeer.Priority, incomingPriority);
                await lowestPriorityPeer.CloseAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug("Rejecting incoming stream connection - at limit ({MaxPeers})", _settings.Connection.MaxPeersPerTorrent);
                stream.Close();
                return;
            }
        }

        // Check global governor limits
        if (!_governor.TryAcquireConnectionSlot())
        {
            _logger.LogDebug("Rejecting incoming connection - global limit reached ({MaxConnections})", _settings.Connection.MaxConnections);
            stream.Close();
            return;
        }

        PeerCommunication peer;
        if (remote != null)
        {
            peer = _peerFactory.Create(_torrent, this, _timeProvider, stream, remote);
        }
        else
        {
            peer = _peerFactory.Create(_torrent, this, _timeProvider, stream); // Will have Unknown endpoint
        }

        // Authoritative duplicate gate: claim the endpoint before registering anything else.
        // If another live connection already owns this endpoint, keep it and drop the new one.
        if (!TryRegisterConnectedEndpoint(peer))
        {
            _governor.ReleaseConnectionSlot();
            _logger.LogDebug("Rejecting duplicate incoming connection from {RemoteEndPoint}", peer.RemoteEndPoint);
            stream.Close();
            return;
        }

        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            // BEP 40: Use already calculated priority
            peer.Priority = incomingPriority;

            // Refused in both directions, or a peer that has been dropped for serving bad data simply
            // connects back and carries on.
            if (IsRefusedForBadData(peer.RemoteEndPoint.Address))
            {
                _logger.LogDebug(
                    "Rejecting incoming connection from {RemoteEndPoint} - it has served bad data before",
                    peer.RemoteEndPoint);
                // The peer has not joined _connectedPeers yet, so CloseAsync cannot release the
                // governor slot (ConnectionClosedAsync only releases slots owned by registered
                // peers). Undo the provisional endpoint claim and slot explicitly.
                UnregisterConnectedEndpoint(peer);
                _governor.ReleaseConnectionSlot();
                stream.Close();
                return;
            }

            // Tracked by the endpoint the connection arrived on, which for an incoming one is an
            // ephemeral source port. Useful for this connection's own uTP history and nothing else:
            // it is not an address anybody can dial, so it is not marked connectable and not offered
            // to anyone. Where the peer really listens arrives later, in its BEP 10 handshake.
            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint, isListenAddress: false);
            if (peer.UtpStream != null)
            {
                history.RegisterUtpSuccess(_timeProvider.GetUtcNow());
            }
        }

        if (!await peer.SetHandshakeReceivedAsync(handshake).ConfigureAwait(false))
        {
            _logger.LogDebug("Rejecting peer {RemoteEndPoint} - invalid handshake", peer.RemoteEndPoint);
            UnregisterConnectedEndpoint(peer);
            _governor.ReleaseConnectionSlot();
            stream.Close();
            return;
        }

        // The add always succeeds because peer is a freshly created instance (reference equality).
        // Duplicate endpoints were already rejected by the endpoint gate above.
        _connectedPeers.TryAdd(peer, 0);
        Interlocked.Increment(ref _connectedPeersCount);

        peer.Start(stream, encryption);
    }

    private async Task AddIncomingTcpPeerCoreAsync(System.Net.Sockets.TcpClient client, byte[] handshake, ProtocolEncryption? encryption)
    {
        // Reject if force proxy is enabled (incoming connections are not proxied)
        if (_torrent.Settings.Proxy.ForceProxy && _torrent.Settings.Proxy.Type != ProxyType.None)
        {
            _logger.LogDebug("Rejecting incoming TCP connection - ForceProxy is enabled");
            client.Close();
            return;
        }

        // Calculate priority early for BEP 40 decisions
        var remoteEp = NetworkUtils.NormalizeEndPoint(client.Client.RemoteEndPoint as IPEndPoint);

        // Check blocklist first
        if (_torrent.Blocklist?.IsBlocked(remoteEp) == true)
        {
            _logger.LogDebug("Blocked incoming connection from {RemoteEp} (blocklist)", remoteEp);
            client.Close();
            return;
        }

        if (remoteEp is not null && IsRefusedForBadData(remoteEp.Address))
        {
            _logger.LogDebug(
                "Rejecting incoming TCP connection from {RemoteEp} - it has served bad data before",
                remoteEp);
            client.Close();
            return;
        }
        uint incomingPriority = remoteEp != null
            ? PeerPriority.Calculate(remoteEp.Address, _torrent.Hash.ToArray())
            : 0;

        // Check connection limits for incoming connections
        int currentConnections = Interlocked.CompareExchange(ref _connectedPeersCount, 0, 0);
        if (currentConnections >= _settings.Connection.MaxPeersPerTorrent)
        {
            // BEP 40: Try to replace lowest priority peer if incoming has higher priority
            var lowestPriorityPeer = TryGetLowestPriorityPeer();
            if (lowestPriorityPeer != null && incomingPriority > lowestPriorityPeer.Priority)
            {
                _logger.LogDebug("BEP 40: Disconnecting low-priority peer {LowestPeer} (priority={LowestPriority}) for higher-priority incoming peer (priority={IncomingPriority})", lowestPriorityPeer.RemoteEndPoint, lowestPriorityPeer.Priority, incomingPriority);
                await lowestPriorityPeer.CloseAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug("Rejecting incoming connection - at limit ({MaxPeers})", _settings.Connection.MaxPeersPerTorrent);
                client.Close();
                return;
            }
        }

        // Check global governor limits
        if (!_governor.TryAcquireConnectionSlot())
        {
            _logger.LogDebug("Rejecting incoming connection - global limit reached ({MaxConnections})", _settings.Connection.MaxConnections);
            client.Close();
            return;
        }

        var peer = _peerFactory.Create(_torrent, this, _timeProvider, client);

        // Authoritative duplicate gate: claim the endpoint before registering anything else.
        // If another live connection already owns this endpoint, keep it and drop the new one.
        if (!TryRegisterConnectedEndpoint(peer))
        {
            _governor.ReleaseConnectionSlot();
            _logger.LogDebug("Rejecting duplicate incoming connection from {RemoteEndPoint}", peer.RemoteEndPoint);
            client.Close();
            return;
        }

        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            // BEP 40: Calculate canonical peer priority
            peer.Priority = incomingPriority;

            // See AddIncomingPeerAsync: an incoming connection's source port is not a dialable address.
            GetOrAddKnownPeerHistory(peer.RemoteEndPoint, isListenAddress: false);
        }

        if (!await peer.SetHandshakeReceivedAsync(handshake).ConfigureAwait(false))
        {
            _logger.LogDebug("Rejecting peer {RemoteEndPoint} - invalid handshake", peer.RemoteEndPoint);
            UnregisterConnectedEndpoint(peer);
            _governor.ReleaseConnectionSlot();
            client.Close();
            return;
        }

        // The add always succeeds because peer is a freshly created instance (reference equality).
        // Duplicate endpoints were already rejected by the endpoint gate above.
        _connectedPeers.TryAdd(peer, 0);
        Interlocked.Increment(ref _connectedPeersCount);

        peer.Start(client.GetStream(), encryption);
    }

    public void AddPeers(IEnumerable<IPEndPoint> peers, PeerSourceKind sourceKind = PeerSourceKind.Unknown, PeerCommunication? source = null)
    {
        AddPeersInternal(peers, sourceKind, source, flags: null);
    }

    public Task AddConnectedPeerAsync(Stream stream, bool initiator, IPEndPoint? remote = null, PeerSourceKind sourceKind = PeerSourceKind.Unknown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return AddConnectedPeerCoreAsync(stream, initiator, remote, sourceKind, cancellationToken);
    }

    public async Task BroadcastHaveAsync(int pieceIndex)
    {
        var tasks = new List<Task>();
        foreach (var kvp in _connectedPeers)
        {
            var p = kvp.Key;
            var msg = new PeerMessage(MessageId.Have) { HavePieceIndex = pieceIndex };
            tasks.Add(SendHaveWithExceptionHandlingAsync(p, msg));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task ConnectionClosedAsync(IPeerCommunication peer, int code)
    {
        var p = (PeerCommunication)peer;
        var fileTransfer = _torrent.FileTransferInternal;
        if (fileTransfer?.IsDisposed == false)
        {
            fileTransfer.UnregisterPeerAvailability(p);
        }

        // BEP 16: Clean up superseed state
        _torrent.SuperSeedManager.HandlePeerDisconnected(p);

        bool wasRegistered = _connectedPeers.TryRemove(p, out _);
        if (wasRegistered)
        {
            Interlocked.Decrement(ref _connectedPeersCount);
            // Release global connection slot
            _governor.ReleaseConnectionSlot();
        }
        _peerHealth.Remove(p);

        RecordConnectionOutcome(p, wasRegistered);

        // Only remove the endpoint/id entries if this peer owns them. A rejected duplicate
        // closing must not evict the surviving connection's entries, or the dedup gates would
        // let a new connection through while the survivor is still alive.
        UnregisterConnectedEndpoint(p);
        UnregisterConnectedPeerId(p);

        _logger.LogDebug("Connection closed to {RemoteEndPoint} (code={Code}), downloaded={Downloaded}B, uploaded={Uploaded}B, strikes={Strikes}, remaining peers={RemainingPeers}",
            p.RemoteEndPoint, code, p.Downloaded, p.Uploaded, p.Strikes, _connectedPeersCount);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Settles what a connection was worth, now that it is over and the answer is known.
    ///
    /// <para>
    /// A connection that moved bytes earns its peer a clean slate. One that moved none was not worth
    /// making, however cleanly it connected, and is counted against the peer exactly like a dial that
    /// never landed - which is what makes the existing backoff grow from five seconds towards five
    /// minutes instead of staying at its first step forever.
    /// </para>
    ///
    /// <para>
    /// Seeding is where the absence of this showed. Two seeds have nothing to give each other, so the
    /// connection completes, sits idle and closes, and nothing about that discouraged dialling the same
    /// peer again moments later: a six minute seeding run made 6298 outgoing connections to 1515 peers,
    /// several of them thirty-nine times. Leeching hid it, because there the connections stay up and do
    /// work.
    /// </para>
    /// </summary>
    /// <param name="p">The connection that has just closed.</param>
    /// <param name="wasRegistered">
    /// Whether this connection was ever a live peer. A duplicate rejected before it registered has
    /// nothing to say about whether the peer is worth dialling - the connection that displaced it is
    /// very likely still running.
    /// </param>
    private void RecordConnectionOutcome(PeerCommunication p, bool wasRegistered)
    {
        if (!wasRegistered || p.RemoteEndPoint is not { } endpoint)
        {
            return;
        }

        // For an incoming connection, RemoteEndPoint is its ephemeral source port. If cache pruning
        // removed the original non-dialable entry while the connection was alive, recreating it with
        // the default would turn it into a PEX candidate. Preserve how this connection was opened.
        var history = GetOrAddKnownPeerHistory(endpoint, isListenAddress: p.IsOutgoing);

        // Either direction counts. Uploading is the whole point of seeding, and until now only a piece
        // we received marked a peer as having exchanged data - so a seeder that uploaded for an hour
        // still had every one of its peers on record as never having given it anything.
        if (p.Downloaded > 0 || p.Uploaded > 0 || p.HasExchangedUsefulData)
        {
            history.ExchangedData = true;
            history.FruitlessConnectionCount = 0;
            history.NextConnectAttempt = DateTimeOffset.MinValue;
            return;
        }

        // Backing off an address nobody can dial would be meaningless: an incoming connection's source
        // port belongs to that one connection and is never a candidate in the first place.
        if (!history.IsListenAddress)
        {
            return;
        }

        history.FruitlessConnectionCount++;
        ApplyConnectionBackoff(history);
    }

    /// <summary>
    /// How many pieces a peer may contribute bad data to before it is left alone. Attribution is not
    /// exact - a piece is assembled from several peers and all of them are counted - so this is set
    /// where a peer that keeps appearing in failures is caught while one that appeared beside a bad
    /// peer once or twice is not.
    /// </summary>
    private const int MaxHashFailuresPerAddress = 3;

    /// <summary>
    /// Bounded so a swarm cannot make this grow without limit. Far above the number of addresses that
    /// ever serve bad data in one session; reaching it at all would itself be the anomaly.
    /// </summary>
    private const int MaxTrackedHashFailureAddresses = 4096;

    private readonly ConcurrentDictionary<IPAddress, int> _hashFailuresByAddress = new();

    /// <inheritdoc />
    public bool RecordHashFailure(PeerCommunication peer)
    {
        if (peer.RemoteEndPoint?.Address is not { } address)
        {
            return false;
        }

        address = NormaliseForBadDataKey(address);

        if (_hashFailuresByAddress.Count >= MaxTrackedHashFailureAddresses
            && !_hashFailuresByAddress.ContainsKey(address))
        {
            return false;
        }

        int failures = _hashFailuresByAddress.AddOrUpdate(address, 1, static (_, count) => count + 1);
        return failures >= MaxHashFailuresPerAddress;
    }

    /// <summary>Whether this address has served enough bad data to be left alone.</summary>
    private bool IsRefusedForBadData(IPAddress address)
        => _hashFailuresByAddress.TryGetValue(NormaliseForBadDataKey(address), out int failures)
            && failures >= MaxHashFailuresPerAddress;

    /// <summary>
    /// One host, one key. The same peer reaches us as 1.2.3.4 or ::ffff:1.2.3.4 depending on which
    /// socket saw it, and those are different <see cref="IPAddress"/> values: recording under one and
    /// looking up the other quietly never matches. Done here rather than at the call sites so that
    /// every recorder and every check agrees without having to remember to.
    /// </summary>
    private static IPAddress NormaliseForBadDataKey(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public void ConnectTo(string ip, int port, bool forceUtp = false)
    {
        // Check blocklist first
        if (_torrent.Blocklist?.IsBlocked(ip) == true)
        {
            _logger.LogDebug("Blocked outgoing connection to {Ip}:{Port} (blocklist)", ip, port);
            return;
        }

        // Hanging up on a peer that serves bad data accomplishes nothing if we dial it again a moment
        // later, which is exactly what happened: the count lived on the connection, so reconnecting
        // cleared it, and the peer went back to the front of the queue having just proved itself
        // useless. Worse since connections are now judged by bytes moved - corrupt blocks are still
        // bytes, so a peer feeding us rubbish looked like a productive one.
        if (IPAddress.TryParse(ip, out var parsedForBadData) && IsRefusedForBadData(parsedForBadData))
        {
            _logger.LogDebug("Not dialling {Ip}:{Port} - it has served bad data before", ip, port);
            return;
        }

        // Check global and local connection limits before attempting new connections
        int currentConnections = Interlocked.CompareExchange(ref _connectedPeersCount, 0, 0);
        int currentConnecting = Interlocked.CompareExchange(ref _connectingPeersCount, 0, 0);

        // Limit active connections
        if (currentConnections >= _settings.Connection.MaxPeersPerTorrent && !forceUtp)
        {
            return;
        }

        // Limit pending/half-open connections (prevents router saturation)
        if (currentConnecting >= _settings.Connection.MaxPendingConnections && !forceUtp)
        {
            return;
        }

        // Check global governor limits (unless forceUtp/holepunch)
        if (!forceUtp)
        {
            if (_governor.ActiveConnections >= _settings.Connection.MaxConnections)
            {
                return;
            }

            if (_governor.PendingConnections >= _settings.Connection.MaxPendingConnections)
            {
                return;
            }
        }

        // For holepunch (forceUtp=true), connect immediately - it's time-sensitive
        if (forceUtp)
        {
            // Rate limit holepunch attempts to prevent DoS via Relay
            long tickCount = Environment.TickCount64;
            long windowStart = Interlocked.Read(ref _holepunchWindowStart);
            if (tickCount - windowStart > 60000)
            {
                int refused = Interlocked.Exchange(ref _holepunchRefused, 0);
                if (refused > 1)
                {
                    _logger.LogDebug(
                        "Refused {Count} further holepunch requests over the last minute", refused - 1);
                }

                Interlocked.Exchange(ref _holepunchWindowStart, tickCount);
                Interlocked.Exchange(ref _holepunchCount, 0);
            }

            if (Interlocked.Increment(ref _holepunchCount) > _settings.Connection.MaxHolepunchPerMinute)
            {
                // One line per window, not per refusal. A relay that has hit the limit goes on asking,
                // so this reported every rejection: several hundred warnings in a few minutes, all
                // saying the same thing about a limit that was doing its job. The rest are counted and
                // summarised when the window rolls over.
                if (Interlocked.Increment(ref _holepunchRefused) == 1)
                {
                    _logger.LogWarning(
                        "Holepunch rate limit of {Limit}/minute reached; refusing further rendezvous this minute (first was {Ip}:{Port})",
                        _settings.Connection.MaxHolepunchPerMinute,
                        ip,
                        port);
                }
                return;
            }

            ConnectToInternal(ip, port, forceUtp);
            return;
        }

        // Check if we already have this connection pending or established
        if (!IPAddress.TryParse(ip, out var ipAddr))
        {
            return;
        }

        // Normalize IPv4-mapped IPv6 so dedup keys match what connections store
        if (ipAddr.IsIPv4MappedToIPv6)
        {
            ipAddr = ipAddr.MapToIPv4();
            ip = ipAddr.ToString();
        }
        var endpoint = new IPEndPoint(ipAddr, port);

        // O(1) check if we're already connected to this peer
        if (_connectedEndpoints.ContainsKey(endpoint))
        {
            return; // Already connected
        }

        // Enforce per-peer cooldown to reduce churn
        var history = GetOrAddKnownPeerHistory(endpoint);
        var now = _timeProvider.GetUtcNow();
        if (!forceUtp && history.NextConnectAttempt > now)
        {
            return;
        }

        if (!_pendingConnections.TryAdd(endpoint, Environment.TickCount64))
        {
            // Already pending
            return;
        }

        // Record attempt in history
        history.LastAttempt = now;

        // Queue the connection request
        if (!_connectionQueue.Writer.TryWrite(new ConnectionRequest(ip, port, forceUtp)))
        {
            _pendingConnections.TryRemove(endpoint, out _);
            ReportConnectionQueueOverflow();
        }
    }

    private long _queueOverflowWindowStart = Environment.TickCount64;
    private int _queueOverflowCount;

    /// <summary>
    /// Says the connection queue is overflowing, once a window rather than once a request.
    ///
    /// <para>
    /// Discarding candidates when the queue is full is the right thing to do - the queue drains at a
    /// fixed rate and a swarm can offer peers far faster than that - but a line each made this 55% of
    /// a three minute log: 22,737 of 41,189 lines, 14,778 of them inside ten seconds while the DHT was
    /// delivering. The rate is the interesting quantity, not the individual address, and a count says
    /// it better than thousands of repetitions.
    /// </para>
    /// </summary>
    private void ReportConnectionQueueOverflow()
    {
        long now = Environment.TickCount64;
        long windowStart = Interlocked.Read(ref _queueOverflowWindowStart);
        int dropped = Interlocked.Increment(ref _queueOverflowCount);

        if (now - windowStart < 10000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _queueOverflowWindowStart, now, windowStart) != windowStart)
        {
            return;
        }

        Interlocked.Exchange(ref _queueOverflowCount, 0);
        _logger.LogDebug(
            "Connection queue full: discarded {Count} candidate(s) in the last {Seconds}s - peers are arriving faster than they can be dialled",
            dropped,
            (now - windowStart) / 1000);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync().ConfigureAwait(false);
            _connectionQueue.Writer.TryComplete();
            _mainLoopCts?.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake)
    {
        var p = (PeerCommunication)peer;
        try
        {
            // BEP 10 'p' is the only way an incoming peer's real address becomes known: it arrived on
            // an ephemeral port, so until now there was nothing about it worth keeping or passing on.
            // Recorded as learned from the extension protocol, which is exactly what happened.
            if (p.RemoteListenEndPoint is { } listenEndPoint)
            {
                GetOrAddKnownPeerHistory(listenEndPoint).UpdateSource(PeerSourceKind.Ltep);
            }

            _torrent.MetadataDownloadInternal?.PeerConnected(p);
            if (!_torrent.HasMetadata &&
                _torrent.MetadataDownloadInternal?.Active == true &&
                p.RemoteSupportsExtensions &&
                p.RemoteExtensions?.MessageIds.ContainsKey(UtMetadata.Name) == true)
            {
                _logger.LogDebug(
                    "Peer {RemoteEndPoint} supports ut_metadata (id={MessageId}, size={MetadataSize})",
                    p.RemoteEndPoint,
                    p.UtMetadata.RemoteMessageId,
                    p.RemoteExtensions.MetadataSize);
                FireAndForget(p.SetInterestedAsync(true), "SetInterested (Metadata)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtendedHandshakeFinished error for {RemoteEndPoint}", p.RemoteEndPoint);
        }
        return Task.CompletedTask;
    }

    public async Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data)
    {
        var p = (PeerCommunication)peer;
        try
        {
            if (p.UtMetadata.LocalMessageId == type)
            {
                var (Node, Consumed) = BencodeParser.ParseWithConsumed(data);
                var node = Node;
                int consumed = Consumed;
                if (node is BDict dict)
                {
                    var msgType = dict.GetLong("msg_type") ?? 0;
                    var piece = (int)(dict.GetLong("piece") ?? 0);
                    var totalSize = (int?)dict.GetLong("total_size");

                    if (totalSize.HasValue && _torrent.MetadataDownloadInternal != null)
                    {
                        try { _torrent.MetadataDownloadInternal.InitializeMetadataBuffer(totalSize.Value); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Metadata buffer init error");
                            _torrent.FireErrorEvent(new TorrentException("Metadata buffer initialization error.", _torrent.Hash, ex));
                        }
                    }

                    if (msgType == (int)UtMetadata.MessageType.Data)
                    {
                        byte[] payload = data.Length > consumed ? data[consumed..] : [];
                        if (_torrent.MetadataDownloadInternal != null)
                        {
                            await _torrent.MetadataDownloadInternal.MetadataPieceReceivedAsync(p, piece, payload).ConfigureAwait(false);
                        }
                    }
                    else if (msgType == (int)UtMetadata.MessageType.Request)
                    {
                        _torrent.MetadataDownloadInternal?.MetadataRequestReceived(p, piece);
                    }
                    else if (msgType == (int)UtMetadata.MessageType.Reject)
                    {
                        _torrent.MetadataDownloadInternal?.MetadataRejectReceived(p, piece);
                    }
                }
            }
            else if (_torrent.MetadataDownloadInternal?.Active == true)
            {
                // Fallback: some peers may respond with mismatched ext IDs. Detect ut_metadata by payload shape.
                var (Node, Consumed) = BencodeParser.ParseWithConsumed(data);
                if (Node is BDict dict && dict.GetLong("msg_type") is long msgTypeVal)
                {
                    var msgType = (int)msgTypeVal;
                    var piece = (int)(dict.GetLong("piece") ?? 0);
                    var totalSize = (int?)dict.GetLong("total_size");

                    _logger.LogWarning(
                        "Received ut_metadata message with mismatched ext id {ExtId} (expected {ExpectedId}) from {RemoteEndPoint}",
                        type,
                        p.UtMetadata.LocalMessageId,
                        p.RemoteEndPoint);

                    if (totalSize.HasValue && _torrent.MetadataDownloadInternal != null)
                    {
                        try { _torrent.MetadataDownloadInternal.InitializeMetadataBuffer(totalSize.Value); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Metadata buffer init error");
                            _torrent.FireErrorEvent(new TorrentException("Metadata buffer initialization error.", _torrent.Hash, ex));
                        }
                    }

                    if (msgType == (int)UtMetadata.MessageType.Data)
                    {
                        byte[] payload = data.Length > Consumed ? data[Consumed..] : [];
                        if (_torrent.MetadataDownloadInternal != null)
                        {
                            await _torrent.MetadataDownloadInternal.MetadataPieceReceivedAsync(p, piece, payload).ConfigureAwait(false);
                        }
                    }
                    else if (msgType == (int)UtMetadata.MessageType.Request)
                    {
                        _torrent.MetadataDownloadInternal?.MetadataRequestReceived(p, piece);
                    }
                    else if (msgType == (int)UtMetadata.MessageType.Reject)
                    {
                        _torrent.MetadataDownloadInternal?.MetadataRejectReceived(p, piece);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtendedMessageReceived error from {RemoteEndPoint}", p.RemoteEndPoint);
        }
    }

    public IReadOnlyList<PeerInfo> GetConnectedPeers()
    {
        var result = new List<PeerInfo>(_connectedPeers.Count);
        foreach (var kvp in _connectedPeers)
        {
            var peer = kvp.Key;
            result.Add(new PeerInfo(
                peer.RemoteEndPoint ?? new IPEndPoint(IPAddress.Any, 0),
                peer.Country,
                ClientIdentification.GetClientName(peer.PeerId),
                peer.DownloadSpeed,
                peer.UploadSpeed,
                peer.Downloaded,
                peer.Uploaded,
                peer.AmChoking,
                peer.AmInterested,
                peer.PeerChoking,
                peer.PeerInterested,
                peer.UtpStream != null,
                peer.Stream is EncryptedStream,
                peer.PeerPieces.ReceivedCount / (float)Math.Max(1, _torrent.Pieces.Count),
                peer.SmoothedRttMs)
            {
                HasReportedPieces = peer.HasReportedPieces
            });
        }
        return result.AsReadOnly();
    }

    public IEnumerable<PeerCommunication> GetConnectedPeersInternal()
    {
        return _connectedPeers.Select(x => x.Key);
    }

    public int[] GetPieceAvailability()
    {
        int piecesCount = _torrent.Pieces.Count;
        int[] availability = new int[piecesCount];

        foreach (var kvp in _connectedPeers)
        {
            var peer = kvp.Key;
            var peerPieces = peer.PeerPieces;
            if (peerPieces == null)
            {
                continue;
            }

            for (int i = 0; i < piecesCount; i++)
            {
                if (peerPieces.HasPiece(i))
                {
                    availability[i]++;
                }
            }
        }

        return availability;
    }

    public async Task HandshakeFinishedAsync(IPeerCommunication peer)
    {
        var p = (PeerCommunication)peer;

        // Now that the handshake revealed the remote peer id, drop self-connections and
        // resolve duplicate connections to the same peer (e.g. crossed simultaneous opens
        // that endpoint-based dedup cannot correlate).
        if (!await TryResolvePeerIdentityAsync(p).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // BEP 3: "'bitfield' is only ever sent as the first message." Everything else - the BEP 5
            // Port message included - has to wait until after it.
            //
            // This ordering is load-bearing rather than pedantic. Strict clients discard a bitfield that
            // arrives after another message, and a peer that believes we hold nothing never asks us for
            // anything. Measured against a live swarm while seeding a complete torrent: 48 incomplete
            // peers connected and not one became interested, because a Port message had preceded our
            // bitfield. Our own parser tolerates the wrong order - it exempts Port and Extended from the
            // first-message rule - which is precisely why no local test ever caught this.
            bool superSeeding = _torrent.SuperSeedManager.Enabled && _torrent.SuperSeedManager.HandlePeerConnected(p);

            if (superSeeding)
            {
                // BEP 16: claim nothing, then dole pieces out one at a time.
                if (!await p.SendHaveNoneAsync().ConfigureAwait(false))
                {
                    // Peer doesn't support Fast Extension, send empty bitfield
                    var msg = new PeerMessage(MessageId.Bitfield)
                    {
                        Data = new byte[(_torrent.Pieces.Count + 7) / 8]
                    };
                    await p.SendMessageAsync(msg).ConfigureAwait(false);
                }
            }
            else
            {
                int receivedCount = _torrent.Pieces.ReceivedCount;
                int totalPieces = _torrent.Pieces.Count;

                if (receivedCount == totalPieces && receivedCount > 0)
                {
                    // BEP-6: Use HaveAll if peer supports Fast Extension
                    if (!await p.SendHaveAllAsync().ConfigureAwait(false))
                    {
                        // Peer doesn't support Fast Extension, send full bitfield
                        var msg = new PeerMessage(MessageId.Bitfield)
                        {
                            Data = _torrent.Pieces.ToBitfield()
                        };
                        await p.SendMessageAsync(msg).ConfigureAwait(false);
                    }
                }
                else if (receivedCount > 0)
                {
                    // Have some pieces, send bitfield
                    var msg = new PeerMessage(MessageId.Bitfield)
                    {
                        Data = _torrent.Pieces.ToBitfield()
                    };
                    await p.SendMessageAsync(msg).ConfigureAwait(false);
                }
                // else: Have no pieces - no need to send anything (HaveNone is optional and implicit)
            }

            // BEP 5: advertise our DHT UDP port, now that the bitfield has gone out ahead of it.
            if (_torrent.DhtManager != null && _settings.Dht.Enabled)
            {
                await p.SendPortAsync(_settings.Connection.UdpPort).ConfigureAwait(false);
            }

            if (superSeeding)
            {
                // Give the peer their first piece to download
                await _torrent.SuperSeedManager.AssignPieceToPeerAsync(p).ConfigureAwait(false);
                return;
            }

            // to start downloading as quickly as possible
            await _torrent.FileTransferInternal.RequestBlocksAsync(p, immediate: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandshakeFinished error for {RemoteEndPoint}", p.RemoteEndPoint);
            RecordInternalFailure("HandshakeFinished", ex);
            await CloseAfterHandshakeFailureAsync(p).ConfigureAwait(false);
        }
    }

    public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, IPEndPoint endpoint, UtHolepunch.ErrorCode error)
    {
        var p = (PeerCommunication)peer;
        _logger.LogDebug("Holepunch msg from {RemoteEndPoint}: {MsgId} {Endpoint} {ErrorCode}", p.RemoteEndPoint, id, endpoint, error);

        if (id == UtHolepunch.MsgId.Connect)
        {
            // Relay told us to connect to 'endpoint' via uTP to punch a hole
            _logger.LogDebug("Initiating holepunch connection to {Endpoint}", endpoint);
            ConnectTo(endpoint.Address.ToString(), endpoint.Port, true);
        }
        return Task.CompletedTask;
    }

    public async Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg)
    {
        var p = (PeerCommunication)peer;
        switch (msg.Id)
        {
            case MessageId.Unchoke:
                // to minimize delay between unchoke and first request
                FireAndForget(_torrent.FileTransferInternal.RequestBlocksAsync(p, immediate: true), "RequestBlocks (Unchoke)");
                break;

            case MessageId.Interested:
                // Fast-path: if we have free upload slots, unchoke immediately.
                // This avoids waiting for the periodic unchoke cycle in small swarms.
                if (p.PeerInterested && p.AmChoking)
                {
                    TryImmediateUnchoke(p);
                }
                if (p.RemoteSupportsFastExtension)
                {
                    FireAndForget(SendAllowedFastSetAsync(p), "SendAllowedFastSet");
                }
                break;

            case MessageId.NotInterested:
                // If the peer no longer wants data, choke to free a slot.
                if (!p.PeerInterested && !p.AmChoking)
                {
                    p.Choke();
                }
                break;

            case MessageId.Have:
                if (!_torrent.HasMetadata)
                {
                    if (p.RemoteSupportsExtensions && p.RemoteExtensions?.MessageIds.ContainsKey(UtMetadata.Name) == true)
                    {
                        _torrent.MetadataDownloadInternal?.PeerConnected(p);
                    }
                    FireAndForget(p.SetInterestedAsync(true), "SetInterested (Metadata Have)");
                    break;
                }
                if (msg.HavePieceIndex < 0 || msg.HavePieceIndex >= _torrent.Pieces.Count)
                {
                    _logger.LogWarning("Invalid Have piece index {PieceIndex} from {RemoteEndPoint}", msg.HavePieceIndex, p.RemoteEndPoint);
                    break;
                }
                // BEP 16: Track HAVE messages for superseed distribution tracking
                FireAndForget(_torrent.SuperSeedManager.HandlePeerHaveAsync(p, msg.HavePieceIndex), "SuperSeed HandlePeerHave");

                _torrent.FileTransferInternal.IncrementAvailability(msg.HavePieceIndex);
                if (p.PeerChoking)
                {
                    FireAndForget(p.SetInterestedAsync(true), "SetInterested (Have)");
                }
                else
                {
                    FireAndForget(_torrent.FileTransferInternal.RequestBlocksAsync(p), "RequestBlocks (Have)");
                }
                break;

            case MessageId.Bitfield:
                if (!_torrent.HasMetadata)
                {
                    if (p.RemoteSupportsExtensions && p.RemoteExtensions?.MessageIds.ContainsKey(UtMetadata.Name) == true)
                    {
                        _torrent.MetadataDownloadInternal?.PeerConnected(p);
                    }
                    FireAndForget(p.SetInterestedAsync(true), "SetInterested (Metadata Bitfield)");
                    break;
                }
                // BEP 16: Track bitfield for superseed distribution tracking
                _torrent.SuperSeedManager.HandlePeerBitfield(p, p.PeerPieces);

                if (p.RemoteEndPoint != null && p.PeerPieces.IsFull)
                {
                    var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint, isListenAddress: p.IsOutgoing);
                    history.IsSeed = true;
                }

                _torrent.FileTransferInternal.RegisterPeerAvailability(p);
                if (p.PeerChoking)
                {
                    FireAndForget(p.SetInterestedAsync(true), "SetInterested (Bitfield)");
                }
                else
                {
                    FireAndForget(_torrent.FileTransferInternal.RequestBlocksAsync(p), "RequestBlocks (Bitfield)");
                }
                break;

            case MessageId.HaveAll:
            case MessageId.HaveNone:
                // BEP-6: Fast Extension - Handle HaveAll/HaveNone like Bitfield
                // PeerPieces already updated in PeerCommunication.ProcessMessageAsync()
                _torrent.SuperSeedManager.HandlePeerBitfield(p, p.PeerPieces);

                if (msg.Id == MessageId.HaveAll && p.RemoteEndPoint != null)
                {
                    var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint, isListenAddress: p.IsOutgoing);
                    history.IsSeed = true;
                }

                _torrent.FileTransferInternal.RegisterPeerAvailability(p);
                if (p.PeerChoking)
                {
                    FireAndForget(p.SetInterestedAsync(true), "SetInterested (HaveAll/None)");
                }
                else
                {
                    FireAndForget(_torrent.FileTransferInternal.RequestBlocksAsync(p), "RequestBlocks (HaveAll/None)");
                }
                break;

            case MessageId.Piece:
                if (msg.PooledBlock != null)
                {
                    await _torrent.FileTransferInternal.BlockReceivedAsync(p, msg.PooledBlock).ConfigureAwait(false);
                    msg.PooledBlock = null;
                    // Track successful data exchange
                    if (p.RemoteEndPoint != null)
                    {
                        var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint, isListenAddress: p.IsOutgoing);
                        history.ExchangedData = true;
                    }
                }
                FireAndForget(_torrent.FileTransferInternal.RequestBlocksAsync(p), "RequestBlocks (Piece)");
                break;

            case MessageId.Request:
                FireAndForget(_torrent.FileTransferInternal.BlockRequestedAsync(p, msg), "BlockRequested");
                break;

            case MessageId.Reject:
                FireAndForget(_torrent.FileTransferInternal.BlockRejectedAsync(p, msg), "BlockRejected");
                break;

            case MessageId.Cancel:
                // BEP-3: Peer no longer wants this block
                _torrent.FileTransferInternal.BlockRequestCancelled(p, msg);
                break;
        }
    }

    public Task PexReceivedAsync(IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped)
    {
        var p = (PeerCommunication)peer;
        // BEP 27: Don't accept peers from PEX for private torrents
        if (_torrent.InfoFile.Info.IsPrivate)
        {
            return Task.CompletedTask;
        }

        AddPeersInternal(added, PeerSourceKind.Pex, p, addedFlags);
        return Task.CompletedTask;
    }

    /// <summary>
    /// BEP 5: Called when a peer sends a Port message advertising their DHT UDP port.
    /// This allows us to add them to our DHT routing table for peer discovery.
    /// </summary>
    public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort)
    {
        var p = (PeerCommunication)peer;
        if (p.RemoteEndPoint == null || _torrent.DhtManager == null)
        {
            return Task.CompletedTask;
        }

        // Create DHT endpoint using peer's IP and their advertised DHT port
        var dhtEndpoint = new IPEndPoint(p.RemoteEndPoint.Address, dhtPort);

        // Ping the DHT node to add it to our routing table
        // The DHT manager will validate the node when it responds
        _logger.LogDebug("Peer {RemoteEndPoint} advertised DHT port {DhtPort}, pinging {DhtEndpoint}", p.RemoteEndPoint, dhtPort, dhtEndpoint);
        _torrent.DhtManager.Ping(dhtEndpoint);
        return Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        _mainLoopCts?.Dispose();
        _mainLoopCts = new CancellationTokenSource();

        // Start the main periodic task loop (replaces 3 timers with single async loop)
        _mainLoopTask = MainLoopAsync(_mainLoopCts.Token);

        // Start the connection queue processor
        _connectionQueueTask = ProcessConnectionQueueAsync(_mainLoopCts.Token);

        try
        {
            if (_torrent.TrackerManager != null)
            {
                await _torrent.TrackerManager.StartAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start tracker manager");
            _torrent.FireErrorEvent(new TorrentException("Failed to start tracker manager.", _torrent.Hash, ex));
        }
    }

    public async Task StopAsync()
    {
        // Stop the main loop and connection processor
        if (_mainLoopCts != null)
        {
            await _mainLoopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_mainLoopTask is { } mainLoopTask)
        {
            await mainLoopTask.ConfigureAwait(false);
        }
        if (_connectionQueueTask is { } connectionQueueTask)
        {
            await connectionQueueTask.ConfigureAwait(false);
        }

        // Wait for active connection attempts to finish or fail
        // Use a timeout to avoid hanging indefinitely if a task is stuck
        try
        {
            if (!_activeConnectionTasks.IsEmpty)
            {
                await Task.WhenAll([.. _activeConnectionTasks.Keys]).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException) { /* Ignore timeout */ }
        catch (Exception ex) { _logger.LogError(ex, "Error awaiting connection tasks during stop"); }

        _activeConnectionTasks.Clear();
        _pendingConnections.Clear();

        var toClose = new List<PeerCommunication>(_connectedPeersCount + _connectingPeersCount);
        foreach (var kvp in _connectedPeers)
        {
            toClose.Add(kvp.Key);
        }
        _connectedPeers.Clear();
        Interlocked.Exchange(ref _connectedPeersCount, 0);

        foreach (var kvp in _connectingPeers)
        {
            toClose.Add(kvp.Key);
        }
        _connectingPeers.Clear();
        Interlocked.Exchange(ref _connectingPeersCount, 0);

        _connectedEndpoints.Clear();
        _connectedPeerIds.Clear();

        if (toClose.Count > 0)
        {
            var closeTasks = toClose.Select(p => p.CloseAsync()).ToArray();
            try
            {
                await Task.WhenAll(closeTasks).WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Ignore timeout
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PeerManager.StopAsync: error while closing peers");
            }
        }
    }

    private static void ApplyPexFlags(PeerHistory history, byte flags) => PeerExchangeCoordinator.ApplyFlags(history, flags);

    internal void BroadcastPex() => _peerExchange.Broadcast(_connectedPeers.Keys);

    private void AddPeersInternal(IEnumerable<IPEndPoint> peers, PeerSourceKind sourceKind, PeerCommunication? source, List<byte>? flags)
    {
        if (peers == null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _knownPeersCacheCount, 0, 0) >= _settings.MaxKnownPeersCache)
        {
            PruneKnownPeersCache();
        }

        bool isSeeding = _torrent.Finished;
        var now = _timeProvider.GetUtcNow();
        var candidates = new List<(PeerHistory History, long Score)>();
        int index = 0;
        foreach (var rawEndpoint in peers)
        {
            var endpoint = NetworkUtils.NormalizeEndPoint(rawEndpoint);
            if (_torrent.Blocklist?.IsBlocked(endpoint) == true)
            {
                index++;
                continue;
            }

            var history = GetOrAddKnownPeerHistory(endpoint);
            history.UpdateSource(sourceKind);
            if (flags != null && index < flags.Count)
            {
                ApplyPexFlags(history, flags[index]);
            }
            if (!_connectedEndpoints.ContainsKey(endpoint))
            {
                candidates.Add((history, history.GetScore(isSeeding, Priority.Normal, now)));
            }
            if (source != null && _peerSources.Count < _settings.MaxKnownPeersCache)
            {
                _peerSources[endpoint] = source;
            }
            index++;
        }

        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
        int max = (int)_settings.MaxPeersPerTrackerRequest;
        foreach (var (history, _) in candidates.Take(max))
        {
            try { ConnectTo(history.EndPoint.Address.ToString(), history.EndPoint.Port); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to initiate connection"); }
        }
    }

    private IReadOnlyList<TransportPreference> BuildTransportPlan(ConnectionSettings settings, PeerHistory? history, bool forceUtp)
    {
        bool hasUtpManager = _torrent.UtpManager != null;
        var now = _timeProvider.GetUtcNow();
        bool utpAvailable = hasUtpManager
            && now.UtcTicks >= Volatile.Read(ref _globalUtpPenaltyUntilUtcTicks)
            && (history?.IsUtpAllowed(now) ?? true);
        bool inWarmup = (now - _startTime) < TimeSpan.FromSeconds(settings.UtpWarmupSeconds);

        return TransportPlanBuilder.Build(new TransportPlanBuilder.Inputs(
            Settings: settings,
            ForceUtp: forceUtp,
            UtpAvailable: utpAvailable,
            UtpHinted: history?.UtpHinted ?? false,
            InWarmupPeriod: inWarmup,
            CurrentUtpRatioPercent: GetUtpRatioPercent));
    }

    private Task CheckPeerHealthAsync() => _peerHealth.CheckAsync(_connectedPeers.Keys, ConnectedCount);
    private void CleanupPendingConnections()
    {
        long now = Environment.TickCount64;
        foreach (var kvp in _pendingConnections)
        {
            // Check if connection attempt has timed out (older than 10 seconds)
            if (now - kvp.Value > PendingConnectionTimeoutMs)
            {
                _pendingConnections.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void ApplyConnectionBackoff(PeerHistory history)
    {
        var settings = _settings.Connection;
        var now = _timeProvider.GetUtcNow();
        var delay = ConnectionBackoffCalculator.Calculate(
            history.FruitlessConnectionCount,
            settings.PeerReconnectBaseSeconds,
            settings.PeerReconnectMaxSeconds,
            settings.PeerReconnectJitterMs);
        history.NextConnectAttempt = now + delay;
    }

    /// <summary>
    /// Dials a peer over each transport in its plan until one connects, then records the outcome.
    ///
    /// <para>
    /// <c>isHolepunch</c> marks a dial made because a relay asked us to, as the far side of a NAT
    /// traversal. Such a dial deliberately bypasses the per-peer backoff, since both ends have to fire
    /// at the same moment for the hole to open - which is exactly why it must not be allowed to request
    /// another rendezvous when it fails, or a peer that is simply unreachable is retried forever.
    /// </para>
    /// </summary>
    private async Task ConnectAndHandleAsync(PeerCommunication peer, string ip, int port, IReadOnlyList<TransportPreference> transportPlan, bool useGovernor, bool isHolepunch)
    {
        IPEndPoint? endpoint = null;
        try
        {
            endpoint = NetworkUtils.NormalizeEndPoint(new IPEndPoint(IPAddress.Parse(ip), port));
        }
        catch { /* Invalid IP - will use null endpoint */ }

        try
        {
            // Get adaptive timeout based on settings and observed network conditions
            int timeoutMs;
            if (_settings.Connection.EnableAdaptiveTimeouts)
            {
                // For endpoints with history, use their specific adapted timeout.
                // For new endpoints, stick to the Initial timeout to fail fast on dead peers.
                // We avoid using CurrentTimeoutMs (global average) because in a swarm with many dead peers,
                // the global variance skyrockets, causing us to wait unnecessarily long (e.g. 30s) for every new peer.
                timeoutMs = (endpoint != null && AdaptiveTimeout.HasHistory(endpoint))
                    ? AdaptiveTimeout.GetTimeoutForEndpoint(endpoint)
                    : _settings.Connection.InitialConnectionTimeoutMs;
            }
            else
            {
                timeoutMs = _settings.Connection.InitialConnectionTimeoutMs;
            }

            // What worked for this peer last time. Encryption support cannot be discovered without
            // trying, so the choice alternates across attempts and is remembered on the peer rather than
            // retried inside one attempt - see PeerHistory.OfferEncryptionNext.
            bool offerEncryption = true;
            if (endpoint is not null && _knownPeersCache.TryGetValue(endpoint, out var knownHistory))
            {
                offerEncryption = knownHistory.OfferEncryptionNext;
            }

            bool success = false;
            bool usedUtp = false;
            bool attemptedUtp = false;
            int remainingTimeoutMs = timeoutMs;
            int fallbackTimeoutMs = ConnectionBudgetCalculator.FallbackCap(
                timeoutMs,
                _settings.Connection.UtpFallbackTimeoutMs,
                _settings.Connection.MinConnectionTimeoutMs);

            for (int attempt = 0; attempt < transportPlan.Count; attempt++)
            {
                var transport = transportPlan[attempt];

                bool hasFallback = attempt < transportPlan.Count - 1;
                int attemptTimeoutMs = ConnectionBudgetCalculator.ForAttempt(
                    remainingTimeoutMs, hasFallback, fallbackTimeoutMs);

                bool attemptUtp = transport == TransportPreference.Utp;
                success = await peer.ConnectAsync(ip, port, attemptUtp, attemptTimeoutMs, offerEncryption: offerEncryption).ConfigureAwait(false);

                if (success)
                {
                    usedUtp = peer.UtpStream != null;
                    break;
                }

                if (attemptUtp)
                {
                    attemptedUtp = true;
                }

                remainingTimeoutMs = ConnectionBudgetCalculator.Remaining(
                    remainingTimeoutMs, attemptTimeoutMs, _settings.Connection.MinConnectionTimeoutMs);
            }

            // Record connection result for adaptive timeout and history
            var history = GetOrAddKnownPeerHistory(endpoint ?? new IPEndPoint(IPAddress.Parse(ip), port));

            if (_settings.Connection.EnableAdaptiveTimeouts)
            {
                int elapsedMs = peer.GetConnectionElapsedMs();
                if (success && elapsedMs > 0)
                {
                    AdaptiveTimeout.RecordSuccess(elapsedMs, endpoint);
                }
                else if (!success)
                {
                    AdaptiveTimeout.RecordTimeout(endpoint);
                }
            }

            if (success)
            {
                // Deliberately not clearing the fruitless count here. Connecting is not the same as
                // achieving anything, and treating it as such is what let a seed redial a swarm of
                // other seeds forever: the handshake succeeded every time, so the count reset every
                // time, so the backoff never grew past its first step. What the connection was worth
                // is only known when it closes, and ConnectionClosedAsync settles it there.
                history.RegisterHandshakeSuccess(peer.Stream is EncryptedStream);
            }
            else
            {
                history.FruitlessConnectionCount++;
                ApplyConnectionBackoff(history);

                // Flip what we offer next time. A peer that refuses both ends up alternating, which
                // costs nothing extra because the attempt was going to happen anyway; a peer that only
                // speaks one of them is reached on the following try.
                history.RegisterHandshakeFailure();
            }

            // Remove from connecting list regardless of outcome
            if (_connectingPeers.TryRemove(peer, out _))
            {
                Interlocked.Decrement(ref _connectingPeersCount);
            }

            // Release pending slot from governor
            if (useGovernor)
            {
                _governor.ReleasePendingSlot();
            }

            if (!success)
            {
                if (attemptedUtp)
                {
                    history.RegisterUtpFailure(_timeProvider.GetUtcNow(), _settings.Connection);
                }

                // A holepunch that failed does not earn another one. The dial a relay asks us to make
                // skips the per-peer backoff by design, so letting its failure request a fresh
                // rendezvous closes a loop with nothing to stop it: fail, ask, dial, fail. One endpoint
                // in a live run was dialled 29 times in eight minutes that way, and the holepunch budget
                // it consumed is shared with every other peer. libtorrent guards the same call with
                // !m_holepunch_mode for the same reason.
                var ep = endpoint ?? new IPEndPoint(IPAddress.Parse(ip), port);
                if (!isHolepunch
                    && _peerSources.TryGetValue(ep, out var source)
                    && source.RemoteExtensions?.MessageIds.ContainsKey(UtHolepunch.Name) == true)
                {
                    _logger.LogDebug("Connection failed to {Endpoint}, attempting holepunch via {Via}", ep, source.RemoteEndPoint);
                    source.UtHolepunch.SendRendezvous(ep);
                }
                return;
            }

            // We dialled this endpoint and it accepted, which is what makes it confirmed connectable -
            // and the only place that can be confirmed. It used to be set when a peer connected to us,
            // against the ephemeral port it dialled from, which is the one address that is certainly
            // not connectable.
            history.IsConnectable = true;

            if (attemptedUtp && !usedUtp)
            {
                history.RegisterUtpFailure(_timeProvider.GetUtcNow(), _settings.Connection);
            }

            if (usedUtp)
            {
                history.RegisterUtpSuccess(_timeProvider.GetUtcNow());
            }

            // Acquire active slot from governor
            if (useGovernor && !_governor.TryAcquireConnectionSlot())
            {
                _logger.LogDebug("Global connection limit reached, closing successful connection to {Ip}:{Port}", ip, port);
                await peer.CloseAsync().ConfigureAwait(false);
                return;
            }

            // Authoritative duplicate gate: claim the endpoint before registering the peer.
            // The check in ConnectTo ran at queue time; another connection to the same endpoint
            // (e.g. an incoming one) may have been established while we were dialing.
            // If so, keep the existing connection and drop the new one.
            if (!TryRegisterConnectedEndpoint(peer))
            {
                _logger.LogDebug("Rejecting duplicate outgoing connection to {RemoteEndPoint}", peer.RemoteEndPoint);
                if (useGovernor)
                {
                    _governor.ReleaseConnectionSlot();
                }

                await peer.CloseAsync().ConfigureAwait(false);
                return;
            }

            // Connection successful - move to connected list.
            // peer is a freshly created instance (reference equality), so this always succeeds.
            _connectedPeers.TryAdd(peer, 0);
            Interlocked.Increment(ref _connectedPeersCount);

            if (peer.RemoteEndPoint != null)
            {
                peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
                // BEP 40: Calculate canonical peer priority
                peer.Priority = PeerPriority.Calculate(peer.RemoteEndPoint.Address, _torrent.Hash.ToArray());
            }

            // The connection may have died between ConnectAsync succeeding and the registration
            // above (its receive loops are already running). In that case ConnectionClosedAsync
            // already ran and found nothing to remove, so undo the registration here.
            if (peer.Connected == 0 && _connectedPeers.TryRemove(peer, out _))
            {
                Interlocked.Decrement(ref _connectedPeersCount);
                UnregisterConnectedEndpoint(peer);
                if (useGovernor)
                {
                    _governor.ReleaseConnectionSlot();
                }
            }
        }
        catch (Exception ex)
        {
            // Log any errors in the continuation to prevent silent failures
            _logger.LogError(ex, "Connection continuation error for {Ip}:{Port}", ip, port);

            // Cleanup on exception
            if (_connectingPeers.TryRemove(peer, out _))
            {
                Interlocked.Decrement(ref _connectingPeersCount);
            }
            if (_connectedPeers.TryRemove(peer, out _))
            {
                Interlocked.Decrement(ref _connectedPeersCount);
                UnregisterConnectedEndpoint(peer);
                // The peer is only ever added to _connectedPeers after the governor
                // connection slot is acquired, so removing it here means the slot would
                // otherwise leak (ConnectionClosedAsync won't run for a peer we just removed).
                if (useGovernor)
                {
                    _governor.ReleaseConnectionSlot();
                }
            }
        }
        finally
        {
            // The pending entry blocked new dials to this endpoint while we were connecting.
            // On success the endpoint is registered in _connectedEndpoints before we get here,
            // on failure the per-peer backoff (NextConnectAttempt) throttles retries.
            if (endpoint != null)
            {
                _pendingConnections.TryRemove(endpoint, out _);
            }
        }
    }

    private void ConnectToInternal(string ip, int port, bool forceUtp)
    {
        var settings = _settings.Connection;
        PeerHistory? history = null;
        if (!forceUtp && IPAddress.TryParse(ip, out var parsed))
        {
            _knownPeersCache.TryGetValue(new IPEndPoint(parsed, port), out history);
        }

        var transportPlan = BuildTransportPlan(settings, history, forceUtp);
        if (transportPlan.Count == 0)
        {
            _logger.LogDebug("Cannot connect to {Ip}:{Port} - no allowed connection method (TCP={TcpOut}, uTP={UtpOut})", ip, port, settings.EnableTcpOut, settings.EnableUtpOut);
            return;
        }

        // Acquire global pending slot
        if (!forceUtp && !_governor.TryAcquirePendingSlot())
        {
            return;
        }

        var peer = _peerFactory.Create(_torrent, this, _timeProvider);

        // Add to connecting list first (pending TCP handshake)
        if (_connectingPeers.TryAdd(peer, 0))
        {
            Interlocked.Increment(ref _connectingPeersCount);
        }

        _logger.LogDebug("Initiating connection to {Ip}:{Port} (plan={Plan}), connecting={Connecting}, connected={Connected}", ip, port, string.Join("->", transportPlan), _connectingPeersCount, _connectedPeersCount);

        // Track the connection task
        var task = ConnectAndHandleAsync(peer, ip, port, transportPlan, !forceUtp, isHolepunch: forceUtp);
        _activeConnectionTasks.TryAdd(task, 0);

        _ = task.ContinueWith(t =>
        {
            _activeConnectionTasks.TryRemove(t, out _);
            if (t.IsFaulted && t.Exception != null)
            {
                _logger.LogCritical(t.Exception?.GetBaseException(), "CRITICAL: Unhandled exception in peer connection handler for {Ip}:{Port}", ip, port);
                // Cleanup will be handled by the connection failure path
            }
        }, TaskScheduler.Default);
    }

    private async Task SendAllowedFastSetAsync(PeerCommunication peer)
    {
        var remoteEndPoint = peer.RemoteEndPoint;
        if (remoteEndPoint == null)
        {
            return;
        }

        int numPieces = _torrent.Pieces.Count;
        if (numPieces == 0)
        {
            return;
        }

        // BEP-6: SHA1(IP_bytes + info_hash) generates deterministic piece indices for the allowed-fast set.
        var ip = remoteEndPoint.Address;
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        byte[] ipBytes = ip.GetAddressBytes();

        byte[] input = new byte[ipBytes.Length + InfoHash.V1Length];
        ipBytes.CopyTo(input, 0);
        _torrent.Hash.Span.CopyTo(input.AsSpan(ipBytes.Length));

        byte[] hash = SHA1.HashData(input);
        var sent = new HashSet<int>();
        int attempts = 0;
        int loops = 0;

        while (true)
        {
            for (int i = 0; i < hash.Length / 4; i++)
            {
                loops++;
                uint raw = (uint)hash[i * 4] << 24 | (uint)hash[(i * 4) + 1] << 16
                         | (uint)hash[(i * 4) + 2] << 8 | hash[(i * 4) + 3];
                int pieceIndex = (int)(raw % (uint)numPieces);

                if (sent.Contains(pieceIndex))
                {
                    if (++loops > 500)
                    {
                        return;
                    }

                    continue;
                }

                if (_torrent.Pieces.HasPiece(pieceIndex))
                {
                    await peer.SendAllowedFastAsync(pieceIndex).ConfigureAwait(false);
                    sent.Add(pieceIndex);
                }

                if (++attempts >= AllowedFastSetSize)
                {
                    return;
                }
            }

            hash = SHA1.HashData(hash);
        }
    }

    private void FireAndForget(Task task, string context)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Exception exception = t.Exception.GetBaseException();
                _logger.LogWarning(exception, "Async operation failed: {Context}", context);
                RecordInternalFailure(context, exception);
            }
        }, TaskScheduler.Default);
    }

    private async Task CloseAfterHandshakeFailureAsync(PeerCommunication peer)
    {
        try
        {
            await peer.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception closeException)
        {
            _logger.LogWarning(closeException, "Failed to close peer after handshake callback failure for {RemoteEndPoint}", peer.RemoteEndPoint);
        }
    }

    private void RecordInternalFailure(string operation, Exception exception)
    {
        var record = _failureTracker.Record(_timeProvider.GetUtcNow());
        if (record.ShouldEscalate)
        {
            _logger.LogError(exception, "Peer manager observed {FailureCount} internal failures within one minute; escalating {Operation}", record.RecentCount, operation);
            try
            {
                _torrent.FireErrorEvent(new TorrentException(
                    $"Peer manager observed {record.RecentCount} internal failures within one minute (latest: {operation}).",
                    _torrent.Hash,
                    exception));
            }
            catch (Exception eventException)
            {
                _logger.LogError(eventException, "Torrent error subscriber failed while escalating peer-manager failures");
            }
        }
    }

    private int GetUtpRatioPercent()
    {
        int total = 0;
        int utp = 0;
        foreach (var kvp in _connectedPeers)
        {
            total++;
            if (kvp.Key.UtpStream != null)
            {
                utp++;
            }
        }

        if (total == 0)
        {
            return 0;
        }

        return utp * 100 / total;
    }

    private bool IsSpeedStable(DateTimeOffset now)
    {
        if (_stableSpeedSince == DateTimeOffset.MinValue)
        {
            return false;
        }

        int stableSeconds = _settings.Connection.StableSpeedSeconds;
        if (stableSeconds <= 0)
        {
            return true;
        }

        return (now - _stableSpeedSince) >= TimeSpan.FromSeconds(stableSeconds);
    }

    /// <summary>How long to wait before asking the DHT again once the torrent is in a working swarm.</summary>
    private const int DhtLookupIntervalSeconds = 900;

    /// <summary>
    /// How soon to try again while the torrent has nothing at all - no nodes to ask, or nobody found
    /// by asking them. Both are the same emergency from the torrent's point of view: it cannot start.
    /// </summary>
    private const int DhtLookupRetrySeconds = 15;

    /// <summary>How soon to try again while the torrent still has too few peers to make progress.</summary>
    private const int DhtLookupHungrySeconds = 60;

    /// <summary>
    /// Below this many connected peers a torrent counts as still looking, and keeps asking the DHT on
    /// the short interval. Well above what a transfer needs to run: the point is not to reach a target
    /// but to stop hammering the DHT for a torrent that has clearly found its swarm.
    /// </summary>
    private const int DhtHealthyPeerCount = 10;

    private int _nextDhtLookupTick;

    /// <summary>
    /// Asks the DHT for peers and re-announces us, and says how many seconds to wait before doing it
    /// again.
    ///
    /// <para>
    /// The interval is keyed on whether the torrent still needs peers rather than on whether the last
    /// lookup worked, because those are not the same question. A lookup that reached somebody has not
    /// necessarily found anybody: from a cold routing table the first one reaches a handful of nodes
    /// and comes back with almost nothing, which is precisely when asking again soon matters most. A
    /// magnet counts as hungry until its metadata arrives however many peers it has, since a peer that
    /// cannot serve metadata leaves it no way to start.
    /// </para>
    /// </summary>
    private int RunDhtLookup()
    {
        if (_torrent.DhtManager is not { } dht || _torrent.InfoFile.Info.IsPrivate || !_torrent.Started)
        {
            return DhtLookupIntervalSeconds;
        }

        var hash = _torrent.InfoFile.Info.GetTrackerInfoHash();
        int queried = dht.FindPeers(hash);

        if (queried == 0)
        {
            _logger.LogTrace("DHT has no nodes to ask yet; retrying in {Seconds}s", DhtLookupRetrySeconds);
            return DhtLookupRetrySeconds;
        }

        // Announce where we actually listen. The configured port may be zero, meaning "any", in which
        // case only the listener knows what was bound - announcing the configured value would publish
        // an address nobody can reach.
        int port = _torrent.PortListener?.Port ?? _settings.Connection.TcpPort;
        if (port > 0)
        {
            dht.Announce(hash, port);
        }

        // Three tiers, not two. A torrent with no peers whatsoever is not merely hungry - it cannot
        // begin, and until the DHT finds someone nothing else will. Waiting a minute to ask again is
        // what put the long tail on start-up: across twenty-one runs of the same magnet, discovery took
        // between 0.5 and 3.5 seconds seventeen times, and 14, 36, 62, 123 and 123 seconds the rest.
        // Those are not a distribution, they are this schedule - one retry, one minute, two minutes -
        // and a torrent whose tracker is failing waits through all of them before it sees a peer.
        int nextIn;
        if (ConnectedCount == 0)
        {
            nextIn = DhtLookupRetrySeconds;
        }
        else if (!_torrent.HasMetadata || ConnectedCount < DhtHealthyPeerCount)
        {
            nextIn = DhtLookupHungrySeconds;
        }
        else
        {
            nextIn = DhtLookupIntervalSeconds;
        }

        _logger.LogDebug(
            "DHT lookup asked {Count} nodes for peers, announced on port {Port}, {Peers} peers connected, next in {Seconds}s",
            queried,
            port,
            ConnectedCount,
            nextIn);

        return nextIn;
    }

    private async Task MainLoopAsync(CancellationToken cancellationToken)
    {
        int tickCount = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(MainLoopIntervalMs), _timeProvider, cancellationToken).ConfigureAwait(false);
                tickCount++;

                // UpdateSpeeds - every 1 second
                try { await UpdateSpeedsAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "UpdateSpeeds error"); }

                // CheckPeerHealth (Watchdog) - every 5 seconds
                if (tickCount % WatchdogIntervalSeconds == 0)
                {
                    try { await CheckPeerHealthAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogError(ex, "CheckPeerHealth error"); }
                }

                // DHT peer lookup. This has to repeat, and the reason is a race that used to make it
                // useless: the torrent starts moments after the DHT is told to bootstrap, so the
                // routing table is still empty, FindPeers has nobody to ask, and nothing ever asked
                // again. A torrent whose tracker returns few peers - which is the normal case, since
                // most trackers hand out a handful and expect the DHT to do the rest - then sat at
                // zero peers forever. Retrying quickly until the table has someone in it, then
                // settling into the ordinary interval, is what makes the DHT actually contribute.
                if (tickCount >= _nextDhtLookupTick)
                {
                    try { _nextDhtLookupTick = tickCount + RunDhtLookup(); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "DHT lookup error");
                        _nextDhtLookupTick = tickCount + DhtLookupRetrySeconds;
                    }
                }

                // UnchokePeers - interval configurable (default 10s)
                int rechokeInterval = Math.Max(2, _settings.Connection.RechokeIntervalSeconds);
                if (tickCount % rechokeInterval == 0)
                {
                    try { UnchokePeers(); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "UnchokePeers error");
                        _torrent.FireErrorEvent(new TorrentException("UnchokePeers error.", _torrent.Hash, ex));
                    }

                    try { CleanupPendingConnections(); }
                    catch (Exception ex) { _logger.LogError(ex, "CleanupPendingConnections error"); }
                }

                // BroadcastPex - every 60 seconds
                int pexIntervalSeconds = Math.Max(
                    MinPexIntervalSeconds,
                    (int)_settings.Connection.PexInterval.TotalSeconds);
                if (tickCount % pexIntervalSeconds == 0)
                {
                    try { BroadcastPex(); }
                    catch (Exception ex) { _logger.LogError(ex, "BroadcastPex error"); }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task ProcessConnectionQueueAsync(CancellationToken cancellationToken)
    {
        // Dynamic rate limit based on settings
        int cps = Math.Max(1, _settings.Connection.ConnectionsPerSecond);
        int delayMs = 1000 / cps;

        _logger.LogDebug("Connection queue processor started (rate: {Rate}/sec, delay: {Delay}ms)", cps, delayMs);
        try
        {
            await foreach (var request in _connectionQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Start connection attempt (fire-and-forget, the actual TCP handshake happens asynchronously)
                ConnectToInternal(request.Ip, request.Port, request.ForceUtp);

                // Pending connections are cleaned up periodically in MainLoopAsync
                // No Task.Run per connection - much more efficient

                // Refresh rate limit from settings
                cps = Math.Max(1, _settings.Connection.ConnectionsPerSecond);
                var now = _timeProvider.GetUtcNow();
                if (IsSpeedStable(now))
                {
                    cps = Math.Min(cps, Math.Max(1, _settings.Connection.StableConnectionsPerSecond));
                }
                delayMs = 1000 / cps;

                // Rate limit: small delay between connection attempts to prevent burst
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection queue processor error");
            _torrent.FireErrorEvent(new TorrentException("Connection queue processor error.", _torrent.Hash, ex));
        }
    }

    /// <summary>
    /// Claims the peer's endpoint in _connectedEndpoints. Returns false if another live
    /// connection already owns that endpoint (i.e. this peer is a duplicate and must be
    /// dropped). Peers without a known endpoint cannot be deduplicated and always pass.
    /// </summary>
    private bool TryRegisterConnectedEndpoint(PeerCommunication peer)
    {
        if (peer.RemoteEndPoint == null)
        {
            return true;
        }

        // The address-only policy is a compound check over multiple endpoint keys. Serialize that
        // check with insertion so two simultaneous connections from different ports on the same IP
        // cannot both see the other and reject each other, leaving no surviving connection.
        lock (_connectedEndpointRegistrationLock)
        {
            if (!_connectedEndpoints.TryAdd(peer.RemoteEndPoint, peer))
            {
                return false;
            }

            // The endpoint gate above only stops the exact same address and port twice. One host
            // dialling from a different source port each time gets past it, and can take as many slots
            // as it likes. libtorrent matches on address alone for the same reason.
            if (!_settings.Connection.AllowMultipleConnectionsPerIp && SharesAddressWithAnotherPeer(peer))
            {
                _connectedEndpoints.TryRemove(KeyValuePair.Create(peer.RemoteEndPoint, peer));
                return false;
            }

            return true;
        }
    }

    /// <summary>Whether some other live connection is already using this peer's address.</summary>
    private bool SharesAddressWithAnotherPeer(PeerCommunication peer)
    {
        var address = peer.RemoteEndPoint!.Address;
        foreach (var (endpoint, other) in _connectedEndpoints)
        {
            if (!ReferenceEquals(other, peer) && endpoint.Address.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes the peer's endpoint from _connectedEndpoints, but only if this peer owns the
    /// entry. A duplicate connection closing must not evict the surviving connection's entry.
    /// </summary>
    private void UnregisterConnectedEndpoint(PeerCommunication peer)
    {
        if (peer.RemoteEndPoint != null)
        {
            lock (_connectedEndpointRegistrationLock)
            {
                _connectedEndpoints.TryRemove(KeyValuePair.Create(peer.RemoteEndPoint, peer));
            }
        }
    }

    private static bool IsPeerIdSet(byte[]? peerId)
    {
        if (peerId is not { Length: 20 })
        {
            return false;
        }

        foreach (byte b in peerId)
        {
            if (b != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Called once the handshake has revealed the remote peer id. Enforces at most one live
    /// connection per peer id and drops connections to ourselves. Returns false if
    /// <paramref name="p"/> must not be kept - in that case it has already been closed.
    /// </summary>
    private async Task<bool> TryResolvePeerIdentityAsync(PeerCommunication p)
    {
        var remoteId = p.PeerId;
        if (!IsPeerIdSet(remoteId))
        {
            // No usable id (e.g. handshake not fully parsed) - nothing to dedup on
            return true;
        }

        // Self-connection: we dialed our own external address (e.g. a tracker announced our own
        // IP back to us). Back off the endpoint so we don't keep redialing ourselves.
        if (IsPeerIdSet(_settings.PeerId) && remoteId.AsSpan().SequenceEqual(_settings.PeerId))
        {
            _logger.LogDebug("Detected connection to ourselves at {RemoteEndPoint}, closing", p.RemoteEndPoint);
            if (p.RemoteEndPoint != null)
            {
                var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint, isListenAddress: p.IsOutgoing);
                history.FruitlessConnectionCount++;
                ApplyConnectionBackoff(history);
            }

            await p.CloseAsync().ConfigureAwait(false);
            return false;
        }

        string key = Convert.ToHexString(remoteId);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (_connectedPeerIds.TryAdd(key, p))
            {
                return true;
            }

            if (!_connectedPeerIds.TryGetValue(key, out var existing))
            {
                // Owner released the id between TryAdd and TryGetValue - retry the claim
                continue;
            }

            if (ReferenceEquals(existing, p))
            {
                return true;
            }

            if (!ShouldReplaceExistingConnection(existing, p))
            {
                _logger.LogDebug("Closing duplicate connection to peer {PeerId} at {RemoteEndPoint} (keeping existing connection at {ExistingEndPoint})",
                    key, p.RemoteEndPoint, existing.RemoteEndPoint);
                await p.CloseAsync().ConfigureAwait(false);
                return false;
            }

            _logger.LogDebug("Replacing connection to peer {PeerId} at {ExistingEndPoint} with new connection at {RemoteEndPoint}",
                key, existing.RemoteEndPoint, p.RemoteEndPoint);
            await existing.CloseAsync().ConfigureAwait(false);

            // ConnectionClosedAsync normally releases the id, but make sure the slot is free
            // even if the close raced with another shutdown path, then retry the claim.
            _connectedPeerIds.TryRemove(KeyValuePair.Create(key, existing));
        }

        // Could not claim the id - treat the new connection as the duplicate and drop it
        await p.CloseAsync().ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Tie-break between two live connections that turned out to belong to the same peer id.
    /// Same-direction duplicates keep active existing connections (this also prevents a peer
    /// that spoofs another's id from evicting an established connection). If the existing
    /// same-direction connection is already idle past the watchdog timeout, replace it with
    /// the fresh candidate immediately instead of wasting a slot until the next health sweep.
    /// Crossed connections (simultaneous open) deterministically keep the one initiated by the
    /// side with the lexicographically smaller peer id, so both ends converge on the same connection.
    /// </summary>
    private bool ShouldReplaceExistingConnection(PeerCommunication existing, PeerCommunication candidate)
    {
        if (existing.IsOutgoing == candidate.IsOutgoing)
        {
            return IsIdlePastWatchdogTimeout(existing);
        }

        bool keepOutgoing = _settings.PeerId.AsSpan().SequenceCompareTo(candidate.PeerId) < 0;
        return keepOutgoing == candidate.IsOutgoing;
    }

    private static bool IsIdlePastWatchdogTimeout(PeerCommunication peer)
    {
        return Environment.TickCount64 - peer.LastActivityTicks > ProtocolConstants.IdleTimeoutMs;
    }

    /// <summary>
    /// Removes the peer's id from _connectedPeerIds, but only if this peer owns the entry.
    /// </summary>
    private void UnregisterConnectedPeerId(PeerCommunication peer)
    {
        if (IsPeerIdSet(peer.PeerId))
        {
            _connectedPeerIds.TryRemove(KeyValuePair.Create(Convert.ToHexString(peer.PeerId), peer));
        }
    }

    // --- Test hooks (used via InternalsVisibleTo; not called from production code) ---

    /// <summary>
    /// Test hook: registers an already-constructed peer as connected, bypassing the
    /// connection pipeline while preserving the endpoint/count invariants.
    /// </summary>
    internal void AddConnectedPeerForTesting(PeerCommunication peer)
    {
        if (peer.RemoteEndPoint != null)
        {
            _connectedEndpoints.TryAdd(peer.RemoteEndPoint, peer);
        }

        if (_connectedPeers.TryAdd(peer, 0))
        {
            Interlocked.Increment(ref _connectedPeersCount);
        }
    }

    /// <summary>Test hook: number of peer-id registrations currently held.</summary>
    internal int ConnectedPeerIdCountForTesting => _connectedPeerIds.Count;

    internal int GetOptimisticUnchokeIntervalSecondsForTesting() => _choker.GetOptimisticUnchokeIntervalSeconds();

    internal int GetUploadSlotsForTesting() => _choker.GetUploadSlotsForTesting(ConnectedCount);

    internal Task CheckPeerHealthForTestingAsync() => CheckPeerHealthAsync();

    internal int SlowPeerCountForTesting => _peerHealth.SlowPeerCountForTesting;

    internal int InternalFailureCountForTesting => _failureTracker.TotalFailures;

    internal void RecordInternalFailureForTesting(string operation, Exception exception) => RecordInternalFailure(operation, exception);

    internal void FireAndForgetForTesting(Task task, string context) => FireAndForget(task, context);

    internal void MarkPeerSlowForTesting(PeerCommunication peer, long startedAt) => _peerHealth.MarkSlowForTesting(peer, startedAt);

    internal int ConnectedEndpointCountForTesting => _connectedEndpoints.Count;

    internal bool TryRegisterConnectedEndpointForTesting(PeerCommunication peer) => TryRegisterConnectedEndpoint(peer);

    internal bool TryRegisterConnectedPeerIdForTesting(PeerCommunication peer)
    {
        return IsPeerIdSet(peer.PeerId) && _connectedPeerIds.TryAdd(Convert.ToHexString(peer.PeerId), peer);
    }

    internal void UnregisterConnectedEndpointForTesting(PeerCommunication peer) => UnregisterConnectedEndpoint(peer);

    internal void UnregisterConnectedPeerIdForTesting(PeerCommunication peer) => UnregisterConnectedPeerId(peer);

    /// <summary>Test hook: pins the optimistic-unchoke slot to make rechoke tests deterministic.</summary>
    internal void SetOptimisticPeerForTesting(PeerCommunication? peer, DateTimeOffset changedAt)
    {
        _choker.SetOptimisticPeerForTesting(peer, changedAt);
    }

    /// <summary>Test hook: sets the global uTP penalty window.</summary>
    internal void SetGlobalUtpPenaltyForTesting(DateTimeOffset until)
    {
        Volatile.Write(ref _globalUtpPenaltyUntilUtcTicks, until.UtcTicks);
    }

    /// <param name="endpoint">The endpoint to look up or record.</param>
    /// <param name="isListenAddress">
    /// False when the endpoint is an incoming connection's source port rather than an address anyone
    /// can dial. Applied only when the entry is created: an existing entry was recorded by a peer
    /// source that does deal in listening addresses, and a client that dials from its own listening
    /// port must not have that downgraded by the coincidence.
    /// </param>
    private PeerHistory GetOrAddKnownPeerHistory(IPEndPoint endpoint, bool isListenAddress = true)
    {
        if (_knownPeersCache.TryGetValue(endpoint, out var history))
        {
            return history;
        }

        var created = new PeerHistory { EndPoint = endpoint, IsListenAddress = isListenAddress };
        if (_knownPeersCache.TryAdd(endpoint, created))
        {
            Interlocked.Increment(ref _knownPeersCacheCount);
            return created;
        }

        _knownPeersCache.TryGetValue(endpoint, out history);
        return history ?? created;
    }

    private void PruneKnownPeersCache()
    {
        // Remove oldest 20% of entries to make room for new peers (by LastAttempt).
        //
        // ToArray rather than AddRange. List.AddRange sees ICollection, reads Count, then calls
        // CopyTo and advances its size by that original count - but ConcurrentDictionary.CopyTo
        // recomputes the count under its own locks, so an entry removed in between leaves default
        // pairs, with a null Value, in the tail of the list. Sorting those dereferences null.
        // ToArray is the dictionary's own consistent snapshot and cannot disagree with itself.
        var entries = _knownPeersCache.ToArray();

        int removeCount = entries.Length / 5;
        if (removeCount == 0)
        {
            return;
        }

        // Sort on captured keys. LastAttempt is written by connection attempts on other threads, and a
        // comparison whose answer changes mid-sort makes the sort itself throw.
        var ordered = new (IPEndPoint EndPoint, DateTimeOffset LastAttempt)[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            ordered[i] = (entries[i].Key, entries[i].Value.LastAttempt);
        }

        Array.Sort(ordered, static (a, b) => a.LastAttempt.CompareTo(b.LastAttempt));

        var toRemove = new List<IPEndPoint>(removeCount);
        for (int i = 0; i < removeCount; i++)
        {
            toRemove.Add(ordered[i].EndPoint);
        }

        foreach (var ep in toRemove)
        {
            if (_knownPeersCache.TryRemove(ep, out _))
            {
                Interlocked.Decrement(ref _knownPeersCacheCount);
            }
            _peerSources.TryRemove(ep, out _);
        }

        _logger.LogDebug("Pruned {Count} old entries from peer cache (was at limit {Limit})", toRemove.Count, _settings.MaxKnownPeersCache);
    }

    private async Task SendHaveWithExceptionHandlingAsync(PeerCommunication p, PeerMessage msg)
    {
        try
        {
            await p.SendMessageAsync(msg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast Have message to {RemoteEndPoint}", p.RemoteEndPoint);
        }
    }

    /// <summary>
    /// BEP 40: Get the lowest priority connected peer, or null if no peers.
    /// </summary>
    private PeerCommunication? TryGetLowestPriorityPeer()
    {
        PeerCommunication? lowestPeer = null;
        uint lowestPriority = uint.MaxValue;

        foreach (var kvp in _connectedPeers)
        {
            var peer = kvp.Key;
            if (peer.Priority < lowestPriority)
            {
                lowestPriority = peer.Priority;
                lowestPeer = peer;
            }
        }

        return lowestPeer;
    }

    private void TryImmediateUnchoke(PeerCommunication peer)
    {
        if (_choker.HasAvailableUploadSlot(_connectedPeers.Keys, ConnectedCount))
        {
            peer.Unchoke();
        }
    }

    internal void UnchokePeers() => _choker.Rechoke(_connectedPeers.Keys, ConnectedCount);
    private async Task UpdateSpeedsAsync()
    {
        int totalDownloadSpeed = 0;
        int totalUploadSpeed = 0;
        int unchokedCount = 0;
        int fastPeerCount = 0; // Peers > 1 MB/s
        int utpConnectedCount = 0;
        PeerCommunication? fastestPeer = null;
        int fastestSpeed = 0;
        var now = _timeProvider.GetUtcNow();
        int utpMinSpeed = _settings.Connection.UtpDegradeMinDownloadSpeedBytesPerSec;
        int utpGraceMs = Math.Max(0, _settings.Connection.UtpDegradeGraceSeconds * 1000);

        var toClose = new List<PeerCommunication>();

        foreach (var kvp in _connectedPeers)
        {
            var peer = kvp.Key;
            peer.UpdateSpeed();
            totalDownloadSpeed += peer.DownloadSpeed;
            totalUploadSpeed += peer.UploadSpeed;
            if (!peer.PeerChoking)
            {
                unchokedCount++;
            }
            // Use SmoothedDownloadSpeed for fast peer count to avoid feedback loop
            if (peer.SmoothedDownloadSpeed > 1_000_000)
            {
                fastPeerCount++;
            }

            if (peer.SmoothedDownloadSpeed > fastestSpeed)
            {
                fastestSpeed = peer.SmoothedDownloadSpeed;
                fastestPeer = peer;
            }

            if (peer.UtpStream != null &&
                peer.RemoteEndPoint != null &&
                _settings.Connection.PreferUtp &&
                peer.GetConnectionElapsedMs() > utpGraceMs &&
                !peer.PeerChoking &&
                peer.AmInterested &&
                peer.SmoothedDownloadSpeed < utpMinSpeed)
            {
                var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint, isListenAddress: peer.IsOutgoing);
                if (history.RegisterUtpSlow(now, _settings.Connection) && _settings.Connection.EnableTcpOut)
                {
                    toClose.Add(peer);
                }
            }

            if (peer.UtpStream != null)
            {
                utpConnectedCount++;
            }
        }

        foreach (var p in toClose)
        {
            await p.CloseAsync().ConfigureAwait(false);
        }

        // Track peak speed
        if (totalDownloadSpeed > _peakSpeed)
        {
            _peakSpeed = totalDownloadSpeed;
        }

        // Detect speed drops: if current speed is less than 25% of recent peak, log it
        bool isSpeedDrop = _lastAggregateSpeed > 1_000_000 && totalDownloadSpeed < _lastAggregateSpeed / 4;

        // Log every 2 seconds, or immediately on speed drop
        if (isSpeedDrop || (now - _lastSpeedLog).TotalSeconds >= 2)
        {
            string dlMbps = (totalDownloadSpeed * 8.0 / 1_000_000).ToString("F1");
            string ulMbps = (totalUploadSpeed * 8.0 / 1_000_000).ToString("F1");
            string peakMbps = (_peakSpeed * 8.0 / 1_000_000).ToString("F1");

            if (isSpeedDrop)
            {
                string lastMbps = (_lastAggregateSpeed * 8.0 / 1_000_000).ToString("F1");
                string fastestMbps = (fastestSpeed * 8.0 / 1_000_000).ToString("F1");
                _logger.LogDebug("SPEED DROP DETECTED: {LastMbps}Mbps -> {DlMbps}Mbps ({Percent}% of previous), unchoked={Unchoked}, fastPeers={FastPeers}, fastest={FastestPeer}@{FastestMbps}Mbps",
                    lastMbps, dlMbps, totalDownloadSpeed * 100 / _lastAggregateSpeed, unchokedCount, fastPeerCount, fastestPeer?.Name, fastestMbps);
            }
            else
            {
                _logger.LogTrace("Speed: DL={DlMbps}Mbps UL={UlMbps}Mbps, peak={PeakMbps}Mbps, peers={Peers}, unchoked={Unchoked}, fastPeers(>1MB/s)={FastPeers}",
                    dlMbps, ulMbps, peakMbps, _connectedPeersCount, unchokedCount, fastPeerCount);
            }
            _lastSpeedLog = now;
        }

        _lastAggregateSpeed = totalDownloadSpeed;

        UpdateStableSpeedState(now, totalDownloadSpeed);

        if (isSpeedDrop && utpConnectedCount > 0)
        {
            var penaltySeconds = Math.Max(10, _settings.Connection.UtpSlowPenaltySeconds / 2);
            long untilUtcTicks = now.AddSeconds(penaltySeconds).UtcTicks;
            // Single writer (main loop), so read-compare-write does not need a CAS loop
            if (untilUtcTicks > Volatile.Read(ref _globalUtpPenaltyUntilUtcTicks))
            {
                Volatile.Write(ref _globalUtpPenaltyUntilUtcTicks, untilUtcTicks);
            }
        }
    }

    private void UpdateStableSpeedState(DateTimeOffset now, int totalDownloadSpeed)
    {
        int threshold = _settings.Connection.StableSpeedThresholdBytesPerSec;
        if (threshold <= 0)
        {
            _stableSpeedSince = DateTimeOffset.MinValue;
            return;
        }

        if (totalDownloadSpeed >= threshold)
        {
            if (_stableSpeedSince == DateTimeOffset.MinValue)
            {
                _stableSpeedSince = now;
            }
        }
        else
        {
            _stableSpeedSince = DateTimeOffset.MinValue;
        }
    }

    private Task AddConnectedPeerCoreAsync(Stream stream, bool initiator, IPEndPoint? remote, PeerSourceKind sourceKind, CancellationToken cancellationToken)
    {
        // Cancelling the attach means the caller no longer wants this connection, so the stream
        // is closed on the way out - exactly as on the reject paths below, which also take
        // ownership of the stream once it has been handed over.
        if (cancellationToken.IsCancellationRequested)
        {
            stream.Close();
            cancellationToken.ThrowIfCancellationRequested();
        }

        remote = NetworkUtils.NormalizeEndPoint(remote);

        if (_torrent.Settings.Proxy.ForceProxy && _torrent.Settings.Proxy.Type != ProxyType.None)
        {
            _logger.LogDebug("Rejecting connected stream peer - ForceProxy is enabled");
            stream.Close();
            return Task.CompletedTask;
        }

        if (_torrent.Blocklist?.IsBlocked(remote) == true)
        {
            _logger.LogDebug("Blocked connected stream peer from {Remote} (blocklist)", remote);
            stream.Close();
            return Task.CompletedTask;
        }

        if (remote is not null && IsRefusedForBadData(remote.Address))
        {
            _logger.LogDebug(
                "Rejecting connected stream peer from {Remote} - it has served bad data before",
                remote);
            stream.Close();
            return Task.CompletedTask;
        }

        int currentConnections = Interlocked.CompareExchange(ref _connectedPeersCount, 0, 0);
        if (currentConnections >= _settings.Connection.MaxPeersPerTorrent)
        {
            _logger.LogDebug("Rejecting connected stream peer - at limit ({MaxPeers})", _settings.Connection.MaxPeersPerTorrent);
            stream.Close();
            return Task.CompletedTask;
        }

        if (!_governor.TryAcquireConnectionSlot())
        {
            _logger.LogDebug("Rejecting connected stream peer - global limit reached ({MaxConnections})", _settings.Connection.MaxConnections);
            stream.Close();
            return Task.CompletedTask;
        }

        var peer = remote != null
            ? _peerFactory.Create(_torrent, this, _timeProvider, stream, remote)
            : _peerFactory.Create(_torrent, this, _timeProvider, stream);
        peer.IsOutgoing = initiator;

        // Authoritative duplicate gate: claim the endpoint before registering anything else.
        if (!TryRegisterConnectedEndpoint(peer))
        {
            _governor.ReleaseConnectionSlot();
            _logger.LogDebug("Rejecting duplicate connected stream peer from {RemoteEndPoint}", peer.RemoteEndPoint);
            stream.Close();
            return Task.CompletedTask;
        }

        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            peer.Priority = PeerPriority.Calculate(peer.RemoteEndPoint.Address, _torrent.Hash.ToArray());

            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint, isListenAddress: initiator);
            history.UpdateSource(sourceKind);
        }

        // Last chance to bail out before the peer owns the stream and starts its own handshake,
        // which from here on is governed by the peer's connection lifetime rather than by this
        // token.
        if (cancellationToken.IsCancellationRequested)
        {
            UnregisterConnectedEndpoint(peer);
            _governor.ReleaseConnectionSlot();
            stream.Close();
            cancellationToken.ThrowIfCancellationRequested();
        }

        // The add always succeeds because peer is a freshly created instance (reference equality).
        // Duplicate endpoints were already rejected by the endpoint gate above.
        _connectedPeers.TryAdd(peer, 0);
        Interlocked.Increment(ref _connectedPeersCount);

        if (initiator)
        {
            peer.StartAsInitiator(stream);
        }
        else
        {
            peer.Start(stream);
        }

        return Task.CompletedTask;
    }
}
