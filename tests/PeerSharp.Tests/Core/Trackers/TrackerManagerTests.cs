using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Trackers;

public class TrackerManagerTests
{
    private class MockTracker : ITracker
    {
        public string Url { get; private set; } = string.Empty;
        public ITrackerCallback? Callback { get; private set; }
        public int AnnounceCount { get; private set; }
        public int DeinitCount { get; private set; }
        public TrackerEvent LastEvent { get; private set; }
        private readonly SemaphoreSlim _announceSemaphore = new(0);
        private readonly SemaphoreSlim _deinitSemaphore = new(0);

        public void Init(string url, Torrent torrent, ITrackerCallback callback)
        {
            Url = url;
            Callback = callback;
        }

        public void Deinit()
        {
            DeinitCount++;
            _deinitSemaphore.Release();
        }

        public async Task WaitAnnounceAsync(TimeSpan timeout)
        {
            await _announceSemaphore.WaitAsync(timeout);
        }

        public async Task WaitDeinitAsync(TimeSpan timeout)
        {
            await _deinitSemaphore.WaitAsync(timeout);
        }

        public Task AnnounceAsync(TrackerEvent evt = TrackerEvent.None, CancellationToken ct = default)
        {
            AnnounceCount++;
            LastEvent = evt;
            _announceSemaphore.Release();
            return Task.CompletedTask;
        }

        public Task ScrapeAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void TriggerResult(bool success, AnnounceResponse response, string? error = null)
        {
            Callback?.OnAnnounceResult(success, response, this, error);
        }

        public void TriggerMultiScrapeResult(bool success, MultiScrapeResponse response)
        {
            Callback?.OnMultiScrapeResult(success, response, this);
        }

        public Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }

    private class MockTrackerFactory : ITrackerFactory
    {
        public Dictionary<string, MockTracker> Trackers { get; } = [];

        public ITracker? CreateTracker(string url, TimeProvider timeProvider)
        {
            var tracker = new MockTracker();
            Trackers[url] = tracker;
            return tracker;
        }
    }

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly MockTrackerFactory _factory = new();
    private readonly Torrent _torrent;

