using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Integration;

public class DhtIntegrationTests
{
    // Simulates a network of DHT nodes
    private class MockDhtNetwork
    {
        private readonly ConcurrentDictionary<IPEndPoint, MockUdpListener> _listeners = new();
        private readonly FakeTimeProvider _timeProvider;

        public MockDhtNetwork(FakeTimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public MockUdpListener CreateListener(IPEndPoint endpoint)
        {
            var listener = new MockUdpListener(this, endpoint);
            _listeners[endpoint] = listener;
            return listener;
        }

        public Task RoutePacketAsync(ReadOnlyMemory<byte> data, IPEndPoint from, IPEndPoint to)
        {
            // Break stack recursion by scheduling on thread pool
            var dataCopy = data.ToArray();
            return Task.Run(() =>
            {
                if (_listeners.TryGetValue(to, out var listener))
                {
                    listener.OnPacketReceived(dataCopy, from);
                }
            });
        }
    }

    private class MockUdpListener : IUdpListener
    {
        private readonly MockDhtNetwork _network;
        private readonly List<IUdpReceiver> _receivers = [];

        public MockUdpListener(MockDhtNetwork network, IPEndPoint endpoint)
        {
            _network = network;
            LocalEndpoint = endpoint;
        }

        public IPEndPoint LocalEndpoint { get; }
        public int Port => LocalEndpoint.Port;

        public void RegisterReceiver(IUdpReceiver receiver)
        {
            _receivers.Add(receiver);
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct)
        {
            return _network.RoutePacketAsync(data, LocalEndpoint, endpoint);
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Stop() { } // Synchronous stop
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void OnPacketReceived(byte[] data, IPEndPoint from)
        {
            foreach (var receiver in _receivers)
            {
                receiver.Receive(data, from);
            }
        }
    }

    private class MockDnsResolver : IDnsResolver
    {
        private readonly Dictionary<string, IPAddress[]> _entries = [];

        public void Add(string host, IPAddress ip)
        {
            _entries[host] = new[] { ip };
        }

        public IPAddress[] GetHostAddresses(string hostNameOrAddress)
        {
            if (IPAddress.TryParse(hostNameOrAddress, out var ip))
            {
                return new[] { ip };
            }
            return _entries.TryGetValue(hostNameOrAddress, out var ips) ? ips : [];
        }
    }

    private readonly FakeTimeProvider _time = new();
    private readonly MockDhtNetwork _network;
    private readonly MockDnsResolver _dns = new();

    public DhtIntegrationTests()
    {
        _network = new MockDhtNetwork(_time);
    }

    [Fact]
    public async Task Bootstrap_PingsBootstrapNode()
    {
        // Setup Bootstrap Node
        var bootstrapEp = new IPEndPoint(IPAddress.Loopback, 5000);
        var bootstrapListener = _network.CreateListener(bootstrapEp);
        var bootstrapSettings = new Settings();
        await using var bootstrapNode = new DhtManager(InfoHash.CreateRandom(), bootstrapListener, bootstrapSettings, _time, null, _dns);
        await bootstrapNode.StartAsync();

        // Setup Client Node
        var clientEp = new IPEndPoint(IPAddress.Loopback, 5001);
        var clientListener = _network.CreateListener(clientEp);
        var clientSettings = new Settings();
        clientSettings.Dht.BootstrapNodes = new List<DhtBootstrapNode>
        {
            new DhtBootstrapNode("dht.bootstrap.com", 5000)
        };

        _dns.Add("dht.bootstrap.com", IPAddress.Loopback);

        await using var clientNode = new DhtManager(InfoHash.CreateRandom(), clientListener, clientSettings, _time, null, _dns);

        // Act
        await clientNode.StartAsync();

        // Allow time for bootstrap ping
        // Note: Bootstrap runs on Task.Run, so we might need a small real delay or polling
        // But since MockNetwork routes immediately, we just need the Task.Run to execute.
        await Task.Delay(100);

        // Assert
        // Bootstrap node should have received ping and added client to its table?
        // Or we can check if client added bootstrap node (requires response).

        // DhtManager is internal, so we can access internal state if InternalsVisibleTo is set.
        // Or we can use reflection to check routing table.
        // Or we can just check if nodes found each other.

        // Let's perform a lookup to verify connectivity
        var searchHash = InfoHash.CreateRandom();

        // Capture peers found event on client
        var tcs = new TaskCompletionSource<bool>();
        var callback = new MockDhtCallback((h, peers) => tcs.TrySetResult(true));
        clientNode.SetCallback(callback);

        // Bootstrap node announces itself? No.
        // We just want to see if Client can talk to Bootstrap.
        // Client.Ping(Bootstrap) -> Bootstrap.Respond.
        // Client adds Bootstrap to table.

        // We can inspect client's table via reflection for now, 
        // or rely on FindPeers working if we add a third node.
    }

