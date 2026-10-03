namespace PeerSharp.Config;

/// <summary>
/// Options for adding a torrent to the client.
/// </summary>
/// <remarks>Options and their collections are copied when an add begins. Later changes apply only to subsequent adds.</remarks>
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
    /// Gets or sets peer endpoints to seed into discovery when the torrent is added. This is useful
    /// when a magnet was previewed as a <see cref="TorrentFile"/> and its BEP 9 <c>x.pe</c> hints need
    /// to be carried into the subsequent torrent-file add.
    /// </summary>
    public IReadOnlyList<System.Net.IPEndPoint>? AdditionalPeers { get; set; }

    /// <summary>
    /// Gets or sets the download bandwidth limit in bytes per second.
    /// If null, uses the global limit. Negative values are rejected when the torrent is added.
    /// </summary>
    public long? DownloadLimitBytesPerSecond { get; set; }

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
    /// Gets or sets the initial file selection/priority settings: one entry per file, in the torrent's
    /// file order. If null, all files are selected with normal priority.
    /// </summary>
    /// <remarks>
    /// Applied before the torrent starts, so nothing deselected is requested - not even by web seeds,
    /// which fetch from the moment a torrent starts - and over any selection in
    /// <see cref="ResumeData"/> or a magnet link's "so=" restriction. For a torrent file, a list whose
    /// length differs from the file count is an <see cref="ArgumentException"/>; for a magnet link it is
    /// applied once the metadata arrives, and ignored with a warning if the counts differ then.
    /// </remarks>
    public IReadOnlyList<FileSelection>? FileSelections { get; set; }

    /// <summary>
    /// Gets or sets the queue priority for this torrent.
    /// Higher values are started before lower values. Default is 0.
    /// </summary>
    public int QueuePriority { get; set; }

    /// <summary>
    /// Gets or sets the seeding ratio limit for auto-stop.
    /// If null, no ratio-based auto-stop is applied.
    /// Non-null values must be finite and non-negative.
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
    /// Negative values are rejected when the torrent is added.
    /// </summary>
    public TimeSpan? SeedTimeLimit { get; set; }

    /// <summary>
    /// Gets or sets whether to start the torrent immediately after adding.
    /// Default is true.
    /// </summary>
    public bool StartImmediately { get; set; } = true;

    /// <summary>
    /// Gets or sets extra BEP 19 web seed URLs to use alongside any the torrent's metadata declares.
    /// </summary>
    public IReadOnlyList<string>? AdditionalWebSeeds { get; set; }

    /// <summary>
    /// Gets or sets whether the torrent seeds in BEP 16 super-seed mode. Default is false.
    /// </summary>
    /// <remarks>
    /// For introducing content to an empty swarm from a single seed. See
    /// <see cref="Interfaces.ITorrent.SuperSeeding"/> for when it helps and when it hurts.
    /// </remarks>
    public bool SuperSeeding { get; set; }

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
    /// If null, uses the global limit. Negative values are rejected when the torrent is added.
    /// </summary>
    public long? UploadLimitBytesPerSecond { get; set; }

    internal AddTorrentOptions Snapshot()
    {
        var snapshot = (AddTorrentOptions)MemberwiseClone();
        snapshot.AdditionalTrackers = snapshot.AdditionalTrackers?.ToArray();
        snapshot.AdditionalWebSeeds = snapshot.AdditionalWebSeeds?.ToArray();
        snapshot.AdditionalPeers = snapshot.AdditionalPeers?.Select(peer => new System.Net.IPEndPoint(
            peer.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? new System.Net.IPAddress(peer.Address.GetAddressBytes(), peer.Address.ScopeId)
                : new System.Net.IPAddress(peer.Address.GetAddressBytes()), peer.Port)).ToArray();
        snapshot.FileSelections = snapshot.FileSelections?.ToArray();
        if (snapshot.ResumeData is { } resume)
        {
            snapshot.ResumeData = new TorrentResumeData { Hash = resume.Hash, Timestamp = resume.Timestamp, Data = resume.Data.ToArray() };
        }
        TorrentOptionValidation.ValidateStrategy(snapshot.DownloadStrategy);
        TorrentOptionValidation.ValidateRatio(snapshot.RatioLimit);
        TorrentOptionValidation.ValidateSeedTime(snapshot.SeedTimeLimit);
        if (snapshot.DownloadLimitBytesPerSecond is { } download) ArgumentOutOfRangeException.ThrowIfNegative(download);
        if (snapshot.UploadLimitBytesPerSecond is { } upload) ArgumentOutOfRangeException.ThrowIfNegative(upload);
        if (snapshot.FileSelections is { } selections)
        {
            foreach (var selection in selections)
            {
                ArgumentNullException.ThrowIfNull(selection);
                if (!Enum.IsDefined(selection.Priority)) throw new ArgumentException("File selection contains an invalid priority.");
            }
        }
        return snapshot;
    }
}

