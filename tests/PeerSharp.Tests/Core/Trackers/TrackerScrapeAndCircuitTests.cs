using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Trackers;

/// <summary>
/// Two tracker paths that nothing exercised: what a scrape result does with the numbers it brings
/// back, and the circuit breaker forgiving a tracker that has recovered.
/// </summary>
/// <remarks>
/// <para>
/// A scrape is how the swarm size shown to the caller gets its numbers, and it is the one tracker
/// callback with no announce behind it - a tracker can answer a scrape while announces are failing,
/// and the other way round.
/// </para>
/// <para>
/// The circuit breaker matters more. It stops the engine hammering a tracker that is down, but a
/// tracker that comes back has to be forgiven completely: the backoff is exponential in how many
/// times the circuit has opened, so a count that is never cleared means a tracker that failed a year
/// ago is still being waited on for an hour at a time.
/// </para>
/// </remarks>
public class TrackerScrapeAndCircuitTests
{
    private const string Url = "http://tracker.example/announce";

    [Fact(Timeout = 30_000)]
    public async Task AScrapeResultUpdatesTheSwarmCounts()
    {
        var fixture = await Fixture.CreateAsync();

        fixture.Manager.OnScrapeResult(
            success: true,
            new ScrapeResponse { SeedCount = 42, LeechCount = 7, Downloaded = 900 },
            fixture.Tracker);

        var status = Assert.Single(fixture.Manager.GetTrackers());
        Assert.Equal(42u, status.SeedCount);
        Assert.Equal(7u, status.LeechCount);

        await fixture.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailedScrapeLeavesTheLastKnownCountsAlone()
    {
        // A tracker that answers once and then stops is better described by its last real numbers
        // than by zeroes, which would read as an empty swarm.
        var fixture = await Fixture.CreateAsync();

        fixture.Manager.OnScrapeResult(true, new ScrapeResponse { SeedCount = 12, LeechCount = 3 }, fixture.Tracker);
        fixture.Manager.OnScrapeResult(false, new ScrapeResponse { SeedCount = 0, LeechCount = 0 }, fixture.Tracker);

        var status = Assert.Single(fixture.Manager.GetTrackers());
        Assert.Equal(12u, status.SeedCount);
        Assert.Equal(3u, status.LeechCount);

        await fixture.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task AScrapeFromATrackerThatIsNoLongerHeldIsIgnored()
    {
        // Scrapes are answered asynchronously, so one can arrive after the tracker was removed. It
        // has nowhere to go and must not throw its way back into the tracker's own callback.
        var fixture = await Fixture.CreateAsync();
        var tracker = fixture.Tracker;

        Assert.True(fixture.Manager.RemoveTracker(Url));

        fixture.Manager.OnScrapeResult(true, new ScrapeResponse { SeedCount = 5, LeechCount = 5 }, tracker);

        Assert.Empty(fixture.Manager.GetTrackers());

        await fixture.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task ATrackerThatRecoversDoesNotInheritItsOldBackoff()
    {
        // Opening the circuit is the easy half; this is the other. The wait before a retry is
        // exponential in how many times the circuit has opened, so unless that count is cleared on
        // recovery, a tracker that had a bad hour last week starts its next outage backed off for
        // an hour - and eventually for the maximum, permanently.
        //
        // Nothing else observable distinguishes this: closing the circuit already resets the failure
        // count and the state, so only the length of the next backoff shows whether the history was
        // really forgotten.
        var fixture = await Fixture.CreateAsync();
        var now = fixture.Time.GetUtcNow();

        TimeSpan FirstBackoff()
        {
            for (int i = 0; i < 3; i++)
            {
                fixture.Manager.OnAnnounceResult(false, new AnnounceResponse(), fixture.Tracker, "down");
            }

            var open = Assert.Single(fixture.Manager.GetTrackers());
            Assert.Equal(TrackerStatusType.CircuitOpen, open.Status);
            return open.NextAnnounce - now;
        }

        TimeSpan initial = FirstBackoff();
        Assert.True(initial > TimeSpan.Zero, "the circuit opened without scheduling a retry");

        // Five consecutive successes is the documented reset threshold.
        for (int i = 0; i < 5; i++)
        {
            fixture.Manager.OnAnnounceResult(true, new AnnounceResponse { Interval = 900 }, fixture.Tracker);
        }

        Assert.Equal(TrackerStatusType.Working, Assert.Single(fixture.Manager.GetTrackers()).Status);
        Assert.Equal(0, Assert.Single(fixture.Manager.GetTrackers()).ConsecutiveFailures);

        TimeSpan afterRecovery = FirstBackoff();

        Assert.Equal(initial, afterRecovery);

        await fixture.DisposeAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(Torrent torrent, TrackerManager manager, ITracker tracker, FakeTimeProvider time)
        {
            Torrent = torrent;
            Manager = manager;
            Tracker = tracker;
            Time = time;
        }

        public TrackerManager Manager { get; }
        public ITracker Tracker { get; }
        public FakeTimeProvider Time { get; }
        private Torrent Torrent { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var torrent = TorrentTestUtility.CreateMinimal();
            var factory = new RecordingTrackerFactory();
            var time = new FakeTimeProvider();
            var manager = new TrackerManager(torrent, factory, time);

            manager.AddTracker(Url);
            await Task.CompletedTask;

            return new Fixture(torrent, manager, factory.Trackers[Url], time);
        }

        public async ValueTask DisposeAsync()
        {
            await Torrent.DisposeAsync();
        }
    }

    private sealed class RecordingTrackerFactory : ITrackerFactory
    {
        public Dictionary<string, StubTracker> Trackers { get; } = [];

        public ITracker? CreateTracker(string url, TimeProvider timeProvider)
        {
            var tracker = new StubTracker();
            Trackers[url] = tracker;
            return tracker;
        }
    }

    /// <summary>A tracker that goes through the motions so the manager will hold on to it.</summary>
    private sealed class StubTracker : ITracker
    {
        public Task AnnounceAsync(TrackerEvent evt, CancellationToken ct) => Task.CompletedTask;

        public void Deinit()
        {
        }

        public void Init(string url, Torrent torrent, ITrackerCallback callback)
        {
        }

        public Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct) => Task.CompletedTask;

        public Task ScrapeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
