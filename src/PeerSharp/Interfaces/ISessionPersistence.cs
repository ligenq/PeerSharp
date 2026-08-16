namespace PeerSharp.Interfaces;

/// <summary>
/// Represents a saved torrent entry in the session.
/// </summary>
/// <param name="Hash">The info hash identifying this torrent.</param>
/// <param name="TorrentFileData">The .torrent file data, or null for magnet-only entries.</param>
/// <param name="MagnetLink">The magnet link string, or null if added via .torrent file.</param>
/// <param name="ResumeData">The resume data for fast resume, or null if not available.</param>
/// <param name="Options">Options that were used when adding this torrent.</param>
public sealed record SavedTorrentEntry(
    InfoHash Hash,
    byte[]? TorrentFileData = null,
    string? MagnetLink = null,
    TorrentResumeData? ResumeData = null,
    SavedTorrentOptions? Options = null);

/// <summary>
/// Saved torrent options that should be restored.
/// </summary>
/// <param name="DownloadPath">The download path.</param>
/// <param name="WasStarted">Whether the torrent was running when saved.</param>
/// <param name="DownloadLimitBytesPerSecond">Download speed limit in bytes per second.</param>
/// <param name="UploadLimitBytesPerSecond">Upload speed limit in bytes per second.</param>
/// <param name="QueuePriority">Queue priority.</param>
/// <param name="RatioLimit">Ratio limit for auto-stop.</param>
/// <param name="SeedTimeLimit">Seed time limit for auto-stop.</param>
/// <param name="DownloadStrategy">Download strategy.</param>
public sealed record SavedTorrentOptions(
    string? DownloadPath = null,
    bool WasStarted = false,
    long DownloadLimitBytesPerSecond = 0,
    long UploadLimitBytesPerSecond = 0,
    int QueuePriority = 0,
    float? RatioLimit = null,
    TimeSpan? SeedTimeLimit = null,
    DownloadStrategy DownloadStrategy = DownloadStrategy.RarestFirst)
{
    /// <summary>
    /// Learned connection preferences for peer endpoints. Only preferences which differ from the
    /// engine defaults need to be stored.
    /// </summary>
    public IReadOnlyList<SavedPeerPreference>? PeerPreferences { get; init; }
}

/// <summary>
/// Connection preferences learned for a peer endpoint and retained with the torrent session.
/// </summary>
/// <param name="Address">The numeric IPv4 or IPv6 address.</param>
/// <param name="Port">The peer's listening port.</param>
/// <param name="UtpSupported">Whether future connection attempts should try uTP.</param>
/// <param name="OfferEncryptionNext">Whether the next handshake should offer encryption.</param>
public sealed record SavedPeerPreference(
    string Address,
    int Port,
    bool UtpSupported = true,
    bool OfferEncryptionNext = true);

/// <summary>
/// Interface for session persistence operations.
/// Implementations handle saving and loading torrent state to/from storage.
/// </summary>
public interface ISessionPersistence
{
    /// <summary>
    /// Deletes a torrent entry from storage.
    /// </summary>
    /// <param name="hash">The info hash of the torrent to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all saved torrent entries from storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of saved torrent entries.</returns>
    Task<IReadOnlyList<SavedTorrentEntry>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves all current torrent entries to storage.
    /// </summary>
    /// <param name="entries">The entries to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a torrent entry to storage.
    /// </summary>
    /// <param name="entry">The torrent entry to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the DHT state to storage.
    /// </summary>
    /// <param name="state">The DHT state to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the DHT state from storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded DHT state, or null if not found.</returns>
    Task<DhtState?> LoadDhtStateAsync(CancellationToken cancellationToken = default);
}
