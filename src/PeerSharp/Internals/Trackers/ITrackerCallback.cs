namespace PeerSharp.Internals.Trackers;

internal interface ITrackerCallback
{
    void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage = null,
        CancellationToken ct = default);

    void OnScrapeResult(bool success, ScrapeResponse response, ITracker tracker);

    void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker);
}
