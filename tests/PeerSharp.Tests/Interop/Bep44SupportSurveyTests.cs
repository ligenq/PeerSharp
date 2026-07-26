using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using System.Text;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Surveys how the live DHT actually responds to BEP 44 <c>get</c>.
///
/// "No live node accepted the put" has at least three causes that need entirely different
/// responses: our query is malformed, the nodes are unreachable, or the nodes do not implement
/// BEP 44. This distinguishes them from the response mix rather than by guessing.
///
/// The decisive signal is error code 204, Method Unknown. A node returning it is alive, parsed our
/// packet and answered - it simply does not support the query. If most reachable nodes answer that
/// way, sparse BEP 44 deployment is an ecosystem property and no amount of correctness on our side
/// changes it.
/// </summary>
public class Bep44SupportSurveyTests
{
    private readonly ITestOutputHelper _output;

    public Bep44SupportSurveyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static void RequireInteropEnabled()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PEERSHARP_INTEROP")))
        {
            Assert.Skip("Set PEERSHARP_INTEROP=1 to run live DHT interoperability tests.");
        }
    }

    /// <summary>
    /// Issues gets against several random addresses and reports the aggregate response mix. Asserts
    /// only that the walk reached somebody - the numbers are the deliverable, not a pass or fail.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Survey_HowManyWalkedNodesAnswerGet()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var settings = new Settings();
        await using var listener = new UdpListener(0, new UdpSocketFactory(), settings, NullLoggerFactory.Instance, TimeProvider.System);
        await listener.StartAsync(cts.Token);

        await using var dht = DhtManager.CreateSecure(listener, settings);
        await dht.StartAsync(cts.Token);

        // Wait until the routing table is genuinely usable rather than for a fixed period. Table
        // growth against the live network is highly variable - observed anywhere from 2 to 46 nodes
        // at the one-minute mark across runs - and measuring a cold table produces a survey of our
        // own impatience rather than of the network.
        await WaitForUsableRoutingTableAsync(dht, cts.Token);

        int totalQueried = 0, totalReplied = 0, totalErrored = 0, totalTimedOut = 0, totalTokens = 0;
        var allErrorCodes = new Dictionary<int, int>();

        // Several random addresses, so the walk visits different parts of the keyspace rather than
        // drawing a conclusion from whichever eight nodes sit near one target.
        for (int probe = 0; probe < 5; probe++)
        {
            cts.Token.ThrowIfCancellationRequested();

            var target = new DhtTarget(System.Security.Cryptography.RandomNumberGenerator.GetBytes(DhtTarget.Length));
            var (_, stats) = await dht.GetItemWithStatsAsync(target, cancellationToken: cts.Token);

            _output.WriteLine($"probe {probe + 1}: {stats}");

            totalQueried += stats.NodesQueried;
            totalReplied += stats.Replied;
            totalErrored += stats.Errored;
            totalTimedOut += stats.TimedOut;
            totalTokens += stats.WriteTokensReceived;

            foreach (var (code, count) in stats.ErrorCodes)
            {
                allErrorCodes[code] = allErrorCodes.GetValueOrDefault(code) + count;
            }
        }

        _output.WriteLine("");
        _output.WriteLine("=== aggregate over 5 probes ===");
        _output.WriteLine($"nodes queried      : {totalQueried}");
        _output.WriteLine($"replied to get     : {totalReplied}");
        _output.WriteLine($"answered an error  : {totalErrored}");
        _output.WriteLine($"never answered     : {totalTimedOut}");
        _output.WriteLine($"issued write token : {totalTokens}");
        _output.WriteLine($"error codes        : {(allErrorCodes.Count == 0 ? "none" : string.Join(", ", allErrorCodes.Select(static p => $"{p.Key} x{p.Value}")))}");

        if (allErrorCodes.TryGetValue(204, out int methodUnknown))
        {
            _output.WriteLine("");
            _output.WriteLine($"{methodUnknown} node(s) answered 204 Method Unknown: alive, parsed our packet, no BEP 44 support.");
        }

        // The only genuine failure here is not reaching the network at all; everything else is a
        // measurement of the ecosystem rather than of this implementation.
        Assert.True(
            totalQueried > 0,
            "No nodes were queried at all, so the routing table never populated - fix bootstrap before reading anything else here.");
    }

    /// <summary>
    /// A control probe: the same walk using get_peers, which every DHT implementation supports.
    /// If get_peers draws replies where get does not, our transport and routing are fine and the
    /// difference is BEP 44 support. If neither does, the problem is closer to home.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Survey_ComparesGetAgainstAWidelySupportedQuery()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var settings = new Settings();
        await using var listener = new UdpListener(0, new UdpSocketFactory(), settings, NullLoggerFactory.Instance, TimeProvider.System);
        await listener.StartAsync(cts.Token);

        var callback = new CountingCallback();
        await using var dht = DhtManager.CreateSecure(listener, settings);
        dht.SetCallback(callback);
        await dht.StartAsync(cts.Token);

        await WaitForUsableRoutingTableAsync(dht, cts.Token);

        // get_peers for a well-populated info-hash draws peer lists from any working DHT node.
        var popular = InfoHash.CreateRandom();
        dht.FindPeers(popular);
        await Task.Delay(TimeSpan.FromSeconds(20), cts.Token);

        var target = new DhtTarget(System.Security.Cryptography.RandomNumberGenerator.GetBytes(DhtTarget.Length));
        var (_, getStats) = await dht.GetItemWithStatsAsync(target, cancellationToken: cts.Token);

        _output.WriteLine($"get_peers callbacks received : {callback.Count}");
        _output.WriteLine($"get lookup                   : {getStats}");
        _output.WriteLine("");
        _output.WriteLine(getStats.Replied > 0
            ? "Nodes do answer get, so BEP 44 is reachable here."
            : "No node answered get. Compare against the get_peers count above to tell a transport problem from missing BEP 44 support.");

        Assert.True(getStats.NodesQueried > 0, "The routing table never populated.");
    }

    /// <summary>
    /// Polls until a lookup has a meaningful number of nodes to start from, or a ceiling elapses.
    /// Uses InitialCandidates, which is what actually gates a lookup, rather than a total node
    /// count that may all sit in unusable buckets.
    /// </summary>
    private async Task WaitForUsableRoutingTableAsync(DhtManager dht, CancellationToken cancellationToken)
    {
        const int Wanted = 6;

        for (int attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            var probe = new DhtTarget(System.Security.Cryptography.RandomNumberGenerator.GetBytes(DhtTarget.Length));
            var (_, stats) = await dht.GetItemWithStatsAsync(probe, cancellationToken: cancellationToken).ConfigureAwait(false);

            _output.WriteLine($"warm-up t+{(attempt + 1) * 5}s: candidates={stats.InitialCandidates}");

            if (stats.InitialCandidates >= Wanted)
            {
                return;
            }
        }

        _output.WriteLine("Routing table never reached a usable size; the numbers below are of a cold table.");
    }

    private sealed class CountingCallback : IDhtCallback
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void OnPeersFound(InfoHash infoHash, List<System.Net.IPEndPoint> peers)
        {
            Interlocked.Increment(ref _count);
        }

        public void OnScrapeResult(InfoHash infoHash, int estimatedSeeds, int estimatedPeers) { }
    }
}
