using Microsoft.Extensions.Time.Testing;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// What the DHT server side accepts from strangers, driven over the wire.
///
/// <para>
/// Everything a DHT node stores and answers arrives unsolicited. <c>DhtQueryRateLimiterTests</c>
/// covers the budget's own arithmetic; these prove it is reached from the query handler, and that
/// the peer store the handler writes into has a bottom as well as sides - it capped peers per
/// info-hash and left the number of info-hashes to whoever was announcing.
/// </para>
/// </summary>
public class DhtServerLimitsTests
{
    private sealed class RecordingUdpListener : IUdpListener
    {
        public int Port => 6881;
        public List<(byte[] Data, IPEndPoint EndPoint)> SentPackets { get; } = [];

        public void RegisterReceiver(IUdpReceiver receiver) { }

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct = default)
        {
            SentPackets.Add((data.ToArray(), endpoint));
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static BDict BuildQuery(string method, string transactionId, Action<BDict> arguments)
    {
        var query = new BDict();
        query.Dict["t"] = new BString(System.Text.Encoding.ASCII.GetBytes(transactionId));
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString(System.Text.Encoding.ASCII.GetBytes(method));

        var a = new BDict();
        a.Dict["id"] = new BString(InfoHash.CreateRandom().ToArray());
        arguments(a);
        query.Dict["a"] = a;
        return query;
    }

    private static byte[] Ping(string transactionId) => BencodeWriter.Write(BuildQuery("ping", transactionId, _ => { }));

    /// <summary>Runs one get_peers/announce_peer pair, returning whether the peer was recorded.</summary>
    private static void AnnounceOne(DhtManager dht, RecordingUdpListener listener, InfoHash infoHash, IPEndPoint source)
    {
        var getPeers = BuildQuery("get_peers", "gp", a => a.Dict["info_hash"] = new BString(infoHash.ToArray()));
        listener.SentPackets.Clear();
        dht.Receive(BencodeWriter.Write(getPeers), source);

        var response = Assert.IsType<BDict>(BencodeParser.Parse(listener.SentPackets[0].Data));
        var r = Assert.IsType<BDict>(response.Get("r"));
        byte[] token = r.GetBytes("token")!.Value.ToArray();

        var announce = BuildQuery("announce_peer", "ap", a =>
        {
            a.Dict["info_hash"] = new BString(infoHash.ToArray());
            a.Dict["port"] = new BNumber(6881);
            a.Dict["token"] = new BString(token);
        });

        listener.SentPackets.Clear();
        dht.Receive(BencodeWriter.Write(announce), source);
    }

    private static async Task<(DhtManager Dht, RecordingUdpListener Listener)> CreateServerAsync(TimeProvider? time = null)
    {
        var listener = new RecordingUdpListener();
        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];
        var dht = new DhtManager(InfoHash.CreateRandom(), listener, settings, time ?? TimeProvider.System);
        await dht.StartAsync();
        return (dht, listener);
    }

    [Fact(Timeout = 60000)]
    public async Task TheNumberOfStoredInfoHashesIsBounded()
    {
        // An announce costs one get_peers round trip for a token and nothing else, so the size of
        // this table used to be decided entirely by whoever was announcing at us.
        var (dht, listener) = await CreateServerAsync();
        await using var _ = dht;

        int overCap = DhtManager.MaxPeerCacheEntries + 50;
        for (int i = 0; i < overCap; i++)
        {
            // Vary the source address so the per-source query budget is not what stops this - the
            // storage cap is what is under test.
            var source = new IPEndPoint(new IPAddress([198, 51, (byte)(i / 250), (byte)(i % 250)]), 6881);
            AnnounceOne(dht, listener, InfoHash.CreateRandom(), source);
        }

        Assert.Equal(DhtManager.MaxPeerCacheEntries, dht.PeerCacheEntryCount);
    }

    [Fact(Timeout = 60000)]
    public async Task AFullStoreStillAnswersAnnounces()
    {
        // Declining to record is not declining to answer. BEP 5 expects a reply either way, and a
        // node that silently stopped responding would be dropped from routing tables.
        var (dht, listener) = await CreateServerAsync();
        await using var _ = dht;

        for (int i = 0; i < DhtManager.MaxPeerCacheEntries + 5; i++)
        {
            var source = new IPEndPoint(new IPAddress([198, 51, (byte)(i / 250), (byte)(i % 250)]), 6881);
            AnnounceOne(dht, listener, InfoHash.CreateRandom(), source);
        }

        // listener holds only the last announce's reply, because AnnounceOne clears between steps.
        var reply = Assert.IsType<BDict>(BencodeParser.Parse(listener.SentPackets[0].Data));
        Assert.Equal("r", reply.GetString("y"));
    }

