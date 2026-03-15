using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Network;
using PeerSharp.BEncoding;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Internals.Dht;

internal class DhtManager : IUdpReceiver, IDhtManager
{
    private const int ExternalIpVotesRequired = 3;
    private const int MaxTransactions = 5000;
    private const int MaxPeersPerInfoHash = 200;
    private const int MaxRecentQueries = 10000;
    private readonly Lock _externalIpLock = new();
    private readonly IUdpListener _listener;
    private readonly ILogger<DhtManager> _logger = TorrentLoggerFactory.CreateLogger<DhtManager>();
    private readonly ConcurrentDictionary<string, List<DhtPeer>> _peers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentGetPeersQueries = new();
    private readonly Settings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly IDnsResolver _dnsResolver;
    private readonly ConcurrentDictionary<string, Transaction> _transactions = new();
    private Task? _bootstrapTask;
    private IDhtCallback? _callback;
    private CancellationTokenSource? _cts;
    private AtomicDisposal _disposal = new();
    private bool _stateDirty;

    // BEP 42: External IP tracking for secure node ID
    private IPAddress? _externalIp;

    private int _externalIpVotes;

    private DateTimeOffset _lastSecretRotation;

    private Task? _maintenanceTask;

    private string _prevSecret = Guid.NewGuid().ToString();

    private Task? _rebootstrapTask;

    private bool _running;

    private string _secret = Guid.NewGuid().ToString();

    private RoutingTable _table;

