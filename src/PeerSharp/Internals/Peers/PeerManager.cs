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
    private const int PexIntervalSeconds = 60;

    // 1 second base loop
    private const int WatchdogIntervalSeconds = 5;


    // Track active connection attempts for clean shutdown
    private readonly ConcurrentDictionary<Task, byte> _activeConnectionTasks = new();

    // Maps each connected remote endpoint to the PeerCommunication that owns it.
    // Authoritative duplicate gate - see thread-safety notes at the top of this file.
    private readonly ConcurrentDictionary<IPEndPoint, PeerCommunication> _connectedEndpoints = new();

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
    private readonly ILogger<PeerManager> _logger = TorrentLoggerFactory.CreateLogger<PeerManager>();
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
    private DateTimeOffset _globalUtpPenaltyUntil = DateTimeOffset.MinValue;

    private int _holepunchCount = 0;

    private long _holepunchWindowStart = Environment.TickCount64;

    private int _lastAggregateSpeed = 0;


    private DateTimeOffset _lastSpeedLog = DateTimeOffset.MinValue;

    private CancellationTokenSource? _mainLoopCts;

    private Task? _mainLoopTask;

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

    public Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake)
    {
        return AddIncomingTcpPeerCoreAsync(client, handshake, encryption: null);
    }

    public Task AddIncomingPeerAsync(System.Net.Sockets.TcpClient client, byte[] handshake, ProtocolEncryption encryption)
    {
        return AddIncomingTcpPeerCoreAsync(client, handshake, encryption);
    }

    public async Task AddIncomingPeerAsync(Stream stream, byte[] handshake, IPEndPoint? remote = null)
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
            UnregisterConnectedEndpoint(peer);
            _governor.ReleaseConnectionSlot();
            stream.Close();
            return;
        }

        // The add always succeeds because peer is a freshly created instance (reference equality).
        // Duplicate endpoints were already rejected by the endpoint gate above.
        _connectedPeers.TryAdd(peer, 0);
        Interlocked.Increment(ref _connectedPeersCount);

        peer.Start(stream);
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

            // Mark as connectable in history
            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint);
            history.IsConnectable = true;
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
        _peerHealth.Remove(p);

        // Only remove the endpoint/id entries if this peer owns them. A rejected duplicate
        // closing must not evict the surviving connection's entries, or the dedup gates would
        // let a new connection through while the survivor is still alive.
        UnregisterConnectedEndpoint(p);
        UnregisterConnectedPeerId(p);

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

        // Now that the handshake revealed the remote peer id, drop self-connections and
        // resolve duplicate connections to the same peer (e.g. crossed simultaneous opens
        // that endpoint-based dedup cannot correlate).
        if (!await TryResolvePeerIdentityAsync(p).ConfigureAwait(false))
        {
            return;
        }

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

    private async Task ConnectAndHandleAsync(PeerCommunication peer, string ip, int port, IReadOnlyList<TransportPreference> transportPlan, bool useGovernor)
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

    /// <summary>
    /// Claims the peer's endpoint in _connectedEndpoints. Returns false if another live
    /// connection already owns that endpoint (i.e. this peer is a duplicate and must be
    /// dropped). Peers without a known endpoint cannot be deduplicated and always pass.
    /// </summary>
    private bool TryRegisterConnectedEndpoint(PeerCommunication peer)
    {
        return peer.RemoteEndPoint == null || _connectedEndpoints.TryAdd(peer.RemoteEndPoint, peer);
    }

    /// <summary>
    /// Removes the peer's endpoint from _connectedEndpoints, but only if this peer owns the
    /// entry. A duplicate connection closing must not evict the surviving connection's entry.
    /// </summary>
    private void UnregisterConnectedEndpoint(PeerCommunication peer)
    {
        if (peer.RemoteEndPoint != null)
        {
            _connectedEndpoints.TryRemove(KeyValuePair.Create(peer.RemoteEndPoint, peer));
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
                var history = GetOrAddKnownPeerHistory(p.RemoteEndPoint);
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
    /// Same-direction duplicates keep the existing connection (this also prevents a peer that
    /// spoofs another's id from evicting an established connection). Crossed connections
    /// (simultaneous open) deterministically keep the one initiated by the side with the
    /// lexicographically smaller peer id, so both ends converge on the same connection.
    /// </summary>
    private bool ShouldReplaceExistingConnection(PeerCommunication existing, PeerCommunication candidate)
    {
        if (existing.IsOutgoing == candidate.IsOutgoing)
        {
            return false;
        }

        bool keepOutgoing = _settings.PeerId.AsSpan().SequenceCompareTo(candidate.PeerId) < 0;
        return keepOutgoing == candidate.IsOutgoing;
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
        _globalUtpPenaltyUntil = until;
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
        remote = NetworkUtils.NormalizeEndPoint(remote);

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
        peer.IsOutgoing = initiator;

        // Authoritative duplicate gate: claim the endpoint before registering anything else.
        if (!TryRegisterConnectedEndpoint(peer))
        {
            _governor.ReleaseConnectionSlot();
            _logger.LogDebug("Rejecting duplicate connected stream peer from {RemoteEndPoint}", peer.RemoteEndPoint);
            stream.Close();
            return;
        }

        if (peer.RemoteEndPoint != null)
        {
            peer.Country = _geoIp.GetCountry(peer.RemoteEndPoint.Address);
            peer.Priority = PeerPriority.Calculate(peer.RemoteEndPoint.Address, _torrent.Hash.ToArray());

            var history = GetOrAddKnownPeerHistory(peer.RemoteEndPoint);
            history.UpdateSource(sourceKind);
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

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
