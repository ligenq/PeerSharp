using Microsoft.Extensions.Logging;
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
 * - _connectedPeers and _connectedEndpoints must stay in sync
 *   (add to both on connect, remove from both on disconnect)
 * - _connectedPeersCount reflects _connectedPeers.Count
 * - Connection queue enforces rate limiting (5 connections/second)
 *
 * ALLOCATION OPTIMIZATION:
 * - _unchokePeersList, _unchokeCandidates, _unchokeSet are reused to avoid GC pressure
 */

internal class PeerManager : IInternalPeers, IPeerListener, IAsyncDisposable
{
    // relative to the top candidate are kept unchoked to maintain stability
    private const int AllowedFastSetSize = 10;
    private const double GradualUnchokeThreshold = 0.7;

    // Periodic task intervals
    private const int MainLoopIntervalMs = 1000;

    private const int OptimisticUnchokeIntervalMinSeconds = 5;
    private const int PendingConnectionTimeoutMs = 10000;
    private const int PexIntervalSeconds = 60;

    // 1 second base loop
    private const int WatchdogIntervalSeconds = 5;

    private static readonly Random PexRandom = new();

    // Track active connection attempts for clean shutdown
    private readonly ConcurrentDictionary<Task, byte> _activeConnectionTasks = new();

    private readonly ConcurrentDictionary<IPEndPoint, byte> _connectedEndpoints = new();
    private readonly ConcurrentDictionary<PeerCommunication, byte> _connectedPeers = new();
    private readonly ConcurrentDictionary<PeerCommunication, byte> _connectingPeers = new();

    // Connection throttling: rate limiter to prevent burst connections
    private readonly Channel<ConnectionRequest> _connectionQueue;

    private readonly IGeoIpService _geoIp;
    private readonly IConnectionGovernor _governor;
    private readonly ConcurrentDictionary<IPEndPoint, PeerHistory> _knownPeersCache = new();
    private readonly ILogger<PeerManager> _logger = TorrentLoggerFactory.CreateLogger<PeerManager>();
    private readonly IPeerCommunicationFactory _peerFactory;
    private readonly ConcurrentDictionary<IPEndPoint, PeerCommunication> _peerSources = new();

    // Value is timestamp (Environment.TickCount64) when connection was initiated
    private readonly ConcurrentDictionary<IPEndPoint, long> _pendingConnections = new();
    private readonly ConcurrentDictionary<PeerCommunication, long> _slowPeers = new();

    private readonly Settings _settings;
    private readonly DateTimeOffset _startTime;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private readonly List<PeerCommunication> _unchokeCandidates = [];

    // Reusable collections for UnchokePeers to avoid allocations every 10 seconds
    private readonly List<PeerCommunication> _unchokePeersList = [];

    private readonly HashSet<PeerCommunication> _unchokeSet = [];

    // Track pending connections separately
    // O(1) lookup for connected endpoints
    private int _connectedPeersCount = 0;
    private int _knownPeersCacheCount = 0;

    private int _connectingPeersCount = 0;
    private Task? _connectionQueueTask;
    private AtomicDisposal _disposal = new();
    private DateTimeOffset _globalUtpPenaltyUntil = DateTimeOffset.MinValue;

    private int _holepunchCount = 0;

    private long _holepunchWindowStart = Environment.TickCount64;

    private int _lastAggregateSpeed = 0;

    private DateTimeOffset _lastOptimisticChange = DateTimeOffset.MinValue;

    private DateTimeOffset _lastSpeedLog = DateTimeOffset.MinValue;

    private CancellationTokenSource? _mainLoopCts;

    private Task? _mainLoopTask;

    // Keep peers at 70%+ of best speed (libtransmission inspired)
    private PeerCommunication? _optimisticPeer;

    private int _peakSpeed = 0;

    private DateTimeOffset _stableSpeedSince = DateTimeOffset.MinValue;

