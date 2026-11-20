using System.Collections;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Dht;

public sealed class DhtLookupFaultTests
{
    private static readonly FieldInfo PendingQueriesField = typeof(DhtManager).GetField("_pendingQueries",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task PublishingSurvivesMissingTokensAndUnreliableReplies(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var clock = new FakeTimeProvider();
        var item = new DhtImmutableItem { Value = new BString("replayable publishing"u8.ToArray()) };
        byte[] AtDistance(byte distance)
        {
            byte[] id = item.Target.Span.ToArray();
            id[^1] ^= distance;
            return id;
        }

        var nodes = Enumerable.Range(1, 10)
            .Select(i => new DhtNode(AtDistance((byte)i), new IPEndPoint(IPAddress.Loopback, 6100 + i))).ToArray();
        var ids = nodes.ToDictionary(node => node.EndPoint, node => node.Id);
        var silent = nodes[8].EndPoint;
        var holder = nodes[9].EndPoint;
        var token = new BString("write-token"u8.ToArray());
        var referrals = new BString(DhtCompactNodeCodec.Encode(nodes.Skip(8)
            .Select(node => new NodeInfo(node.Id, node.EndPoint)).ToList()));
        var transport = new LookupTransport();
        var settings = new Settings { Dht = { BootstrapNodes = [], InitialState = new DhtState(AtDistance(128), nodes[..8]) } };
        await using var dht = new DhtManager(new InfoHash(AtDistance(128)), transport, settings, clock);
        await dht.StartAsync(ct);
        var pending = (IDictionary)PendingQueriesField.GetValue(dht)!;
        using var publishing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<int> publish = dht.PutItemAsync(item, cancellationToken: publishing.Token);
        try
        {
            for (int round = 0; round < 3; round++)
            {
                var batch = await transport.ReadBatchAsync(3, "get", ct);
                Assert.Equal(3, pending.Count);
                var forged = batch[scenario.Random.Next(batch.Length)];
                var stranger = new IPEndPoint(IPAddress.Loopback, 6199);
                scenario.Record($"round {round}: forged token for port {forged.Endpoint.Port} from port {stranger.Port}");
                transport.Reply(forged, ids[forged.Endpoint], stranger, token);
                Assert.Equal(3, pending.Count);

                scenario.Random.Shuffle(batch);
                foreach (var query in batch)
                {
                    if (query.Endpoint.Equals(silent))
                    {
                        scenario.Record($"round {round}: drop reply from port {query.Endpoint.Port}");
                        continue;
                    }
                    scenario.Record($"round {round}: reply and duplicate from port {query.Endpoint.Port} without a token");
                    transport.Reply(query, ids[query.Endpoint], query.Endpoint, referrals: referrals);
                    transport.Reply(query, ids[query.Endpoint], query.Endpoint, token);
                }
            }

            scenario.Record("advance the clock through the dropped reply's timeout");
            await TorrentTestUtility.AdvanceUntilAsync(clock, () => pending.Count == 0 || transport.HasQueries,
                TimeSpan.FromSeconds(1), because: "the silent node to time out");
            scenario.Record($"query the only token holder on port {holder.Port}");
            var get = Assert.Single(await transport.ReadBatchAsync(1, "get", ct));
            Assert.Equal(holder, get.Endpoint);
            transport.Reply(get, ids[holder], holder, token);
            var put = Assert.Single(await transport.ReadBatchAsync(1, "put", ct));
            Assert.Equal(holder, put.Endpoint);
            var args = Assert.IsType<BDict>(put.Message.Get("a"));
            Assert.Equal(token.Value.ToArray(), args.GetBytes("token")!.Value.ToArray());
            transport.Reply(put, ids[holder], holder);
            Assert.Equal(1, await publish.WaitAsync(TimeSpan.FromSeconds(5), ct));
            Assert.Empty(pending);
        }
        finally
        {
            await publishing.CancelAsync();
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lookup = dht.GetItemAsync(item.Target, cancellationToken: cancellation.Token);
        for (int round = 0; round < 2; round++)
        {
            var batch = await transport.ReadBatchAsync(3, "get", ct);
            if (round == 0 && (seed & 1) == 0)
            {
                scenario.Record("let the first read batch time out");
                await TorrentTestUtility.AdvanceUntilAsync(clock, () => transport.HasQueries,
                    TimeSpan.FromSeconds(1), because: "the next read batch");
                continue;
            }
            foreach (var query in batch)
            {
                transport.Reply(query, ids[query.Endpoint], query.Endpoint);
            }
        }
        var outstanding = await transport.ReadBatchAsync(2, "get", ct);
        scenario.Record("cancel the lookup in its final batch");
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(TimeSpan.FromSeconds(5), ct));
        foreach (var late in outstanding)
        {
            transport.Reply(late, ids[late.Endpoint], late.Endpoint, token);
        }
        Assert.Empty(pending);
        Assert.False(transport.HasQueries);
    }

    private sealed record Query(BDict Message, IPEndPoint Endpoint);

    private sealed class LookupTransport : IUdpListener
    {
        private readonly Channel<Query> _queries = Channel.CreateUnbounded<Query>();
        private IUdpReceiver? _receiver;

        public bool HasQueries => _queries.Reader.TryPeek(out _);
        public int Port => 6881;
        public void RegisterReceiver(IUdpReceiver receiver) => _receiver = receiver;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            if (BencodeParser.Parse(data.ToArray()) is BDict message && message.GetString("q") is "get" or "put")
            {
                _queries.Writer.TryWrite(new Query(message, endpoint));
            }
            return Task.CompletedTask;
        }

        public async Task<Query[]> ReadBatchAsync(int count, string method, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var batch = new Query[count];
            for (int i = 0; i < count; i++)
            {
                batch[i] = await _queries.Reader.ReadAsync(deadline.Token);
                Assert.Equal(method, batch[i].Message.GetString("q"));
            }
            return batch;
        }

        public void Reply(Query query, byte[] id, IPEndPoint sender, BString? token = null, BString? referrals = null)
        {
            var reply = new BDict();
            reply.Dict["id"] = new BString(id);
            if (token != null) reply.Dict["token"] = token;
            if (referrals != null) reply.Dict["nodes"] = referrals;
            var response = new BDict();
            response.Dict["t"] = query.Message.Get("t")!;
            response.Dict["y"] = new BString("r"u8.ToArray());
            response.Dict["r"] = reply;
            _receiver!.Receive(BencodeWriter.Write(response), sender);
        }
    }
}
