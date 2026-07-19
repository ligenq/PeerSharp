namespace PeerSharp.Config;

/// <summary>
/// Options for adding a torrent to the client.
/// </summary>
public sealed class AddTorrentOptions
{
    /// <summary>
    /// Creates a new instance with default options.
    /// </summary>
    public AddTorrentOptions()
    {
    }

    /// <summary>
    /// Creates options with the specified download path.
    /// </summary>
    /// <param name="downloadPath">The download directory path.</param>
    public AddTorrentOptions(string downloadPath)
    {
        DownloadPath = downloadPath;
    }

    /// <summary>
    /// Creates a new default options instance (start immediately, use default paths, rarest-first strategy).
    /// </summary>
    /// <remarks>
    /// Returns a new instance each call to prevent shared mutable state.
    /// </remarks>
    public static AddTorrentOptions Default => new();

    /// <summary>
    /// Gets or sets additional tracker URLs to use.
    /// These are added to any trackers in the torrent/magnet.
    /// </summary>
    public IReadOnlyList<string>? AdditionalTrackers { get; set; }

    /// <summary>
    /// Gets or sets the download bandwidth limit in bytes per second.
    /// If null, uses the global limit.
    /// </summary>
    public int? DownloadLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the download directory path.
    /// If null, uses the default directory from settings.
    /// </summary>
    public string? DownloadPath { get; set; }

    /// <summary>
    /// Gets or sets the download strategy for piece selection.
    /// Default is RarestFirst.
    /// </summary>
    public DownloadStrategy DownloadStrategy { get; set; } = DownloadStrategy.RarestFirst;

    /// <summary>
    /// Gets or sets the event handler for torrent progress notifications.
    /// If set, the handler will receive callbacks when progress changes,
    /// pieces complete, errors occur, etc.
    /// </summary>
    public ITorrentEvents? Events { get; set; }

    /// <summary>
    /// Gets or sets the initial file selection/priority settings.
    /// If null, all files are selected with normal priority.
    /// </summary>
    public IReadOnlyList<FileSelection>? FileSelections { get; set; }

    /// <summary>
    /// Gets or sets the queue priority for this torrent.
    /// Higher values are started before lower values. Default is 0.
    /// </summary>
    public int QueuePriority { get; set; }

    /// <summary>
    /// Gets or sets the seeding ratio limit for auto-stop.
    /// If null, no ratio-based auto-stop is applied.
    /// </summary>
    public float? RatioLimit { get; set; }

    /// <summary>
    /// Gets or sets the initial resume data for the torrent.
    /// If provided, the torrent will use this state to avoid a full file recheck.
    /// </summary>
    public TorrentResumeData? ResumeData { get; set; }

    /// <summary>
    /// Gets or sets the seeding time limit for auto-stop.
    /// If null, no time-based auto-stop is applied.
    /// </summary>
    public TimeSpan? SeedTimeLimit { get; set; }

    /// <summary>
    /// Gets or sets whether to start the torrent immediately after adding.
    /// Default is true.
    /// </summary>
    public bool StartImmediately { get; set; } = true;

    /// <summary>
    /// Magnet links only: when true, the torrent runs just long enough to download its
    /// metadata and is then left stopped instead of resuming into the download. This gives
    /// the application a race-free window to preview the file list and adjust selections
    /// (await <see cref="Interfaces.ITorrent.WaitForMetadataAsync"/>, then inspect files and
    /// set priorities) before calling StartAsync. Ignored for torrents added with metadata.
    /// </summary>
    public bool StopAfterMetadata { get; set; }

    /// <summary>
    /// Gets or sets the upload bandwidth limit in bytes per second.
    /// If null, uses the global limit.
    /// </summary>
    public int? UploadLimitBytesPerSecond { get; set; }
}

