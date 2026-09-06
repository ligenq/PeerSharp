using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Trackers;
using PeerSharp.Streaming;
using System.Net;

namespace PeerSharp.Tests.Core;

public class NullAlertsManagerTests
{
    [Fact]
    public void EveryPostIsSwallowedSoATransientTorrentNeverReachesTheEnginesQueue()
    {
        // The engine adds torrents on its own behalf - a metadata fetch - and nothing downstream can
        // tell one of those from a real one once an alert is queued. The distinction has to be made
        // at the source, which is this.
        var alerts = NullAlertsManager.Instance;
        var torrent = TorrentTestUtility.CreateMinimal();

        alerts.RegisterAlerts(uint.MaxValue);
        alerts.TorrentAlert(AlertId.TorrentAdded, torrent);
        alerts.MetadataAlert(AlertId.MetadataInitialized, torrent);
        alerts.MetadataProgressAlert(torrent, 0.5f, 1, 2);
        alerts.PieceCompletedAlert(torrent, 0, 1, 2);
        alerts.ProgressChangedAlert(torrent, 0.5f, 0.5f, 1, 2, 1, 2);
        alerts.StateChangedAlert(torrent, TorrentState.Stopped, TorrentState.Active);
        alerts.ConfigAlert(AlertId.ConfigChanged, "Transfer");
        alerts.ListenPortChangedAlert(6881, 6882, ListenTransport.Tcp);
        alerts.TransferStatsAlert(torrent, 1, 2, 3, 4, 5);

        Assert.Empty(alerts.PopAlerts());
        Assert.Equal(0, alerts.DroppedAlertCount);
    }

    [Fact]
    public void RegisteringAMaskChangesNothingBecauseNothingIsEverQueued()
    {
        // A caller holding this by its interface can still register, and the registration has to be
        // accepted rather than throwing - it simply has nothing to act on.
        var alerts = NullAlertsManager.Instance;

        alerts.RegisterAlerts(uint.MaxValue);
        alerts.TorrentErrorAlert(TorrentTestUtility.CreateMinimal(), new InvalidOperationException("boom"));

        Assert.Empty(alerts.PopAlerts());
    }

    [Fact]
    public void TheRemainingAlertKindsAreSwallowedToo()
    {
        var alerts = NullAlertsManager.Instance;
        var torrent = TorrentTestUtility.CreateMinimal();

        alerts.PieceHashFailedAlert(torrent, 0, 1, new IPEndPoint(IPAddress.Loopback, 6881));
        alerts.PeerBlockedAlert(torrent, new IPEndPoint(IPAddress.Loopback, 6881), PeerBlockReason.Blocklist);

        Assert.Empty(alerts.PopAlerts());
    }

    [Fact]
    public void PostAlertDirectlyIsAlsoSwallowed()
    {
        NullAlertsManager.Instance.PostAlert(new ConfigAlert
        {
            Id = AlertId.ConfigChanged,
            ConfigType = "test",
            Timestamp = DateTimeOffset.UnixEpoch
        });

        Assert.Empty(NullAlertsManager.Instance.PopAlerts());
    }

    [Fact]
    public async Task TheStreamEndsOnlyByCancellation()
    {
        // Matching the real manager's contract: nothing is ever queued here, so the only way out is
        // the token - and a caller that awaits this must see the same exception either way.
        using var cts = new CancellationTokenSource();
        var enumerator = NullAlertsManager.Instance
            .GetAlertsAsync(cancellationToken: cts.Token)
            .GetAsyncEnumerator(cts.Token);

        var moving = enumerator.MoveNextAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moving);
    }

    [Fact]
    public void ItIsASingletonSoNothingCanAccidentallyMakeASecondOne()
    {
        Assert.Same(NullAlertsManager.Instance, NullAlertsManager.Instance);
    }
}

public class TorrentWebSeedsTests
{
    [Fact]
    public void AddingAUrlPutsItInTheEffectiveList()
    {
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());

