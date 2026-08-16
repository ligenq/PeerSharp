using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Dht;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// The BEP 51 crawler, driven against a real responder over the loopback transport.
///
/// The crawl is deliberately open-ended, so what these tests pin down is that it terminates when told
/// to - by limit, by cancellation, or by running out of nodes - and that it does not return the same
/// hash twice. A crawler that cannot be stopped is worse than none.
/// </summary>
public class DhtInfoHashCrawlerTests
{
    private static DhtInfoHashCrawler CreateCrawler(DhtLoopbackFixture fixture, DhtIndexerOptions options)
    {
        return fixture.Client.CreateInfoHashCrawler(options, NullLoggerFactory.Instance);
    }

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
    public async Task Crawl_YieldsTheHashesTheRespondersHold()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var stored = SeedServerStore(fixture, 4);

        // The limit is what ends the crawl: with everything found, every node goes into cooldown and
        // an unlimited crawl would simply wait for it to expire.
        var crawler = CreateCrawler(fixture, new DhtIndexerOptions { MaxInfoHashes = 4 });

        var found = new List<DiscoveredInfoHash>();
        await foreach (var discovered in crawler.CrawlAsync(TestContext.Current.CancellationToken))
        {
            found.Add(discovered);
        }

        Assert.Equal(
            [.. stored.Select(hash => hash.ToHexStringUpper()).Order()],
            [.. found.Select(hash => hash.InfoHash.ToHexStringUpper()).Order()]);
        Assert.All(found, discovered => Assert.Equal(fixture.ServerEndPoint, discovered.Source));
        Assert.All(found, discovered => Assert.Equal(4, discovered.SourceTotalInfoHashes));
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_StopsAtTheInfoHashLimit()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        SeedServerStore(fixture, 10);

        var crawler = CreateCrawler(fixture, new DhtIndexerOptions { MaxInfoHashes = 3 });

        var found = new List<DiscoveredInfoHash>();
        await foreach (var discovered in crawler.CrawlAsync(TestContext.Current.CancellationToken))
        {
            found.Add(discovered);
        }

        Assert.Equal(3, found.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_NeverRepeatsAHash()
    {
        // The server holds one hash and will keep offering it on every query; only dedupe keeps that
        // from becoming an endless stream of the same result.
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        SeedServerStore(fixture, 1);

        var crawler = CreateCrawler(fixture, new DhtIndexerOptions
        {
            MaxInfoHashes = 1,
            MinNodeRequeryInterval = TimeSpan.Zero
        });

        var found = new List<DiscoveredInfoHash>();
        await foreach (var discovered in crawler.CrawlAsync(TestContext.Current.CancellationToken))
        {
            found.Add(discovered);
        }

        Assert.Single(found);
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_CanReturnRepeatSightingsWithoutChangingTheUniqueLimit()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var stored = SeedServerStore(fixture, 1);
        var crawler = CreateCrawler(fixture, new DhtIndexerOptions
        {
            MaxInfoHashes = null,
            MinNodeRequeryInterval = TimeSpan.Zero,
            ReturnDuplicateSightings = true
        });

        var found = new List<DiscoveredInfoHash>();
        await foreach (var discovered in crawler.CrawlAsync(TestContext.Current.CancellationToken))
        {
            found.Add(discovered);
            if (found.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(2, found.Count);
        Assert.All(found, item => Assert.Equal(stored[0], item.InfoHash));
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_StopsWhenCancelled()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        SeedServerStore(fixture, 5);

        // No limit: cancellation is the only thing that can end this one. Cancelling an
        // IAsyncEnumerable throws, as the platform convention expects.
        var crawler = CreateCrawler(fixture, new DhtIndexerOptions { MaxInfoHashes = null });
        using var cts = new CancellationTokenSource();

        var found = new List<DiscoveredInfoHash>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var discovered in crawler.CrawlAsync(cts.Token))
            {
                found.Add(discovered);
                if (found.Count == 2)
                {
                    await cts.CancelAsync();
                }
            }
        });

        // Cancelling part way through a batch must stop there, not deliver the rest of it first.
        Assert.Equal(2, found.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_AbandonedEarly_DoesNotThrow()
    {
        // Breaking out of the loop is the ordinary way to use this API, so disposing the enumerator
        // mid-crawl has to be clean.
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        SeedServerStore(fixture, 5);

        var crawler = CreateCrawler(fixture, new DhtIndexerOptions { MaxInfoHashes = null });

        var found = new List<DiscoveredInfoHash>();
        await foreach (var discovered in crawler.CrawlAsync(TestContext.Current.CancellationToken))
        {
            found.Add(discovered);
            if (found.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(2, found.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_WhenNodesDoNotAnswer_EndsWithAnExhaustedFrontier()
    {
        // A node that never replies is dropped rather than cooled down, so a DHT of dead nodes
        // leaves nothing to come back to and the crawl completes on its own.
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        SeedServerStore(fixture, 5);
        fixture.ServerTransport.Blackhole = true;

        var crawler = CreateCrawler(fixture, new DhtIndexerOptions { MaxInfoHashes = null });

        var found = new List<DiscoveredInfoHash>();
        await foreach (var discovered in crawler.CrawlAsync(TestContext.Current.CancellationToken))
        {
            found.Add(discovered);
        }

        Assert.Empty(found);
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_WhenEveryNodeIsInCooldown_WaitsRatherThanFinishing()
    {
        // Responders that hold nothing still answer, so they go into cooldown for the interval they
        // asked for. Honouring that means waiting - a crawl that instead declared itself finished
        // would stop harvesting nodes that simply have not come due yet.
        await using var fixture = await DhtLoopbackFixture.CreateAsync();

        var crawler = CreateCrawler(fixture, new DhtIndexerOptions
        {
            MinNodeRequeryInterval = TimeSpan.FromHours(6)
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var found = new List<DiscoveredInfoHash>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var discovered in crawler.CrawlAsync(cts.Token))
            {
                found.Add(discovered);
            }
        });

        Assert.Empty(found);
    }

    [Fact(Timeout = 30000)]
    public async Task Crawl_WithNoRoutingTable_ReportsThatItCannotStart()
    {
        // A crawl needs a node to begin from. Failing loudly beats returning an empty stream that
        // looks like "the DHT holds nothing".
        var transport = new DhtLoopbackFixture.LoopbackTransport
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.9"), 6889)
        };

        var settings = new Settings();
        settings.Dht.BootstrapNodes = [];

        await using var dht = new DhtManager(InfoHash.CreateRandom(), transport, settings, TimeProvider.System);
        await dht.StartAsync();

        var crawler = dht.CreateInfoHashCrawler(new DhtIndexerOptions(), NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // The warmup wait is two minutes, so cancellation is what ends this within the test's budget;
        // either way the crawl must not silently yield an empty success.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in crawler.CrawlAsync(cts.Token))
            {
                Assert.Fail("A crawl with no routing table must not produce results.");
            }
        });
    }

    [Fact]
    public void Options_RejectInvalidConcurrency()
    {
        var options = new DhtIndexerOptions { MaxConcurrency = 0 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate("options"));
        Assert.Contains(nameof(DhtIndexerOptions.MaxConcurrency), ex.Message);
    }

    [Fact]
    public void Options_RejectAZeroInfoHashLimit()
    {
        var options = new DhtIndexerOptions { MaxInfoHashes = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate("options"));
    }

    [Fact]
    public void Options_RejectANegativeRequeryInterval()
    {
        var options = new DhtIndexerOptions { MinNodeRequeryInterval = TimeSpan.FromSeconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate("options"));
    }
}
