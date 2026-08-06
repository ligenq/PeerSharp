using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Network;
using PeerSharp.BEncoding;
using System.Buffers;
using System.Collections.Concurrent;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PeerSharp.Internals.Dht;

internal partial class DhtManager : IUdpReceiver, IDhtManager
{
    private const int ExternalIpVotesRequired = 3;
    private const int MaxTransactions = 5000;
    private const int MaxPeersPerInfoHash = 200;
    private const int MaxRecentQueries = 10000;

    /// <summary>
    /// How many rounds of find_node a bootstrap walk performs. Each round asks the nodes learned in
    /// the previous one, so the routing table fills outward from the bootstrap routers. Three is
    /// enough to populate the buckets near our own id without turning startup into a flood.
    /// </summary>
    private const int MaxFindNodeDepth = 3;

    /// <summary>BEP 5: "203 Protocol Error, such as a malformed packet, invalid arguments, or bad token".</summary>
    private const int DhtErrorProtocol = 203;

    /// <summary>BEP 5: "204 Method Unknown".</summary>
    private const int DhtErrorMethodUnknown = 204;
    private readonly IUdpListener _listener;
    private readonly ILogger<DhtManager> _logger;
    private readonly ConcurrentDictionary<string, List<DhtPeer>> _peers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentGetPeersQueries = new();

    /// <summary>BEP 44 items held on behalf of the network.</summary>
    private readonly DhtItemStore _itemStore;

    /// <summary>BEP 51: the rotating subset of stored info-hashes offered to indexers.</summary>
    private readonly DhtInfoHashSampler _infoHashSampler;
    private readonly Settings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly IDnsResolver _dnsResolver;
    private readonly ConcurrentDictionary<string, Transaction> _transactions = new();
    private Task? _bootstrapTask;
    private IDhtCallback? _callback;
    private CancellationTokenSource? _cts;
    private AtomicDisposal _disposal = new();
    private bool _stateDirty;

    private DhtExternalIpVoteTracker _externalIpVoteTracker = new(requiredVotes: ExternalIpVotesRequired);

    private DateTimeOffset _lastSecretRotation;

    private Task? _maintenanceTask;

    private byte[] _prevSecret = GenerateSecret();

    private Task? _rebootstrapTask;

    private bool _running;

    private byte[] _secret = GenerateSecret();

    private RoutingTable _table;

    public DhtManager(InfoHash id, IUdpListener listener, Settings settings, TimeProvider timeProvider, IDhtCallback? callback = null, IDnsResolver? dnsResolver = null)
        : this(id, listener, settings, timeProvider, callback, dnsResolver, NullLoggerFactory.Instance)
    {
    }

    private DhtManager(InfoHash id, IUdpListener listener, Settings settings, TimeProvider timeProvider, IDhtCallback? callback, IDnsResolver? dnsResolver, ILoggerFactory loggerFactory)
    {
        NodeId = id;
        _listener = listener;
        _settings = settings;
        _timeProvider = timeProvider;
        _dnsResolver = dnsResolver!;
        _table = new RoutingTable(NodeId.ToArray(), _timeProvider);
        _callback = callback;
        _logger = loggerFactory.CreateLogger<DhtManager>();
        _lastSecretRotation = _timeProvider.GetUtcNow();
        _itemStore = new DhtItemStore(_timeProvider);
        _infoHashSampler = new DhtInfoHashSampler(_timeProvider);
        _listener.RegisterReceiver(this);
    }

    // Require multiple confirmations
    /// <summary>
    /// BEP 42: Get our current node ID.
    /// </summary>
    public InfoHash NodeId { get; private set; }

    // DHT operations use the component lifetime token when available.
    private CancellationToken DhtToken => _cts?.Token ?? CancellationToken.None;

    /// <summary>
    /// BEP 42: Create a DhtManager with a secure node ID based on external IP.
    /// If externalIp is null, a random ID is generated initially,
    /// and will be regenerated when external IP is discovered.
    /// </summary>
    public static DhtManager CreateSecure(IUdpListener listener, Settings settings, IPAddress? externalIp = null, TimeProvider? timeProvider = null, IDnsResolver? dnsResolver = null)
    {
        byte[] id;
        if (externalIp != null && DhtSecurity.ShouldValidate(externalIp))
        {
            id = DhtSecurity.GenerateSecureNodeId(externalIp);
        }
        else
        {
            id = DhtSecurity.GenerateRandomNodeId();
        }

        var actualTimeProvider = timeProvider ?? TimeProvider.System;
        var actualDnsResolver = dnsResolver ?? new SystemDnsResolver();
        var manager = new DhtManager(id, listener, settings, actualTimeProvider, null, actualDnsResolver)
        {
            _externalIpVoteTracker = new DhtExternalIpVoteTracker(
                externalIp,
                externalIp != null ? ExternalIpVotesRequired : 0,
                ExternalIpVotesRequired)
        };
        return manager;
    }

    public static DhtManager Create(
        InfoHash id,
        IUdpListener listener,
        Settings settings,
        TimeProvider timeProvider,
        IDhtCallback? callback,
        IDnsResolver? dnsResolver,
        ILoggerFactory loggerFactory)
    {
        return new DhtManager(id, listener, settings, timeProvider, callback, dnsResolver, loggerFactory);
    }

    public void Announce(InfoHash infoHash, int port)
    {
        _disposal.ThrowIfDisposed(this);
        if (!_running)
        {
            return;
        }

        // A fresh budget per announce. The walk that follows can reach thousands of nodes, and every
        // one of them that returns a token would otherwise be told to store us.
        _announcesSentPerHash[Convert.ToHexString(infoHash.Span)] = 0;

        var nodes = _table.FindClosest(infoHash.Span, 8);
        foreach (var node in nodes)
        {
            SendGetPeers(node.EndPoint, infoHash, announce: true, port: port);
        }
    }

    /// <summary>
    /// BEP 5 stores a peer on the nodes nearest the info hash, and eight is the number every
    /// implementation uses. The announce rides along with an iterative lookup, so it inherits that
    /// walk unless it is bounded: once announcing started working at all, one 100 second run reached
    /// 3688 distinct nodes. That is not storing a peer, it is flooding the DHT with one.
    /// </summary>
    private const int MaxAnnouncePeerNodes = 8;

    private readonly ConcurrentDictionary<string, int> _announcesSentPerHash = new();

