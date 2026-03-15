namespace PeerSharp.Interfaces;

/// <summary>
/// Represents the current operational state of a torrent.
/// </summary>
public enum TorrentState
{
    /// <summary>Actively connecting to peers and downloading/uploading files.</summary>
    Active,

    /// <summary>In the process of stopping operations.</summary>
    Stopping,

    /// <summary>Stopped and not performing any operations.</summary>
    Stopped,

    /// <summary>Verifying integrity of existing downloaded files.</summary>
    CheckingFiles,

    /// <summary>Downloading torrent metadata from a magnet link.</summary>
    DownloadingMetadata,
}

/// <summary>
/// Represents a torrent being managed by the BitTorrent client.
/// Provides access to torrent state, progress, files, and peer connections.
/// </summary>
public interface ITorrent
{
    /// <summary>
    /// Gets the remaining bytes to download.
    /// </summary>
    long DataLeft { get; }

    /// <summary>
    /// Gets or sets the per-torrent download limit in bytes per second. 0 means unlimited.
    /// </summary>
    int DownloadLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the per-torrent disk read limit in bytes per second. 0 means unlimited.
    /// </summary>
    int DiskReadLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the per-torrent disk write limit in bytes per second. 0 means unlimited.
    /// </summary>
    int DiskWriteLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the download strategy for piece selection.
    /// </summary>
    DownloadStrategy DownloadStrategy { get; set; }

    /// <summary>
    /// Gets the event handler for torrent progress notifications.
    /// Set via <see cref="AddTorrentOptions.Events"/> when adding the torrent.
    /// </summary>
    ITorrentEvents? Events { get; }

    /// <summary>
    /// Gets the number of files in this torrent.
    /// </summary>
    int FileCount { get; }

    /// <summary>
    /// Gets the file management interface for this torrent.
    /// </summary>
    IFiles Files { get; }

    /// <summary>
    /// Gets the file transfer interface for this torrent.
    /// </summary>
    IFileTransfer FileTransfer { get; }

    /// <summary>
    /// Gets whether the entire torrent has been downloaded.
    /// </summary>
    bool Finished { get; }

    /// <summary>
    /// Gets the total number of bytes downloaded and verified.
    /// </summary>
    ulong FinishedBytes { get; }

    /// <summary>
    /// Gets the number of bytes downloaded for selected files only.
    /// </summary>
    ulong FinishedSelectedBytes { get; }

    /// <summary>
    /// Gets the info hash uniquely identifying this torrent.
    /// For hybrid torrents, this is the V1 hash.
    /// </summary>
    InfoHash Hash { get; }

    /// <summary>
    /// Gets the BEP 52 V2 info hash (32 bytes SHA-256).
    /// Returns InfoHash.EmptyV2 if this is a V1-only torrent.
    /// </summary>
    InfoHash HashV2 { get; }

    /// <summary>
    /// Gets whether torrent metadata is available.
    /// False for magnet links until metadata is downloaded.
    /// </summary>
    bool HasMetadata { get; }

    /// <summary>
    /// Gets whether this torrent contains streamable media files.
    /// </summary>
    bool HasStreamableFiles { get; }

    /// <summary>
    /// Gets the last exception encountered by the torrent during background operations, if any.
    /// This is cleared when the torrent is restarted.
    /// </summary>
    Exception? LastException { get; }

    /// <summary>
    /// Gets the metadata download handler for magnet links.
    /// Null if metadata is already available.
    /// </summary>
    IMetadataDownload? MetadataDownload { get; }

    /// <summary>
    /// Gets the display name of the torrent (from metadata or magnet link).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the peer management interface for this torrent.
    /// </summary>
    IPeers Peers { get; }

    /// <summary>
    /// Gets the total number of pieces.
    /// </summary>
    int PieceCount { get; }

    /// <summary>
    /// Gets the piece size in bytes.
    /// </summary>
    uint PieceSize { get; }

    /// <summary>
    /// Gets the number of pieces that have been downloaded and verified.
    /// </summary>
    int PiecesReceived { get; }

    /// <summary>
    /// Gets the overall download progress as a value from 0.0 to 1.0.
    /// </summary>
    float Progress { get; }

    /// <summary>
    /// Gets or sets whether this torrent is eligible for auto-start.
    /// </summary>
    bool QueueAutoStart { get; set; }

    /// <summary>
    /// Gets or sets the queue priority for auto-start ordering.
    /// Higher values are started first.
    /// </summary>
    int QueuePriority { get; set; }

