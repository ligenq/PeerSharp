namespace PeerSharp.Core;

/// <summary>
/// Specifies the category of an alert for filtering purposes.
/// </summary>
public enum AlertCategory : uint
{
    /// <summary>Alerts related to torrent lifecycle and progress.</summary>
    Torrent = 0x00FFFF,

    /// <summary>Alerts related to metadata download.</summary>
    Metadata = 0x0F0000,

    /// <summary>Alerts related to configuration changes.</summary>
    Config = 0xF00000,
}

/// <summary>
/// Unique identifier for each type of alert.
/// </summary>
[Flags]
public enum AlertId : uint
{
    /// <summary>No alert. Used as an empty value when filtering.</summary>
    None = 0,

    /// <summary>Every selected file has been downloaded and verified.</summary>
    TorrentFinished = 1,

    /// <summary>A torrent was added to the engine.</summary>
    TorrentAdded = 1 << 1,

    /// <summary>A torrent was removed from the engine.</summary>
    TorrentRemoved = 1 << 2,

    /// <summary>A hash check of existing data on disk has begun.</summary>
    TorrentCheckStarted = 1 << 3,

    /// <summary>A hash check has completed.</summary>
    TorrentCheckFinished = 1 << 4,

    /// <summary>The torrent stopped unexpectedly, for example after an unrecoverable storage failure.</summary>
    TorrentInterrupted = 1 << 5,

    /// <summary>The torrent began downloading or seeding.</summary>
    TorrentStarted = 1 << 6,

    /// <summary>The torrent was stopped by the caller.</summary>
    TorrentStopped = 1 << 7,

    /// <summary>The torrent moved between states, for example from downloading to seeding.</summary>
    TorrentStateChanged = 1 << 8,

    /// <summary>A piece finished downloading and passed hash verification.</summary>
    PieceCompleted = 1 << 9,

    /// <summary>Overall download progress changed.</summary>
    ProgressChanged = 1 << 10,

    /// <summary>Transfer rate and volume counters were refreshed.</summary>
    TransferStatsUpdated = 1 << 11,

    /// <summary>The torrent encountered an error.</summary>
    TorrentError = 1 << 12,

    /// <summary>Metadata was fetched from the swarm, so the file list is now known.</summary>
    MetadataInitialized = 1 << 16,

    /// <summary>Progress of an in-flight metadata download changed.</summary>
    MetadataProgressChanged = 1 << 17,

    /// <summary>A configuration value was changed at runtime.</summary>
    ConfigChanged = 1 << 20,
}

/// <summary>
/// Base class for all alert messages fired by the client.
/// </summary>
public abstract record Alert
{
    /// <summary>
    /// Gets the unique identifier for this alert type.
    /// </summary>
    public required AlertId Id { get; init; }

    /// <summary>
    /// Gets the timestamp when this alert was generated.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Base class for alerts related to a specific torrent.
/// </summary>
public abstract record TorrentAlert : Alert
{
    /// <summary>
    /// Gets the torrent associated with this alert.
    /// </summary>
    public required ITorrent Torrent { get; init; }
}

/// <summary>
/// Base class for alerts related to metadata download.
/// </summary>
public abstract record MetadataAlert : Alert
{
    /// <summary>
    /// Gets the torrent associated with this alert.
    /// </summary>
    public required ITorrent Torrent { get; init; }
}

/// <summary>
/// Simple alert type for torrent lifecycle events that don't carry extra data.
/// </summary>
public sealed record SimpleTorrentAlert : TorrentAlert;

/// <summary>
/// Simple alert type for metadata events that don't carry extra data.
/// </summary>
public sealed record SimpleMetadataAlert : MetadataAlert;

/// <summary>
/// Alert fired when a configuration setting changes.
/// </summary>
public sealed record ConfigAlert : Alert
{
    /// <summary>
    /// Gets the name of the configuration category that changed.
    /// </summary>
    public required string ConfigType { get; init; }
}

/// <summary>
/// Alert fired when a piece is successfully downloaded and verified.
/// </summary>
public sealed record PieceCompletedAlert : TorrentAlert
{
    /// <summary>
    /// Gets the index of the piece that was completed.
    /// </summary>
    public required int PieceIndex { get; init; }

    /// <summary>
    /// Gets the total number of pieces in the torrent.
    /// </summary>
    public required int TotalPieces { get; init; }

    /// <summary>
    /// Gets the number of pieces that have been downloaded and verified so far.
    /// </summary>
    public required int CompletedPieces { get; init; }
}

/// <summary>
/// Alert fired when torrent download progress changes significantly.
/// </summary>
public sealed record ProgressChangedAlert : TorrentAlert
{
    /// <summary>
    /// Gets the overall download progress (0.0 to 1.0).
    /// </summary>
    public required float Progress { get; init; }

    /// <summary>
    /// Gets the download progress of selected files only (0.0 to 1.0).
    /// </summary>
    public required float SelectionProgress { get; init; }

    /// <summary>
    /// Gets the total number of bytes downloaded and verified.
    /// </summary>
    public required ulong FinishedBytes { get; init; }

    /// <summary>
    /// Gets the total size of the torrent in bytes.
    /// </summary>
    public required ulong TotalBytes { get; init; }

    /// <summary>
    /// Gets the number of pieces that have been downloaded and verified.
    /// </summary>
    public required int CompletedPieces { get; init; }

    /// <summary>
    /// Gets the total number of pieces in the torrent.
    /// </summary>
    public required int TotalPieces { get; init; }
}

/// <summary>
/// Alert fired periodically with transfer statistics.
/// </summary>
public sealed record TransferStatsAlert : TorrentAlert
{
    /// <summary>
    /// Gets the total number of bytes downloaded during the current session.
    /// </summary>
    public required long Downloaded { get; init; }

    /// <summary>
    /// Gets the total number of bytes uploaded during the current session.
    /// </summary>
    public required long Uploaded { get; init; }

    /// <summary>
    /// Gets the current download speed in bytes per second.
    /// </summary>
    public required int DownloadSpeed { get; init; }

    /// <summary>
    /// Gets the current upload speed in bytes per second.
    /// </summary>
    public required int UploadSpeed { get; init; }

    /// <summary>
    /// Gets the current number of connected peers.
    /// </summary>
    public required int ConnectedPeers { get; init; }
}

/// <summary>
/// Alert fired when torrent state changes.
/// </summary>
public sealed record StateChangedAlert : TorrentAlert
{
    /// <summary>
    /// Gets the operational state before the change.
    /// </summary>
    public required TorrentState PreviousState { get; init; }

    /// <summary>
    /// Gets the new operational state.
    /// </summary>
    public required TorrentState NewState { get; init; }
}

/// <summary>
/// Alert fired when a torrent error occurs.
/// </summary>
public sealed record TorrentErrorAlert : TorrentAlert
{
    /// <summary>
    /// Gets the exception that caused the error.
    /// </summary>
    public required Exception Exception { get; init; }
}

/// <summary>
/// Alert fired when metadata download progress changes.
/// </summary>
public sealed record MetadataProgressAlert : MetadataAlert
{
    /// <summary>
    /// Gets the progress of metadata download (0.0 to 1.0).
    /// </summary>
    public required float Progress { get; init; }

    /// <summary>
    /// Gets the number of metadata pieces received so far.
    /// </summary>
    public required int ReceivedPieces { get; init; }

    /// <summary>
    /// Gets the total number of metadata pieces to download.
    /// </summary>
    public required int TotalPieces { get; init; }
}

