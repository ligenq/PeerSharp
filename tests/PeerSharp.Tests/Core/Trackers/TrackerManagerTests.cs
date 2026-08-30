using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Trackers;
using System.Net;

namespace PeerSharp.Tests.Core.Trackers;

public class TrackerManagerTests
{
    /// <summary>
    /// Records the BEP 24 addresses the tracker layer forwards for BEP 42 voting.
    /// </summary>
    private sealed class RecordingDhtManager : IDhtManager
    {
        public List<IPAddress> Reports { get; } = [];
        public bool ThrowOnReport { get; set; }
        public InfoHash NodeId { get; } = new InfoHash(new byte[20]);
        public System.Net.IPAddress? ExternalIp => null;

        public void ReportExternalIp(IPAddress address)
        {
            Reports.Add(address);
            if (ThrowOnReport)
            {
                throw new InvalidOperationException("DHT is not running");
            }
        }

        public void Announce(InfoHash infoHash, int port) { }
        public int FindPeers(InfoHash infoHash)
        {
            return 0;
        }
        public void Ping(IPEndPoint ep) { }
        public void ScrapeInfoHash(InfoHash infoHash) { }
        public void SetCallback(IDhtCallback callback) { }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public DhtState? ConsumeStateSnapshot() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockTracker : ITracker
    {
        public string Url { get; private set; } = string.Empty;
        public ITrackerCallback? Callback { get; private set; }
        public int AnnounceCount { get; private set; }
        public int ScrapeCount { get; private set; }
        public int DeinitCount { get; private set; }
        public TrackerEvent LastEvent { get; private set; }
        public Func<TrackerEvent, CancellationToken, Task>? AnnounceHandler { get; set; }
        public Func<CancellationToken, Task>? ScrapeHandler { get; set; }
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
            return AnnounceHandler?.Invoke(evt, ct) ?? Task.CompletedTask;
        }

        public Task ScrapeAsync(CancellationToken ct = default)
        {
            ScrapeCount++;
            return ScrapeHandler?.Invoke(ct) ?? Task.CompletedTask;
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
    public async Task ScrapeAsync_AsksEveryActiveTrackerAndWaitsForThem()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string firstUrl = "http://one.example/announce";
        const string secondUrl = "udp://two.example:6969/announce";
        manager.AddTracker(firstUrl);
        manager.AddTracker(secondUrl);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Trackers[secondUrl].ScrapeHandler = _ => completion.Task;

        Task scrape = manager.ScrapeAsync();

        Assert.Equal(1, _factory.Trackers[firstUrl].ScrapeCount);
        Assert.Equal(1, _factory.Trackers[secondUrl].ScrapeCount);
        Assert.False(scrape.IsCompleted);

        completion.SetResult();
        await scrape;
    }

    [Fact]
    public async Task ScrapeAsync_OneTrackerFailureDoesNotHideAnotherResult()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string failedUrl = "http://failed.example/announce";
        const string successfulUrl = "http://successful.example/announce";
        manager.AddTracker(failedUrl);
        manager.AddTracker(successfulUrl);
        _factory.Trackers[failedUrl].ScrapeHandler = _ => Task.FromException(new IOException("offline"));

        await manager.ScrapeAsync();

        Assert.Equal(1, _factory.Trackers[failedUrl].ScrapeCount);
        Assert.Equal(1, _factory.Trackers[successfulUrl].ScrapeCount);
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
    public async Task StopAsync_AwaitsStoppedAnnouncesAndRunsThemConcurrently()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        manager.AddTracker("http://t1.example/announce");
        manager.AddTracker("http://t2.example/announce");
        await manager.StartAsync();

        var entered = _factory.Trackers.Values.ToDictionary(
            tracker => tracker,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var tracker in _factory.Trackers.Values)
        {
            tracker.AnnounceHandler = async (evt, ct) =>
            {
                if (evt != TrackerEvent.Stopped)
                {
                    return;
                }
                entered[tracker].TrySetResult();
                await release.Task.WaitAsync(ct);
            };
        }

        Task stop = manager.StopAsync();
        await Task.WhenAll(entered.Values.Select(static signal => signal.Task));
        Assert.False(stop.IsCompleted);

        release.TrySetResult();
        await stop;
    }

