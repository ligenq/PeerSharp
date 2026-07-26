using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Exercises BEP 51 against the live Mainline DHT. Opt-in, excluded from CI, gated on
/// <c>PEERSHARP_INTEROP=1</c>.
///
/// Loopback tests prove our responder and our parser agree with each other, which is exactly the
/// mistake they cannot catch. BEP 51 support in the wild is also patchy in a way that matters to
/// anyone planning to index: these report the response mix rather than asserting a particular one.
/// </summary>
public class Bep51LiveDhtTests
{
    private readonly ITestOutputHelper _output;

    public Bep51LiveDhtTests(ITestOutputHelper output)
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
    /// Fills the routing table by actually walking the DHT.
    ///
    /// <para>
    /// Both halves are needed. Waiting alone stalls at the two or three routers bootstrap resolved,
    /// because its find_node rounds are bounded and then stop - measured at 2 nodes after two minutes
    /// of polling. Probing alone does nothing either: with an empty table a lookup has no candidates
    /// and returns instantly without touching the network. So each round sleeps to let bootstrap land,
    /// then walks from whatever is there.
    /// </para>
    /// </summary>
    private async Task<int> WaitForRoutingTableAsync(DhtManager dht, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            var probe = new DhtTarget(RandomNumberGenerator.GetBytes(DhtTarget.Length));
            await dht.GetItemWithStatsAsync(probe, cancellationToken: cancellationToken).ConfigureAwait(false);

            int known = dht.GetKnownNodeEndpoints(500).Count;
            _output.WriteLine($"warm-up probe {attempt + 1}: {known} node(s) in the routing table");

            if (known >= 40)
            {
                return known;
            }
        }

        return dht.GetKnownNodeEndpoints(500).Count;
    }

    /// <summary>
    /// Asks real nodes for samples and reports how many answer. The numbers are the deliverable; the
    /// only assertion is that we reached the network at all.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Survey_HowManyWalkedNodesAnswerSampleInfoHashes()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var settings = new Settings();
        await using var listener = new UdpListener(0, new UdpSocketFactory(), settings, NullLoggerFactory.Instance, TimeProvider.System);
        await listener.StartAsync(cts.Token);

        await using var dht = DhtManager.CreateSecure(listener, settings);
        await dht.StartAsync(cts.Token);

        await WaitForRoutingTableAsync(dht, cts.Token);

        var nodes = dht.GetKnownNodeEndpoints(60);
        int replied = 0, silent = 0, withSamples = 0;
        int totalSamples = 0;
        long claimedStorage = 0;
        var intervals = new List<int>();
        var distinct = new HashSet<InfoHash>();

        foreach (var node in nodes)
        {
            cts.Token.ThrowIfCancellationRequested();

            var target = new DhtTarget(RandomNumberGenerator.GetBytes(DhtTarget.Length));
            var reply = await dht.SampleInfoHashesAsync(node, target, cts.Token);

            if (reply is not { } sample)
            {
                // Either no answer at all or an error - 204 Method Unknown from anything predating
                // BEP 51 lands here too.
                silent++;
                continue;
            }

            replied++;
            intervals.Add((int)sample.Interval.TotalSeconds);
            claimedStorage += sample.TotalInfoHashes;
            totalSamples += sample.Samples.Count;

            if (sample.Samples.Count > 0)
            {
                withSamples++;
            }

            foreach (var hash in sample.Samples)
            {
                distinct.Add(hash);
            }
        }

        _output.WriteLine("");
        _output.WriteLine("=== BEP 51 support among walked nodes ===");
        _output.WriteLine($"nodes asked            : {nodes.Count}");
        _output.WriteLine($"answered               : {replied}");
        _output.WriteLine($"no usable answer       : {silent}");
        _output.WriteLine($"answered with hashes   : {withSamples}");
        _output.WriteLine($"hashes returned        : {totalSamples} ({distinct.Count} distinct)");
        _output.WriteLine($"storage they claim     : {claimedStorage} info-hash(es) in total");
        if (intervals.Count > 0)
        {
            _output.WriteLine($"interval range         : {intervals.Min()}s - {intervals.Max()}s");
        }

        if (nodes.Count > 0)
        {
            _output.WriteLine($"support rate           : {100.0 * replied / nodes.Count:F1}%");
        }

        Assert.True(
            nodes.Count > 0,
            "The routing table never populated, so nothing was measured - fix bootstrap before reading anything here.");
    }

    /// <summary>
    /// Runs the crawler itself against the live network. This is the end-to-end claim: that the
    /// public API discovers real info-hashes being shared right now.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Crawler_CollectsInfoHashesFromTheLiveDht()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var settings = new Settings();
        await using var listener = new UdpListener(0, new UdpSocketFactory(), settings, NullLoggerFactory.Instance, TimeProvider.System);
        await listener.StartAsync(cts.Token);

        await using var dht = DhtManager.CreateSecure(listener, settings);
        await dht.StartAsync(cts.Token);

        await WaitForRoutingTableAsync(dht, cts.Token);

        var crawler = dht.CreateInfoHashCrawler(
            new DhtIndexerOptions { MaxInfoHashes = 200, MaxConcurrency = 8 },
            NullLoggerFactory.Instance);

        var found = new List<DiscoveredInfoHash>();
        var sources = new HashSet<System.Net.IPEndPoint>();

        try
        {
            await foreach (var discovered in crawler.CrawlAsync(cts.Token))
            {
                found.Add(discovered);
                sources.Add(discovered.Source);
            }
        }
        catch (OperationCanceledException)
        {
            // The four-minute budget elapsed. Whatever was collected by then is still the result.
        }

        _output.WriteLine("");
        _output.WriteLine("=== live BEP 51 crawl ===");
        _output.WriteLine($"info-hashes discovered : {found.Count}");
        _output.WriteLine($"contributing nodes     : {sources.Count}");

        foreach (var discovered in found.Take(10))
        {
            _output.WriteLine($"  {discovered.InfoHash} from {discovered.Source} (holds {discovered.SourceTotalInfoHashes})");
        }

        Assert.Equal(found.Count, found.Select(static f => f.InfoHash).Distinct().Count());
        Assert.NotEmpty(found);
    }
}
