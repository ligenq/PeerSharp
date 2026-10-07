using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// Two <see cref="DhtManager"/> instances wired to each other over an in-memory transport, so a
/// query really is encoded, sent, parsed, answered and decoded.
///
/// This is what makes the BEP 44 and BEP 46 tests worth having: unit-testing the store and codec
/// proves the rules are right, but only a round trip proves they are reachable over the protocol.
/// A mislabelled argument key or a token computed over the wrong bytes passes every unit test and
/// fails against every real node.
/// </summary>
internal sealed class DhtLoopbackFixture : IAsyncDisposable
{
    /// <summary>
    /// Delivers datagrams straight into the peer's receiver, synchronously. Real UDP is lossy and
    /// reordered, which is a separate concern from whether the encoding is right.
    /// </summary>
    public sealed class LoopbackTransport : IUdpListener
    {
        private IUdpReceiver? _receiver;
        private IPEndPoint? _replySource;
        public HashSet<IPEndPoint> Aliases { get; } = [];
        public Dictionary<IPEndPoint, LoopbackTransport> Routes { get; } = [];

        public required IPEndPoint LocalEndPoint { get; init; }

        public LoopbackTransport? Peer { get; set; }

        public int Port => LocalEndPoint.Port;

        public int DroppedPackets { get; private set; }

        /// <summary>When set, packets are counted and discarded instead of delivered.</summary>
        public bool Blackhole { get; set; }

        /// <summary>Observes each outgoing datagram, for tests that assert on what went on the wire.</summary>
        public Action<ReadOnlyMemory<byte>>? OnSend { get; set; }

        public void RegisterReceiver(IUdpReceiver receiver) => _receiver = receiver;

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            OnSend?.Invoke(data);

            if (Blackhole)
            {
                DroppedPackets++;
                return Task.CompletedTask;
            }

            var destination = endpoint.Equals(LocalEndPoint) ? this
                : Routes.GetValueOrDefault(endpoint) ?? Peer;
            if (destination?._receiver is null ||
                (!endpoint.Equals(destination.LocalEndPoint) && !destination.Aliases.Contains(endpoint)))
            {
                DroppedPackets++;
                return Task.CompletedTask;
            }
            var previousSource = destination._replySource;
            destination._replySource = endpoint;
            try { destination._receiver.Receive(data.ToArray(), _replySource ?? LocalEndPoint); }
            finally { destination._replySource = previousSource; }
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;


        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public required DhtManager Client { get; init; }

    public required DhtManager Server { get; init; }

    public required LoopbackTransport ClientTransport { get; init; }

    public required LoopbackTransport ServerTransport { get; init; }

    public required IPEndPoint ServerEndPoint { get; init; }
    private readonly List<DhtManager> _additionalServers = [];

    public async Task AddServersAsync(int count)
    {
        // Distinct managers have independent storage and per-source query budgets, just as
        // replication targets in the real DHT do.
        for (int index = 0; index < count; index++)
        {
            var endpoint = new IPEndPoint(IPAddress.Parse($"192.0.2.{index + 10}"), 7000 + index);
            var transport = new LoopbackTransport { LocalEndPoint = endpoint, Peer = ClientTransport };
            ClientTransport.Routes.Add(endpoint, transport);
            var settings = new Settings();
            settings.Dht.BootstrapNodes = [];
            var manager = new DhtManager(InfoHash.CreateRandom(), transport, settings, TimeProvider.System);
            _additionalServers.Add(manager);
            await manager.StartAsync();
            Client.Ping(endpoint);
        }
        // Independent nodes confirm our external address during the first pings. That changes
        // the client's node ID and resets its routing table, so refresh the earlier contacts.
        foreach (var endpoint in ClientTransport.Routes.Keys) Client.Ping(endpoint);
    }

    /// <summary>
    /// Builds a client and a server, starts both, and seeds the client's routing table with the
    /// server so a lookup has somewhere to begin.
    /// </summary>
    /// <param name="configureSettings">
    /// Adjusts the settings both managers share, for tests that need a non-default DHT
    /// configuration.
    /// </param>
    public static async Task<DhtLoopbackFixture> CreateAsync(Action<Settings>? configureSettings = null)
    {
        var clientEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 6881);
        var serverEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.2"), 6882);

        var clientTransport = new LoopbackTransport { LocalEndPoint = clientEndPoint };
        var serverTransport = new LoopbackTransport { LocalEndPoint = serverEndPoint };
        clientTransport.Peer = serverTransport;
        serverTransport.Peer = clientTransport;

        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        configureSettings?.Invoke(settings);
        var serverId = InfoHash.CreateRandom();

        // TimeProvider.System rather than a fake: the client awaits real replies against a real
        // timeout, and a frozen clock would make that timeout unreachable.
        var client = new DhtManager(InfoHash.CreateRandom(), clientTransport, settings, TimeProvider.System);
        var server = new DhtManager(serverId, serverTransport, settings, TimeProvider.System);

        // Both must be started: Receive drops everything until then.
        await client.StartAsync();
        await server.StartAsync();

        // Seed the client's routing table the way it happens in reality: ping the server and learn
        // about it from the reply. The transport delivers synchronously, so the node is present by
        // the time Ping returns.
        client.Ping(serverEndPoint);

        return new DhtLoopbackFixture
        {
            Client = client,
            Server = server,
            ClientTransport = clientTransport,
            ServerTransport = serverTransport,
            ServerEndPoint = serverEndPoint,
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _additionalServers) await manager.DisposeAsync();
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}