    [Fact(Timeout = 30000)]
    public async Task StopAsync_CancelsAndDrainsCurrentAnnounceBeforeSendingStopped()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        var currentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.AnnounceHandler = async (evt, ct) =>
        {
            if (evt == TrackerEvent.None)
            {
                currentEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    currentDrained.TrySetResult();
                }
            }
            else if (evt == TrackerEvent.Stopped)
            {
                Assert.True(currentDrained.Task.IsCompleted);
            }
        };

        await manager.AnnounceAsync(url);
        await currentEntered.Task;
        await manager.StopAsync();

        Assert.True(currentDrained.Task.IsCompleted);
        Assert.Equal(TrackerEvent.Stopped, tracker.LastEvent);
    }

    [Fact(Timeout = 30000)]
    public async Task TrackedAnnounce_DoesNotCaptureCallerSynchronizationContext()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://synccontext.example/announce";
        manager.AddTracker(url);

        // Tracker work must run on the thread pool, never on the caller's context. A UI host
        // would otherwise get network continuations on its UI thread - and since StopAsync
        // awaits these tasks, a UI thread blocked on shutdown would deadlock.
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task started;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            // StartAsync is fully synchronous, so the announce is kicked off while the context
            // is installed - exactly the moment an `await Task.Yield()` would capture it.
            // Deliberately not awaited here: awaiting under the context would post the test's
            // own continuation and mask what we are measuring.
            started = manager.StartAsync();
            Assert.True(started.IsCompleted);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await started;
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync();

        Assert.Equal(0, context.PostCount);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            // Still run the work, so a regression shows up as a non-zero count rather than a hang.
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            d(state);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task StopAsync_TimesOutDrainingAnnounceThatIgnoresCancellation()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://stubborn.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        var currentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.AnnounceHandler = async (evt, ct) =>
        {
            if (evt != TrackerEvent.None)
            {
                return;
            }

            currentEntered.TrySetResult();
            // Deliberately ignores ct: a tracker implementation that never honours its token
            // must not be able to block engine shutdown indefinitely.
            await releaseCurrent.Task;
        };

        await manager.AnnounceAsync(url);
        await currentEntered.Task;

        try
        {
            Task stop = manager.StopAsync();

            // The drain runs on the injected clock, so nothing but the clock can release it. Stepping
            // past the deadline is what proves the bound exists - a stopwatch would only have proved
            // the machine was not busy at that moment.
            await TorrentTestUtility.AdvanceUntilCompleteAsync(_timeProvider, stop, TimeSpan.FromSeconds(5));

            Assert.Equal(TrackerEvent.Stopped, tracker.LastEvent);
        }
        finally
        {
            releaseCurrent.TrySetResult();
        }
    }



    [Fact(Timeout = 30000)]
    public async Task StopAsync_AwaitsRemovedTrackersPendingStoppedAnnounce()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://removed.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        var stoppedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.AnnounceHandler = async (evt, ct) =>
        {
            if (evt == TrackerEvent.Stopped)
            {
                stoppedEntered.TrySetResult();
                await releaseStopped.Task.WaitAsync(ct);
            }
        };

        Assert.True(manager.RemoveTracker(url));
        await stoppedEntered.Task;
        Task stop = manager.StopAsync();
        Assert.False(stop.IsCompleted);

        releaseStopped.TrySetResult();
        await stop;
        Assert.Equal(1, tracker.DeinitCount);
    }

    /// <summary>
    /// A tracker that never answers the Stopped announce must not hold up shutdown.
    ///
    /// <para>
    /// This used to time a stopwatch around StopAsync and assert the result landed between 1.5 and 6
    /// seconds. That measures the machine as much as the code: a busy CI runner took 8.2 seconds for a
    /// two-second timeout and failed. The timeout now runs on the injected clock, so the test can hold
    /// the clock still, confirm StopAsync really is waiting, and then move it past the deadline -
    /// which asserts the actual intent rather than how quickly the runner happened to schedule.
    /// </para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task StopAsync_TimesOutUnresponsiveStoppedAnnounce()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://unresponsive.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        var stoppedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.AnnounceHandler = (evt, ct) =>
        {
            if (evt != TrackerEvent.Stopped)
            {
                return Task.CompletedTask;
            }

            stoppedEntered.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };

        Task stop = manager.StopAsync();

        // The announce is in progress and will never finish on its own.
        await stoppedEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(stop.IsCompleted, "StopAsync returned before the Stopped announce had even begun.");

        // Nothing but the clock should release it, so stepping past the deadline is what completes it.
        await TorrentTestUtility.AdvanceUntilCompleteAsync(_timeProvider, stop, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A real stop tells the trackers, as BEP 3 asks.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task StopAsync_StoppingForReal_AnnouncesStopped()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://real-stop.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();

        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(5));

        await manager.StopAsync();

        Assert.Equal(TrackerEvent.Stopped, tracker.LastEvent);
    }

    /// <summary>
    /// Rebuilding the torrent is not stopping it, and must not say so.
    ///
    /// <para>
    /// When a magnet's metadata arrives the torrent is torn down and built again, but the info hash was
    /// just verified against that metadata - the tracker session is unchanged and a started announce
    /// follows within milliseconds. Claiming to have stopped in between is untrue, and it is not free:
    /// the announce is bounded by StopAnnounceTimeout, so one unresponsive UDP tracker costs the full
    /// two seconds. On a real magnet that was 2.5 of the 5.75 seconds between the last metadata byte
    /// and the first block being requested.
    /// </para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task StopAsync_Rebuilding_DoesNotAnnounceStopped()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://rebuild.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();

        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(5));
        var beforeStop = tracker.LastEvent;

        await manager.StopAsync(sendStoppedAnnounce: false);

        Assert.NotEqual(TrackerEvent.Stopped, tracker.LastEvent);
        Assert.Equal(beforeStop, tracker.LastEvent);
    }

    /// <summary>
    /// The point of skipping it: an unresponsive tracker cannot hold a rebuild up, because nothing is
    /// sent for it to fail to answer. The clock never moves here, so anything waiting on a timeout
    /// would wait forever.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task StopAsync_Rebuilding_IsNotDelayedByAnUnresponsiveTracker()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://unresponsive-rebuild.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();

        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(5));

        tracker.AnnounceHandler = (evt, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        await manager.StopAsync(sendStoppedAnnounce: false).WaitAsync(TimeSpan.FromSeconds(10));
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

    [Fact(Timeout = 30000)]
    public async Task RetryNever_StopsAnnouncingAndReportsDisabled()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://not-a-tracker.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        tracker.TriggerResult(false, new AnnounceResponse { RetryHint = TrackerRetryHint.NeverRetry }, "Not a tracker");
        int announcesWhenDisabled = tracker.AnnounceCount;

        // A day of timer ticks must not produce another announce.
        _timeProvider.Advance(TimeSpan.FromDays(1));

        Assert.Equal(announcesWhenDisabled, tracker.AnnounceCount);
        Assert.Equal(TrackerStatusType.Disabled, manager.GetTrackers().Single().Status);
    }

    [Fact(Timeout = 30000)]
    public async Task RetryNever_IgnoresManualAnnounce()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://not-a-tracker.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        tracker.TriggerResult(false, new AnnounceResponse { RetryHint = TrackerRetryHint.NeverRetry }, "Not a tracker");
        int announcesWhenDisabled = tracker.AnnounceCount;

        await manager.AnnounceAsync();

        Assert.Equal(announcesWhenDisabled, tracker.AnnounceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task RetryNever_SkipsStoppedAnnounceOnStop()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://not-a-tracker.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        tracker.TriggerResult(false, new AnnounceResponse { RetryHint = TrackerRetryHint.NeverRetry }, "Not a tracker");
        int announcesWhenDisabled = tracker.AnnounceCount;

        await manager.StopAsync();

        // "Never send this query again" includes the courtesy Stopped announce.
        Assert.Equal(announcesWhenDisabled, tracker.AnnounceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task RetryNever_LetsTheNextTierTakeOver()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        manager.AddTrackers(
        [
            new[] { "http://tier0.example/announce" },
            ["http://tier1.example/announce"]
        ]);

        await manager.StartAsync();
        var disabledTracker = _factory.Trackers["http://tier0.example/announce"];
        var fallbackTracker = _factory.Trackers["http://tier1.example/announce"];
        await disabledTracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        // One "never" is enough to exhaust the tier - waiting for three failures would never happen.
        disabledTracker.TriggerResult(false, new AnnounceResponse { RetryHint = TrackerRetryHint.NeverRetry }, "Not a tracker");

        await fallbackTracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fallbackTracker.AnnounceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task RetryIn_DelaysTheNextAnnounceBeyondTheDefaultRetry()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://busy.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        // A single failure would normally be retried after one minute.
        tracker.TriggerResult(false, new AnnounceResponse { RetryHint = TrackerRetryHint.After(TimeSpan.FromMinutes(20)) }, "Rate limited");
        int announcesWhenFailed = tracker.AnnounceCount;

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(announcesWhenFailed, tracker.AnnounceCount);

        _timeProvider.Advance(TimeSpan.FromMinutes(16));
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(announcesWhenFailed + 1, tracker.AnnounceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task RetryIn_CannotShortenOurOwnBackoff()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://fail.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        // Open the circuit, then have the tracker ask to be hammered again in one minute.
        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(false, new AnnounceResponse());
        tracker.TriggerResult(false, new AnnounceResponse(), "third failure");
        Assert.Equal(TrackerStatusType.CircuitOpen, manager.GetTrackers().Single().Status);

        tracker.TriggerResult(false, new AnnounceResponse { RetryHint = TrackerRetryHint.After(TimeSpan.FromMinutes(1)) }, "come back soon");
        int announcesWhenFailed = tracker.AnnounceCount;

        // The fourth failure opened the circuit again, so the backoff is already past one minute.
        _timeProvider.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(announcesWhenFailed, tracker.AnnounceCount);
    }

    [Fact(Timeout = 30000)]
    public async Task RetryIn_IsClampedToTwentyFourHours()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://hostile.example/announce";
        manager.AddTracker(url);
        await manager.StartAsync();
        var tracker = _factory.Trackers[url];
        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));

        // An unbounded hint would silence this tracker for the life of the process.
        tracker.TriggerResult(false, new AnnounceResponse { RetryHint = TrackerRetryHint.After(TimeSpan.FromDays(400)) }, "go away");
        int announcesWhenFailed = tracker.AnnounceCount;

        _timeProvider.Advance(TimeSpan.FromHours(25));

        await tracker.WaitAnnounceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(announcesWhenFailed + 1, tracker.AnnounceCount);
    }

    [Fact]
    public void ExternalIp_FromTracker_IsReportedToTheDht()
    {
        var dht = new RecordingDhtManager();
        _torrent.DhtManager = dht;
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        tracker.TriggerResult(true, new AnnounceResponse { ExternalIp = IPAddress.Parse("203.0.113.7") });

        Assert.Equal([IPAddress.Parse("203.0.113.7")], dht.Reports);
    }

    [Fact]
    public void ExternalIp_RepeatedFromSameTracker_VotesOnlyOnce()
    {
        // Otherwise a single tracker announcing on its interval would reach the BEP 42 vote
        // threshold on its own and drive a node ID regeneration.
        var dht = new RecordingDhtManager();
        _torrent.DhtManager = dht;
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        var address = IPAddress.Parse("203.0.113.7");
        tracker.TriggerResult(true, new AnnounceResponse { ExternalIp = address });
        tracker.TriggerResult(true, new AnnounceResponse { ExternalIp = address });
        tracker.TriggerResult(true, new AnnounceResponse { ExternalIp = address });

        Assert.Single(dht.Reports);
    }

    [Fact]
    public void ExternalIp_ChangedValue_IsReportedAgain()
    {
        var dht = new RecordingDhtManager();
        _torrent.DhtManager = dht;
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        tracker.TriggerResult(true, new AnnounceResponse { ExternalIp = IPAddress.Parse("203.0.113.7") });
        tracker.TriggerResult(true, new AnnounceResponse { ExternalIp = IPAddress.Parse("198.51.100.4") });

        Assert.Equal(
            [IPAddress.Parse("203.0.113.7"), IPAddress.Parse("198.51.100.4")],
            dht.Reports);
    }

    [Fact]
    public void ExternalIp_AbsentFromResponse_IsNotReported()
    {
        var dht = new RecordingDhtManager();
        _torrent.DhtManager = dht;
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        tracker.TriggerResult(true, new AnnounceResponse());

        Assert.Empty(dht.Reports);
    }

    [Fact]
    public void ExternalIp_WhenDhtReportThrows_AnnounceStillSucceeds()
    {
        var dht = new RecordingDhtManager { ThrowOnReport = true };
        _torrent.DhtManager = dht;
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        tracker.TriggerResult(true, new AnnounceResponse
        {
            ExternalIp = IPAddress.Parse("203.0.113.7"),
            SeedCount = 9
        });

        var status = manager.GetTrackers().Single();
        Assert.Equal(TrackerStatusType.Working, status.Status);
        Assert.Equal(9u, status.SeedCount);
    }

    [Fact]
    public void ExternalIp_WithoutDht_IsIgnored()
    {
        var manager = new TrackerManager(_torrent, _factory, _timeProvider);
        const string url = "http://tracker.example/announce";
        manager.AddTracker(url);
        var tracker = _factory.Trackers[url];

        tracker.TriggerResult(true, new AnnounceResponse { ExternalIp = IPAddress.Parse("203.0.113.7") });

        Assert.Equal(TrackerStatusType.Working, manager.GetTrackers().Single().Status);
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





