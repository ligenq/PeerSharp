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
    long DownloadLimitBytesPerSecond { get; set; }

    /// <summary>Gets the current aggregate download speed in bytes per second.</summary>
    long DownloadSpeed => 0;

    /// <summary>
    /// Gets or sets the per-torrent disk read limit in bytes per second. 0 means unlimited.
    /// </summary>
    long DiskReadLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the per-torrent disk write limit in bytes per second. 0 means unlimited.
    /// </summary>
    long DiskWriteLimitBytesPerSecond { get; set; }

    /// <summary>Gets the current aggregate upload speed in bytes per second.</summary>
    long UploadSpeed => 0;

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
    /// Gets whether the entire torrent has been downloaded. Always false while
    /// <see cref="HasMetadata"/> is false: a magnet that does not yet know its own piece count has not
    /// finished anything, however little is outstanding.
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
    /// Determines whether this torrent and another share a non-empty V1 or V2 info hash. Empty hashes
    /// represent an unavailable hash version and are never treated as identity evidence.
    /// </summary>
    /// <param name="other">The torrent to compare.</param>
    /// <returns><see langword="true"/> when a non-empty hash version matches.</returns>
    bool HasSameIdentity(ITorrent? other);

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
    /// Gets or sets the most peers this torrent may connect to, overriding
    /// <see cref="Config.ConnectionSettings.MaxPeersPerTorrent"/>. Zero, the default, uses that
    /// engine-wide setting.
    /// </summary>
    /// <remarks>
    /// Lowering this does not disconnect peers already connected; it stops new ones being accepted
    /// until the count falls below the new ceiling. The engine-wide
    /// <see cref="Config.ConnectionSettings.MaxConnections"/> still applies on top of whatever is set
    /// here.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets how many peers this torrent uploads to at once. Zero, the default, lets the
    /// engine choose from the upload rate limit and
    /// <see cref="Config.ConnectionSettings.UploadSlotsMin"/>/<see cref="Config.ConnectionSettings.UploadSlotsMax"/>.
    /// </summary>
    /// <remarks>
    /// A number set here is used as given, never widened by the automatic calculation - though it is
    /// still bounded by how many peers are actually connected.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    int MaxUploadSlots { get; set; }

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
    /// Gets or sets whether this torrent seeds in BEP 16 super-seed mode. Default is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Super-seeding claims to hold nothing and then hands each peer a single piece at a time, moving
    /// on only once that piece has been seen coming back from somebody else. It exists for the case
    /// where one seed is introducing content to an empty swarm: it costs the seed far less upload to
    /// get a full copy distributed, because no two peers are handed the same piece to begin with.
    /// </para>
    /// <para>
    /// It is the wrong setting everywhere else. Against an established swarm it throttles peers that
    /// could have downloaded freely, and a peer that cannot tell super-seeding from a client with
    /// nothing to offer may simply disconnect. Turn it on for an initial seed and off afterwards.
    /// </para>
    /// <para>
    /// The setting can be changed at any time and survives the metadata fetch of a magnet link. It
    /// takes effect for peers connecting afterwards; peers already sent a bitfield keep it.
    /// </para>
    /// </remarks>
    bool SuperSeeding { get; set; }

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
    /// The BEP 19 web seeds this torrent pulls from, and the ones a caller adds.
    /// </summary>
    IWebSeeds WebSeeds { get; }

    /// <summary>
    /// Gets or sets the per-torrent upload limit in bytes per second. 0 means unlimited.
    /// </summary>
    long UploadLimitBytesPerSecond { get; set; }

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
    /// <remarks>
    /// Reads block until the pieces covering the requested range have been downloaded and
    /// verified. A zero-byte read means end-of-file and nothing else: a cancelled read throws
    /// <see cref="OperationCanceledException"/>, and a read that waits more than 60 seconds
    /// without receiving the piece it needs throws <see cref="TimeoutException"/>. Callers can
    /// therefore treat the stream like any other <see cref="Stream"/>, including with
    /// <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/>.
    /// </remarks>
    Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the download priority for all files in the torrent.
    /// </summary>
    /// <param name="priority">The priority level to apply to all files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the priority update is finished.</returns>
    Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Points this torrent at a different download path without touching any data already on disk.
    /// Must be stopped before calling this method.
    /// </summary>
    /// <remarks>
    /// For a torrent that has not downloaded anything yet. If it has, the data stays where it was and
    /// the torrent starts again believing it holds nothing - use
    /// <see cref="MoveStorageAsync(string, CancellationToken)"/> to take the data along.
    /// </remarks>
    /// <param name="path">The new download path.</param>
    /// <param name="cancellationToken">
    /// Cancellation token. Bounds the wait for other state transitions (start, stop, recheck)
    /// to finish; once the path change itself begins it runs to completion.
    /// </param>
    Task SetDownloadPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the torrent's downloaded data to <paramref name="path"/> and continues from there.
    /// </summary>
    /// <param name="path">The directory the content should live under from now on.</param>
    /// <param name="cancellationToken">Cancels the wait for the torrent lock and the move itself.</param>
    /// <remarks>
    /// <para>
    /// This is the one to use once a torrent has data on disk.
    /// <see cref="SetDownloadPathAsync(string, CancellationToken)"/> only repoints at a new directory
    /// and leaves whatever was downloaded where it was, which on the next start reads as a torrent
    /// that has nothing.
    /// </para>
    /// <para>
    /// The torrent must be stopped. Files are moved with their layout intact; a move within a volume
    /// is a rename, one that crosses volumes is a copy and takes as long as the data is large. If any
    /// file cannot be moved, the ones already moved are put back before the exception is thrown, so
    /// the torrent is never left half in each place.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The torrent is running.</exception>
    /// <exception cref="Exceptions.StorageException">The data could not be moved.</exception>
    Task MoveStorageAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one downloaded piece back from disk.
    /// </summary>
    /// <param name="pieceIndex">The piece to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The piece's bytes. The last piece of a torrent is shorter than the rest.</returns>
    /// <remarks>
    /// For inspecting content the torrent already holds - a thumbnail from the first piece of a
    /// video, a signature block, a header. To read a file rather than a piece, and to have the
    /// missing parts fetched on demand, use
    /// <see cref="OpenStreamAsync(int, CancellationToken)"/> instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The metadata is not known, the storage is not open, or the piece has not been downloaded and
    /// verified.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the torrent.</exception>
    Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives one piece a priority of its own, overriding whatever this torrent's file selection
    /// implies for it.
    /// </summary>
    /// <param name="pieceIndex">The piece to prioritise.</param>
    /// <param name="priority">
    /// Its new priority. <see cref="Priority.DoNotDownload"/> excludes the piece even where the file
    /// it belongs to is selected.
    /// </param>
    /// <remarks>
    /// File priorities are the right tool for "fetch this file first". This is for the cases below
    /// that: the piece holding a media header, a range a reader is about to seek to, the last piece
    /// of an archive that carries its index. An override stays in force until it is replaced or
    /// <see cref="ClearPiecePriorities"/> is called, and outranks the file selection in both
    /// directions.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The metadata is not known yet.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the torrent.</exception>
    void SetPiecePriority(int pieceIndex, Priority priority);

    /// <summary>
    /// The priority in force for a piece - the one set with
    /// <see cref="SetPiecePriority(int, Priority)"/> if there is one, otherwise the highest priority
    /// among the files it touches.
    /// </summary>
    /// <param name="pieceIndex">The piece to ask about.</param>
    /// <exception cref="InvalidOperationException">The metadata is not known yet.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the torrent.</exception>
    Priority GetPiecePriority(int pieceIndex);

    /// <summary>
    /// Drops every per-piece priority, returning the whole torrent to what its file selection says.
    /// </summary>
    void ClearPiecePriorities();

    /// <summary>
    /// Stores one of the torrent's files under a different name, keeping whatever has been downloaded.
    /// </summary>
    /// <param name="fileIndex">The file's index within this torrent.</param>
    /// <param name="newPath">
    /// The new location relative to the download path. May contain directories, which are created as
    /// needed; may not be absolute or contain <c>..</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the torrent lock and the rename itself.</param>
    /// <remarks>
    /// <para>
    /// This changes only where the data is written; the torrent's own metadata is untouched, so the
    /// info hash and everything announced about it stay as they were. The new name is kept in the
    /// resume data, because rebuilding paths from the metadata on the next start would otherwise put
    /// every renamed file back.
    /// </para>
    /// <para>The torrent must be stopped.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="newPath"/> is absolute or escapes the download path.</exception>
    /// <exception cref="InvalidOperationException">The torrent is running, or has no metadata yet.</exception>
    /// <exception cref="Exceptions.StorageException">The file could not be renamed on disk.</exception>
    Task RenameFileAsync(int fileIndex, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The files renamed with <see cref="RenameFileAsync(int, string, CancellationToken)"/>, keyed by
    /// file index. Empty when none have been.
    /// </summary>
    IReadOnlyDictionary<int, string> GetRenamedFiles();

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
    /// <param name="cancellationToken">
    /// Bounds the wait for a concurrent state transition (start, stop, recheck, download-path
    /// change) to release the torrent. Once the stop itself begins it runs to completion, so
    /// the torrent is never left half-stopped: shutting down peers, trackers and the piece
    /// writer has to finish for on-disk state to stay consistent.
    /// </param>
    /// <returns>A task that completes when the torrent has stopped.</returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the token is cancelled before the stop begins; the torrent is left running.
    /// </exception>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes once the torrent's metadata is available and fully applied: immediately for
    /// torrents added from a .torrent file, or after the metadata download finishes for
    /// torrents added from a magnet link. When the metadata task completes, the file list
    /// (<see cref="FileCount"/>, <see cref="GetFileInfo"/>) and file selection APIs are usable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop waiting.</param>
    Task WaitForMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconstructs a <see cref="TorrentFile"/> from this torrent's metadata, e.g. to cache
    /// magnet-link metadata so a later add can skip the metadata download entirely
    /// (persist <see cref="TorrentFile.RawData"/> and re-add via <see cref="TorrentFile.Parse(byte[])"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when metadata is not yet available.</exception>
    TorrentFile ExportTorrentFile();

    /// <summary>
    /// Registers an optional peer transport (e.g. WebTorrent over WebRTC). The torrent will
    /// start, stop, and dispose the transport in step with its own lifecycle. Must be called
    /// before <see cref="StartAsync"/>; transports registered after the torrent has started
    /// will not be started until the torrent is stopped and restarted.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the same transport instance is already registered.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the torrent has been disposed.</exception>
    void RegisterPeerTransport(IPeerTransport transport);
}
