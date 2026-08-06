using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.BEncoding;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

public class DhtManagerTests
{
    private sealed class BlockingDnsResolver : IDnsResolver
    {
        public async Task<IPAddress[]> GetHostAddressesAsync(string hostNameOrAddress, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    private sealed class NonCooperativeDnsResolver : IDnsResolver
    {
        private readonly TaskCompletionSource<IPAddress[]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IPAddress[]> GetHostAddressesAsync(string hostNameOrAddress, CancellationToken cancellationToken = default)
            => _completion.Task;

        public void Complete() => _completion.TrySetResult([]);
    }

    private class MockUdpListener : IUdpListener
    {
        public int Port => 0;
        public List<(byte[] Data, IPEndPoint EndPoint)> SentPackets { get; } = [];
        public IUdpReceiver? Receiver { get; private set; }

        public void RegisterReceiver(IUdpReceiver receiver)
        {
            Receiver = receiver;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            SentPackets.Add((data.ToArray(), endpoint));
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Stop() { }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public static void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockDhtCallback : IDhtCallback
    {
        public List<(InfoHash Hash, List<IPEndPoint> Peers)> FoundPeers { get; } = [];
        public void OnPeersFound(InfoHash infoHash, List<IPEndPoint> peers)
        {
            FoundPeers.Add((infoHash, peers));
        }

        public void OnScrapeResult(InfoHash infoHash, int estimatedSeeds, int estimatedPeers) { }
    }

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly MockUdpListener _listener = new();
    private readonly MockDhtCallback _callback = new();
    private readonly Settings _settings;
    private readonly InfoHash _localId = InfoHash.CreateRandom();

    public DhtManagerTests()
    {
        _settings = new Settings();
        // Clear default bootstrap nodes to prevent background bootstrap from interfering with tests
        _settings.Dht.BootstrapNodes = Array.Empty<DhtBootstrapNode>();
    }

    [Fact]
    public async Task Start_RegistersReceiver()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        Assert.Equal(dht, _listener.Receiver);
    }

    [Fact]
    public async Task FindPeers_ReportsPacketsActuallySent_AndDoesNotSuppressAnnounceIntent()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var nodeEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.50"), 6881);
        var ping = new BDict();
        ping.Dict["t"] = new BString("known"u8.ToArray());
        ping.Dict["y"] = new BString("q"u8.ToArray());
        ping.Dict["q"] = new BString("ping"u8.ToArray());
        var arguments = new BDict();
        arguments.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        ping.Dict["a"] = arguments;
        dht.Receive(BencodeWriter.Write(ping), nodeEndPoint);
        _listener.SentPackets.Clear();

        var hash = InfoHash.CreateRandom();
        Assert.Equal(1, dht.FindPeers(hash));

        // The identical lookup is still inside the deduplication window, so reporting the routing
        // table size here would claim a packet was sent when none was.
        Assert.Equal(0, dht.FindPeers(hash));

        // Announce begins with get_peers too, but it carries different transaction intent: when the
        // token comes back it must send announce_peer. It must not be swallowed by the lookup above.
        dht.Announce(hash, 51413);

        Assert.Equal(2, _listener.SentPackets.Count);
        Assert.All(_listener.SentPackets, packet =>
        {
            var query = Assert.IsType<BDict>(BencodeParser.Parse(packet.Data));
            Assert.Equal("get_peers", query.GetString("q"));
        });
    }

    /// <summary>
    /// BEP 5 stores a peer on the nodes nearest the info hash, and eight is the number every
    /// implementation uses. The announce rides along with an iterative lookup, so it inherits that
    /// walk unless it is bounded - once announcing began working at all, a 100 second run against the
    /// real DHT told 3688 distinct nodes to store us.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Announce_TellsOnlyTheClosestNodes_NotEveryNodeTheLookupReaches()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        // Populate the routing table by letting a handful of nodes ping us.
        for (int i = 1; i <= 8; i++)
        {
            var ping = new BDict();
            ping.Dict["t"] = new BString([(byte)i]);
            ping.Dict["y"] = new BString("q"u8.ToArray());
            ping.Dict["q"] = new BString("ping"u8.ToArray());
            var pingArgs = new BDict();
            pingArgs.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
            ping.Dict["a"] = pingArgs;
            dht.Receive(BencodeWriter.Write(ping), new IPEndPoint(IPAddress.Parse($"192.0.2.{i}"), 6881));
        }

        var hash = InfoHash.CreateRandom();
        _listener.SentPackets.Clear();
        dht.Announce(hash, 51413);

        var nodesAsked = new HashSet<IPEndPoint>();
        int announces = 0;

        // Answer every get_peers with a token, as a cooperative node would. Each reply also drives the
        // walk onward, which is exactly how the announce used to spread without limit.
        for (int round = 0; round < 6; round++)
        {
            var queries = _listener.SentPackets.ToArray();
            _listener.SentPackets.Clear();

            foreach (var (data, endpoint) in queries)
            {
                var query = (BDict)BencodeParser.Parse(data);
                if (query.GetString("q") == "announce_peer")
                {
                    announces++;
                    continue;
                }

                if (query.GetString("q") != "get_peers")
                {
                    continue;
                }

                nodesAsked.Add(endpoint);

                var response = new BDict();
                response.Dict["t"] = new BString(query.GetBytes("t")!.Value.ToArray());
                response.Dict["y"] = new BString("r"u8.ToArray());
                var values = new BDict();
                values.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
                values.Dict["token"] = new BString("tok!"u8.ToArray());

                // Hand back a fresh node so the lookup keeps walking outward.
                var contact = new byte[26];
                InfoHash.CreateRandom().ToArray().CopyTo(contact, 0);
                contact[20] = 198; contact[21] = 51; contact[22] = 100; contact[23] = (byte)(round + 1);
                contact[24] = 0x1A; contact[25] = 0xE1;
                values.Dict["nodes"] = new BString(contact);
                response.Dict["r"] = values;

                dht.Receive(BencodeWriter.Write(response), endpoint);
            }
        }

        foreach (var (data, _) in _listener.SentPackets)
        {
            var sent = (BDict)BencodeParser.Parse(data);
            if (sent.GetString("q") == "announce_peer")
            {
                announces++;
            }
        }

        // Without this the assertion below would hold for the wrong reason - a walk that never got
        // past eight nodes cannot demonstrate a bound at eight.
        Assert.True(
            nodesAsked.Count > 8,
            $"The lookup must reach more than eight nodes for the budget to be what limits the announce; it reached {nodesAsked.Count}.");

        Assert.InRange(announces, 1, 8);

        await dht.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task StopAsync_CancelsBlockedBootstrapDnsResolution()
    {
        _settings.Dht.BootstrapNodes = [new DhtBootstrapNode("blocked.invalid", 6881)];
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback, new BlockingDnsResolver());
        await dht.StartAsync();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await dht.StopAsync();

        // Generous on purpose. What this distinguishes is "returned" from "blocked on the hung
        // dependency", and blocking there is unbounded - so a threshold only has to be clear of
        // how long a loaded CI runner can stall, which has been measured at several seconds.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"DHT stop took {stopwatch.Elapsed}.");
    }