    public PeerManager(Torrent torrent, IGeoIpService geoIp, IPeerCommunicationFactory peerFactory, TimeProvider timeProvider, IConnectionGovernor governor)
    {
        _torrent = torrent;
        _settings = torrent.Settings;
        _governor = governor;

        _geoIp = geoIp;
        _peerFactory = peerFactory;
        _timeProvider = timeProvider;
        _startTime = _timeProvider.GetUtcNow();

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

    public async Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake)
    {
        // Reject if force proxy is enabled (incoming connections are not proxied)
        if (_torrent.Settings.Proxy.ForceProxy && _torrent.Settings.Proxy.Type != ProxyType.None)
        {
            _logger.LogDebug("Rejecting incoming TCP connection - ForceProxy is enabled");
            client.Close();
            return;
        }

        // Calculate priority early for BEP 40 decisions
        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;

        // Check blocklist first
        if (_torrent.Blocklist?.IsBlocked(remoteEp) == true)
        {
            _logger.LogDebug("Blocked incoming connection from {RemoteEp} (blocklist)", remoteEp);
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
        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            // BEP 40: Calculate canonical peer priority
            peer.Priority = incomingPriority;
            _connectedEndpoints.TryAdd(peer.RemoteEndPoint, 0);

            // Mark as connectable in history
            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint);
            history.IsConnectable = true;
        }

        if (!await peer.SetHandshakeReceivedAsync(handshake).ConfigureAwait(false))
        {
            _logger.LogDebug("Rejecting peer {RemoteEndPoint} - invalid handshake", peer.RemoteEndPoint);
            if (peer.RemoteEndPoint != null)
            {
                _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
            }

            _governor.ReleaseConnectionSlot();
            client.Close();
            return;
        }

        if (_connectedPeers.TryAdd(peer, 0))
        {
            Interlocked.Increment(ref _connectedPeersCount);
        }
        else
        {
            // Duplicate connection
            if (peer.RemoteEndPoint != null)
            {
                _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
            }
            _governor.ReleaseConnectionSlot();
            _logger.LogDebug("Rejecting duplicate incoming connection from {RemoteEndPoint}", peer.RemoteEndPoint);
            client.Close();
            return;
        }