    /// <summary>
    /// Gets or sets the ratio limit for auto-stop. Null disables ratio auto-stop.
    /// </summary>
    float? RatioLimit { get; set; }

    /// <summary>
    /// Gets or sets the seed time limit for auto-stop. Null disables time-based auto-stop.
    /// </summary>
    TimeSpan? SeedTimeLimit { get; set; }

    /// <summary>
    /// Gets whether all selected files have been downloaded.
    /// </summary>
    bool SelectionFinished { get; }

    /// <summary>
    /// Gets the download progress of selected files only (0.0 to 1.0).
    /// </summary>
    float SelectionProgress { get; }

    /// <summary>
    /// Gets whether the torrent is currently started (active or stopping).
    /// </summary>
    bool Started { get; }

    /// <summary>
    /// Gets the current operational state of the torrent.
    /// </summary>
    TorrentState State { get; }

    /// <summary>
    /// Gets the timestamp when the current state was entered.
    /// </summary>
    DateTimeOffset StateTimestamp { get; }

    /// <summary>
    /// Gets the indices of files that can be streamed (video/audio files).
    /// </summary>
    IReadOnlyList<int> StreamableFileIndices { get; }

    /// <summary>
    /// Gets the timestamp when this torrent was added to the client.
    /// </summary>
    DateTimeOffset TimeAdded { get; }

    /// <summary>
    /// Gets the total size of all files in the torrent in bytes.
    /// </summary>
    long TotalSize { get; }

    /// <summary>
    /// Gets the tracker management interface for this torrent.
    /// </summary>
    ITrackers Trackers { get; }

    /// <summary>
    /// Gets or sets the per-torrent upload limit in bytes per second. 0 means unlimited.
    /// </summary>
    int UploadLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Forces a full recheck of all pieces against their hashes.
    /// Must be stopped before calling this method.
    /// </summary>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of valid pieces found.</returns>
    Task<int> ForceRecheckAsync(IProgress<PieceCheckProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about all files in the torrent.
    /// </summary>
    /// <returns>List of file information.</returns>
    IReadOnlyList<TorrentFileInfo> GetAllFileInfo();

    /// <summary>
    /// Gets the selection state for all files in the torrent.
    /// </summary>
    /// <returns>Read-only list of file selection states.</returns>
    IReadOnlyList<FileSelection> GetAllFileSelections();

    /// <summary>
    /// Gets information about a specific file by index.
    /// </summary>
    /// <param name="fileIndex">Zero-based index of the file.</param>
    /// <returns>File information including path and size.</returns>
    TorrentFileInfo GetFileInfo(int fileIndex);

    /// <summary>
    /// Gets the selection state for a specific file.
    /// </summary>
    /// <param name="fileIndex">Zero-based index of the file.</param>
    /// <returns>The file's selection state including priority.</returns>
    FileSelection GetFileSelection(int fileIndex);

    /// <summary>
    /// Gets a bitfield representing the verified pieces the local client has.
    /// Each bit corresponds to a piece index (most significant bit of first byte is piece 0).
    /// </summary>
    /// <returns>A byte array bitfield.</returns>
    byte[] GetPieceBitfield();

    /// <summary>
    /// Captures the current torrent state into resume data.
    /// This can be used to restart the torrent later without a full file recheck.
    /// </summary>
    /// <returns>A resume data object containing the current state.</returns>
    TorrentResumeData GetResumeData();

    /// <summary>
    /// Opens a readable, seekable stream for a specific file in the torrent.
    /// The stream handles buffering and piece prioritization automatically.
    /// </summary>
    /// <param name="fileIndex">The index of the file to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A Stream object for the file.</returns>
    Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the download priority for all files in the torrent.
    /// </summary>
    /// <param name="priority">The priority level to apply to all files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the priority update is finished.</returns>
    Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the download path for this torrent.
    /// Must be stopped before calling this method.
    /// </summary>
    /// <param name="path">The new download path.</param>
    Task SetDownloadPathAsync(string path);

    /// <summary>
    /// Sets the download priority for a specific file.
    /// </summary>
    /// <param name="fileIndex">Zero-based index of the file.</param>
    /// <param name="priority">The new priority level.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the priority update is finished.</returns>
    Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the selection state for a specific file.
    /// </summary>
    /// <param name="fileIndex">Zero-based index of the file.</param>
    /// <param name="selection">The new selection state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the file selection update is finished.</returns>
    Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the torrent, initiating peer connections and file transfers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the torrent has started.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the torrent is already started.</exception>
    /// <exception cref="TorrentException">Thrown when the torrent fails to start.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the torrent, disconnecting from peers and halting transfers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the torrent has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
