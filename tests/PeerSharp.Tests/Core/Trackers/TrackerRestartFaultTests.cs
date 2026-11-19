using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Trackers;

public sealed class TrackerRestartFaultTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly FieldInfo KnownPeersField = typeof(PeerManager).GetField("_knownPeersCache",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task AnOldAnnounceCannotChangeTheRestartedTracker(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var clock = new FakeTimeProvider();
        await using var torrent = TorrentTestUtility.CreateMinimal(timeProvider: clock);
        int freshInterval = scenario.Random.Next(300, 601);
        var client = new ScriptedClient(freshInterval, lateFailure: (seed & 2) != 0);
        await using var manager = new TrackerManager(torrent, new ScriptedFactory(client), clock);
        manager.AddTracker("http://tracker.test/announce");
        var knownPeers = (ConcurrentDictionary<IPEndPoint, PeerHistory>)KnownPeersField.GetValue(torrent.PeersInternal)!;
        var stalePeer = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 6881);
        var freshPeer = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 6881);
        var probePeer = new IPEndPoint(IPAddress.Parse("203.0.113.3"), 6881);
        try
        {
            scenario.Record("hold the old announce at EOF and let the bounded stop time out");
            await manager.StartAsync();
            await client.Stale.AtEnd.Task.WaitAsync(Timeout, ct);
            await TorrentTestUtility.AdvanceUntilCompleteAsync(clock, manager.StopAsync(), TimeSpan.FromSeconds(3));
            Assert.True(client.Stale.Cancellation.IsCancellationRequested);
            Assert.False(client.Stale.Closed.Task.IsCompleted);

            await manager.StartAsync();
            await client.Fresh.AtEnd.Task.WaitAsync(Timeout, ct);
            bool staleFirst = (seed & 1) == 0;
            scenario.Record($"deliver the old {(client.LateFailure ? "failure" : "success")} {(staleFirst ? "before" : "after")} the fresh success");
            if (staleFirst)
            {
                await ReleaseAsync(client.Stale, ct);
                Assert.Equal(TrackerStatusType.Unknown, Assert.Single(manager.GetTrackers()).Status);
                Assert.Empty(knownPeers);
            }
            await ReleaseAsync(client.Fresh, ct);
            await TorrentTestUtility.WaitUntilAsync(() => knownPeers.ContainsKey(freshPeer),
                because: "the restarted announce to publish its peer");
            if (!staleFirst)
            {
                await ReleaseAsync(client.Stale, ct);
            }

            Assert.DoesNotContain(stalePeer, knownPeers.Keys);
            var status = Assert.Single(manager.GetTrackers());
            Assert.Equal((freshInterval, 7u, 3u, 0), (status.Interval, status.SeedCount, status.LeechCount, status.ConsecutiveFailures));
            Assert.Equal(clock.GetUtcNow().AddSeconds(freshInterval), status.NextAnnounce);
            // A result can reach the callback just as its attempt is cancelled.
            var racedResult = new AnnounceResponse { SeedCount = 100, Interval = 86400 };
            racedResult.Peers.Add(probePeer);
            manager.OnAnnounceResult(true, racedResult, client.Tracker, ct: client.Stale.Cancellation);
            Assert.Equal(status, Assert.Single(manager.GetTrackers()));
            Assert.DoesNotContain(probePeer, knownPeers.Keys);
            // Drive the first timer after the restart and check the tracker ID it sends.
            clock.Advance(TimeSpan.FromSeconds(freshInterval));
            string followUp = await client.FollowUp.Task.WaitAsync(Timeout, ct);
            Assert.Contains("trackerid=fresh", followUp, StringComparison.Ordinal);
        }
        finally
        {
            client.Stale.Release.TrySetResult();
            client.Fresh.Release.TrySetResult();
        }
    }

    private static async Task ReleaseAsync(DelayedEof stream, CancellationToken ct)
    {
        stream.Release.TrySetResult();
        await stream.Finished.Task.WaitAsync(Timeout, ct);
    }

    private sealed class ScriptedFactory(ScriptedClient client) : ITrackerFactory
    {
        public ITracker CreateTracker(string url, TimeProvider timeProvider)
        {
            client.Tracker.SetTestClient(client);
            return client.Tracker;
        }
    }

    private sealed class ObservedTracker(ScriptedClient client)
        : HttpTracker(NullLoggerFactory.Instance, new HttpClientFactory())
    {
        private int _starts;

        public override async Task AnnounceAsync(TrackerEvent evt, CancellationToken ct)
        {
            int start = evt == TrackerEvent.Started ? Interlocked.Increment(ref _starts) : 0;
            try
            {
                await base.AnnounceAsync(evt, ct);
            }
            finally
            {
                if (start == 1) client.Stale.Finished.TrySetResult();
                if (start == 2) client.Fresh.Finished.TrySetResult();
            }
        }
    }

    private sealed class ScriptedClient : IHttpClient
    {
        private int _attempt;

        public ScriptedClient(int freshInterval, bool lateFailure)
        {
            Tracker = new ObservedTracker(this);
            LateFailure = lateFailure;
            Stale = new DelayedEof(lateFailure ? Encoding.ASCII.GetBytes("d14:failure reason4:latee")
                : Response("stale", 86400, 99, 88, 1));
            Fresh = new DelayedEof(Response("fresh", freshInterval, 7, 3, 2));
        }

        public HttpTracker Tracker { get; }
        public bool LateFailure { get; }
        public DelayedEof Stale { get; }
        public DelayedEof Fresh { get; }
        public TaskCompletionSource<string> FollowUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            string query = request.RequestUri!.Query;
            if (query.Contains("event=stopped", StringComparison.Ordinal))
            {
                return Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
            }

            int attempt = Interlocked.Increment(ref _attempt);
            if (attempt >= 3)
            {
                FollowUp.TrySetResult(query);
                return Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(attempt == 1 ? Stale : Fresh)
            });
        }

        private static byte[] Response(string id, int interval, int seeds, int leeches, byte peer)
        {
            byte[] prefix = Encoding.ASCII.GetBytes($"d8:completei{seeds}e10:incompletei{leeches}e8:intervali{interval}e5:peers6:");
            byte[] suffix = Encoding.ASCII.GetBytes($"10:tracker id{id.Length}:{id}e");
            return [.. prefix, 203, 0, 113, peer, 26, 225, .. suffix];
        }
    }

    private sealed class DelayedEof(byte[] body) : MemoryStream(body)
    {
        public TaskCompletionSource AtEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Cancellation { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (read != 0) return read;
            Cancellation = cancellationToken;
            AtEnd.TrySetResult();
            // Simulate a transport handing back its final read after cancellation was requested.
            await Release.Task;
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Closed.TrySetResult();
        }
    }
}
