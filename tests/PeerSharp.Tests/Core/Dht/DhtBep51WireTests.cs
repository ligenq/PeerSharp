using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using System.Net;
using System.Text;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// BEP 51 over the wire: a real <c>sample_infohashes</c> query, encoded, sent, answered and decoded
/// between two <see cref="DhtManager"/> instances.
///
/// The responder is only worth anything if real indexers can read it, and the failure modes there are
/// invisible to unit tests - an argument under the wrong key, a samples blob that is not a whole
/// multiple of 20, a missing field where the spec requires an empty one.
/// </summary>
public class DhtBep51WireTests
{
    private static InfoHash[] SeedServerStore(DhtLoopbackFixture fixture, int count)
    {
        var hashes = new InfoHash[count];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = InfoHash.CreateRandom();
            fixture.Server.InjectPeer(
                hashes[i].ToHexStringUpper(),
                new IPEndPoint(IPAddress.Parse("198.51.100.9"), 6881 + i),
                DateTimeOffset.UtcNow);
        }

        return hashes;
    }

    [Fact(Timeout = 30000)]
    public async Task SampleInfoHashes_ReturnsTheStoredHashes()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var stored = SeedServerStore(fixture, 3);

        var reply = await fixture.Client.SampleInfoHashesAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        Assert.NotNull(reply);
        Assert.Equal(3, reply.Value.TotalInfoHashes);
        Assert.Equal(
            [.. stored.Select(hash => hash.ToHexStringUpper()).Order()],
            [.. reply.Value.Samples.Select(hash => hash.ToHexStringUpper()).Order()]);
    }

    [Fact(Timeout = 30000)]
    public async Task SampleInfoHashes_ReportsAnIntervalWithinTheSpecRange()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        SeedServerStore(fixture, 1);

        var reply = await fixture.Client.SampleInfoHashesAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        Assert.NotNull(reply);
        Assert.InRange(reply.Value.Interval, TimeSpan.Zero, DhtInfoHashSampler.MaxRefreshInterval);
    }

    [Fact(Timeout = 30000)]
    public async Task SampleInfoHashes_AlsoReturnsNodesForTraversal()
    {
        // The node list is what makes one RPC per node enough to cover the keyspace. The server
        // learned about the client from the fixture's ping, so it has someone to name.
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        SeedServerStore(fixture, 1);

        var reply = await fixture.Client.SampleInfoHashesAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        Assert.NotNull(reply);
        Assert.NotEmpty(reply.Value.Nodes);
    }

    [Fact(Timeout = 30000)]
    public async Task SampleInfoHashes_WithAnEmptyStore_StillAnswers()
    {
        // The spec requires the samples field even when empty, so an indexer can tell "I hold
        // nothing" apart from "I do not implement this".
        await using var fixture = await DhtLoopbackFixture.CreateAsync();

        var reply = await fixture.Client.SampleInfoHashesAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        Assert.NotNull(reply);
        Assert.Empty(reply.Value.Samples);
        Assert.Equal(0, reply.Value.TotalInfoHashes);
    }

    [Fact(Timeout = 30000)]
    public async Task FindNode_ReturnsNeighbours()
    {
        // The crawler's traversal fallback for nodes without BEP 51 support, which includes every
        // bootstrap router a fresh crawl starts from. Awaited rather than fire-and-forget, unlike the
        // find_node used during bootstrap.
        await using var fixture = await DhtLoopbackFixture.CreateAsync();

        var nodes = await fixture.Client.FindNodeAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        Assert.NotEmpty(nodes);
    }

    [Fact(Timeout = 30000)]
    public async Task FindNode_StillWorksWhenSamplingIsDisabled()
    {
        // Turning off sampling must not turn off traversal: a node that declines to be indexed is
        // still a normal DHT participant.
        await using var fixture = await DhtLoopbackFixture.CreateAsync(
            settings => settings.Dht.AnswerInfoHashSampling = false);

        var nodes = await fixture.Client.FindNodeAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        Assert.NotEmpty(nodes);
    }

    [Fact(Timeout = 30000)]
    public async Task SampleInfoHashes_WhenDisabled_ReadsAsNoReply()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync(
            settings => settings.Dht.AnswerInfoHashSampling = false);
        SeedServerStore(fixture, 3);

        var reply = await fixture.Client.SampleInfoHashesAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        Assert.Null(reply);
    }

    [Fact(Timeout = 30000)]
    public async Task SampleInfoHashes_WhenDisabled_RepliesMethodUnknown()
    {
        // Pinned to the exact code, because an indexer distinguishes "does not implement BEP 51"
        // from "is broken" by seeing 204 - the same reply every pre-BEP 51 node gives.
        var error = await QueryRawAsync(
            answerInfoHashSampling: false,
            target: InfoHash.CreateRandom().Span.ToArray());

        Assert.NotNull(error);
        Assert.Equal(204, error.Value.Code);
    }

    [Fact(Timeout = 30000)]
    public async Task SampleInfoHashes_WithAMalformedTarget_IsAProtocolError()
    {
        var error = await QueryRawAsync(answerInfoHashSampling: true, target: new byte[4]);

        Assert.NotNull(error);
        Assert.Equal(203, error.Value.Code);
    }

    /// <summary>
    /// Sends a hand-built query straight at a responder and returns the error reply, so the wire
    /// level codes can be asserted rather than inferred from a null.
    /// </summary>
    private static async Task<(int Code, string Message)?> QueryRawAsync(bool answerInfoHashSampling, byte[] target)
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
        settings.Dht.AnswerInfoHashSampling = answerInfoHashSampling;

        await using var server = new DhtManager(InfoHash.CreateRandom(), serverTransport, settings, TimeProvider.System);
        await server.StartAsync();

        var a = new BDict();
        a.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        a.Dict["target"] = new BString(target);

        var query = new BDict();
        query.Dict["t"] = new BString("zz"u8.ToArray());
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString("sample_infohashes"u8.ToArray());
        query.Dict["a"] = a;

        // The transport delivers synchronously, so the reply has landed by the time this returns.
        await probeTransport.SendAsync(BencodeWriter.Write(query), serverEndPoint, CancellationToken.None);

        if (capture.LastPacket is null || BencodeParser.Parse(capture.LastPacket) is not BDict reply)
        {
            return null;
        }

        if (reply.GetString("y") != "e" || reply.Get("e") is not BList error || error.List.Count < 2)
        {
            return null;
        }

        return ((int)((BNumber)error.List[0]).Value, ((BString)error.List[1]).Text);
    }

    private sealed class CapturingReceiver : IUdpReceiver
    {
        public byte[]? LastPacket { get; private set; }

        public void Receive(byte[] data, IPEndPoint remote) => LastPacket = data;
    }
}