    /// <summary>Whether this announce still has budget left for one more node.</summary>
    private bool TryTakeAnnounceSlot(InfoHash infoHash)
    {
        string key = Convert.ToHexString(infoHash.Span);
        int sent = _announcesSentPerHash.AddOrUpdate(key, 1, static (_, count) => count + 1);
        return sent <= MaxAnnouncePeerNodes;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public int FindPeers(InfoHash infoHash)
    {
        _disposal.ThrowIfDisposed(this);
        if (!_running)
        {
            return 0;
        }

        var nodes = _table.FindClosest(infoHash.Span, 8);
        int queried = 0;
        foreach (var node in nodes)
        {
            if (SendGetPeers(node.EndPoint, infoHash))
            {
                queried++;
            }
        }

        return queried;
    }

    /// <summary>
    /// BEP 5: Send a ping to a DHT node. Used when receiving Port messages from peers.
    /// The node will be added to our routing table when it responds.
    /// </summary>
    public void Ping(IPEndPoint ep)
    {
        _disposal.ThrowIfDisposed(this);
        if (!_running)
        {
            return;
        }

        Span<byte> tid = stackalloc byte[4];
        GenerateTransactionId(tid);

        var dict = new BDict();
        dict.Dict["t"] = new BString(tid.ToArray());
        dict.Dict["y"] = new BString("q"u8.ToArray());
        dict.Dict["q"] = new BString("ping"u8.ToArray());

        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        dict.Dict["a"] = a;

        RegisterTransaction(tid, "ping", InfoHash.Empty);
        SendPacket(dict, ep, DhtToken);
    }

    public void Receive(byte[] data, IPEndPoint remote)
    {
        if (!_running)
        {
            return;
        }
        // DHT packets are Bencoded dictionaries, starting with 'd' (100)
        if (data.Length == 0 || data[0] != (byte)'d')
        {
            return;
        }

        ProcessMessage(data, remote);
    }

    /// <summary>
    /// BEP 33: Request scrape statistics (seed/peer counts) for an info hash.
    /// Results are delivered via OnScrapeResult event.
    /// </summary>
    public void ScrapeInfoHash(InfoHash infoHash)
    {
        _disposal.ThrowIfDisposed(this);
        if (!_running)
        {
            return;
        }

        var nodes = _table.FindClosest(infoHash.Span, 8);
        foreach (var node in nodes)
        {
            SendGetPeers(node.EndPoint, infoHash, scrape: true);
        }
    }

    /// <summary>
    /// Set the callback for DHT results.
    /// </summary>
    public void SetCallback(IDhtCallback callback)
    {
        _callback = callback;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_running)
        {
            return Task.CompletedTask;
        }

        _running = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        RestoreInitialState();

        _bootstrapTask = BootstrapAsync(_cts.Token);

        _maintenanceTask = RunMaintenanceAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _running = false;

        var cts = _cts;
        if (cts != null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        Task[] tasks = [_bootstrapTask ?? Task.CompletedTask, _maintenanceTask ?? Task.CompletedTask, _rebootstrapTask ?? Task.CompletedTask];

        // Detach state up front so no new background work observes a live token.
        _cts = null;
        _bootstrapTask = null;
        _maintenanceTask = null;
        _rebootstrapTask = null;

        var completion = Task.WhenAll(tasks);
        if (completion.IsCompleted)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts?.IsCancellationRequested == true)
            {
                // Expected when all background work honours the component cancellation token.
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "DHT background task failed during shutdown");
            }