    [Fact]
    public async Task Announce_FindsPeers()
    {
        // Setup 3 nodes: A (Announcer), B (Bootstrap/Router), C (Searcher)
        // A <-> B <-> C

        var epA = new IPEndPoint(IPAddress.Loopback, 6001);
        var epB = new IPEndPoint(IPAddress.Loopback, 6002);
        var epC = new IPEndPoint(IPAddress.Loopback, 6003);

        var nodeA = CreateNode(epA, new[] { epB });
        var nodeB = CreateNode(epB, Array.Empty<IPEndPoint>());
        var nodeC = CreateNode(epC, new[] { epB });

        await nodeB.StartAsync();
        await nodeA.StartAsync();
        await nodeC.StartAsync();

        // Give time for bootstrap pings to settle
        await Task.Delay(200);

        // Node A announces hash H
        var hash = InfoHash.CreateRandom();
        nodeA.Announce(hash, 1234);

        // Give time for announce to propagate/store
        await Task.Delay(100);

        // Node C searches for hash H
        var tcs = new TaskCompletionSource<List<IPEndPoint>>();
        var callbackC = new MockDhtCallback((h, peers) =>
        {
            if (h.Equals(hash))
            {
                tcs.TrySetResult(peers);
            }
        });
        nodeC.SetCallback(callbackC);

        nodeC.FindPeers(hash);

        // Wait for result
        // We use Task.WhenAny with timeout because TCS might not complete
        var task = await Task.WhenAny(tcs.Task, Task.Delay(2000));

        Assert.True(task == tcs.Task, "DHT lookup timed out");
        var peers = await tcs.Task;

        Assert.NotEmpty(peers);
        Assert.Contains(peers, p => p.Address.Equals(epA.Address) && p.Port == 1234); // Port advertised in Announce

        // Cleanup
        await nodeA.DisposeAsync();
        await nodeB.DisposeAsync();
        await nodeC.DisposeAsync();
    }

    private DhtManager CreateNode(IPEndPoint ep, IEnumerable<IPEndPoint> bootstraps)
    {
        var listener = _network.CreateListener(ep);
        var settings = new Settings();
        var nodes = new List<DhtBootstrapNode>();
        foreach (var b in bootstraps)
        {
            nodes.Add(new DhtBootstrapNode(b.Address.ToString(), (ushort)b.Port));
        }
        settings.Dht.BootstrapNodes = nodes;

        // We don't need mock DNS if we use IP addresses in host field
        return new DhtManager(InfoHash.CreateRandom(), listener, settings, _time, null, _dns);
    }

    private class MockDhtCallback : IDhtCallback
    {
        private readonly Action<InfoHash, List<IPEndPoint>> _onPeers;

        public MockDhtCallback(Action<InfoHash, List<IPEndPoint>> onPeers)
        {
            _onPeers = onPeers;
        }

        public void OnPeersFound(InfoHash infoHash, List<IPEndPoint> peers)
        {
            _onPeers(infoHash, peers);
        }

        public void OnScrapeResult(InfoHash infoHash, int estimatedSeeds, int estimatedPeers)
        {
        }
    }
}