        Assert.True(seeds.Add("http://example.com/file"));
        Assert.Equal(["http://example.com/file"], seeds.GetAll());
    }

    [Fact]
    public void TheSameUrlIsNotAddedTwiceEvenInADifferentCase()
    {
        // A duplicate source would be dialled as a separate seed, doubling the requests sent to one
        // server for no extra throughput.
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());

        Assert.True(seeds.Add("http://example.com/file"));
        Assert.False(seeds.Add("http://example.com/file"));
        Assert.False(seeds.Add("HTTP://EXAMPLE.COM/FILE"));

        Assert.Single(seeds.GetAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("magnet:?xt=urn:btih:0000")]
    public void AUrlTheSeedCannotFetchIsRefused(string url)
    {
        // Accepting one would produce a source that fails on every request while taking its share
        // of the concurrency budget doing so.
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());

        Assert.False(seeds.Add(url));
        Assert.Empty(seeds.GetAll());
    }

    [Theory]
    [InlineData("http://example.com/file")]
    [InlineData("https://example.com/file")]
    [InlineData("ftp://example.com/file")]
    public void TheThreeSchemesBep19AllowsAreAccepted(string url)
    {
        // BEP 19 names HTTP and FTP, and HTTPS follows from HTTP. FTP in particular is easy to
        // exclude by accident when the fetching code only ever speaks HTTP.
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());

        Assert.True(seeds.Add(url));
        Assert.Contains(url, seeds.GetAll());
    }

    [Fact]
    public void RemovingAUrlTakesItOutAndRemovingItAgainReportsNothingToDo()
    {
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());
        seeds.Add("http://example.com/file");

        Assert.True(seeds.Remove("http://example.com/file"));
        Assert.Empty(seeds.GetAll());
        Assert.False(seeds.Remove("http://example.com/file"));
    }

    [Fact]
    public void RemovingIsCaseInsensitiveTheSameWayAddingIs()
    {
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());
        seeds.Add("http://example.com/file");

        Assert.True(seeds.Remove("HTTP://EXAMPLE.COM/FILE"));
        Assert.Empty(seeds.GetAll());
    }

    [Fact]
    public void AUrlRemovedAndAddedAgainComesBack()
    {
        // Remove records a suppression rather than deleting an entry, so adding has to lift it or
        // the seed would be gone for the life of the torrent.
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());
        seeds.Add("http://example.com/file");
        seeds.Remove("http://example.com/file");

        Assert.True(seeds.Add("http://example.com/file"));
        Assert.Single(seeds.GetAll());
    }

    [Fact]
    public void RemovingAnEmptyUrlIsRefusedRatherThanTreatedAsAMatch()
    {
        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal());

        Assert.False(seeds.Remove(""));
        Assert.False(seeds.Remove("   "));
    }

    [Fact]
    public void SeedsDeclaredInTheTorrentAppearWithoutBeingAdded()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        metadata.Info.Files.Add(new PeerSharp.Internals.TorrentFileEntry { Path = "file", Size = 16384 });
        metadata.Info.Pieces.Add(new byte[20]);
        metadata.WebSeedUrls.Add("http://declared.example/file");

        var seeds = new TorrentWebSeeds(TorrentTestUtility.CreateMinimal(metadata));

        Assert.Contains("http://declared.example/file", seeds.GetAll());

        // And one declared in the torrent can still be removed by the caller.
        Assert.True(seeds.Remove("http://declared.example/file"));
        Assert.Empty(seeds.GetAll());
    }
}

public class TrackerBaseTests
{
    [Fact]
    public void ATrackerIsNotInitialisedUntilInitIsCalled()
    {
        // Torrent throws rather than returning null, because every caller past this point assumes a
        // torrent and would otherwise fail somewhere less obvious.
        var tracker = new StubTracker();

        Assert.False(tracker.IsInitialized);
        Assert.Equal(string.Empty, tracker.Url);
        Assert.Throws<InvalidOperationException>(() => tracker.Torrent);
    }

    [Fact]
    public void InitRecordsTheUrlAndTorrentTheTrackerWillAnnounceFor()
    {
        var tracker = new StubTracker();
        var torrent = TorrentTestUtility.CreateMinimal();

        tracker.Init("http://tracker.example/announce", torrent, new StubCallback());

        Assert.True(tracker.IsInitialized);
        Assert.Equal("http://tracker.example/announce", tracker.Url);
        Assert.Same(torrent, tracker.Torrent);
    }

    [Fact]
    public void TheAnnounceResultReachesTheCallbackWithItsSuccessAndError()
    {
        // TrackerManager decides whether to retry, back off, or drop the tracker from this, so the
        // success flag and the message both have to survive the hop.
        var tracker = new StubTracker();
        var callback = new StubCallback();
        tracker.Init("http://tracker.example/announce", TorrentTestUtility.CreateMinimal(), callback);

        tracker.RaiseAnnounce(success: false, new AnnounceResponse(), "tracker said no");

        var (success, _, error) = Assert.Single(callback.Announces);
        Assert.False(success);
        Assert.Equal("tracker said no", error);
    }

    [Fact]
    public void ScrapeAndMultiScrapeResultsReachTheCallbackToo()
    {
        var tracker = new StubTracker();
        var callback = new StubCallback();
        tracker.Init("http://tracker.example/announce", TorrentTestUtility.CreateMinimal(), callback);

        tracker.RaiseScrape(true, new ScrapeResponse());
        tracker.RaiseMultiScrape(true, new MultiScrapeResponse());

        Assert.Single(callback.Scrapes);
        Assert.Single(callback.MultiScrapes);
    }

    [Fact]
    public void ReinitialisingPointsTheTrackerAtTheNewUrl()
    {
        var tracker = new StubTracker();
        var first = TorrentTestUtility.CreateMinimal();
        var second = TorrentTestUtility.CreateMinimal();

        tracker.Init("http://first.example/announce", first, new StubCallback());
        tracker.Init("http://second.example/announce", second, new StubCallback());

        Assert.Equal("http://second.example/announce", tracker.Url);
        Assert.Same(second, tracker.Torrent);
    }

    private sealed class StubTracker : TrackerBase
    {
        public override Task AnnounceAsync(TrackerEvent evt, CancellationToken ct) => Task.CompletedTask;
        public override void Deinit() { }
        public override Task ScrapeAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct) => Task.CompletedTask;

        public void RaiseAnnounce(bool success, AnnounceResponse response, string? error) =>
            RaiseAnnounceResult(success, response, error);

        public void RaiseScrape(bool success, ScrapeResponse response) => RaiseScrapeResult(success, response);

        public void RaiseMultiScrape(bool success, MultiScrapeResponse response) => RaiseMultiScrapeResult(success, response);
    }

    private sealed class StubCallback : ITrackerCallback
    {
        public List<(bool Success, AnnounceResponse Response, string? Error)> Announces { get; } = [];
        public List<(bool Success, ScrapeResponse Response)> Scrapes { get; } = [];
        public List<(bool Success, MultiScrapeResponse Response)> MultiScrapes { get; } = [];

        public void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage) =>
            Announces.Add((success, response, errorMessage));

        public void OnScrapeResult(bool success, ScrapeResponse response, ITracker tracker) =>
            Scrapes.Add((success, response));

        public void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker) =>
            MultiScrapes.Add((success, response));
    }
}