    [Fact(Timeout = 60000)]
    public async Task AnAlreadyStoredInfoHashIsStillUpdatedWhenTheStoreIsFull()
    {
        // The cap refuses new hashes, not new peers for hashes already held. Otherwise a full store
        // would freeze: existing swarms would stop learning about new peers.
        var (dht, listener) = await CreateServerAsync();
        await using var _ = dht;

        var knownHash = InfoHash.CreateRandom();
        AnnounceOne(dht, listener, knownHash, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 6881));

        for (int i = 0; i < DhtManager.MaxPeerCacheEntries + 5; i++)
        {
            var source = new IPEndPoint(new IPAddress([198, 51, (byte)(i / 250), (byte)(i % 250)]), 6881);
            AnnounceOne(dht, listener, InfoHash.CreateRandom(), source);
        }

        // A second peer for the hash we already hold.
        AnnounceOne(dht, listener, knownHash, new IPEndPoint(IPAddress.Parse("203.0.113.2"), 6881));

        var getPeers = BuildQuery("get_peers", "gq", a => a.Dict["info_hash"] = new BString(knownHash.ToArray()));
        listener.SentPackets.Clear();
        dht.Receive(BencodeWriter.Write(getPeers), new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6881));

        var response = Assert.IsType<BDict>(BencodeParser.Parse(listener.SentPackets[0].Data));
        var r = Assert.IsType<BDict>(response.Get("r"));
        var values = Assert.IsType<BList>(r.Get("values"));
        Assert.Equal(2, values.List.Count);
    }

    [Fact(Timeout = 60000)]
    public async Task QueriesFromOneSourceAreRateLimited()
    {
        var time = new FakeTimeProvider();
        var (dht, listener) = await CreateServerAsync(time);
        await using var _ = dht;

        var source = new IPEndPoint(IPAddress.Parse("198.51.100.10"), 6881);

        int answered = 0;
        for (int i = 0; i < DhtQueryRateLimiter.DefaultQueriesPerAddress + 25; i++)
        {
            listener.SentPackets.Clear();
            dht.Receive(Ping($"p{i}"), source);
            answered += listener.SentPackets.Count;
        }

        Assert.Equal(DhtQueryRateLimiter.DefaultQueriesPerAddress, answered);
        Assert.Equal(25, dht.RateLimitedQueryCount);
    }

    [Fact(Timeout = 60000)]
    public async Task ARateLimitedQueryGetsNoReplyAtAll()
    {
        // Not even an error reply: the source address of a UDP datagram is unverified, so answering
        // an over-limit query is the amplification the limit exists to prevent.
        var time = new FakeTimeProvider();
        var (dht, listener) = await CreateServerAsync(time);
        await using var _ = dht;

        var source = new IPEndPoint(IPAddress.Parse("198.51.100.11"), 6881);
        for (int i = 0; i < DhtQueryRateLimiter.DefaultQueriesPerAddress; i++)
        {
            dht.Receive(Ping($"p{i}"), source);
        }

        listener.SentPackets.Clear();
        dht.Receive(Ping("over"), source);

        Assert.Empty(listener.SentPackets);
    }

    [Fact(Timeout = 60000)]
    public async Task OneFloodingSourceDoesNotSilenceAnother()
    {
        var time = new FakeTimeProvider();
        var (dht, listener) = await CreateServerAsync(time);
        await using var _ = dht;

        var flooding = new IPEndPoint(IPAddress.Parse("198.51.100.12"), 6881);
        for (int i = 0; i < DhtQueryRateLimiter.DefaultQueriesPerAddress + 10; i++)
        {
            dht.Receive(Ping($"p{i}"), flooding);
        }

        listener.SentPackets.Clear();
        dht.Receive(Ping("ok"), new IPEndPoint(IPAddress.Parse("198.51.100.13"), 6881));

        Assert.Single(listener.SentPackets);
    }

    [Fact(Timeout = 60000)]
    public async Task ARateLimitedSourceIsNotAddedToTheRoutingTable()
    {
        // The budget is checked before the routing-table insert as well as before the reply:
        // otherwise a flood still buys the sender a place in our table for free.
        var time = new FakeTimeProvider();
        var (dht, listener) = await CreateServerAsync(time);
        await using var _ = dht;

        var source = new IPEndPoint(IPAddress.Parse("198.51.100.14"), 6881);
        for (int i = 0; i < DhtQueryRateLimiter.DefaultQueriesPerAddress; i++)
        {
            dht.Receive(Ping($"p{i}"), source);
        }

        int before = dht.KnownNodeCount;
        dht.Receive(Ping("over"), new IPEndPoint(IPAddress.Parse("198.51.100.14"), 7000));

        Assert.Equal(before, dht.KnownNodeCount);
    }
}
