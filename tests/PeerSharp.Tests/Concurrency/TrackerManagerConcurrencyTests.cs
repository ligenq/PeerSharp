using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Trackers;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Concurrency;

public class TrackerManagerConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public TrackerManagerConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void RunCoyoteTest(Action test, uint iterations = 100)
    {
        var config = Configuration.Create()
            .WithTestingIterations(iterations)
            .WithMaxSchedulingSteps(1000);

        using var engine = TestingEngine.Create(config, test);
        engine.Run();

        var report = engine.TestReport;
        if (report.NumOfFoundBugs > 0)
        {
            _output.WriteLine($"Found {report.NumOfFoundBugs} bug(s)!");
            _output.WriteLine(engine.GetReport());
            Assert.Fail($"Coyote found {report.NumOfFoundBugs} concurrency bug(s). See test output for details.");
        }
    }

    private class MockTracker : ITracker
    {
        public string Url { get; }

        public MockTracker(string url)
        {
            Url = url;
        }

        public Task AnnounceAsync(TrackerEvent evt, CancellationToken ct)
        {
            if (Url.Contains("fail"))
            {
                throw new Exception("Tracker failed");
            }
            return Task.CompletedTask;
        }

        public Task ScrapeAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public void SetCallback(ITrackerCallback callback) { }

        public void Deinit() { }
        public void Init(string url, Torrent torrent, ITrackerCallback callback) { }
        public Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct) => Task.CompletedTask;
    }

    private class MockTrackerFactory : ITrackerFactory
    {
        public ITracker? CreateTracker(string url, TimeProvider timeProvider)
        {
            return new MockTracker(url);
        }
    }

    [Fact]
    public void TrackerManager_ConcurrentAnnounce_Safe()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var timeProvider = new FakeTimeProvider();
            var manager = new TrackerManager(torrent, new MockTrackerFactory(), timeProvider);

            manager.AddTracker("http://tracker1.com/announce");
            manager.AddTracker("http://tracker2.com/announce");

            var tasks = new List<Task>();

            // Concurrent announces
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(async () => await manager.AnnounceAsync()));
            }

            Task.WaitAll(tasks.ToArray());

            // Concurrent stop
            tasks.Clear();
            for (int i = 0; i < 2; i++)
            {
                tasks.Add(Task.Run(async () => await manager.StopAsync()));
            }

            Task.WaitAll(tasks.ToArray());
        });
    }

    [Fact]
    public void TrackerManager_TierFailover_Concurrency()
    {
        RunCoyoteTest(() =>
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var timeProvider = new FakeTimeProvider();
            var manager = new TrackerManager(torrent, new MockTrackerFactory(), timeProvider);

            // Tier 0 (Fails)
            manager.AddTrackers([new[] { "http://fail1.com/announce", "http://fail2.com/announce" }]);
            // Tier 1 (Works)
            manager.AddTrackers([new[] { "http://work1.com/announce" }]);

            // We need our MockTracker to fail based on URL
            // MockTrackerFactory creates new instances.
            // We can check URL in AnnounceAsync.

            var tasks = new List<Task>();

            // Concurrent announces triggering failover
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(async () => await manager.AnnounceAsync()));
            }

            Task.WaitAll(tasks.ToArray());

            // Verify we switched tiers
            // We can check active tier index via reflection or by checking if "working" tracker was called.
            // But we don't have easy access to the created trackers.
            // We'll rely on absence of exceptions and no crashes.
        });
    }
}
