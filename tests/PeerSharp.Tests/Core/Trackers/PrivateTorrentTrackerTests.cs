using PeerSharp.Core;
using PeerSharp.Internals;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Trackers;

/// <summary>
/// BEP 27: a private torrent announces only to the trackers named in its own metadata.
///
/// <para>
/// The library already refused to widen a private swarm through the DHT, PEX and LSD, but
/// <see cref="PeerSharp.Interfaces.ITrackers.AddTracker"/> was ungated - so a consumer wiring up a
/// public tracker list, which is exactly what the popular ones invite, would have announced a closed
/// swarm to the open internet and had the user's account banned for it.
/// </para>
///
/// <para>
/// The half of this that is easy to get wrong is the other direction: a torrent's own announce URLs
/// arrive through the same manager, so gating the lot would leave a private torrent with nowhere to
/// announce at all. Hence a test for each direction.
/// </para>
/// </summary>
public class PrivateTorrentTrackerTests
{
    private const string OwnTracker = "http://private.example/announce";
    private const string OutsideTracker = "udp://tracker.opentrackr.org:1337/announce";

    /// <summary>Enough of a tracker to be registered; none of these tests announce.</summary>
    private sealed class StubTracker : ITracker
    {
        public void Init(string url, Torrent torrent, ITrackerCallback callback) { }

        public void Deinit() { }

        public Task AnnounceAsync(TrackerEvent evt = TrackerEvent.None, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ScrapeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StubTrackerFactory : ITrackerFactory
    {
        public ITracker? CreateTracker(string url, TimeProvider timeProvider) => new StubTracker();
    }

    private static Torrent CreateTorrent(bool isPrivate)
    {
        var metadata = new TorrentFileMetadata { Announce = OwnTracker };
        metadata.Info.IsPrivate = isPrivate;
        return TorrentTestUtility.CreateMinimal(metadata, trackerFactory: new StubTrackerFactory());
    }

    [Fact]
    public void APrivateTorrentStillAnnouncesToItsOwnTracker()
    {
        var torrent = CreateTorrent(isPrivate: true);

        Assert.Contains(torrent.Trackers.GetTrackers(), t => t.Url == OwnTracker);
    }

    [Fact]
    public void APrivateTorrentRefusesATrackerAddedFromOutside()
    {
        var torrent = CreateTorrent(isPrivate: true);

        torrent.Trackers.AddTracker(OutsideTracker);

        var trackers = torrent.Trackers.GetTrackers();
        Assert.DoesNotContain(trackers, t => t.Url == OutsideTracker);
        Assert.Contains(trackers, t => t.Url == OwnTracker);
    }

    [Fact]
    public void APublicTorrentAcceptsATrackerAddedFromOutside()
    {
        var torrent = CreateTorrent(isPrivate: false);

        torrent.Trackers.AddTracker(OutsideTracker);

        Assert.Contains(torrent.Trackers.GetTrackers(), t => t.Url == OutsideTracker);
    }
}