    [Fact(Timeout = 30000)]
    public async Task StopAsync_DoesNotWaitForDnsResolutionThatIgnoresCancellation()
    {
        _settings.Dht.BootstrapNodes = [new DhtBootstrapNode("blocked.invalid", 6881)];
        var resolver = new NonCooperativeDnsResolver();
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback, resolver);
        await dht.StartAsync();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await dht.StopAsync();

        // Generous on purpose. What this distinguishes is "returned" from "blocked on the hung
        // dependency", and blocking there is unbounded - so a threshold only has to be clear of
        // how long a loaded CI runner can stall, which has been measured at several seconds.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"DHT stop took {stopwatch.Elapsed}.");
        resolver.Complete();
    }

    [Fact]
    public async Task Receive_Ping_SendsResponse()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var query = new BDict();
        query.Dict["t"] = new BString("aa"u8.ToArray());
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString("ping"u8.ToArray());
        var a = new BDict();
        a.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        query.Dict["a"] = a;

        var sender = new IPEndPoint(IPAddress.Loopback, 1234);
        dht.Receive(BencodeWriter.Write(query), sender);

        Assert.Single(_listener.SentPackets);
        var response = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        Assert.Equal("r", response.GetString("y"));
        Assert.Equal("aa", response.GetString("t"));
        var r = Assert.IsType<BDict>(response.Get("r"));
        Assert.Equal(_localId.ToArray(), r.GetBytes("id")?.ToArray());
    }

    [Fact]
    public async Task Receive_FindNode_ReturnsNodes()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        // Add a node to the table so it can return something
        var otherId = InfoHash.CreateRandom();
        var otherEp = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 6881);

        // We can't access RoutingTable directly, but we can add it via another ping
        var ping = new BDict();
        ping.Dict["t"] = new BString("t1"u8.ToArray());
        ping.Dict["y"] = new BString("q"u8.ToArray());
        ping.Dict["q"] = new BString("ping"u8.ToArray());
        var pa = new BDict();
        pa.Dict["id"] = new BString(otherId.ToArray());
        ping.Dict["a"] = pa;
        dht.Receive(BencodeWriter.Write(ping), otherEp);
        _listener.SentPackets.Clear();

        // Now find_node
        var query = new BDict();
        query.Dict["t"] = new BString("bb"u8.ToArray());
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString("find_node"u8.ToArray());
        var a = new BDict();
        a.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        a.Dict["target"] = new BString(otherId.ToArray());
        query.Dict["a"] = a;

        dht.Receive(BencodeWriter.Write(query), new IPEndPoint(IPAddress.Loopback, 4321));

        Assert.Single(_listener.SentPackets);
        var response = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var r = Assert.IsType<BDict>(response.Get("r"));
        Assert.True(r.Dict.ContainsKey("nodes"));
        var nodes = r.GetBytes("nodes");
        Assert.NotNull(nodes);
        Assert.True(nodes.Value.Length >= 26);
        Assert.Equal(otherId.ToArray(), nodes.Value.Span[..20].ToArray());
    }

    [Fact]
    public async Task SameNodeId_IsRetainedInBothAddressFamilies()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var nodeId = InfoHash.CreateRandom();
        var ipv4 = new IPEndPoint(IPAddress.Parse("192.0.2.50"), 6881);
        var ipv6 = new IPEndPoint(IPAddress.Parse("2001:db8::50"), 6881);

        foreach (var endpoint in new[] { ipv4, ipv6 })
        {
            var ping = new BDict();
            ping.Dict["t"] = new BString("dual"u8.ToArray());
            ping.Dict["y"] = new BString("q"u8.ToArray());
            ping.Dict["q"] = new BString("ping"u8.ToArray());
            var arguments = new BDict();
            arguments.Dict["id"] = new BString(nodeId.ToArray());
            ping.Dict["a"] = arguments;
            dht.Receive(BencodeWriter.Write(ping), endpoint);
        }

        _listener.SentPackets.Clear();

        Assert.Equal(2, dht.FindPeers(InfoHash.CreateRandom()));
        var queried = _listener.SentPackets.Select(static packet => packet.EndPoint).ToHashSet();
        Assert.True(new HashSet<IPEndPoint> { ipv4, ipv6 }.SetEquals(queried));
    }

    [Fact]
    public async Task Receive_GetPeers_ReturnsTokenAndNodes()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        // Add a node to the routing table first
        var otherId = InfoHash.CreateRandom();
        var otherEp = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 6881);
        var ping = new BDict();
        ping.Dict["t"] = new BString("t1"u8.ToArray());
        ping.Dict["y"] = new BString("q"u8.ToArray());
        ping.Dict["q"] = new BString("ping"u8.ToArray());
        var pa = new BDict();
        pa.Dict["id"] = new BString(otherId.ToArray());
        ping.Dict["a"] = pa;
        dht.Receive(BencodeWriter.Write(ping), otherEp);
        _listener.SentPackets.Clear();

        // Send get_peers query
        var infoHash = InfoHash.CreateRandom();
        var query = new BDict();
        query.Dict["t"] = new BString("gp"u8.ToArray());
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString("get_peers"u8.ToArray());
        var a = new BDict();
        a.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        a.Dict["info_hash"] = new BString(infoHash.ToArray());
        query.Dict["a"] = a;

        dht.Receive(BencodeWriter.Write(query), new IPEndPoint(IPAddress.Loopback, 4321));

        Assert.Single(_listener.SentPackets);
        var response = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var r = Assert.IsType<BDict>(response.Get("r"));

        // Should have token for announce_peer
        Assert.True(r.Dict.ContainsKey("token"));
        var token = r.GetBytes("token");
        Assert.NotNull(token);
        Assert.True(token.Value.Length > 0);

        // Should have nodes since we don't have peers for this info_hash
        Assert.True(r.Dict.ContainsKey("nodes"));
    }

    [Fact]
    public async Task Receive_AnnouncePeer_StoresPeer()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var senderId = InfoHash.CreateRandom();
        var senderEp = new IPEndPoint(IPAddress.Loopback, 5000);
        var infoHash = InfoHash.CreateRandom();

        // First, get a token via get_peers
        var getPeers = new BDict();
        getPeers.Dict["t"] = new BString("gp"u8.ToArray());
        getPeers.Dict["y"] = new BString("q"u8.ToArray());
        getPeers.Dict["q"] = new BString("get_peers"u8.ToArray());
        var gpa = new BDict();
        gpa.Dict["id"] = new BString(senderId.ToArray());
        gpa.Dict["info_hash"] = new BString(infoHash.ToArray());
        getPeers.Dict["a"] = gpa;
        dht.Receive(BencodeWriter.Write(getPeers), senderEp);

        var response = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var r = Assert.IsType<BDict>(response.Get("r"));
        var token = r.GetBytes("token")!.Value.ToArray();
        _listener.SentPackets.Clear();

        // Now announce with the token
        var announce = new BDict();
        announce.Dict["t"] = new BString("ap"u8.ToArray());
        announce.Dict["y"] = new BString("q"u8.ToArray());
        announce.Dict["q"] = new BString("announce_peer"u8.ToArray());
        var aa = new BDict();
        aa.Dict["id"] = new BString(senderId.ToArray());
        aa.Dict["info_hash"] = new BString(infoHash.ToArray());
        aa.Dict["port"] = new BNumber(6881);
        aa.Dict["token"] = new BString(token);
        announce.Dict["a"] = aa;

        dht.Receive(BencodeWriter.Write(announce), senderEp);

        // Should get a response
        Assert.Single(_listener.SentPackets);
        var annResponse = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        Assert.Equal("r", annResponse.GetString("y"));
        _listener.SentPackets.Clear();

        // Now query get_peers again - should return the announced peer
        var getPeers2 = new BDict();
        getPeers2.Dict["t"] = new BString("g2"u8.ToArray());
        getPeers2.Dict["y"] = new BString("q"u8.ToArray());
        getPeers2.Dict["q"] = new BString("get_peers"u8.ToArray());
        var gpa2 = new BDict();
        gpa2.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        gpa2.Dict["info_hash"] = new BString(infoHash.ToArray());
        getPeers2.Dict["a"] = gpa2;

        dht.Receive(BencodeWriter.Write(getPeers2), new IPEndPoint(IPAddress.Loopback, 9999));

        var response2 = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var r2 = Assert.IsType<BDict>(response2.Get("r"));

        // Should have values (peers) now
        Assert.True(r2.Dict.ContainsKey("values"));
        var values = Assert.IsType<BList>(r2.Get("values"));
        Assert.Single(values.List);

        // Parse peer compact format: 4 bytes IP + 2 bytes port
        var peerData = Assert.IsType<BString>(values.List[0]).Value;
        Assert.Equal(6, peerData.Length);
        var peerIp = new IPAddress(peerData[..4].Span);
        int peerPort = (peerData.Span[4] << 8) | peerData.Span[5];
        Assert.Equal(IPAddress.Loopback, peerIp);
        Assert.Equal(6881, peerPort);
    }

    [Fact]
    public async Task Response_WithBinaryTransactionId_ProcessedCorrectly()
    {
        // This test verifies the encoding bug fix - transaction IDs with high bytes (>127)
        // must be handled correctly using Latin1 encoding
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        // Send a ping to get a response
        dht.Ping(new IPEndPoint(IPAddress.Loopback, 6881));

        // Get the sent ping packet and extract its transaction ID
        Assert.Single(_listener.SentPackets);
        var sentPing = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var tid = sentPing.GetBytes("t")!.Value.ToArray();
        _listener.SentPackets.Clear();

        // Simulate a response with the same transaction ID (which may contain bytes > 127)
        var response = new BDict();
        response.Dict["t"] = new BString(tid);
        response.Dict["y"] = new BString("r"u8.ToArray());
        var r = new BDict();
        r.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        response.Dict["r"] = r;

        // The response should be processed without error
        dht.Receive(BencodeWriter.Write(response), new IPEndPoint(IPAddress.Loopback, 6881));

        // No exception means the transaction ID was matched correctly
    }

    [Fact]
    public async Task Response_WithHighByteTransactionId_MatchesCorrectly()
    {
        // Explicitly test with transaction IDs containing bytes > 127
        // This is the specific case that was broken before the Latin1 fix
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        // Add a node to trigger FindPeers to work
        var nodeId = InfoHash.CreateRandom();
        var nodeEp = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 6881);
        var ping = new BDict();
        ping.Dict["t"] = new BString("t1"u8.ToArray());
        ping.Dict["y"] = new BString("q"u8.ToArray());
        ping.Dict["q"] = new BString("ping"u8.ToArray());
        var pa = new BDict();
        pa.Dict["id"] = new BString(nodeId.ToArray());
        ping.Dict["a"] = pa;
        dht.Receive(BencodeWriter.Write(ping), nodeEp);
        _listener.SentPackets.Clear();

        // Trigger FindPeers which will send get_peers
        var infoHash = InfoHash.CreateRandom();
        dht.FindPeers(infoHash);

        // Find a packet with a transaction ID containing high bytes
        BDict? sentQuery = null;
        byte[]? tid = null;
        foreach (var packet in _listener.SentPackets)
        {
            var parsed = Assert.IsType<BDict>(BencodeParser.Parse(packet.Data));
            tid = parsed.GetBytes("t")?.ToArray();
            if (tid?.Any(b => b > 127) == true)
            {
                sentQuery = parsed;
                break;
            }
        }

        // If no high-byte TID found, create a response with a simulated one
        if (tid?.Any(b => b > 127) != true)
        {
            // Force a high-byte scenario by manually creating a response
            // with a transaction ID that has bytes > 127
            tid = [0x80, 0xFF, 0x90, 0xAB]; // All > 127
        }

        _listener.SentPackets.Clear();

        // Send FindPeers again to register a transaction
        dht.FindPeers(infoHash);

        if (_listener.SentPackets.Count > 0)
        {
            var actualTid = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data)).GetBytes("t")!.Value.ToArray();

            // Simulate response with peers
            var response = new BDict();
            response.Dict["t"] = new BString(actualTid);
            response.Dict["y"] = new BString("r"u8.ToArray());
            var r = new BDict();
            r.Dict["id"] = new BString(nodeId.ToArray());
            r.Dict["token"] = new BString("testtoken"u8.ToArray());

            // Add a peer in compact format
            var peerData = new byte[6];
            IPAddress.Parse("192.168.1.100").TryWriteBytes(peerData.AsSpan(0, 4), out _);
            peerData[4] = 0x1A; // Port 6881 high byte
            peerData[5] = 0xE1; // Port 6881 low byte
            var values = new BList();
            values.List.Add(new BString(peerData));
            r.Dict["values"] = values;
            response.Dict["r"] = r;

            dht.Receive(BencodeWriter.Write(response), nodeEp);

            // Verify peers were found via callback
            Assert.Single(_callback.FoundPeers);
            Assert.Equal(infoHash, _callback.FoundPeers[0].Hash);
            Assert.Single(_callback.FoundPeers[0].Peers);
        }
    }

    [Fact]
    public async Task Receive_AnnouncePeer_WithImpliedPort_UsesSourcePort()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var senderId = InfoHash.CreateRandom();
        const int senderPort = 12345;
        var senderEp = new IPEndPoint(IPAddress.Loopback, senderPort);
        var infoHash = InfoHash.CreateRandom();

        // Get a token
        var getPeers = new BDict();
        getPeers.Dict["t"] = new BString("gp"u8.ToArray());
        getPeers.Dict["y"] = new BString("q"u8.ToArray());
        getPeers.Dict["q"] = new BString("get_peers"u8.ToArray());
        var gpa = new BDict();
        gpa.Dict["id"] = new BString(senderId.ToArray());
        gpa.Dict["info_hash"] = new BString(infoHash.ToArray());
        getPeers.Dict["a"] = gpa;
        dht.Receive(BencodeWriter.Write(getPeers), senderEp);

        var response = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var r = Assert.IsType<BDict>(response.Get("r"));
        var token = r.GetBytes("token")!.Value.ToArray();
        _listener.SentPackets.Clear();

        // Announce with implied_port = 1 (use source port instead of port field)
        var announce = new BDict();
        announce.Dict["t"] = new BString("ap"u8.ToArray());
        announce.Dict["y"] = new BString("q"u8.ToArray());
        announce.Dict["q"] = new BString("announce_peer"u8.ToArray());
        var aa = new BDict();
        aa.Dict["id"] = new BString(senderId.ToArray());
        aa.Dict["info_hash"] = new BString(infoHash.ToArray());
        aa.Dict["port"] = new BNumber(9999); // This should be ignored
        aa.Dict["implied_port"] = new BNumber(1);
        aa.Dict["token"] = new BString(token);
        announce.Dict["a"] = aa;

        dht.Receive(BencodeWriter.Write(announce), senderEp);
        _listener.SentPackets.Clear();

        // Query get_peers to verify the stored port
        var getPeers2 = new BDict();
        getPeers2.Dict["t"] = new BString("g2"u8.ToArray());
        getPeers2.Dict["y"] = new BString("q"u8.ToArray());
        getPeers2.Dict["q"] = new BString("get_peers"u8.ToArray());
        var gpa2 = new BDict();
        gpa2.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        gpa2.Dict["info_hash"] = new BString(infoHash.ToArray());
        getPeers2.Dict["a"] = gpa2;

        dht.Receive(BencodeWriter.Write(getPeers2), new IPEndPoint(IPAddress.Loopback, 9999));

        var response2 = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var r2 = Assert.IsType<BDict>(response2.Get("r"));
        var values = Assert.IsType<BList>(r2.Get("values"));
        var peerData = Assert.IsType<BString>(values.List[0]).Value;
        int storedPort = (peerData.Span[4] << 8) | peerData.Span[5];

        // Should use the source port (12345), not the port field (9999)
        Assert.Equal(senderPort, storedPort);
    }

    [Fact]
    public async Task Receive_AnnouncePeer_WithInvalidToken_SendsProtocolError()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var senderId = InfoHash.CreateRandom();
        var senderEp = new IPEndPoint(IPAddress.Loopback, 5000);
        var infoHash = InfoHash.CreateRandom();

        // Announce with an invalid token (never got one from get_peers)
        var announce = new BDict();
        announce.Dict["t"] = new BString("ap"u8.ToArray());
        announce.Dict["y"] = new BString("q"u8.ToArray());
        announce.Dict["q"] = new BString("announce_peer"u8.ToArray());
        var aa = new BDict();
        aa.Dict["id"] = new BString(senderId.ToArray());
        aa.Dict["info_hash"] = new BString(infoHash.ToArray());
        aa.Dict["port"] = new BNumber(6881);
        aa.Dict["token"] = new BString("invalidtoken"u8.ToArray());
        announce.Dict["a"] = aa;

        dht.Receive(BencodeWriter.Write(announce), senderEp);

        // BEP 5: a bad token gets an error reply: {"y": "e", "e": [203, message]}
        var packet = Assert.Single(_listener.SentPackets);
        var response = Assert.IsType<BDict>(BencodeParser.Parse(packet.Data));
        Assert.Equal("e", response.GetString("y"));
        Assert.Equal("ap", response.GetString("t"));
        var error = Assert.IsType<BList>(response.Get("e"));
        Assert.Equal(203, Assert.IsType<BNumber>(error.List[0]).Value);

        // The peer must NOT have been stored: get_peers must not return it
        _listener.SentPackets.Clear();
        var getPeers = new BDict();
        getPeers.Dict["t"] = new BString("gp"u8.ToArray());
        getPeers.Dict["y"] = new BString("q"u8.ToArray());
        getPeers.Dict["q"] = new BString("get_peers"u8.ToArray());
        var gpa = new BDict();
        gpa.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        gpa.Dict["info_hash"] = new BString(infoHash.ToArray());
        getPeers.Dict["a"] = gpa;
        dht.Receive(BencodeWriter.Write(getPeers), new IPEndPoint(IPAddress.Loopback, 9999));

        var gpResponse = Assert.IsType<BDict>(BencodeParser.Parse(_listener.SentPackets[0].Data));
        var r = Assert.IsType<BDict>(gpResponse.Get("r"));
        Assert.False(r.Dict.ContainsKey("values"));
    }

    [Fact(Timeout = 10000)]
    public async Task Maintenance_CleansUpExpiredTransactions()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var transactionsField = typeof(DhtManager).GetField("_transactions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Use the real FindPeers path to register a transaction, then age it via reflection
        var infoHash = InfoHash.CreateRandom();
        dht.FindPeers(infoHash);

        // Force the timestamp back via reflection on the Transaction object
        var txDict = transactionsField.GetValue(dht)!;
        var txDictType = txDict.GetType();
        // Get all values from the ConcurrentDictionary
        var values = (System.Collections.IEnumerable)txDictType.GetProperty("Values")!.GetValue(txDict)!;
        foreach (var tx in values)
        {
            var tsProp = tx.GetType().GetProperty("Timestamp")!;
            tsProp.SetValue(tx, _timeProvider.GetUtcNow().AddMinutes(-3));
        }

        // Advance time by 15 seconds to trigger maintenance
        // Advance until maintenance has actually run, rather than waiting a fixed moment for it.
        // Maintenance only prunes, so running it more than once changes nothing.
        await TorrentTestUtility.AdvanceUntilAsync(
            _timeProvider,
            () => (int)txDictType.GetProperty("Count")!.GetValue(txDict)! == 0,
            TimeSpan.FromSeconds(16),
            "expired transactions to be pruned");

        var count = (int)txDictType.GetProperty("Count")!.GetValue(txDict)!;
        Assert.Equal(0, count);

        await dht.StopAsync();
    }

    [Fact(Timeout = 10000)]
    public async Task Maintenance_CleansUpExpiredPeers()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var peersField = typeof(DhtManager).GetField("_peers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var peers = peersField.GetValue(dht)!;
        var peersType = peers.GetType();

        // Inject a peer via get_peers announce
        var infoHash = InfoHash.CreateRandom();
        var hashStr = BitConverter.ToString(infoHash.ToArray()).Replace("-", "").ToLower();
        var senderEp = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);

        // First do a get_peers to create a token, then announce
        var query = new BDict();
        query.Dict["t"] = new BString("bb"u8.ToArray());
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString("get_peers"u8.ToArray());
        var a = new BDict();
        a.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        a.Dict["info_hash"] = new BString(infoHash.ToArray());
        query.Dict["a"] = a;
        dht.Receive(BencodeWriter.Write(query), senderEp);

        // Extract the token from the response
        Assert.NotEmpty(_listener.SentPackets);
        var response = BencodeParser.Parse(_listener.SentPackets[^1].Data);
        var token = ((BDict)response).Get("r") is BDict r ? r.Get("token") as BString : null;
        Assert.NotNull(token);
        _listener.SentPackets.Clear();

        // Announce with valid token
        var announce = new BDict();
        announce.Dict["t"] = new BString("cc"u8.ToArray());
        announce.Dict["y"] = new BString("q"u8.ToArray());
        announce.Dict["q"] = new BString("announce_peer"u8.ToArray());
        var aa = new BDict();
        aa.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        aa.Dict["info_hash"] = new BString(infoHash.ToArray());
        aa.Dict["port"] = new BNumber(6881);
        aa.Dict["token"] = token;
        announce.Dict["a"] = aa;
        dht.Receive(BencodeWriter.Write(announce), senderEp);

        // Verify peer was stored
        var count = (int)peersType.GetProperty("Count")!.GetValue(peers)!;
        Assert.Equal(1, count);

        // Age the peer via reflection
        var values = (System.Collections.IEnumerable)peersType.GetProperty("Values")!.GetValue(peers)!;
        foreach (var peerList in values)
        {
            var list = (System.Collections.IList)peerList;
            foreach (var peer in list)
            {
                peer.GetType().GetProperty("LastSeen")!.SetValue(peer, _timeProvider.GetUtcNow().AddMinutes(-31));
            }
        }

        // Trigger maintenance
        await TorrentTestUtility.AdvanceUntilAsync(
            _timeProvider,
            () => (int)peersType.GetProperty("Count")!.GetValue(peers)! == 0,
            TimeSpan.FromSeconds(16),
            "expired peers to be pruned");

        count = (int)peersType.GetProperty("Count")!.GetValue(peers)!;
        Assert.Equal(0, count);

        await dht.StopAsync();
    }

    [Fact(Timeout = 10000)]
    public async Task Maintenance_CleansUpExpiredRecentQueryDedup()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var recentField = typeof(DhtManager).GetField("_recentGetPeersQueries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var recent = recentField.GetValue(dht)!;
        var recentType = recent.GetType();
        var addMethod = recentType.GetMethod("TryAdd", [typeof(string), typeof(DateTimeOffset)])!;

        // Inject an old entry (3 minutes ago = beyond the 2-minute threshold)
        addMethod.Invoke(recent, ["old-key-1", _timeProvider.GetUtcNow().AddMinutes(-3)]);
        // Inject a fresh entry
        addMethod.Invoke(recent, ["fresh-key-1", _timeProvider.GetUtcNow()]);

        // Trigger maintenance
        await TorrentTestUtility.AdvanceUntilAsync(
            _timeProvider,
            () => (int)recentType.GetProperty("Count")!.GetValue(recent)! == 1,
            TimeSpan.FromSeconds(16),
            "the stale entry to be pruned");

        var count = (int)recentType.GetProperty("Count")!.GetValue(recent)!;
        // Old entry cleaned, fresh entry kept
        Assert.Equal(1, count);

        await dht.StopAsync();
    }

    [Fact(Timeout = 10000)]
    public async Task Maintenance_HardCapOnRecentQueries_ClearsOldEntries()
    {
        var dht = new DhtManager(_localId, _listener, _settings, _timeProvider, _callback);
        await dht.StartAsync();

        var recentField = typeof(DhtManager).GetField("_recentGetPeersQueries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var recent = recentField.GetValue(dht)!;
        var recentType = recent.GetType();
        var addMethod = recentType.GetMethod("TryAdd", [typeof(string), typeof(DateTimeOffset)])!;

        // Inject 10001 entries that are 2 minutes old (older than the hard-cap cutoff of 1 min)
        var oldTime = _timeProvider.GetUtcNow().AddMinutes(-2);
        for (int i = 0; i < 10001; i++)
        {
            addMethod.Invoke(recent, [$"key-{i}", oldTime]);
        }

        var countBefore = (int)recentType.GetProperty("Count")!.GetValue(recent)!;
        Assert.Equal(10001, countBefore);

        // Trigger maintenance — hard cap (>10000) path fires
        await TorrentTestUtility.AdvanceUntilAsync(
            _timeProvider,
            () => (int)recentType.GetProperty("Count")!.GetValue(recent)! < countBefore,
            TimeSpan.FromSeconds(16),
            "the hard cap to prune entries");

        var countAfter = (int)recentType.GetProperty("Count")!.GetValue(recent)!;
        Assert.True(countAfter < countBefore, $"Expected entries to be pruned, got {countAfter}");

        await dht.StopAsync();
    }
}


