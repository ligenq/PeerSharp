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

            if (Blackhole || Peer?._receiver is null)
            {
                DroppedPackets++;
                return Task.CompletedTask;
            }

            Peer._receiver.Receive(data.ToArray(), LocalEndPoint);
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
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}
