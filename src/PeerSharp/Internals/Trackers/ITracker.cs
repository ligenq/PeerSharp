namespace PeerSharp.Internals.Trackers;

internal interface ITracker
{
    Task AnnounceAsync(TrackerEvent evt, CancellationToken ct);

    void Deinit();

    void Init(string url, Torrent torrent, ITrackerCallback callback);

    Task ScrapeAsync(CancellationToken ct);

    Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct);
}