            cts?.Dispose();
        }
        else
        {
            // A platform DNS resolver may ignore cancellation. DHT is already inactive, and
            // its remaining work cannot mutate application state, so observe it without
            // delaying process shutdown. Dispose the cancellation source only once that work
            // finishes, so it is never disposed out from under an in-flight operation.
            _logger.LogDebug("DHT background work is still completing after cancellation");
            ObserveLateCompletion(completion, cts);
        }
    }

    private void ObserveLateCompletion(Task completion, CancellationTokenSource? cts)
    {
        _ = completion.ContinueWith(
            completed =>
            {
                if (completed.Exception is { } ex)
                {
                    _logger.LogTrace(ex.GetBaseException(), "Late DHT background task failed after shutdown");
                }

                cts?.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static byte[] CalculateToken(IPAddress addr, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> infoHash)
    {
        Span<byte> ipBytes = stackalloc byte[16];
        if (!addr.TryWriteBytes(ipBytes, out int ipLen))
        {
            return [];
        }

        int totalLen = ipLen + secret.Length + infoHash.Length;

        var data = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            var work = data[..totalLen].AsSpan();
            addr.TryWriteBytes(work, out _);
            secret.CopyTo(work.Slice(ipLen, secret.Length));
            infoHash.CopyTo(work[(ipLen + secret.Length)..]);

            Span<byte> hash = stackalloc byte[20];
            SHA1.HashData(work, hash);
            return hash[..8].ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static void GenerateTransactionId(Span<byte> destination)
    {
        RandomNumberGenerator.Fill(destination);
    }

    private static byte[] GenerateSecret()
    {
        byte[] secret = new byte[32];
        RandomNumberGenerator.Fill(secret);
        return secret;
    }

    private void RestoreInitialState()
    {
        var state = _settings.Dht.InitialState;
        if (state == null)
        {
            return;
        }

        try
        {
            if (state.NodeId?.Length == 20)
            {
                NodeId = state.NodeId;
                _table = new RoutingTable(NodeId.ToArray(), _timeProvider);
            }

            foreach (var node in state.Nodes)
            {
                _table.AddNode(node.Id, node.EndPoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore initial DHT state");
        }
    }

    private void MarkStateDirty()
    {
        _stateDirty = true;
    }

    public DhtState? ConsumeStateSnapshot()
    {
        if (!_stateDirty)
        {
            return null;
        }

        try
        {
            var nodes = _table.GetAllNodes();
            var state = new DhtState(
                NodeId.ToArray(),
                nodes.ConvertAll(n => new DhtNode(n.Id, n.EndPoint)));

            _stateDirty = false;
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build DHT state snapshot");
            return null;
        }
    }

    /// <summary>
    /// Whether this machine has an IPv6 address worth bootstrapping over. Distinct from the OS merely
    /// supporting IPv6, which Windows reports even on a network that has none: dialling a v6 bootstrap
    /// node from a machine with only a link-local address buys nothing but a timeout.
    /// </summary>
    private static bool HasGlobalIPv6() => NetworkUtils.GetGlobalIPv6Address() is not null;

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var nodes = _settings.Dht.BootstrapNodes;
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var ips = await _dnsResolver
                    .GetHostAddressesAsync(node.Host, cancellationToken)
                    .ConfigureAwait(false);
                // One address of each family, not merely the first the resolver happened to list.
                // BEP 32 describes two overlaid DHTs, and a node is only in the one it can be reached
                // over: bootstrapping into whichever family DNS returned first left the routing table
                // entirely IPv4, so the 'want n6' we ask for had nobody to ask. On a connection with
                // real IPv6, that showed as eight IPv6 peers dialling in and not one being dialled -
                // we had no IPv6 addresses to try, because nothing IPv6 had ever answered us.
                var v4 = Array.Find(ips, static ip => ip.AddressFamily == AddressFamily.InterNetwork);
                var v6 = HasGlobalIPv6()
                    ? Array.Find(ips, static ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                    : null;

                foreach (var ip in new[] { v4, v6 })
                {
                    if (ip is null)
                    {
                        continue;
                    }

                    var endpoint = new IPEndPoint(ip, node.Port);

                    // Which node, over which family. Bootstrap is the one step everything else depends
                    // on, and it logged nothing at all - so "did we even try IPv6?" could not be
                    // answered from a log, only guessed at.
                    _logger.LogDebug(
                        "DHT bootstrapping from {Host} at {Endpoint} ({Family})",
                        node.Host,
                        endpoint,
                        ip.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4");

                    // Ping proves reachability; find_node for our own id is what actually populates
                    // the routing table, by asking for the nodes nearest us and walking into those.
                    Ping(endpoint);
                    SendFindNode(endpoint, NodeId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex)
            {
                // A resolver that will not answer for a host is an environment fact, not a fault here,
                // and its stack trace is the same every time. Some networks filter these names
                // deliberately; the other bootstrap nodes are tried regardless, which is why there are
                // several. Kept at warning because losing them all leaves the DHT with nowhere to
                // start, but without the trace.
                #pragma warning disable S6667
                _logger.LogWarning(
                    "Could not resolve DHT bootstrap node {Host}: {Reason}", node.Host, ex.Message);
                #pragma warning restore S6667
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve DHT bootstrap node {Host}", node.Host);
            }
        }
    }

    private byte[] GenerateToken(IPEndPoint remote, ReadOnlySpan<byte> infoHash)
    {
        RotateSecret();
        return CalculateToken(remote.Address, _secret, infoHash);
    }

    private void HandleQuery(BDict node, IPEndPoint remote)
    {
        var q = node.GetString("q");
        // Transaction ID

        if (node.Get("a") is not BDict a || node.Get("t") is not BString t)
        {
            return;
        }

        // BEP 43: a read-only node "no longer responds to 'query' messages that it receives". Dropping
        // them silently is the point of the mode - a transient or unreachable node that answered would
        // be added to routing tables it cannot serve.
        if (_settings.Dht.ReadOnly)
        {
            return;
        }

        var id = a.GetBytes("id");
        if (id != null)
        {
            // BEP 43: a sender flagged read-only must not enter the routing table. Pinging it later
            // would go unanswered and cost it traffic, so it would only ever be dead weight - but the
            // query itself is still serviced as usual.
            if ((node.GetLong("ro") ?? 0) == 1)
            {
                _logger.LogTrace("BEP 43: not adding read-only node {Remote} to the routing table", remote);
            }
            else
            {
                _table.AddNode(id.Value.Span, remote);
                MarkStateDirty();
            }
        }

        var r = new BDict();
        r.Dict["id"] = new BString(NodeId.ToArray());

        // BEP 32: which address families this querier wants back. Defaults to the family the query
        // arrived over, which is what a node that predates the extension expects.
        var (wantV4, wantV6) = ReadWant(a, remote);

        if (q == "ping")
        {
            SendResponse(t, r, remote);
        }
        else if (q == "find_node")
        {
            var target = a.GetBytes("target");
            if (target != null)
            {
                var nodes = _table.FindClosest(target.Value.Span, 8);
                // BEP 32: Include both nodes (IPv4) and nodes6 (IPv6) in response
                var nodesV4 = DhtCompactNodeCodec.Encode(nodes, ipv6: false);
                var nodesV6 = DhtCompactNodeCodec.Encode(nodes, ipv6: true);
                if (wantV4 && nodesV4.Length > 0)
                {
                    r.Dict["nodes"] = new BString(nodesV4);
                }

                if (wantV6 && nodesV6.Length > 0)
                {
                    r.Dict["nodes6"] = new BString(nodesV6);
                }

                SendResponse(t, r, remote);
            }
        }
        else if (q == "get_peers")
        {
            var infoHash = a.GetBytes("info_hash");
            if (infoHash != null)
            {
                r.Dict["token"] = new BString(GenerateToken(remote, infoHash.Value.Span));

                // BEP 33: Check if scrape data is requested
                bool wantsScrape = a.Get("scrape") is BNumber scrapeRequested && scrapeRequested.Value == 1;

                var hashStr = Convert.ToHexString(infoHash.Value.Span);
                if (_peers.TryGetValue(hashStr, out var peers))
                {
                    // BEP 33: Build bloom filters if scrape requested
                    DhtBloomFilter? bfSeeds = wantsScrape ? new DhtBloomFilter() : null;
                    DhtBloomFilter? bfPeers = wantsScrape ? new DhtBloomFilter() : null;
                    var endpoints = new List<IPEndPoint>();

                    lock (peers)
                    {
                        foreach (var peer in peers.Take(50)) // Limit 50
                        {
                            endpoints.Add(peer.EndPoint);
                            // BEP 33: Add to bloom filters
                            // Note: We don't track seed vs leech status, so add all to peers filter
                            bfPeers?.Add(peer.EndPoint.Address);
                        }
                    }

                    var valuesV4 = DhtCompactPeerCodec.Encode(endpoints, ipv6: false);
                    var valuesV6 = DhtCompactPeerCodec.Encode(endpoints, ipv6: true);

                    if (wantV4 && valuesV4.Count > 0)
                    {
                        var values = new BList();
                        values.List.AddRange(valuesV4.Select(value => new BString(value)));
                        r.Dict["values"] = values;
                    }

                    if (wantV6 && valuesV6.Count > 0)
                    {
                        var values = new BList();
                        values.List.AddRange(valuesV6.Select(value => new BString(value)));
                        r.Dict["values6"] = values;
                    }

                    // BEP 33: Include bloom filters in response
                    if (wantsScrape && bfPeers != null && bfSeeds != null)
                    {
                        // BFsd = seeds bloom filter (we don't track seeds separately, so empty)
                        r.Dict["BFsd"] = new BString(bfSeeds.GetBytes());
                        // BFpe = peers bloom filter
                        r.Dict["BFpe"] = new BString(bfPeers.GetBytes());
                    }
                }
                else
                {
                    // BEP 32: Include both nodes and nodes6 when returning closest nodes
                    var nodes = _table.FindClosest(infoHash.Value.Span, 8);
                    var nodesV4 = DhtCompactNodeCodec.Encode(nodes, ipv6: false);
                    var nodesV6 = DhtCompactNodeCodec.Encode(nodes, ipv6: true);
                    if (wantV4 && nodesV4.Length > 0)
                    {
                        r.Dict["nodes"] = new BString(nodesV4);
                    }

                    if (wantV6 && nodesV6.Length > 0)
                    {
                        r.Dict["nodes6"] = new BString(nodesV6);
                    }

                    // BEP 33: Return empty bloom filters if no peers known
                    if (wantsScrape)
                    {
                        r.Dict["BFsd"] = new BString(new DhtBloomFilter().GetBytes());
                        r.Dict["BFpe"] = new BString(new DhtBloomFilter().GetBytes());
                    }
                }
                SendResponse(t, r, remote);
            }
        }
        else if (q == "sample_infohashes")
        {
            HandleSampleInfoHashesQuery(a, t, r, remote);
        }
        else if (q == "get")
        {
            HandleGetQuery(a, t, r, remote);
        }
        else if (q == "put")
        {
            HandlePutQuery(a, t, remote);
        }
        else if (q == "announce_peer")
        {
            var infoHash = a.GetBytes("info_hash");
            var token = a.GetBytes("token");

            if (infoHash == null || token == null)
            {
                SendError(t, DhtErrorProtocol, "Missing arguments", remote);
            }
            else if (!ValidateToken(token.Value.Span, remote, infoHash.Value.Span))
            {
                // BEP 5: a bad token gets a 203 Protocol Error reply
                SendError(t, DhtErrorProtocol, "Invalid token", remote);
            }
            else
            {
                int p = a.Get("port") is BNumber port ? (int)port.Value : remote.Port;
                if (a.Get("implied_port") is BNumber impliedPort && impliedPort.Value != 0)
                {
                    p = remote.Port;
                }

                var hashStr = Convert.ToHexString(infoHash.Value.Span);
                var ep = new IPEndPoint(remote.Address, p);

                var peerList = _peers.GetOrAdd(hashStr, _ => []);
                lock (peerList)
                {
                    var existing = peerList.FirstOrDefault(x => x.EndPoint.Equals(ep));
                    if (existing != null)
                    {
                        existing.LastSeen = _timeProvider.GetUtcNow();
                    }
                    else if (peerList.Count < MaxPeersPerInfoHash)
                    {
                        peerList.Add(new DhtPeer { EndPoint = ep, LastSeen = _timeProvider.GetUtcNow() });
                    }
                }

                SendResponse(t, r, remote);
            }
        }
    }

    /// <summary>
    /// BEP 51 <c>sample_infohashes</c>. Reports a subset of the info-hashes we hold peers for, how
    /// many we hold in total, and how long that subset stays put, alongside the closest nodes to the
    /// requested target - the node list is what lets an indexer traverse the keyspace with a single
    /// RPC per node instead of interleaving find_node.
    ///
    /// <para>
    /// This discloses nothing that was not already public: any node can learn the same info-hashes by
    /// asking us <c>get_peers</c> for them, and we only ever hold hashes that peers announced to us.
    /// It is still opt-out via <see cref="DhtSettings.AnswerInfoHashSampling"/> for operators who
    /// would rather not make indexing cheap.
    /// </para>
    /// </summary>
    private void HandleSampleInfoHashesQuery(BDict a, BString t, BDict r, IPEndPoint remote)
    {
        if (!_settings.Dht.AnswerInfoHashSampling)
        {
            // BEP 5's "204 Method Unknown" is exactly how a node says it does not implement a query,
            // and an indexer already has to handle it from every node that predates BEP 51.
            SendError(t, DhtErrorMethodUnknown, "Method Unknown", remote);
            return;
        }

        var targetBytes = a.GetBytes("target");
        if (targetBytes is null || targetBytes.Value.Length != DhtTarget.Length)
        {
            SendError(t, DhtErrorProtocol, "Missing or malformed target", remote);
            return;
        }

        var sample = _infoHashSampler.Take(_peers.Keys);

        r.Dict["interval"] = new BNumber(sample.IntervalSeconds);
        r.Dict["num"] = new BNumber(sample.Num);

        // The spec requires the field even when it is empty, so an indexer can tell "no hashes" from
        // "does not implement this".
        r.Dict["samples"] = new BString(sample.Samples);

        // BEP 32: same dual node lists as every other reply that carries nodes.
        var nodes = _table.FindClosest(targetBytes.Value.Span, 8);
        var nodesV4 = DhtCompactNodeCodec.Encode(nodes, ipv6: false);
        var nodesV6 = DhtCompactNodeCodec.Encode(nodes, ipv6: true);
        if (nodesV4.Length > 0)
        {
            r.Dict["nodes"] = new BString(nodesV4);
        }

        if (nodesV6.Length > 0)
        {
            r.Dict["nodes6"] = new BString(nodesV6);
        }

        SendResponse(t, r, remote);
    }

    /// <summary>
    /// BEP 44 <c>get</c>. Answers with the stored item when we hold one, and always with a write
    /// token and the closest nodes we know, so the caller can both continue the lookup and put
    /// afterwards.
    /// </summary>
    private void HandleGetQuery(BDict a, BString t, BDict r, IPEndPoint remote)
    {
        var targetBytes = a.GetBytes("target");
        if (targetBytes is null || targetBytes.Value.Length != DhtTarget.Length)
        {
            SendError(t, DhtErrorProtocol, "Missing or malformed target", remote);
            return;
        }

        // The token is keyed on the target exactly as get_peers keys it on the info-hash, so the
        // same validation covers a subsequent put.
        r.Dict["token"] = new BString(GenerateToken(remote, targetBytes.Value.Span));

        var target = new DhtTarget(targetBytes.Value.Span);
        var item = _itemStore.TryGet(target);

        if (item is DhtMutableItem mutable)
        {
            r.Dict["seq"] = new BNumber(mutable.SequenceNumber);

            // A caller that already holds sequence number N only wants the value if ours is
            // newer; replying with seq alone saves sending a payload it would discard.
            long? knownSequence = a.Get("seq") is BNumber requested ? requested.Value : null;
            if (knownSequence is null || mutable.SequenceNumber > knownSequence.Value)
            {
                r.Dict["v"] = mutable.Value;
                r.Dict["k"] = new BString(mutable.PublicKey);
                r.Dict["sig"] = new BString(mutable.Signature);
            }
        }
        else if (item is not null)
        {
            r.Dict["v"] = item.Value;
        }

        // Closest nodes always accompany the reply; a get is a lookup step as well as a read.
        var nodes = _table.FindClosest(targetBytes.Value.Span, 8);
        var nodesV4 = DhtCompactNodeCodec.Encode(nodes, ipv6: false);
        var nodesV6 = DhtCompactNodeCodec.Encode(nodes, ipv6: true);
        if (nodesV4.Length > 0)
        {
            r.Dict["nodes"] = new BString(nodesV4);
        }

        if (nodesV6.Length > 0)
        {
            r.Dict["nodes6"] = new BString(nodesV6);
        }

        SendResponse(t, r, remote);
    }

    /// <summary>
    /// BEP 44 <c>put</c>.
    ///
    /// The check order is deliberate and is the node's main defence. Token validation and the
    /// rate limit are both cheap and come first; signature verification costs roughly 270
    /// microseconds, so letting an unauthenticated flood reach it would be a CPU exhaustion
    /// primitive.
    /// </summary>
    private void HandlePutQuery(BDict a, BString t, IPEndPoint remote)
    {
        var token = a.GetBytes("token");
        var value = a.Get("v");

        if (token is null || value is null)
        {
            SendError(t, DhtErrorProtocol, "Missing arguments", remote);
            return;
        }

        if (!TryReadPutItem(a, value, out var item, out var parseError))
        {
            SendError(t, (int)parseError, DescribeError(parseError), remote);
            return;
        }

        if (!ValidateToken(token.Value.Span, remote, item.Target.Span))
        {
            SendError(t, DhtErrorProtocol, "Invalid token", remote);
            return;
        }

        if (!_itemStore.IsPutAllowed(remote.Address))
        {
            SendError(t, DhtErrorProtocol, "Rate limited", remote);
            return;
        }

        long? compareAndSwap = a.Get("cas") is BNumber cas ? cas.Value : null;

        var result = _itemStore.Store(item, compareAndSwap);
        if (result != DhtPutError.None)
        {
            SendError(t, (int)result, DescribeError(result), remote);
            return;
        }

        var r = new BDict();
        r.Dict["id"] = new BString(NodeId.ToArray());
        SendResponse(t, r, remote);
    }

    /// <summary>
    /// Reads an item out of a put's arguments. Presence of <c>k</c> is what distinguishes a
    /// mutable put from an immutable one.
    /// </summary>
    private static bool TryReadPutItem(BDict a, IBNode value, out DhtItem item, out DhtPutError error)
    {
        item = null!;
        error = DhtPutError.None;

        var publicKey = a.GetBytes("k");
        if (publicKey is null)
        {
            item = new DhtImmutableItem { Value = value };
            return true;
        }

        var signature = a.GetBytes("sig");
        if (signature is null || a.Get("seq") is not BNumber sequence)
        {
            error = DhtPutError.Protocol;
            return false;
        }

        var salt = a.GetBytes("salt");
        if (salt is { Length: > DhtItem.MaxSaltLength })
        {
            error = DhtPutError.SaltTooBig;
            return false;
        }

        item = new DhtMutableItem
        {
            Value = value,
            PublicKey = publicKey.Value.ToArray(),
            SequenceNumber = sequence.Value,
            Signature = signature.Value.ToArray(),
            Salt = salt?.ToArray(),
        };
        return true;
    }

    private static string DescribeError(DhtPutError error) => error switch
    {
        DhtPutError.ValueTooBig => "message (v field) too big",
        DhtPutError.InvalidSignature => "invalid signature",
        DhtPutError.SaltTooBig => "salt (salt field) too big",
        DhtPutError.CasMismatch => "the CAS hash mismatched, re-read value and try again",
        DhtPutError.SequenceNumberTooLow => "sequence number less than current",
        _ => "protocol error",
    };

    /// <summary>
    /// Continues an iterative lookup into a node we have just learned about. get_peers keeps
    /// walking until it runs out of closer nodes; find_node stops after
    /// <see cref="MaxFindNodeDepth"/> rounds, which is enough to seed the routing table without
    /// fanning out indefinitely.
    /// </summary>
    private void ContinueWalk(Transaction trans, IPEndPoint discovered)
    {
        switch (trans.Type)
        {
            case "get_peers":
                SendGetPeers(discovered, trans.InfoHash, trans.Announce, trans.Port);
                break;

            case "find_node" when trans.Depth + 1 < MaxFindNodeDepth:
                SendFindNode(discovered, trans.InfoHash, trans.Depth + 1);
                break;

            default:
                break;
        }
    }

    private void HandleResponse(BDict node, IPEndPoint remote)
    {
        var tBytes = node.GetBytes("t");
        if (tBytes == null)
        {
            return;
        }

        // Use Latin1 encoding to match how transactions are registered
        var t = Encoding.Latin1.GetString(tBytes.Value.Span);

        // BEP 44 queries are awaited by their caller rather than driven by callbacks, so they
        // have their own correlation table and are matched before the fire-and-forget one.
        if (_pendingQueries.TryRemove(t, out var pending))
        {
            if (node.Get("r") is BDict itemReply)
            {
                var responderId = itemReply.GetBytes("id");
                if (responderId != null)
                {
                    _table.AddNode(responderId.Value.Span, remote);
                    MarkStateDirty();
                }
            }

            pending.TrySetResult(node);
            return;
        }

        if (!_transactions.TryRemove(t, out var trans))
        {
            return;
        }

        if (node.Get("r") is BDict r)
        {
            var id = r.GetBytes("id");
            if (id != null)
            {
                _table.AddNode(id.Value.Span, remote);
                MarkStateDirty();
            }

            // BEP 42: Check for "ip" field containing our external IP
            var ipField = r.GetBytes("ip");
            if (ipField != null)
            {
                ProcessExternalIp(ipField.Value.Span);
            }

            // BEP 32: Parse both IPv4 (nodes) and IPv6 (nodes6) compact node info
            var nodesData = r.GetBytes("nodes");
            if (nodesData != null)
            {
                var nodes = DhtCompactNodeCodec.Parse(nodesData.Value.Span, ipv6: false);
                foreach (var n in nodes)
                {
                    _table.AddNode(n.Id, n.EndPoint);
                    MarkStateDirty();
                    ContinueWalk(trans, n.EndPoint);
                }
            }

            var nodes6Data = r.GetBytes("nodes6");
            if (nodes6Data != null)
            {
                var nodes = DhtCompactNodeCodec.Parse(nodes6Data.Value.Span, ipv6: true);
                foreach (var n in nodes)
                {
                    _table.AddNode(n.Id, n.EndPoint);
                    MarkStateDirty();
                    ContinueWalk(trans, n.EndPoint);
                }
            }

            // BEP 32: Parse both IPv4 (values) and IPv6 (values6) peer lists
            var peers = new List<IPEndPoint>();

            if (r.Get("values") is BList values && trans.Type == "get_peers")
            {
                peers.AddRange(DhtCompactPeerCodec.Parse(values.List.OfType<BString>().Select(value => value.Value), ipv6: false));
            }

            if (r.Get("values6") is BList values6 && trans.Type == "get_peers")
            {
                peers.AddRange(DhtCompactPeerCodec.Parse(values6.List.OfType<BString>().Select(value => value.Value), ipv6: true));
            }

            if (peers.Count > 0)
            {
                _callback?.OnPeersFound(trans.InfoHash, peers);
            }

            // BEP 33: Parse bloom filters if this was a scrape request
            if (trans.Scrape)
            {
                var bfSdData = r.GetBytes("BFsd");
                var bfPeData = r.GetBytes("BFpe");

                if (bfSdData != null && bfPeData != null &&
                    bfSdData.Value.Length == DhtBloomFilter.FilterSizeBytes &&
                    bfPeData.Value.Length == DhtBloomFilter.FilterSizeBytes)
                {
                    var bfSeeds = new DhtBloomFilter(bfSdData.Value.ToArray());
                    var bfPeers = new DhtBloomFilter(bfPeData.Value.ToArray());

                    int estimatedSeeds = bfSeeds.EstimateCount();
                    int estimatedPeers = bfPeers.EstimateCount();

                    _callback?.OnScrapeResult(trans.InfoHash, estimatedSeeds, estimatedPeers);
                }
            }

            var token = r.GetBytes("token");
            if (trans.Announce && token != null && TryTakeAnnounceSlot(trans.InfoHash))
            {
                SendAnnouncePeer(remote, trans.InfoHash.ToArray(), token.Value.ToArray(), trans.Port);
            }
        }
    }

    /// <summary>
    /// BEP 42: Process external IP field from DHT response.
    /// When we receive consistent reports of our external IP, regenerate our node ID.
    /// </summary>
    private void ProcessExternalIp(ReadOnlySpan<byte> ipBytes)
    {
        ApplyExternalIpVote(_externalIpVoteTracker.ProcessReport(ipBytes), "BEP 42 DHT node");
    }

    /// <summary>
    /// BEP 24: an external address a tracker reported. It joins the same vote pool as the DHT's own
    /// reports, so a tracker cannot single-handedly move our node ID - the vote threshold still has
    /// to be met, and the caller is responsible for not submitting the same tracker's opinion twice.
    /// </summary>
    public void ReportExternalIp(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ApplyExternalIpVote(_externalIpVoteTracker.ProcessReport(address), "BEP 24 tracker");
    }

    private void ApplyExternalIpVote(DhtExternalIpVoteResult result, string source)
    {
        switch (result.Status)
        {
            case DhtExternalIpVoteStatus.FirstReport:
                _logger.LogDebug("{Source}: First external IP report: {ExternalIP}", source, result.Address);
                break;
            case DhtExternalIpVoteStatus.Progress:
                _logger.LogDebug("{Source}: External IP confirmed ({Votes}/{Required}): {ExternalIP}", source, result.Votes, result.RequiredVotes, result.Address);
                break;
            case DhtExternalIpVoteStatus.Confirmed:
                _logger.LogDebug("{Source}: External IP confirmed ({Votes}/{Required}): {ExternalIP}", source, result.Votes, result.RequiredVotes, result.Address);
                RegenerateNodeId(result.Address!);
                break;
            case DhtExternalIpVoteStatus.Changed:
                _logger.LogDebug("{Source}: External IP changed to: {ExternalIP}", source, result.Address);
                break;
        }
    }

    private void ProcessMessage(byte[] data, IPEndPoint remote)
    {
        try
        {
            if (BencodeParser.Parse(data) is not BDict node)
            {
                return;
            }

            var y = node.GetString("y");
            if (y == "q")
            {
                HandleQuery(node, remote);
            }
            else if (y == "r")
            {
                HandleResponse(node, remote);
            }
            // Error replies were previously dropped on the floor. That is harmless for the
            // callback-driven queries, but a BEP 44 caller awaiting a reply would sit out its
            // whole timeout against a node that answered immediately.
            else if (y == "e" && !TryCompleteItemQueryWithError(node))
            {
                _logger.LogDebug("Received a DHT error reply from {Remote} with no matching query", remote);
            }
        }
        catch (FormatException)
        {
            // Malformed DHT message - ignore
        }
        catch (InvalidOperationException)
        {
            // Invalid bencode structure - ignore
        }
    }

    /// <summary>
    /// BEP 42: Regenerate our node ID based on external IP.
    /// This clears the routing table since our ID has changed.
    /// </summary>
    private void RegenerateNodeId(IPAddress externalIp)
    {
        byte[] newId = DhtSecurity.GenerateSecureNodeId(externalIp);
        _logger.LogInformation("BEP 42: Regenerating node ID for IP {ExternalIP}: {NodeIdPrefix}...", externalIp, Convert.ToHexString(newId)[..8]);

        // Update our ID and create a new routing table
        NodeId = newId;
        _table = new RoutingTable(NodeId.ToArray(), _timeProvider);
        MarkStateDirty();

        // Re-bootstrap to populate the new routing table. Skip once stopped so we
        // never spawn an uncancellable straggler after the token has been detached.
        if (!_running)
        {
            return;
        }

        _rebootstrapTask = BootstrapAsync(DhtToken);
    }

    private void RegisterTransaction(ReadOnlySpan<byte> tid, string type, InfoHash infoHash, bool announce = false, int port = 0, bool scrape = false, int depth = 0)
    {
        var idString = Encoding.Latin1.GetString(tid);
        _transactions[idString] = new Transaction
        {
            Id = idString,
            Type = type,
            InfoHash = infoHash,
            Timestamp = _timeProvider.GetUtcNow(),
            Announce = announce,
            Port = port,
            Scrape = scrape,
            Depth = depth
        };
    }

    /// <summary>
    /// Sends a find_node query, which is the mechanism by which a routing table actually fills.
    ///
    /// Nothing here sent one previously: find_node existed only as a handler for queries from other
    /// peers. A node that merely pings its bootstrap routers learns exactly those routers and has
    /// no way to discover anyone else, so every iterative lookup - get_peers as much as BEP 44 get
    /// - began from a table of at most three entries. Asking for the nodes nearest a target and
    /// then asking those in turn is what BEP 5 expects, and what makes lookups work at all.
    /// </summary>
    /// <param name="ep">The node to ask.</param>
    /// <param name="target">The id to find nodes near; our own id during bootstrap.</param>
    /// <param name="depth">Rounds already walked. The response handler stops at MaxFindNodeDepth.</param>
    private void SendFindNode(IPEndPoint ep, InfoHash target, int depth = 0)
    {
        if (!_running || _transactions.Count >= MaxTransactions)
        {
            return;
        }

        // The same dedup guard get_peers uses, so a walk cannot loop between nodes that know each
        // other.
        var queryKey = $"fn:{ep}:{Convert.ToHexString(target.Span)}";
        var now = _timeProvider.GetUtcNow();
        if (_recentGetPeersQueries.TryGetValue(queryKey, out var lastQueried) &&
            (now - lastQueried).TotalMinutes < ProtocolConstants.DhtTransactionTimeoutMinutes)
        {
            return;
        }

        _recentGetPeersQueries[queryKey] = now;

        Span<byte> tid = stackalloc byte[4];
        GenerateTransactionId(tid);

        var dict = new BDict();
        dict.Dict["t"] = new BString(tid.ToArray());
        dict.Dict["y"] = new BString("q"u8.ToArray());
        dict.Dict["q"] = new BString("find_node"u8.ToArray());

        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        a.Dict["target"] = new BString(target.Span.ToArray());
        AddWant(a);
        dict.Dict["a"] = a;

        RegisterTransaction(tid, "find_node", target, depth: depth);
        SendPacket(dict, ep, DhtToken);
    }

    /// <summary>
    /// BEP 32 <c>want</c>: the address families we would like back.
    ///
    /// <para>
    /// A node answering a query is entitled to return only the family the query arrived over unless
    /// told otherwise, so a v4 socket that never asks may simply never be given IPv6 nodes and its
    /// v6 routing table stays empty. We ask for both whenever IPv6 is available to us.
    /// </para>
    /// </summary>
    private static void AddWant(BDict a)
    {
        var want = new BList();
        want.List.Add(new BString("n4"u8.ToArray()));

        if (System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            want.List.Add(new BString("n6"u8.ToArray()));
        }

        a.Dict["want"] = want;
    }

    /// <summary>
    /// Which families a querier asked for. Absent <c>want</c>, BEP 32 says to answer with the family
    /// the query arrived over, which is what a node that does not understand the extension expects -
    /// sending it the other family is bytes it will discard.
    /// </summary>
    private static (bool WantV4, bool WantV6) ReadWant(BDict? a, IPEndPoint remote)
    {
        if (a?.Get("want") is BList want)
        {
            bool v4 = false;
            bool v6 = false;
            foreach (var entry in want.List.OfType<BString>())
            {
                var text = System.Text.Encoding.ASCII.GetString(entry.Value.Span);
                if (string.Equals(text, "n4", StringComparison.Ordinal))
                {
                    v4 = true;
                }
                else if (string.Equals(text, "n6", StringComparison.Ordinal))
                {
                    v6 = true;
                }
            }

            if (v4 || v6)
            {
                return (v4, v6);
            }
        }

        bool arrivedOverV6 = remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        return (!arrivedOverV6, arrivedOverV6);
    }

    private void RotateSecret()
    {
        if ((_timeProvider.GetUtcNow() - _lastSecretRotation).TotalMinutes > 10)
        {
            _prevSecret = _secret;
            _secret = GenerateSecret();
            _lastSecretRotation = _timeProvider.GetUtcNow();
        }
    }

    internal void PerformMaintenance(DateTimeOffset now)
    {
        // Cleanup transactions - direct enumeration is safe for ConcurrentDictionary
        foreach (var kvp in _transactions)
        {
            if ((now - kvp.Value.Timestamp).TotalMinutes > ProtocolConstants.DhtTransactionTimeoutMinutes)
            {
                _transactions.TryRemove(kvp.Key, out _);
            }
        }

        // Cleanup recent query deduplication cache
        foreach (var kvp in _recentGetPeersQueries)
        {
            if ((now - kvp.Value).TotalMinutes > ProtocolConstants.DhtTransactionTimeoutMinutes)
            {
                _recentGetPeersQueries.TryRemove(kvp.Key, out _);
            }
        }

        // Hard cap: if the dedup cache is too large, clear old entries aggressively
        if (_recentGetPeersQueries.Count > MaxRecentQueries)
        {
            var cutoff = now.AddMinutes(-1);
            foreach (var kvp in _recentGetPeersQueries)
            {
                if (kvp.Value < cutoff)
                {
                    _recentGetPeersQueries.TryRemove(kvp.Key, out _);
                }
            }
        }

        // Cleanup peers - direct enumeration
        foreach (var kvp in _peers)
        {
            var key = kvp.Key;
            var peerList = kvp.Value;

            lock (peerList)
            {
                peerList.RemoveAll(p => (now - p.LastSeen).TotalMinutes > ProtocolConstants.DhtPeerCacheTimeoutMinutes);

                if (peerList.Count == 0)
                {
                    // Use explicit ICollection remove for value equality check
                    ((ICollection<KeyValuePair<string, List<DhtPeer>>>)_peers)
                        .Remove(new KeyValuePair<string, List<DhtPeer>>(key, peerList));
                }
            }
        }
    }

    internal int TransactionCount => _transactions.Count;
    internal int RecentQueryCount => _recentGetPeersQueries.Count;
    internal int PeerCacheEntryCount => _peers.Count;

    internal void InjectTransaction(string key, DateTimeOffset timestamp)
    {
        _transactions[key] = new Transaction { Id = key, Type = "ping", InfoHash = InfoHash.Empty, Timestamp = timestamp };
    }

    internal void InjectRecentQuery(string key, DateTimeOffset timestamp)
    {
        _recentGetPeersQueries[key] = timestamp;
    }

    internal void InjectPeer(string infoHash, IPEndPoint ep, DateTimeOffset lastSeen)
    {
        var list = _peers.GetOrAdd(infoHash, _ => []);
        lock (list) { list.Add(new DhtPeer { EndPoint = ep, LastSeen = lastSeen }); }
    }

    private async Task RunMaintenanceAsync(CancellationToken token)
    {
        while (_running && !token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _timeProvider, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            try
            {
                PerformMaintenance(_timeProvider.GetUtcNow());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during DHT maintenance");
            }
        }
    }

    private void SendAnnouncePeer(IPEndPoint ep, byte[] infoHash, byte[] token, int port)
    {
        Span<byte> tid = stackalloc byte[4];
        GenerateTransactionId(tid);

        var dict = new BDict();
        dict.Dict["t"] = new BString(tid.ToArray());
        dict.Dict["y"] = new BString("q"u8.ToArray());
        dict.Dict["q"] = new BString("announce_peer"u8.ToArray());

        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        a.Dict["info_hash"] = new BString(infoHash);
        a.Dict["port"] = new BNumber(port);
        a.Dict["token"] = new BString(token);
        dict.Dict["a"] = a;

        // Worth a line. Announcing is how other peers find us at all, and when it silently stopped
        // happening - the lookup that precedes it having consumed its deduplication key - there was
        // nothing in any log to say so.
        _logger.LogDebug("DHT announce_peer to {Node} for port {Port}", ep, port);

        SendPacket(dict, ep, DhtToken);
    }

    private bool SendGetPeers(IPEndPoint ep, InfoHash infoHash, bool announce = false, int port = 0, bool scrape = false)
    {
        // Deduplicate equivalent work, but keep the intent in the key. An announce starts with the
        // same get_peers packet as a lookup, yet its response must be followed by announce_peer; a
        // scrape also asks for extra fields. Treating all three as one query silently discarded the
        // later intent -- PeerManager calls FindPeers and Announce back-to-back, so it never actually
        // announced.
        char intent = 'f';
        if (scrape)
        {
            intent = 's';
        }
        else if (announce)
        {
            intent = 'a';
        }
        var queryKey = $"{ep}:{Convert.ToHexString(infoHash.Span)}:{intent}";
        var now = _timeProvider.GetUtcNow();
        if (_recentGetPeersQueries.TryGetValue(queryKey, out var lastQueried) &&
            (now - lastQueried).TotalMinutes < ProtocolConstants.DhtTransactionTimeoutMinutes)
        {
            return false;
        }

        // Don't create new transactions if we're at capacity
        if (_transactions.Count >= MaxTransactions)
        {
            return false;
        }

        _recentGetPeersQueries[queryKey] = now;

        Span<byte> tid = stackalloc byte[4];
        GenerateTransactionId(tid);

        var dict = new BDict();
        dict.Dict["t"] = new BString(tid.ToArray());
        dict.Dict["y"] = new BString("q"u8.ToArray());
        dict.Dict["q"] = new BString("get_peers"u8.ToArray());

        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        a.Dict["info_hash"] = new BString(infoHash.ToArray());

        // BEP 33: Request scrape data (bloom filters) from the node
        if (scrape)
        {
            a.Dict["scrape"] = new BNumber(1);
        }

        AddWant(a);
        dict.Dict["a"] = a;

        RegisterTransaction(tid, "get_peers", infoHash, announce, port, scrape);
        SendPacket(dict, ep, DhtToken);
        return true;
    }

    private void SendPacket(BDict dict, IPEndPoint ep, CancellationToken ct)
    {
        // BEP 43: "the read-only DHT node places a 'ro' key in the top-level message dictionary and
        // sets its value to 1", on queries. Stamped here rather than in each query builder because this
        // is the one path every outgoing message takes - a builder that forgot the flag would quietly
        // undo the whole point of the mode.
        if (_settings.Dht.ReadOnly && dict.GetString("y") == "q")
        {
            dict.Dict["ro"] = new BNumber(1);
        }

        var result = BencodeWriter.WriteToResult(dict);
        _ = SendAsyncAndDispose(result, ep, ct);
    }

    private async Task SendAsyncAndDispose(BencodeResult result, IPEndPoint ep, CancellationToken ct)
    {
        using (result)
        {
            try
            {
                await _listener.SendAsync(result.Memory, ep, ct).ConfigureAwait(false);
            }
            catch (SocketException) { /* Network error - packet dropped */ }
            catch (ObjectDisposedException) { /* Listener disposed during shutdown */ }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "DHT send error to {EndPoint}", ep);
            }
        }
    }

    /// <summary>
    /// BEP 5: Sends an error reply: {"t": tid, "y": "e", "e": [code, message]}.
    /// </summary>
    /// <summary>
    /// Completes a pending BEP 44 query that came back as an error rather than a reply.
    /// Without this the caller would sit out the full timeout on a node that answered promptly.
    /// </summary>
    private bool TryCompleteItemQueryWithError(BDict node)
    {
        var tBytes = node.GetBytes("t");
        if (tBytes is null)
        {
            return false;
        }

        var t = Encoding.Latin1.GetString(tBytes.Value.Span);
        if (!_pendingQueries.TryRemove(t, out var pending))
        {
            return false;
        }

        pending.TrySetResult(node);
        return true;
    }

    private void SendError(BString t, int code, string message, IPEndPoint ep)
    {
        var e = new BList();
        e.List.Add(new BNumber(code));
        e.List.Add(new BString(Encoding.UTF8.GetBytes(message)));

        var dict = new BDict();
        dict.Dict["t"] = t;
        dict.Dict["y"] = new BString("e"u8.ToArray());
        dict.Dict["e"] = e;
        SendPacket(dict, ep, DhtToken);
    }

    private void SendResponse(BString t, BDict r, IPEndPoint ep)
    {
        // BEP 42: Include the querying node's IP in our response
        // This helps them discover their external IP
        Span<byte> ipBytes = stackalloc byte[16];
        if (ep.Address.TryWriteBytes(ipBytes, out int bytesWritten))
        {
            r.Dict["ip"] = new BString(ipBytes[..bytesWritten].ToArray());
        }

        var dict = new BDict();
        dict.Dict["t"] = t;
        dict.Dict["y"] = new BString("r"u8.ToArray());
        dict.Dict["r"] = r;
        SendPacket(dict, ep, DhtToken);
    }

    private bool ValidateToken(ReadOnlySpan<byte> token, IPEndPoint remote, ReadOnlySpan<byte> infoHash)
    {
        var t1 = CalculateToken(remote.Address, _secret, infoHash);
        var t2 = CalculateToken(remote.Address, _prevSecret, infoHash);

        if (token.SequenceEqual(t1))
        {
            return true;
        }

        if (token.SequenceEqual(t2))
        {
            return true;
        }

        return false;
    }

    // Storage for announced peers: InfoHash (hex) -> List of (Peer, LastSeen)
    internal sealed class DhtPeer
    {
        public required IPEndPoint EndPoint { get; init; }
        public DateTimeOffset LastSeen { get; set; }
    }

    // Transaction tracking
    internal sealed class Transaction
    {
        public bool Announce { get; init; }

        /// <summary>Rounds of find_node already walked, so recursion terminates.</summary>
        public int Depth { get; init; }

        public required string Id { get; init; }
        public InfoHash InfoHash { get; init; }

        // Intent
        public int Port { get; init; }

        public bool Scrape { get; init; }
        public DateTimeOffset Timestamp { get; set; }
        public required string Type { get; init; }
        // BEP 33: Request bloom filter stats
    }
}
