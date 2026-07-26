using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// BEP 43: read-only DHT nodes, over the wire.
///
/// Both halves matter and they are independent: honouring someone else's <c>ro</c> flag keeps our
/// routing table free of nodes that cannot serve queries, and setting our own keeps us out of theirs.
/// </summary>
public class DhtReadOnlyTests
{
    /// <summary>
    /// Sends a hand-built query at a responder and reports its reply (null when it stayed silent) and
    /// how many nodes the responder's routing table then held.
    /// </summary>
    private static async Task<(BDict? Reply, int KnownNodes)> QueryAsync(bool serverReadOnly, bool markQueryReadOnly)
    {
        var serverEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.2"), 6882);
        var probeEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.3"), 6883);

        var serverTransport = new DhtLoopbackFixture.LoopbackTransport { LocalEndPoint = serverEndPoint };
        var probeTransport = new DhtLoopbackFixture.LoopbackTransport { LocalEndPoint = probeEndPoint };
        serverTransport.Peer = probeTransport;
        probeTransport.Peer = serverTransport;

        var capture = new CapturingReceiver();
        probeTransport.RegisterReceiver(capture);

        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        settings.Dht.ReadOnly = serverReadOnly;

        await using var server = new DhtManager(InfoHash.CreateRandom(), serverTransport, settings, TimeProvider.System);
        await server.StartAsync();

        var a = new BDict();
        a.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());

        var query = new BDict();
        query.Dict["t"] = new BString("zz"u8.ToArray());
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString("ping"u8.ToArray());
        query.Dict["a"] = a;
        if (markQueryReadOnly)
        {
            query.Dict["ro"] = new BNumber(1);
        }

        // The transport delivers synchronously, so any reply has landed by the time this returns.
        await probeTransport.SendAsync(BencodeWriter.Write(query), serverEndPoint, CancellationToken.None);

        var reply = capture.LastPacket is null ? null : BencodeParser.Parse(capture.LastPacket) as BDict;
        return (reply, server.GetKnownNodeEndpoints(100).Count);
    }

    [Fact(Timeout = 30000)]
    public async Task AReadOnlySender_IsNotAddedToTheRoutingTable()
    {
        // Pinging it later would go unanswered and cost it traffic, so it would only be dead weight.
        var (reply, knownNodes) = await QueryAsync(serverReadOnly: false, markQueryReadOnly: true);

        Assert.NotNull(reply);
        Assert.Equal(0, knownNodes);
    }

    [Fact(Timeout = 30000)]
    public async Task AReadOnlySender_StillGetsItsQueryServiced()
    {
        // BEP 43: the receiver "should merely service the query as usual".
        var (reply, _) = await QueryAsync(serverReadOnly: false, markQueryReadOnly: true);

        Assert.NotNull(reply);
        Assert.Equal("r", reply.GetString("y"));
    }

    [Fact(Timeout = 30000)]
    public async Task AnOrdinarySender_IsAddedToTheRoutingTable()
    {
        // The control: without the flag the same query does populate the table, so the test above is
        // measuring the flag rather than a query that failed to register for some other reason.
        var (reply, knownNodes) = await QueryAsync(serverReadOnly: false, markQueryReadOnly: false);

        Assert.NotNull(reply);
        Assert.Equal(1, knownNodes);
    }

    [Fact(Timeout = 30000)]
    public async Task AReadOnlyNode_DoesNotAnswerQueries()
    {
        // "It no longer responds to 'query' messages that it receives."
        var (reply, _) = await QueryAsync(serverReadOnly: true, markQueryReadOnly: false);

        Assert.Null(reply);
    }

    [Fact(Timeout = 30000)]
    public async Task AReadOnlyNode_FlagsItsOwnQueries()
    {
        var probeEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.4"), 6884);
        var clientEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.5"), 6885);

        var clientTransport = new DhtLoopbackFixture.LoopbackTransport { LocalEndPoint = clientEndPoint };
        var probeTransport = new DhtLoopbackFixture.LoopbackTransport { LocalEndPoint = probeEndPoint };
        clientTransport.Peer = probeTransport;
        probeTransport.Peer = clientTransport;

        var capture = new CapturingReceiver();
        probeTransport.RegisterReceiver(capture);

        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        settings.Dht.ReadOnly = true;

        await using var client = new DhtManager(InfoHash.CreateRandom(), clientTransport, settings, TimeProvider.System);
        await client.StartAsync();

        client.Ping(probeEndPoint);

        Assert.NotNull(capture.LastPacket);
        var sent = Assert.IsType<BDict>(BencodeParser.Parse(capture.LastPacket));
        Assert.Equal("q", sent.GetString("y"));
        Assert.Equal(1, sent.GetLong("ro"));
    }

    [Fact(Timeout = 30000)]
    public async Task AFullParticipant_DoesNotFlagItsQueries()
    {
        var probeEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.6"), 6886);
        var clientEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.7"), 6887);

        var clientTransport = new DhtLoopbackFixture.LoopbackTransport { LocalEndPoint = clientEndPoint };
        var probeTransport = new DhtLoopbackFixture.LoopbackTransport { LocalEndPoint = probeEndPoint };
        clientTransport.Peer = probeTransport;
        probeTransport.Peer = clientTransport;

        var capture = new CapturingReceiver();
        probeTransport.RegisterReceiver(capture);

        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];

        await using var client = new DhtManager(InfoHash.CreateRandom(), clientTransport, settings, TimeProvider.System);
        await client.StartAsync();

        client.Ping(probeEndPoint);

        Assert.NotNull(capture.LastPacket);
        var sent = Assert.IsType<BDict>(BencodeParser.Parse(capture.LastPacket));
        Assert.Null(sent.Get("ro"));
    }

    private sealed class CapturingReceiver : IUdpReceiver
    {
        public byte[]? LastPacket { get; private set; }

        public void Receive(byte[] data, IPEndPoint remote) => LastPacket = data;
    }
}
