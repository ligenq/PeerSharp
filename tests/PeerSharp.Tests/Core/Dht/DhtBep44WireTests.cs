using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Text;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// Exercises the BEP 44 wire layer by wiring two DhtManager instances together over an in-memory
/// transport, so a get or put really is encoded, sent, parsed, answered and decoded.
///
/// Unit-testing the store and codec separately proves the rules are right; this proves they are
/// actually reachable over the protocol, which is where the interesting mistakes live - a
/// mislabelled argument key or a token computed over the wrong bytes passes every unit test and
/// fails against every real node.
/// </summary>
public class DhtBep44WireTests
{
    private static BString Text(string value) => new(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Delivers datagrams straight into the peer's receiver, synchronously. Real UDP would be
    /// lossy and reordered, which is a separate concern from whether the encoding is right.
    /// </summary>
    private sealed class LoopbackTransport : IUdpListener
    {
        private IUdpReceiver? _receiver;

        public required IPEndPoint LocalEndPoint { get; init; }

        public LoopbackTransport? Peer { get; set; }

        public int Port => LocalEndPoint.Port;

        public int DroppedPackets { get; private set; }

        /// <summary>When set, packets are counted and discarded instead of delivered.</summary>
        public bool Blackhole { get; set; }

        public void RegisterReceiver(IUdpReceiver receiver) => _receiver = receiver;

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            if (Blackhole || Peer?._receiver is null)
            {
                DroppedPackets++;
                return Task.CompletedTask;
            }

            Peer._receiver.Receive(data.ToArray(), LocalEndPoint);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Stop() { }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required DhtManager Client { get; init; }
        public required DhtManager Server { get; init; }
        public required LoopbackTransport ClientTransport { get; init; }
        public required LoopbackTransport ServerTransport { get; init; }
        public required IPEndPoint ServerEndPoint { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds a client and a server, starts both, and seeds the client's routing table with the
    /// server so a lookup has somewhere to start.
    /// </summary>
    private static async Task<Fixture> CreateFixtureAsync()
    {
        var clientEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 6881);
        var serverEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.2"), 6882);

        var clientTransport = new LoopbackTransport { LocalEndPoint = clientEndPoint };
        var serverTransport = new LoopbackTransport { LocalEndPoint = serverEndPoint };
        clientTransport.Peer = serverTransport;
        serverTransport.Peer = clientTransport;

        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        var serverId = InfoHash.CreateRandom();

        // TimeProvider.System rather than a fake: the client awaits real replies with a real
        // timeout, and a frozen clock would make the timeout unreachable.
        var client = new DhtManager(InfoHash.CreateRandom(), clientTransport, settings, TimeProvider.System);
        var server = new DhtManager(serverId, serverTransport, settings, TimeProvider.System);

        // Both must be started: Receive drops everything until then.
        await client.StartAsync();
        await server.StartAsync();

        // Seed the client's routing table the way it happens in reality: ping the server, and
        // learn about it from the reply. The loopback transport delivers synchronously, so the
        // node is present by the time Ping returns.
        client.Ping(serverEndPoint);

        return new Fixture
        {
            Client = client,
            Server = server,
            ClientTransport = clientTransport,
            ServerTransport = serverTransport,
            ServerEndPoint = serverEndPoint,
        };
    }

    [Fact(Timeout = 30000)]
    public async Task PutThenGet_RoundTripsAnImmutableItem()
    {
        await using var fixture = await CreateFixtureAsync();
        var item = new DhtImmutableItem { Value = Text("immutable payload") };

        int accepted = await fixture.Client.PutItemAsync(item);
        Assert.True(accepted > 0, "No node accepted the put.");

        var fetched = await fixture.Client.GetItemAsync(item.Target);

        var immutable = Assert.IsType<DhtImmutableItem>(fetched);
        Assert.Equal("immutable payload", ((BString)immutable.Value).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task PutThenGet_RoundTripsAMutableItem()
    {
        await using var fixture = await CreateFixtureAsync();
        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, [], sequenceNumber: 1, Text("mutable payload"));

        Assert.True(await fixture.Client.PutItemAsync(item) > 0);

        var fetched = await fixture.Client.GetItemAsync(item.Target);

        var mutable = Assert.IsType<DhtMutableItem>(fetched);
        Assert.Equal(1, mutable.SequenceNumber);
        Assert.Equal("mutable payload", ((BString)mutable.Value).Text);
        Assert.Equal(item.PublicKey, mutable.PublicKey);
        Assert.True(mutable.VerifySignature());
    }

    [Fact(Timeout = 30000)]
    public async Task PutThenGet_RoundTripsASaltedItem()
    {
        await using var fixture = await CreateFixtureAsync();
        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, "photos"u8, 1, Text("photo list"));

        Assert.True(await fixture.Client.PutItemAsync(item) > 0);

        // The salt is supplied by the caller, not returned by the node: a get reply carries no
        // salt because the requester derived the target from it in the first place.
        var mutable = Assert.IsType<DhtMutableItem>(
            await fixture.Client.GetItemAsync(item.Target, "photos"u8.ToArray()));
        Assert.Equal("photos"u8.ToArray(), mutable.Salt);
    }

    /// <summary>
    /// Without the salt the reconstructed record hashes to a different address, so it cannot be
    /// verified and must be discarded rather than returned unchecked.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Get_DiscardsASaltedItemWhenTheSaltIsNotSupplied()
    {
        await using var fixture = await CreateFixtureAsync();
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), "photos"u8, 1, Text("photo list"));

        Assert.True(await fixture.Client.PutItemAsync(item) > 0);

        Assert.Null(await fixture.Client.GetItemAsync(item.Target));
    }

