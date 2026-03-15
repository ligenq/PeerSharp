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
        public TrackerEvent LastEvent { get; private set; }
        private readonly SemaphoreSlim _announceSemaphore = new(0);

        public void Init(string url, Torrent torrent, ITrackerCallback callback)
        {
            Url = url;
            Callback = callback;
        }

        public void Deinit() { }

        public async Task WaitAnnounceAsync(TimeSpan timeout)
        {
            await _announceSemaphore.WaitAsync(timeout);
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

        public Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }

    private class MockTrackerFactory : ITrackerFactory
    {
        public Dictionary<string, MockTracker> Trackers { get; } = new();

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
        string url = "http://tracker.com/announce";

        manager.AddTracker(url);

        Assert.True(_factory.Trackers.ContainsKey(url));
        var tracker = _factory.Trackers[url];
        Assert.Equal(url, tracker.Url);
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
        string url = "http://fail.com/announce";
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
        string url = "http://fail.com/announce";
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
        string url = "http://test.com/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(true, new AnnounceResponse());

        var status = manager.GetTrackers().First();
        Assert.Equal(TrackerStatusType.Working, status.Status);
        Assert.Equal(0, status.ConsecutiveFailures);
    }
}