        peer.Start(client.GetStream());
    }

    public async Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake, ProtocolEncryption encryption)
    {
        // Reject if force proxy is enabled (incoming connections are not proxied)
        if (_torrent.Settings.Proxy.ForceProxy && _torrent.Settings.Proxy.Type != ProxyType.None)
        {
            _logger.LogDebug("Rejecting incoming TCP connection - ForceProxy is enabled");
            client.Close();
            return;
        }

        // Calculate priority early for BEP 40 decisions
        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;

        // Check blocklist first
        if (_torrent.Blocklist?.IsBlocked(remoteEp) == true)
        {
            _logger.LogDebug("Blocked incoming connection from {RemoteEp} (blocklist)", remoteEp);
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
        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            // BEP 40: Calculate canonical peer priority
            peer.Priority = incomingPriority;
            _connectedEndpoints.TryAdd(peer.RemoteEndPoint, 0);

            // Mark as connectable in history
            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint);
            history.IsConnectable = true;
        }

        if (!await peer.SetHandshakeReceivedAsync(handshake).ConfigureAwait(false))
        {
            _logger.LogDebug("Rejecting peer {RemoteEndPoint} - invalid handshake", peer.RemoteEndPoint);
            if (peer.RemoteEndPoint != null)
            {
                _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
            }

            _governor.ReleaseConnectionSlot();
            client.Close();
            return;
        }

        if (_connectedPeers.TryAdd(peer, 0))
        {
            Interlocked.Increment(ref _connectedPeersCount);
        }
        else
        {
            // Duplicate connection
            if (peer.RemoteEndPoint != null)
            {
                _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
            }
            _governor.ReleaseConnectionSlot();
            _logger.LogDebug("Rejecting duplicate incoming connection from {RemoteEndPoint}", peer.RemoteEndPoint);
            client.Close();
            return;
        }

        peer.Start(client.GetStream(), encryption);
    }

    public async Task AddIncomingPeerAsync(Stream stream, byte[] handshake, IPEndPoint? remote = null)
    {
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

        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            // BEP 40: Use already calculated priority
            peer.Priority = incomingPriority;
            _connectedEndpoints.TryAdd(peer.RemoteEndPoint, 0);

            // Mark as connectable in history
            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint);
            history.IsConnectable = true;
            if (peer.UtpStream != null)
            {
                history.RegisterUtpSuccess(_timeProvider.GetUtcNow());
            }
        }

        if (!await peer.SetHandshakeReceivedAsync(handshake).ConfigureAwait(false))
        {
            _logger.LogDebug("Rejecting peer {RemoteEndPoint} - invalid handshake", peer.RemoteEndPoint);
            if (peer.RemoteEndPoint != null)
            {
                _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
            }

            _governor.ReleaseConnectionSlot();
            stream.Close();
            return;
        }

        if (_connectedPeers.TryAdd(peer, 0))
        {
            Interlocked.Increment(ref _connectedPeersCount);
        }
        else
        {
            // Duplicate connection
            if (peer.RemoteEndPoint != null)
            {
                _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
            }
            _governor.ReleaseConnectionSlot();
            _logger.LogDebug("Rejecting duplicate incoming connection from {RemoteEndPoint}", peer.RemoteEndPoint);
            stream.Close();
            return;
        }

        peer.Start(stream);
    }

    public void AddPeers(IEnumerable<IPEndPoint> peers, PeerSourceKind sourceKind = PeerSourceKind.Unknown, PeerCommunication? source = null)
    {
        AddPeersInternal(peers, sourceKind, source, flags: null);
    }

    public Task AddConnectedPeerAsync(Stream stream, bool initiator, IPEndPoint? remote = null, PeerSourceKind sourceKind = PeerSourceKind.Unknown)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return AddConnectedPeerCoreAsync(stream, initiator, remote, sourceKind);
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

        if (_connectedPeers.TryRemove(p, out _))
        {
            Interlocked.Decrement(ref _connectedPeersCount);
            // Release global connection slot
            _governor.ReleaseConnectionSlot();
        }
        _slowPeers.TryRemove(p, out _);
        if (p.RemoteEndPoint != null)
        {
            _connectedEndpoints.TryRemove(p.RemoteEndPoint, out _);
        }

        _logger.LogDebug("Connection closed to {RemoteEndPoint} (code={Code}), downloaded={Downloaded}B, uploaded={Uploaded}B, strikes={Strikes}, remaining peers={RemainingPeers}",
            p.RemoteEndPoint, code, p.Downloaded, p.Uploaded, p.Strikes, _connectedPeersCount);
        return Task.CompletedTask;
    }

    public void ConnectTo(string ip, int port, bool forceUtp = false)
    {
        // Check blocklist first
        if (_torrent.Blocklist?.IsBlocked(ip) == true)
        {
            _logger.LogDebug("Blocked outgoing connection to {Ip}:{Port} (blocklist)", ip, port);
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
                Interlocked.Exchange(ref _holepunchWindowStart, tickCount);
                Interlocked.Exchange(ref _holepunchCount, 0);
            }

            if (Interlocked.Increment(ref _holepunchCount) > _settings.Connection.MaxHolepunchPerMinute)
            {
                _logger.LogWarning("Holepunch rate limit exceeded for {Ip}:{Port}", ip, port);
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
            _logger.LogDebug("Connection queue full, dropping request to {Ip}:{Port}", ip, port);
        }
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
                var res = BencodeParser.ParseWithConsumed(data);
                var node = res.Node;
                int consumed = res.Consumed;
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
                var res = BencodeParser.ParseWithConsumed(data);
                if (res.Node is BDict dict && dict.GetLong("msg_type") is long msgTypeVal)
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
                        byte[] payload = data.Length > res.Consumed ? data[res.Consumed..] : [];
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
                peer.SmoothedRttMs));
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
        try
        {
            // BEP 5: Send Port message to advertise our DHT UDP port if DHT is enabled
            if (_torrent.DhtManager != null && _settings.Dht.Enabled)
            {
                await p.SendPortAsync(_settings.Connection.UdpPort).ConfigureAwait(false);
            }

            // BEP 16: Check for super-seeding mode
            if (_torrent.SuperSeedManager.Enabled && _torrent.SuperSeedManager.HandlePeerConnected(p))
            {
                // In superseed mode, send HaveNone (or empty bitfield) instead of our full pieces
                if (!await p.SendHaveNoneAsync().ConfigureAwait(false))
                {
                    // Peer doesn't support Fast Extension, send empty bitfield
                    var msg = new PeerMessage(MessageId.Bitfield)
                    {
                        Data = new byte[(_torrent.Pieces.Count + 7) / 8]
                    };
                    await p.SendMessageAsync(msg).ConfigureAwait(false);
                }

                // Give the peer their first piece to download
                await _torrent.SuperSeedManager.AssignPieceToPeerAsync(p).ConfigureAwait(false);
                return;
            }

            // Normal seeding mode - Await to ensure bitfield is queued before RequestBlocks sends Interested/Request messages
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

            // to start downloading as quickly as possible
            await _torrent.FileTransferInternal.RequestBlocksAsync(p, immediate: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandshakeFinished error for {RemoteEndPoint}", p.RemoteEndPoint);
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
                    var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint);
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
                    var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint);
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
                        var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint);
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
        // BEP 17: Don't accept peers from PEX for private torrents
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
                await Task.WhenAll(_activeConnectionTasks.Keys.ToArray()).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
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

    private static void ApplyPexFlags(PeerHistory history, byte flags)
    {
        if ((flags & (byte)UtPex.Peer.Seed) != 0)
        {
            history.IsSeed = true;
        }

        if ((flags & (byte)UtPex.Peer.Utp) != 0)
        {
            history.UtpSupported = true;
            history.UtpHinted = true;
        }
    }

    private void AddPeersInternal(IEnumerable<IPEndPoint> peers, PeerSourceKind sourceKind, PeerCommunication? source, List<byte>? flags)
    {
        if (peers == null)
        {
            return;
        }

        // Prune oldest entries if cache is at limit (prevents PEX flooding)
        if (Interlocked.CompareExchange(ref _knownPeersCacheCount, 0, 0) >= _settings.MaxKnownPeersCache)
        {
            PruneKnownPeersCache();
        }

        bool isSeeding = _torrent.Finished;
        var now = _timeProvider.GetUtcNow();
        var blocklist = _torrent.Blocklist;

        var candidates = new List<(PeerHistory History, long Score)>();
        int index = 0;

        foreach (var ep in peers)
        {
            if (blocklist?.IsBlocked(ep) == true)
            {
                index++;
                continue;
            }

            // Get or create history
            var history = GetOrAddKnownPeerHistory(ep);
            history.UpdateSource(sourceKind);

            if (flags != null && index < flags.Count)
            {
                ApplyPexFlags(history, flags[index]);
            }

            // Only consider for immediate connection if not already connected
            if (!_connectedEndpoints.ContainsKey(ep))
            {
                candidates.Add((history, history.GetScore(isSeeding, Priority.Normal, now)));
            }

            if (source != null && _peerSources.Count < _settings.MaxKnownPeersCache)
            {
                _peerSources[ep] = source;
            }

            index++;
        }

        // Sort by score (lower is better)
        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));

        int max = (int)_settings.MaxPeersPerTrackerRequest;
        int count = 0;
        foreach (var (history, _) in candidates)
        {
            if (count >= max)
            {
                break;
            }

            try
            {
                ConnectTo(history.EndPoint.Address.ToString(), history.EndPoint.Port);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to initiate connection"); }
            count++;
        }
    }

    private void BroadcastPex()
    {
        // BEP 17: Don't broadcast PEX for private torrents
        if (_torrent.InfoFile.Info.IsPrivate)
        {
            return;
        }

        // Build peer data and connected endpoints set in single pass
        var peerData = new List<(IPEndPoint, byte)>();
        var connectedEndpoints = new HashSet<IPEndPoint>();
        var peersList = new List<PeerCommunication>();

        foreach (var kvp in _connectedPeers)
        {
            var p = kvp.Key;
            peersList.Add(p);
            if (p.RemoteEndPoint == null)
            {
                continue;
            }

            connectedEndpoints.Add(p.RemoteEndPoint);
            byte flags = 0;
            if (p.PeerPieces != null && p.PeerPieces.ReceivedCount == p.PeerPieces.Count)
            {
                flags |= (byte)UtPex.Peer.Seed;
            }

            if (p.UtpStream != null)
            {
                flags |= (byte)UtPex.Peer.Utp;
            }

            if (p.Stream is EncryptedStream)
            {
                flags |= (byte)UtPex.Peer.Encryption;
            }

            if (p.RemoteExtensions?.MessageIds.ContainsKey(UtHolepunch.Name) == true)
            {
                flags |= (byte)UtPex.Peer.Holepunch;
            }

            peerData.Add((p.RemoteEndPoint, flags));
        }

        // Filter known peers using HashSet for O(1) lookup, shuffle without LINQ
        var knownCandidates = new List<IPEndPoint>();
        foreach (var kvp in _knownPeersCache)
        {
            var k = kvp.Key;
            if (!connectedEndpoints.Contains(k))
            {
                knownCandidates.Add(k);
            }
        }

        // Fisher-Yates shuffle for random selection (only shuffle what we need)
        int takeCount = Math.Min(50, knownCandidates.Count);
        for (int i = 0; i < takeCount; i++)
        {
            int j = i + PexRandom.Next(knownCandidates.Count - i);
            (knownCandidates[i], knownCandidates[j]) = (knownCandidates[j], knownCandidates[i]);
        }

        // Build allPeers list
        var allPeers = new List<(IPEndPoint, byte)>(peerData.Count + takeCount);
        allPeers.AddRange(peerData);
        for (int i = 0; i < takeCount; i++)
        {
            allPeers.Add((knownCandidates[i], 0));
        }

        // Reusable filtered list to avoid LINQ allocation per peer
        var filteredPeers = new List<(IPEndPoint, byte)>(allPeers.Count);
        foreach (var p in peersList)
        {
            try
            {
                filteredPeers.Clear();
                foreach (var peer in allPeers)
                {
                    if (!peer.Item1.Equals(p.RemoteEndPoint))
                    {
                        filteredPeers.Add(peer);
                    }
                }
                p.UtPex.Update(filteredPeers);
            }
            catch (Exception ex) { _logger.LogError(ex, "BroadcastPex error for {RemoteEndPoint}", p.RemoteEndPoint); }
        }
    }

    private IReadOnlyList<TransportPreference> BuildTransportPlan(ConnectionSettings settings, PeerHistory? history, bool forceUtp)
    {
        bool hasUtpManager = _torrent.UtpManager != null;
        var now = _timeProvider.GetUtcNow();
        bool utpAvailable = hasUtpManager
            && now >= _globalUtpPenaltyUntil
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

    private async Task CheckPeerHealthAsync()
    {
        long now = Environment.TickCount64;
        var tasks = new List<Task>();
        int connectedCount = Interlocked.CompareExchange(ref _connectedPeersCount, 0, 0);
        var settings = _settings.Connection;
        bool isSeeding = _torrent.Finished;

        foreach (var kvp in _connectedPeers)
        {
            var peer = kvp.Key;
            if (now - peer.LastActivityTicks > ProtocolConstants.IdleTimeoutMs)
            {
                _logger.LogDebug("Connection timed out for {PeerName} (Idle > {IdleTimeout}ms)", peer.Name, ProtocolConstants.IdleTimeoutMs);
                tasks.Add(peer.CloseAsync());
                _slowPeers.TryRemove(peer, out _);
                continue;
            }

            if (connectedCount >= settings.SlowPeerMinConnectedPeers)
            {
                int threshold = isSeeding
                    ? settings.SlowPeerMinUploadSpeedBytesPerSec
                    : settings.SlowPeerMinDownloadSpeedBytesPerSec;

                bool activeTransfer = isSeeding
                    ? (peer.PeerInterested && !peer.AmChoking)
                    : (peer.AmInterested && !peer.PeerChoking);

                if (threshold > 0 && activeTransfer)
                {
                    int speed = isSeeding ? peer.UploadSpeed : peer.SmoothedDownloadSpeed;
                    if (speed < threshold)
                    {
                        long start = _slowPeers.GetOrAdd(peer, _ => now);
                        long elapsedMs = now - start;
                        if (elapsedMs >= Math.Max(1, settings.SlowPeerGraceSeconds) * 1000L)
                        {
                            _logger.LogDebug("Disconnecting slow peer {PeerName} (speed={Speed}B/s < {Threshold}B/s for {Elapsed}ms)",
                                peer.Name, speed, threshold, elapsedMs);
                            tasks.Add(peer.CloseAsync());
                            _slowPeers.TryRemove(peer, out _);
                        }
                    }
                    else
                    {
                        _slowPeers.TryRemove(peer, out _);
                    }
                }
                else
                {
                    _slowPeers.TryRemove(peer, out _);
                }
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

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

    private async Task ConnectAndHandleAsync(PeerCommunication peer, string ip, int port, IReadOnlyList<TransportPreference> transportPlan, bool useGovernor)
    {
        IPEndPoint? endpoint = null;
        try
        {
            endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
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

            bool success = false;
            bool usedUtp = false;
            bool attemptedUtp = false;
            int remainingTimeoutMs = timeoutMs;
            int utpFallbackTimeoutMs = Math.Min(timeoutMs, _settings.Connection.UtpFallbackTimeoutMs);
            if (utpFallbackTimeoutMs < _settings.Connection.MinConnectionTimeoutMs)
            {
                utpFallbackTimeoutMs = _settings.Connection.MinConnectionTimeoutMs;
            }

            foreach (var transport in transportPlan)
            {
                int attemptTimeoutMs = remainingTimeoutMs;
                if (transport == TransportPreference.Utp && transportPlan.Count > 1)
                {
                    attemptTimeoutMs = Math.Min(remainingTimeoutMs, utpFallbackTimeoutMs);
                }

                bool attemptUtp = transport == TransportPreference.Utp;
                success = await peer.ConnectAsync(ip, port, attemptUtp, attemptTimeoutMs).ConfigureAwait(false);

                if (success)
                {
                    usedUtp = peer.UtpStream != null;
                    break;
                }

                if (attemptUtp)
                {
                    attemptedUtp = true;
                }

                remainingTimeoutMs = Math.Max(_settings.Connection.MinConnectionTimeoutMs, remainingTimeoutMs - attemptTimeoutMs);
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
                history.FruitlessConnectionCount = 0;
                history.NextConnectAttempt = DateTimeOffset.MinValue;
            }
            else
            {
                history.FruitlessConnectionCount++;
                ApplyConnectionBackoff(history);
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

                var ep = endpoint ?? new IPEndPoint(IPAddress.Parse(ip), port);
                if (_peerSources.TryGetValue(ep, out var source) && source.RemoteExtensions?.MessageIds.ContainsKey(UtHolepunch.Name) == true)
                {
                    _logger.LogDebug("Connection failed to {Endpoint}, attempting holepunch via {Via}", ep, source.RemoteEndPoint);
                    source.UtHolepunch.SendRendezvous(ep);
                }
                return;
            }

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

            // Connection successful - move to connected list
            if (_connectedPeers.TryAdd(peer, 0))
            {
                Interlocked.Increment(ref _connectedPeersCount);
            }
            else
            {
                // Duplicate connection
                if (useGovernor)
                {
                    _governor.ReleaseConnectionSlot();
                }

                return;
            }

            // This minimizes the race window with ConnectionClosed by checking peer existence BEFORE adding endpoint
            if (peer.RemoteEndPoint != null && _connectedPeers.ContainsKey(peer))
            {
                _connectedEndpoints.TryAdd(peer.RemoteEndPoint, 0);
                peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
                // BEP 40: Calculate canonical peer priority
                peer.Priority = PeerPriority.Calculate(peer.RemoteEndPoint.Address, _torrent.Hash.ToArray());

                // Double-check: if peer was removed between check and add, clean up stale endpoint
                // This handles the race where ConnectionClosed runs between our ContainsKey and TryAdd
                if (!_connectedPeers.ContainsKey(peer))
                {
                    _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
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
        var task = ConnectAndHandleAsync(peer, ip, port, transportPlan, !forceUtp);
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
                _logger.LogWarning(t.Exception.GetBaseException(), "Async operation failed: {Context}", context);
            }
        }, TaskScheduler.Default);
    }

    private int GetOptimisticUnchokeIntervalSeconds()
    {
        return Math.Max(OptimisticUnchokeIntervalMinSeconds, _settings.Connection.OptimisticUnchokeIntervalSeconds);
    }

    private int GetUploadSlots()
    {
        int minSlots = Math.Max(1, _settings.Connection.UploadSlotsMin);
        int maxSlots = Math.Max(minSlots, _settings.Connection.UploadSlotsMax);

        int uploadLimit = _torrent.UploadLimitBytesPerSecond;
        if (uploadLimit <= 0)
        {
            uploadLimit = (int)_settings.Transfer.MaxUploadSpeed;
        }

        if (uploadLimit <= 0)
        {
            return Math.Min(maxSlots, Math.Max(minSlots, _connectedPeersCount));
        }

        int targetPerSlot = Math.Max(8000, _settings.Connection.TargetUploadPerSlotBytesPerSec);
        int slots = (int)Math.Ceiling(uploadLimit / (double)targetPerSlot);
        slots = Math.Clamp(slots, minSlots, maxSlots);
        return Math.Min(slots, Math.Max(minSlots, _connectedPeersCount));
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
                if (tickCount % PexIntervalSeconds == 0)
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
            await foreach (var request in _connectionQueue.Reader.ReadAllAsync(cancellationToken))
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

    private PeerHistory GetOrAddKnownPeerHistory(IPEndPoint endpoint)
    {
        if (_knownPeersCache.TryGetValue(endpoint, out var history))
        {
            return history;
        }

        var created = new PeerHistory { EndPoint = endpoint };
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
        // Remove oldest 20% of entries to make room for new peers (by LastAttempt)
        var snapshot = new List<KeyValuePair<IPEndPoint, PeerHistory>>(Math.Max(0, _knownPeersCacheCount));
        snapshot.AddRange(_knownPeersCache);

        int removeCount = snapshot.Count / 5;
        if (removeCount == 0)
        {
            return;
        }

        snapshot.Sort((a, b) => a.Value.LastAttempt.CompareTo(b.Value.LastAttempt));
        var toRemove = new List<IPEndPoint>(removeCount);
        for (int i = 0; i < removeCount; i++)
        {
            toRemove.Add(snapshot[i].Key);
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
        int unchoked = 0;
        foreach (var kvp in _connectedPeers)
        {
            if (!kvp.Key.AmChoking)
            {
                unchoked++;
            }
        }

        if (unchoked < GetUploadSlots())
        {
            peer.Unchoke();
        }
    }

    private void UnchokePeers()
    {
        // libtransmission optimization: don't rechoke/reconnect if we are seeding
        // and everyone we know is also seeding (and we aren't doing PEX)
        bool isSeeding = _torrent.Finished;
        if (isSeeding && !_torrent.Settings.Connection.EnableLsd && !_torrent.Settings.Dht.Enabled && _connectedPeers.All(p => p.Key.PeerPieces?.IsFull == true))
        {
            _logger.LogTrace("Seeding saturated - skipping unchoke cycle");
            return;
        }

        // Clear and reuse collections to avoid allocations
        _unchokePeersList.Clear();
        _unchokeCandidates.Clear();
        _unchokeSet.Clear();

        // Use local references for the reusable collections
        var peers = _unchokePeersList;
        var candidates = _unchokeCandidates;
        var toUnchokeSet = _unchokeSet;

        // Ensure capacity to avoid reallocations during enumeration
        if (peers.Capacity < _connectedPeersCount)
        {
            peers.Capacity = _connectedPeersCount;
        }

        foreach (var kvp in _connectedPeers)
        {
            peers.Add(kvp.Key);
        }

        foreach (var p in peers)
        {
            p.UpdateSpeed();
        }

        // Build candidates list and sort
        foreach (var p in peers)
        {
            if (p.PeerInterested)
            {
                candidates.Add(p);
            }
        }

        if (isSeeding)
        {
            // Seeding: round-robin rotation.
            // Peers that have exceeded their piece quota and been unchoked for at least
            // one minute are de-prioritized so that waiting peers can rotate in.
            long pieceLength = _torrent.PieceSize;
            var now = _timeProvider.GetUtcNow();
            candidates.Sort((a, b) => SeedingChoker.Compare(
                SeedingChoker.FromPeer(a), SeedingChoker.FromPeer(b),
                pieceLength, SeedingChoker.DefaultPieceQuota, now));
        }
        else
        {
            // Leeching: Tit-for-tat (reciprocity) -> Prioritize peers sending data fastest
            // where a peer appears slow just because they finished their requests
            candidates.Sort((a, b) => b.SmoothedDownloadSpeed.CompareTo(a.SmoothedDownloadSpeed));
        }

        int slots = GetUploadSlots();

        // Reserve one slot for optimistic unchoke if we have overflow
        int regularSlots = (candidates.Count > slots) ? slots - 1 : slots;

        // Instead of just taking top N candidates, we:
        // 1. Always include top candidates (up to regularSlots)
        // 2. Keep currently unchoked peers if they're performing at 50%+ of the best
        // This prevents sudden peer swaps that cause speed drops

        int bestSpeed = 0;
        if (candidates.Count > 0)
        {
            bestSpeed = isSeeding ? candidates[0].UploadSpeed : candidates[0].SmoothedDownloadSpeed;
        }
        int gradualThreshold = (int)(bestSpeed * GradualUnchokeThreshold);

        // First, add top candidates
        int count = Math.Min(regularSlots, candidates.Count);
        for (int i = 0; i < count; i++)
        {
            toUnchokeSet.Add(candidates[i]);
        }

        // This prevents sudden disconnection of productive peers
        int keptFromPrevious = 0;
        foreach (var p in peers)
        {
            if (!p.AmChoking && p.PeerInterested && !toUnchokeSet.Contains(p))
            {
                int peerSpeed = isSeeding ? p.UploadSpeed : p.SmoothedDownloadSpeed;
                if (peerSpeed >= gradualThreshold && toUnchokeSet.Count < slots + 2) // Allow slight overflow
                {
                    toUnchokeSet.Add(p);
                    keptFromPrevious++;
                }
            }
        }

        // Optimistic unchoke: add one random peer from remaining
        if (candidates.Count > slots)
        {
            int remainingCount = candidates.Count - regularSlots;
            int optimisticIndex = -1;

            bool keepCurrent = false;
            if (_optimisticPeer != null &&
                (_timeProvider.GetUtcNow() - _lastOptimisticChange).TotalSeconds < GetOptimisticUnchokeIntervalSeconds())
            {
                // Is the current optimistic peer in the remaining candidates?
                int index = candidates.IndexOf(_optimisticPeer, regularSlots);
                if (index != -1)
                {
                    optimisticIndex = index;
                    keepCurrent = true;
                }
            }

            if (!keepCurrent)
            {
                // Pick a new random peer from remaining (using thread-safe Random.Shared)
                optimisticIndex = regularSlots + Random.Shared.Next(remainingCount);
                _optimisticPeer = candidates[optimisticIndex];
                _lastOptimisticChange = _timeProvider.GetUtcNow();
            }

            if (optimisticIndex != -1)
            {
                toUnchokeSet.Add(candidates[optimisticIndex]);
            }
        }
        else
        {
            _optimisticPeer = null;
        }

        int unchoked = 0;
        int choked = 0;
        foreach (var p in peers)
        {
            if (toUnchokeSet.Contains(p))
            {
                if (p.AmChoking)
                {
                    unchoked++;
                }

                p.Unchoke();
            }
            else
            {
                if (!p.AmChoking)
                {
                    choked++;
                }

                p.Choke();
            }
        }

        // Log unchoke algorithm results
        int interestedPeers = candidates.Count;
        int totalPeers = peers.Count;
        double avgSpeed = 0;
        double maxSpeed = 0;
        if (candidates.Count > 0)
        {
            double sum = 0;
            foreach (var c in candidates)
            {
                double speed = isSeeding ? c.UploadSpeed : c.SmoothedDownloadSpeed;
                sum += speed;
                if (speed > maxSpeed)
                {
                    maxSpeed = speed;
                }
            }
            avgSpeed = sum / candidates.Count;
        }

        string mode = isSeeding ? "Seeding" : "Leeching";
        _logger.LogDebug("Unchoke ({Mode}): {TotalPeers} peers, {InterestedPeers} interested, {UnchokedCount} unchoked (+{NewUnchoked} new, {Kept} kept), {NewChoked} newly choked, avg={AvgSpeed}B/s, max={MaxSpeed}B/s, threshold={Threshold}B/s",
            mode, totalPeers, interestedPeers, toUnchokeSet.Count, unchoked, keptFromPrevious, choked, Math.Round(avgSpeed), maxSpeed, gradualThreshold);
    }

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
                var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint);
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
            var until = now.AddSeconds(penaltySeconds);
            if (until > _globalUtpPenaltyUntil)
            {
                _globalUtpPenaltyUntil = until;
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

    private async Task AddConnectedPeerCoreAsync(Stream stream, bool initiator, IPEndPoint? remote, PeerSourceKind sourceKind)
    {
        if (_torrent.Settings.Proxy.ForceProxy && _torrent.Settings.Proxy.Type != ProxyType.None)
        {
            _logger.LogDebug("Rejecting connected stream peer - ForceProxy is enabled");
            stream.Close();
            return;
        }

        if (_torrent.Blocklist?.IsBlocked(remote) == true)
        {
            _logger.LogDebug("Blocked connected stream peer from {Remote} (blocklist)", remote);
            stream.Close();
            return;
        }

        int currentConnections = Interlocked.CompareExchange(ref _connectedPeersCount, 0, 0);
        if (currentConnections >= _settings.Connection.MaxPeersPerTorrent)
        {
            _logger.LogDebug("Rejecting connected stream peer - at limit ({MaxPeers})", _settings.Connection.MaxPeersPerTorrent);
            stream.Close();
            return;
        }

        if (!_governor.TryAcquireConnectionSlot())
        {
            _logger.LogDebug("Rejecting connected stream peer - global limit reached ({MaxConnections})", _settings.Connection.MaxConnections);
            stream.Close();
            return;
        }

        var peer = remote != null
            ? _peerFactory.Create(_torrent, this, _timeProvider, stream, remote)
            : _peerFactory.Create(_torrent, this, _timeProvider, stream);

        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            peer.Priority = PeerPriority.Calculate(peer.RemoteEndPoint.Address, _torrent.Hash.ToArray());
            _connectedEndpoints.TryAdd(peer.RemoteEndPoint, 0);

            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint);
            history.UpdateSource(sourceKind);
        }

        if (_connectedPeers.TryAdd(peer, 0))
        {
            Interlocked.Increment(ref _connectedPeersCount);
        }
        else
        {
            if (peer.RemoteEndPoint != null)
            {
                _connectedEndpoints.TryRemove(peer.RemoteEndPoint, out _);
            }

            _governor.ReleaseConnectionSlot();
            stream.Close();
            return;
        }

        if (initiator)
        {
            peer.StartAsInitiator(stream);
        }
        else
        {
            peer.Start(stream);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
