using System.Net;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Dht;

public sealed class DhtRemainingReviewTests
{
    [Fact]
    public async Task LookupVisitsUnqueriedCandidatesWhenRepliesContainNoMoreNodes()
    {
        var item = new DhtImmutableItem { Value = new BString("late candidate"u8.ToArray()) };
        var transport = new ReplyTransport((reply, queryCount) =>
        {
            if (queryCount > 3) reply.Dict["v"] = item.Value;
        });
        await using var dht = await CreateAsync(transport);
        Assert.NotNull(await dht.GetItemAsync(item.Target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(8, transport.GetQueries);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("sequence")]
    [InlineData("size")]
    public async Task MalformedItemsAreIgnoredWithoutAbortingALookup(string corruption)
    {
        var seed = Ed25519.GenerateSeed();
        var value = new BString(corruption == "size" ? new byte[1001] : "value"u8.ToArray());
        var item = DhtItemCodec.CreateSigned(seed, [], 0, new BString("value"u8.ToArray()));
        var target = corruption == "size" ? DhtItemCodec.ComputeImmutableTarget(value) : item.Target;
        var transport = new ReplyTransport((reply, _) =>
        {
            reply.Dict["v"] = value;
            if (corruption == "size") return;
            reply.Dict["k"] = new BString(corruption == "key" ? new byte[31] : item.PublicKey);
            long sequence = corruption == "sequence" ? -1 : 0;
            reply.Dict["seq"] = new BNumber(sequence);
            reply.Dict["sig"] = new BString(Ed25519.Sign(DhtItemCodec.BuildSignatureBuffer([], sequence, value), seed));
        });
        await using var dht = await CreateAsync(transport);
        Assert.Null(await dht.GetItemAsync(target, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishingAfterTheMaximumSequenceCannotWrapToANegativeVersion()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var resolver = new Bep46Resolver(fixture.Client);
        Assert.True(await resolver.PublishAsync(seed, InfoHash.CreateRandom(), long.MaxValue) > 0);
        await Assert.ThrowsAsync<OverflowException>(() => resolver.PublishNextAsync(seed, InfoHash.CreateRandom()));
    }

    [Fact]
    public async Task PutLandsOnTheClosestTokenHoldersNotTheFirstToAnswer()
    {
        var item = new DhtImmutableItem { Value = new BString("closest"u8.ToArray()) };
        var target = item.Target.Span.ToArray();
        byte[] IdAtDistance(byte prefix, int distance)
        {
            var id = (byte[])target.Clone();
            id[0] ^= prefix;
            id[^1] ^= (byte)distance;
            return id;
        }

        // The routing table only knows far nodes; each of them refers the client to twelve nodes
        // that sit progressively closer to the target.
        var ids = new Dictionary<IPEndPoint, byte[]>();
        var far = Enumerable.Range(1, 8).Select(i => new IPEndPoint(IPAddress.Loopback, 6000 + i)).ToArray();
        var near = Enumerable.Range(1, 12).Select(i => new IPEndPoint(IPAddress.Loopback, 7000 + i)).ToArray();
        for (int i = 0; i < far.Length; i++) ids[far[i]] = IdAtDistance(0x80, i + 1);
        for (int i = 0; i < near.Length; i++) ids[near[i]] = IdAtDistance(0, i + 1);
        var referral = new BString(DhtCompactNodeCodec.Encode(near.Select(ep => new NodeInfo(ids[ep], ep)).ToList()));

        var transport = new ScriptedTransport(ids, referral);
        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        settings.Dht.InitialState = new DhtState(InfoHash.CreateRandom().ToArray(),
            far.Select(ep => new DhtNode(ids[ep], ep)).ToArray());
        await using var dht = new DhtManager(InfoHash.CreateRandom(), transport, settings, TimeProvider.System);
        await dht.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8, await dht.PutItemAsync(item, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(near.Take(8).OrderBy(ep => ep.Port), transport.PutTargets.OrderBy(ep => ep.Port));
        Assert.DoesNotContain(near[^1], transport.GetTargets);
    }

    private static async Task<DhtManager> CreateAsync(ReplyTransport transport)
    {
        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        settings.Dht.InitialState = new DhtState(InfoHash.CreateRandom().ToArray(), Enumerable.Range(1, 8)
            .Select(i => new DhtNode(NodeId(i), new IPEndPoint(IPAddress.Loopback, 6000 + i))).ToArray());
        var dht = new DhtManager(InfoHash.CreateRandom(), transport, settings, TimeProvider.System);
        await dht.StartAsync(TestContext.Current.CancellationToken);
        return dht;
    }

    private static byte[] NodeId(int i) { var id = new byte[20]; id[^1] = (byte)i; return id; }

    private sealed class ScriptedTransport(Dictionary<IPEndPoint, byte[]> ids, BString referral) : IUdpListener
    {
        private IUdpReceiver? _receiver;
        public List<IPEndPoint> GetTargets { get; } = [];
        public List<IPEndPoint> PutTargets { get; } = [];
        public int Port => 6881;
        public void RegisterReceiver(IUdpReceiver receiver) => _receiver = receiver;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            var query = Assert.IsType<BDict>(BencodeParser.Parse(data.ToArray()));
            if (query.GetString("y") != "q" || !ids.TryGetValue(endpoint, out var id)) return Task.CompletedTask;
            var reply = new BDict();
            reply.Dict["id"] = new BString(id);
            switch (query.GetString("q"))
            {
                case "get":
                    lock (GetTargets) GetTargets.Add(endpoint);
                    reply.Dict["token"] = new BString("token"u8.ToArray());
                    reply.Dict["nodes"] = referral;
                    break;
                case "put":
                    lock (PutTargets) PutTargets.Add(endpoint);
                    break;
                default:
                    return Task.CompletedTask;
            }
            var response = new BDict();
            response.Dict["t"] = query.Get("t")!;
            response.Dict["y"] = new BString("r"u8.ToArray());
            response.Dict["r"] = reply;
            _receiver!.Receive(BencodeWriter.Write(response), endpoint);
            return Task.CompletedTask;
        }
    }

    private sealed class ReplyTransport(Action<BDict, int> configureReply) : IUdpListener
    {
        private IUdpReceiver? _receiver;
        public int GetQueries { get; private set; }
        public int Port => 6881;
        public void RegisterReceiver(IUdpReceiver receiver) => _receiver = receiver;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            var query = Assert.IsType<BDict>(BencodeParser.Parse(data.ToArray()));
            if (query.GetString("y") != "q") return Task.CompletedTask;
            var response = new BDict();
            response.Dict["t"] = query.Get("t")!;
            response.Dict["y"] = new BString("r"u8.ToArray());
            var reply = new BDict(); reply.Dict["id"] = new BString(NodeId(endpoint.Port - 6000));
            if (query.GetString("q") == "get") configureReply(reply, ++GetQueries);
            response.Dict["r"] = reply;
            _receiver!.Receive(BencodeWriter.Write(response), endpoint);
            return Task.CompletedTask;
        }
    }
}