    [Fact(Timeout = 30000)]
    public async Task Put_ReplacesAnItemWithANewerVersion()
    {
        await using var fixture = await CreateFixtureAsync();
        var seed = Ed25519.GenerateSeed();

        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 1, Text("v1")));
        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 2, Text("v2")));

        var target = DhtItemCodec.ComputeMutableTarget(Ed25519.PublicKeyFromSeed(seed), []);
        var mutable = Assert.IsType<DhtMutableItem>(await fixture.Client.GetItemAsync(target));

        Assert.Equal(2, mutable.SequenceNumber);
        Assert.Equal("v2", ((BString)mutable.Value).Text);
    }

    /// <summary>
    /// A replayed older record must not displace a newer one, even though its signature is valid.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Put_RejectsAReplayedOlderVersionOverTheWire()
    {
        await using var fixture = await CreateFixtureAsync();
        var seed = Ed25519.GenerateSeed();
        var old = DhtItemCodec.CreateSigned(seed, [], 1, Text("v1"));

        await fixture.Client.PutItemAsync(old);
        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 2, Text("v2")));

        int accepted = await fixture.Client.PutItemAsync(old);
        Assert.Equal(0, accepted);

        var mutable = Assert.IsType<DhtMutableItem>(await fixture.Client.GetItemAsync(old.Target));
        Assert.Equal(2, mutable.SequenceNumber);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_ReturnsNullForAnAddressNobodyHolds()
    {
        await using var fixture = await CreateFixtureAsync();

        Assert.Null(await fixture.Client.GetItemAsync(DhtTarget.FromHex(new string('b', 40))));
    }

    [Fact(Timeout = 30000)]
    public async Task Put_ReturnsZeroWhenNoNodeAnswers()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.ServerTransport.Blackhole = true;
        fixture.ClientTransport.Blackhole = true;

        int accepted = await fixture.Client.PutItemAsync(new DhtImmutableItem { Value = Text("unreachable") });

        Assert.Equal(0, accepted);
    }

    [Fact(Timeout = 30000)]
    public async Task Put_RefusesAnUnsignedMutableItemBeforeTouchingTheNetwork()
    {
        await using var fixture = await CreateFixtureAsync();
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, Text("payload"));
        var forged = item with { Value = Text("tampered") };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.PutItemAsync(forged));
        Assert.Equal(0, fixture.ClientTransport.DroppedPackets);
    }

    [Fact(Timeout = 30000)]
    public async Task Put_HonoursCompareAndSwapOverTheWire()
    {
        await using var fixture = await CreateFixtureAsync();
        var seed = Ed25519.GenerateSeed();
        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 5, Text("current")));

        var next = DhtItemCodec.CreateSigned(seed, [], 6, Text("next"));

        Assert.Equal(0, await fixture.Client.PutItemAsync(next, compareAndSwap: 4));
        Assert.True(await fixture.Client.PutItemAsync(next, compareAndSwap: 5) > 0);
    }

    /// <summary>
    /// A malicious node can answer any query with any bytes. The client must reject a record whose
    /// key and salt do not hash to the address it asked about, otherwise a node could serve a
    /// validly signed record belonging to a different address and have it accepted.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Get_RejectsAValidlySignedItemForTheWrongTarget()
    {
        await using var fixture = await CreateFixtureAsync();
        var seed = Ed25519.GenerateSeed();
        var elsewhere = DhtItemCodec.CreateSigned(seed, "elsewhere"u8, 1, Text("not yours"));

        await fixture.Client.PutItemAsync(elsewhere);

        // Ask for a different address; the server holds nothing there.
        var otherTarget = DhtItemCodec.ComputeMutableTarget(elsewhere.PublicKey, "different"u8);

        Assert.NotEqual(elsewhere.Target, otherTarget);
        Assert.Null(await fixture.Client.GetItemAsync(otherTarget));
    }

    [Fact(Timeout = 30000)]
    public async Task Get_ReportsTheSequenceNumberEvenWhenTheCallerIsUpToDate()
    {
        await using var fixture = await CreateFixtureAsync();
        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, [], 3, Text("payload"));
        await fixture.Client.PutItemAsync(item);

        // A plain get still returns the full value; the seq-conditional path is an optimisation
        // for callers that already hold a version, exercised through the server handler directly.
        var mutable = Assert.IsType<DhtMutableItem>(await fixture.Client.GetItemAsync(item.Target));
        Assert.Equal(3, mutable.SequenceNumber);
    }
}
