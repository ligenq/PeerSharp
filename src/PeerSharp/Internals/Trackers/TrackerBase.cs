namespace PeerSharp.Internals.Trackers;

internal abstract class TrackerBase : ITracker
{
    private ITrackerCallback? _callback;
    private Torrent? _torrent;

    /// <summary>
    /// Returns true if Init() has been called.
    /// </summary>
    public bool IsInitialized => _torrent != null;

    /// <summary>
    /// The torrent associated with this tracker. Throws if accessed before Init() is called.
    /// </summary>
    public Torrent Torrent => _torrent ?? throw new InvalidOperationException("Tracker not initialized. Call Init() first.");

    public string Url { get; private set; } = string.Empty;

    public abstract Task AnnounceAsync(TrackerEvent evt, CancellationToken ct);

    public abstract void Deinit();

    public virtual void Init(string url, Torrent torrent, ITrackerCallback callback)
    {
        Url = url ?? throw new ArgumentNullException(nameof(url));
        _torrent = torrent ?? throw new ArgumentNullException(nameof(torrent));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public abstract Task ScrapeAsync(CancellationToken ct);

    public abstract Task MultiScrapeAsync(IReadOnlyList<InfoHash> infoHashes, CancellationToken ct);

    protected void RaiseAnnounceResult(bool success, AnnounceResponse response, string? errorMessage = null)
    {
        _callback?.OnAnnounceResult(success, response, this, errorMessage);
    }

    protected void RaiseScrapeResult(bool success, ScrapeResponse response)
    {
        _callback?.OnScrapeResult(success, response, this);
    }

    protected void RaiseMultiScrapeResult(bool success, MultiScrapeResponse response)
    {
        _callback?.OnMultiScrapeResult(success, response, this);
    }
}