    public DhtManager(InfoHash id, IUdpListener listener, Settings settings, TimeProvider timeProvider, IDhtCallback? callback = null, IDnsResolver? dnsResolver = null)
    {
        NodeId = id;
        _listener = listener;
        _settings = settings;
        _timeProvider = timeProvider;
        _dnsResolver = dnsResolver!;
        _table = new RoutingTable(NodeId.ToArray(), _timeProvider);
        _callback = callback;
        _lastSecretRotation = _timeProvider.GetUtcNow();
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
            _externalIp = externalIp
        };
        if (externalIp != null)
        {
            manager._externalIpVotes = ExternalIpVotesRequired; // Already confirmed
        }
        return manager;
    }

    public void Announce(InfoHash infoHash, int port)
    {
        _disposal.ThrowIfDisposed(this);
        if (!_running)
        {
            return;
        }

        var nodes = _table.FindClosest(infoHash.Span, 8);
        foreach (var node in nodes)
        {
            SendGetPeers(node.EndPoint, infoHash, announce: true, port: port);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            await StopAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    public void FindPeers(InfoHash infoHash)
    {
        _disposal.ThrowIfDisposed(this);
        if (!_running)
        {
            return;
        }

        var nodes = _table.FindClosest(infoHash.Span, 8);
        foreach (var node in nodes)
        {
            SendGetPeers(node.EndPoint, infoHash);
        }
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

        // DNS resolution in Bootstrap can block, so run in background
        _bootstrapTask = Task.Run(Bootstrap, ct);

        _maintenanceTask = RunMaintenanceAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _running = false;

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_bootstrapTask is { } bootstrapTask)
            await bootstrapTask.ConfigureAwait(false);

        if (_maintenanceTask is { } maintenanceTask)
            await maintenanceTask.ConfigureAwait(false);

        if (_rebootstrapTask is { } rebootstrapTask)
            await rebootstrapTask.ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
    }

    private static byte[] CalculateToken(IPAddress addr, string secret, ReadOnlySpan<byte> infoHash)
    {
        Span<byte> ipBytes = stackalloc byte[16];
        if (!addr.TryWriteBytes(ipBytes, out int ipLen))
        {
            return Array.Empty<byte>();
        }

        int secretLen = Encoding.ASCII.GetByteCount(secret);
        int totalLen = ipLen + secretLen + infoHash.Length;

        var data = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            var work = data[..totalLen].AsSpan();
            addr.TryWriteBytes(work, out _);
            Encoding.ASCII.GetBytes(secret, work.Slice(ipLen, secretLen));
            infoHash.CopyTo(work.Slice(ipLen + secretLen));

            Span<byte> hash = stackalloc byte[20];
            System.Security.Cryptography.SHA1.HashData(work, hash);
            return hash[..8].ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static byte[] EncodeNodes(List<NodeInfo> nodes, bool ipv6 = false)
    {
        using var ms = new MemoryStream();
        Span<byte> addrBytes = stackalloc byte[16];
        Span<byte> port = stackalloc byte[2];

        foreach (var node in nodes)
        {
            bool isV6 = node.EndPoint.AddressFamily == AddressFamily.InterNetworkV6;
            bool isV4 = node.EndPoint.AddressFamily == AddressFamily.InterNetwork;

            // BEP 32: IPv4 nodes are 26 bytes (20 ID + 4 IP + 2 port)
            //         IPv6 nodes are 38 bytes (20 ID + 16 IP + 2 port)
            if ((ipv6 && isV6) || (!ipv6 && isV4))
            {
                ms.Write(node.Id, 0, 20);
                node.EndPoint.Address.TryWriteBytes(addrBytes, out int written);
                ms.Write(addrBytes[..written]);

                BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)node.EndPoint.Port);
                ms.Write(port);
            }
        }
        return ms.ToArray();
    }

    private static void GenerateTransactionId(Span<byte> destination)
    {
        Random.Shared.NextBytes(destination);
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
            if (state.NodeId != null && state.NodeId.Length == 20)
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

    private static List<NodeInfo> ParseNodes(ReadOnlySpan<byte> data, bool ipv6 = false)
    {
        var list = new List<NodeInfo>();
        // BEP 32: IPv4 nodes are 26 bytes, IPv6 nodes are 38 bytes
        int nodeSize = ipv6 ? 38 : 26;
        int ipSize = ipv6 ? 16 : 4;

        for (int i = 0; i <= data.Length - nodeSize; i += nodeSize)
        {
            ReadOnlySpan<byte> id = data.Slice(i, 20);
            var ip = new IPAddress(data.Slice(i + 20, ipSize));
            int port = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 20 + ipSize));
            list.Add(new NodeInfo(id, new IPEndPoint(ip, port)));
        }
        return list;
    }

    private void Bootstrap()
    {
        var nodes = _settings.Dht.BootstrapNodes;
        foreach (var node in nodes)
        {
            try
            {
                var ips = _dnsResolver.GetHostAddresses(node.Host);
                if (ips.Length > 0)
                {
                    Ping(new IPEndPoint(ips[0], node.Port));
                }
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

        var id = a.GetBytes("id");
        if (id != null)
        {
            _table.AddNode(id.Value.Span, remote);
            MarkStateDirty();
        }

        var r = new BDict();
        r.Dict["id"] = new BString(NodeId.ToArray());

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
                var nodesV4 = EncodeNodes(nodes, ipv6: false);
                var nodesV6 = EncodeNodes(nodes, ipv6: true);
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
                    // BEP 32: Separate IPv4 (values) and IPv6 (values6) peers
                    var valuesV4 = new BList();
                    var valuesV6 = new BList();

                    // BEP 33: Build bloom filters if scrape requested
                    DhtBloomFilter? bfSeeds = wantsScrape ? new DhtBloomFilter() : null;
                    DhtBloomFilter? bfPeers = wantsScrape ? new DhtBloomFilter() : null;

                    lock (peers)
                    {
                        foreach (var peer in peers.Take(50)) // Limit 50
                        {
                            var addrBytes = peer.EndPoint.Address.GetAddressBytes();
                            var pPort = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)peer.EndPoint.Port));

                            if (peer.EndPoint.AddressFamily == AddressFamily.InterNetwork)
                            {
                                // Compact IPv4 peer info: 4 bytes IP + 2 bytes Port
                                var pData = new byte[6];
                                Array.Copy(addrBytes, 0, pData, 0, 4);
                                Array.Copy(pPort, 0, pData, 4, 2);
                                valuesV4.List.Add(new BString(pData));
                            }
                            else if (peer.EndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                            {
                                // Compact IPv6 peer info: 16 bytes IP + 2 bytes Port
                                var pData = new byte[18];
                                Array.Copy(addrBytes, 0, pData, 0, 16);
                                Array.Copy(pPort, 0, pData, 16, 2);
                                valuesV6.List.Add(new BString(pData));
                            }

                            // BEP 33: Add to bloom filters
                            // Note: We don't track seed vs leech status, so add all to peers filter
                            bfPeers?.Add(peer.EndPoint.Address);
                        }
                    }
                    if (valuesV4.List.Count > 0)
                    {
                        r.Dict["values"] = valuesV4;
                    }

                    if (valuesV6.List.Count > 0)
                    {
                        r.Dict["values6"] = valuesV6;
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
                    var nodesV4 = EncodeNodes(nodes, ipv6: false);
                    var nodesV6 = EncodeNodes(nodes, ipv6: true);
                    if (nodesV4.Length > 0)
                    {
                        r.Dict["nodes"] = new BString(nodesV4);
                    }

                    if (nodesV6.Length > 0)
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
        else if (q == "announce_peer")
        {
            var infoHash = a.GetBytes("info_hash");
            var token = a.GetBytes("token");

            if (infoHash != null && token != null && ValidateToken(token.Value.Span, remote, infoHash.Value.Span))
            {
                int p = a.Get("port") is BNumber port ? (int)port.Value : remote.Port;
                if (a.Get("implied_port") is BNumber impliedPort && impliedPort.Value != 0)
                {
                    p = remote.Port;
                }

                var hashStr = Convert.ToHexString(infoHash.Value.Span);
                var ep = new IPEndPoint(remote.Address, p);

                var peerList = _peers.GetOrAdd(hashStr, _ => new List<DhtPeer>());
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

    private void HandleResponse(BDict node, IPEndPoint remote)
    {
        var tBytes = node.GetBytes("t");
        if (tBytes == null)
        {
            return;
        }

        // Use Latin1 encoding to match how transactions are registered
        var t = Encoding.Latin1.GetString(tBytes.Value.Span);
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
                var nodes = ParseNodes(nodesData.Value.Span, ipv6: false);
                foreach (var n in nodes)
                {
                    _table.AddNode(n.Id, n.EndPoint);
                    MarkStateDirty();
                    if (trans.Type == "get_peers")
                    {
                        SendGetPeers(n.EndPoint, trans.InfoHash, trans.Announce, trans.Port);
                    }
                }
            }

            var nodes6Data = r.GetBytes("nodes6");
            if (nodes6Data != null)
            {
                var nodes = ParseNodes(nodes6Data.Value.Span, ipv6: true);
                foreach (var n in nodes)
                {
                    _table.AddNode(n.Id, n.EndPoint);
                    MarkStateDirty();
                    if (trans.Type == "get_peers")
                    {
                        SendGetPeers(n.EndPoint, trans.InfoHash, trans.Announce, trans.Port);
                    }
                }
            }

            // BEP 32: Parse both IPv4 (values) and IPv6 (values6) peer lists
            var peers = new List<IPEndPoint>();

            if (r.Get("values") is BList values && trans.Type == "get_peers")
            {
                foreach (var val in values.List)
                {
                    if (val is BString bs && bs.Value.Length == 6)
                    {
                        var ip = new IPAddress(bs.Value.Slice(0, 4).Span);
                        int port = BinaryPrimitives.ReadUInt16BigEndian(bs.Value.Span.Slice(4));
                        peers.Add(new IPEndPoint(ip, port));
                    }
                }
            }

            if (r.Get("values6") is BList values6 && trans.Type == "get_peers")
            {
                foreach (var val in values6.List)
                {
                    if (val is BString bs && bs.Value.Length == 18)
                    {
                        var ip = new IPAddress(bs.Value.Slice(0, 16).Span);
                        int port = BinaryPrimitives.ReadUInt16BigEndian(bs.Value.Span.Slice(16));
                        peers.Add(new IPEndPoint(ip, port));
                    }
                }
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
            if (trans.Announce && token != null)
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
        IPAddress? reportedIp = null;

        try
        {
            if (ipBytes.Length == 4 || ipBytes.Length == 16)
            {
                reportedIp = new IPAddress(ipBytes);
            }
        }
        catch
        {
            return;
        }

        if (reportedIp == null || !DhtSecurity.ShouldValidate(reportedIp))
        {
            return;
        }

        lock (_externalIpLock)
        {
            if (_externalIp == null)
            {
                // First report - start tracking
                _externalIp = reportedIp;
                _externalIpVotes = 1;
                _logger.LogDebug("BEP 42: First external IP report: {ExternalIP}", reportedIp);
            }
            else if (_externalIp.Equals(reportedIp))
            {
                // Same IP reported - increment vote
                if (_externalIpVotes < ExternalIpVotesRequired)
                {
                    _externalIpVotes++;
                    _logger.LogDebug("BEP 42: External IP confirmed ({Votes}/{Required}): {ExternalIP}", _externalIpVotes, ExternalIpVotesRequired, reportedIp);

                    if (_externalIpVotes >= ExternalIpVotesRequired)
                    {
                        // We have enough confirmation - regenerate node ID
                        RegenerateNodeId(reportedIp);
                    }
                }
            }
            else
            {
                // Different IP reported - reset tracking
                _externalIp = reportedIp;
                _externalIpVotes = 1;
                _logger.LogDebug("BEP 42: External IP changed to: {ExternalIP}", reportedIp);
            }
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
        _logger.LogInformation("BEP 42: Regenerating node ID for IP {ExternalIP}: {NodeIdPrefix}...", externalIp, Convert.ToHexString(newId).Substring(0, 8));

        // Update our ID and create a new routing table
        NodeId = newId;
        _table = new RoutingTable(NodeId.ToArray(), _timeProvider);
        MarkStateDirty();

        // Re-bootstrap to populate the new routing table
        _rebootstrapTask = Task.Run(Bootstrap);
    }

    private void RegisterTransaction(ReadOnlySpan<byte> tid, string type, InfoHash infoHash, bool announce = false, int port = 0, bool scrape = false)
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
            Scrape = scrape
        };
    }

    private void RotateSecret()
    {
        if ((_timeProvider.GetUtcNow() - _lastSecretRotation).TotalMinutes > 10)
        {
            _prevSecret = _secret;
            _secret = Guid.NewGuid().ToString();
            _lastSecretRotation = _timeProvider.GetUtcNow();
        }
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
                var now = _timeProvider.GetUtcNow();

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

        SendPacket(dict, ep, DhtToken);
    }

    private void SendGetPeers(IPEndPoint ep, InfoHash infoHash, bool announce = false, int port = 0, bool scrape = false)
    {
        // Deduplicate: don't query the same node for the same info hash within the transaction timeout
        var queryKey = $"{ep}:{Convert.ToHexString(infoHash.Span)}";
        var now = _timeProvider.GetUtcNow();
        if (_recentGetPeersQueries.TryGetValue(queryKey, out var lastQueried) &&
            (now - lastQueried).TotalMinutes < ProtocolConstants.DhtTransactionTimeoutMinutes)
        {
            return;
        }

        // Don't create new transactions if we're at capacity
        if (_transactions.Count >= MaxTransactions)
        {
            return;
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

        dict.Dict["a"] = a;

        RegisterTransaction(tid, "get_peers", infoHash, announce, port, scrape);
        SendPacket(dict, ep, DhtToken);
    }

    private void SendPacket(BDict dict, IPEndPoint ep, CancellationToken ct)
    {
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
    private sealed class DhtPeer
    {
        public required IPEndPoint EndPoint { get; init; }
        public DateTimeOffset LastSeen { get; set; }
    }

    // Transaction tracking
    private sealed class Transaction
    {
        public bool Announce { get; init; }
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