    public TrackerManagerTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
    }

    [Fact]
    public void AddTracker_ValidUrl_CreatesAndInitsTracker()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.com/announce";

        manager.AddTracker(url);

        Assert.True(_factory.Trackers.ContainsKey(url));
        var tracker = _factory.Trackers[url];
        Assert.Equal(url, tracker.Url);
    }

    [Fact]
    public async Task RemoveTracker_BeforeStart_DoesNotSendStoppedAnnounce()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "udp://tracker.example:6969/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        Assert.True(manager.RemoveTracker(url));
        await tracker.WaitDeinitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, tracker.AnnounceCount);
        Assert.Equal(1, tracker.DeinitCount);
        Assert.Empty(manager.GetTrackers());
    }

    [Fact(Timeout = 30000)]
    public async Task RemoveTracker_AfterStart_SendsStoppedAnnounce()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "udp://tracker.example:6969/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        await manager.StartAsync();
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        Assert.True(manager.RemoveTracker(url));
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));
        await tracker.WaitDeinitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, tracker.AnnounceCount);
        Assert.Equal(TrackerEvent.Stopped, tracker.LastEvent);
        Assert.Equal(1, tracker.DeinitCount);
        Assert.Empty(manager.GetTrackers());
    }

    [Fact(Timeout = 30000)]
    public async Task Announce_HugeInterval_IsClampedAndDoesNotThrow()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.com/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        // A malformed tracker sends an interval that overflows int when cast: this used to
        // schedule a negative timer delay and throw inside the announce-result handler.
        tracker.TriggerResult(true, new AnnounceResponse { Interval = uint.MaxValue });

        var status = manager.GetTrackers().First();
        Assert.True(status.Interval > 0);
        Assert.True(status.Interval <= 24 * 60 * 60);
    }

    [Fact(Timeout = 30000)]
    public async Task Start_AnnouncesToAllTrackers()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        manager.AddTracker("http://t1.com/announce");
        manager.AddTracker("http://t2.com/announce");

        await manager.StartAsync();

        foreach (var tracker in _factory.Trackers.Values)
        {
            await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, tracker.AnnounceCount);
            Assert.Equal(TrackerEvent.Started, tracker.LastEvent);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task CircuitBreaker_OpensAfterThreeFailures()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://fail.com/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        // Wait for initial announce from AddTracker
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        // 3 failures
        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(false, new AnnounceResponse());

        var status = manager.GetTrackers().First();
        Assert.Equal(TrackerStatusType.CircuitOpen, status.Status);
        Assert.Equal(3, status.ConsecutiveFailures);
    }

    [Fact(Timeout = 30000)]
    public async Task CircuitBreaker_RetriesAfterBackoff()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://fail.com/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];

        // Wait for announcements (from AddTracker and Start)
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        // Open circuit
        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(false, new AnnounceResponse());

        int initialAnnounces = tracker.AnnounceCount;

        // Advance time by 61 seconds (default base backoff is 60s)
        _timeProvider.Advance(TimeSpan.FromSeconds(61));

        // Timer should have ticked and triggered announce (Half-Open)
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(initialAnnounces + 1, tracker.AnnounceCount);
    }

    [Fact]
    public void Success_ResetsFailuresAndClosesCircuit()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://test.com/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(true, new AnnounceResponse());

        var status = manager.GetTrackers().First();
        Assert.Equal(TrackerStatusType.Working, status.Status);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact(Timeout = 30000)]
    public async Task StartAsync_WithTrackerTiers_AnnouncesOnlyActiveTier()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        manager.AddTrackers(
        [
            new[] { "http://tier0.example/announce" },
            ["http://tier1.example/announce"]
        ]);

        await manager.StartAsync();

        var activeTracker = _factory.Trackers["http://tier0.example/announce"];
        var fallbackTracker = _factory.Trackers["http://tier1.example/announce"];
        await activeTracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, activeTracker.AnnounceCount);
        Assert.Equal(TrackerEvent.Started, activeTracker.LastEvent);
        Assert.Equal(0, fallbackTracker.AnnounceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task ActiveTierExhausted_AnnouncesNextTier()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        manager.AddTrackers(
        [
            new[] { "http://tier0.example/announce" },
            ["http://tier1.example/announce"]
        ]);

        await manager.StartAsync();
        var failingTracker = _factory.Trackers["http://tier0.example/announce"];
        var fallbackTracker = _factory.Trackers["http://tier1.example/announce"];
        await failingTracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        failingTracker.TriggerResult(false, new AnnounceResponse(), "first failure");
        failingTracker.TriggerResult(false, new AnnounceResponse(), "second failure");
        failingTracker.TriggerResult(false, new AnnounceResponse(), "third failure");

        await fallbackTracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(TrackerStatusType.CircuitOpen, manager.GetTrackers().Single(t => t.Url == failingTracker.Url).Status);
        Assert.Equal(1, fallbackTracker.AnnounceCount);
        Assert.Equal(TrackerEvent.Started, fallbackTracker.LastEvent);
    }

    [Fact]
    public void OnMultiScrapeResult_MatchingHash_UpdatesTrackerStats()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];
        var response = new MultiScrapeResponse();
        response.Results[_torrent.Hash.ToHexStringUpper()] = new ScrapeResponse
        {
            SeedCount = 12,
            LeechCount = 4,
            Downloaded = 20
        };

        tracker.TriggerMultiScrapeResult(true, response);

        var status = manager.GetTrackers().Single();
        Assert.Equal(12u, status.SeedCount);
        Assert.Equal(4u, status.LeechCount);
    }

    [Fact]
    public void OnMultiScrapeResult_NonMatchingHash_LeavesTrackerStatsUnchanged()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];
        var response = new MultiScrapeResponse();
        response.Results[InfoHash.CreateRandom().ToHexStringUpper()] = new ScrapeResponse
        {
            SeedCount = 12,
            LeechCount = 4
        };

        tracker.TriggerMultiScrapeResult(true, response);

        var status = manager.GetTrackers().Single();
        Assert.Equal(0u, status.SeedCount);
        Assert.Equal(0u, status.LeechCount);
    }
}





