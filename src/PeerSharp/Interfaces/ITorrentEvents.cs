namespace PeerSharp.Interfaces;

/// <summary>
/// Read-only interface for torrent progress notifications.
/// Provides immediate, synchronous event handling for a specific torrent.
/// Use <see cref="TorrentEventsBuilder"/> to create instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>ITorrentEvents vs IAlerts - When to Use Which:</b>
/// </para>
/// <para>
/// Use <see cref="ITorrentEvents"/> (this interface) when you need:
/// <list type="bullet">
///   <item>Immediate, synchronous callbacks for a specific torrent</item>
///   <item>Per-torrent event subscriptions with strong typing</item>
///   <item>UI updates that require instant responsiveness</item>
///   <item>Simpler callback-based programming model</item>
/// </list>
/// </para>
/// <para>
/// Use <see cref="IAlerts"/> when you need:
/// <list type="bullet">
///   <item>Centralized monitoring of all torrents from a single location</item>
///   <item>Background processing with controlled polling intervals</item>
///   <item>Batch processing of multiple alerts at once</item>
///   <item>Decoupled event handling (alerts are queued, not synchronous)</item>
/// </list>
/// </para>
/// <para>
/// Both mechanisms can be used simultaneously. Events are synchronous callbacks
/// on the posting thread, while alerts are fire-and-forget (queued).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var events = new TorrentEventsBuilder()
///     .OnPieceCompleted((t, p) => Console.WriteLine($"Piece {p.PieceIndex} completed"))
///     .OnProgressChanged((t, p) => Console.WriteLine($"Progress: {p.Progress:P0}"))
///     .Build();
/// torrent.Events = events;
/// </code>
/// </example>
public interface ITorrentEvents
{
    /// <summary>
    /// Called when a torrent error occurs during background operations.
    /// </summary>
    Action<ITorrent, Exception>? Error { get; }

    /// <summary>
    /// Called when the torrent finishes downloading all pieces or selected files.
    /// The boolean parameter indicates if only selected files finished (true) or all files (false).
    /// </summary>
    Action<ITorrent, bool>? Finished { get; }

    /// <summary>
    /// Called when metadata download progress changes (for magnet links).
    /// </summary>
    Action<ITorrent, MetadataProgress>? MetadataProgress { get; }

    /// <summary>
    /// Called when metadata is fully downloaded and initialized.
    /// </summary>
    Action<ITorrent>? MetadataReceived { get; }

    /// <summary>
    /// Called when a piece is successfully downloaded and verified.
    /// </summary>
    Action<ITorrent, PieceProgress>? PieceCompleted { get; }

    /// <summary>
    /// Called when download progress changes significantly (configurable threshold).
    /// </summary>
    Action<ITorrent, DownloadProgress>? ProgressChanged { get; }

    /// <summary>
    /// Called when torrent state changes (Active, Stopped, CheckingFiles, etc.).
    /// </summary>
    Action<ITorrent, StateTransition>? StateChanged { get; }

    /// <summary>
    /// Called periodically with transfer statistics.
    /// </summary>
    Action<ITorrent, TransferStats>? TransferStats { get; }
}

/// <summary>
/// Progress information for download progress events.
/// </summary>
public readonly struct DownloadProgress
{
    /// <summary>Total number of pieces verified so far.</summary>
    public int CompletedPieces { get; init; }

    /// <summary>Total bytes downloaded and verified.</summary>
    public ulong FinishedBytes { get; init; }

    /// <summary>Overall progress from 0.0 to 1.0.</summary>
    public float Progress { get; init; }

    /// <summary>Bytes remaining to complete the download.</summary>
    public long RemainingBytes => (long)TotalBytes - (long)FinishedBytes;

    /// <summary>Progress of selected files from 0.0 to 1.0.</summary>
    public float SelectionProgress { get; init; }

    /// <summary>Total size of the torrent in bytes.</summary>
    public ulong TotalBytes { get; init; }

    /// <summary>Total number of pieces in the torrent.</summary>
    public int TotalPieces { get; init; }
}

/// <summary>
/// Metadata progress information.
/// </summary>
public readonly struct MetadataProgress
{
    /// <summary>Current progress from 0.0 to 1.0.</summary>
    public float Progress { get; init; }

    /// <summary>Number of metadata pieces received.</summary>
    public int ReceivedPieces { get; init; }

    /// <summary>Total number of metadata pieces.</summary>
    public int TotalPieces { get; init; }
}

/// <summary>
/// Progress information for piece completion events.
/// </summary>
public readonly struct PieceProgress
{
    /// <summary>Total number of pieces verified so far.</summary>
    public int CompletedPieces { get; init; }

    /// <summary>The index of the completed piece.</summary>
    public int PieceIndex { get; init; }

    /// <summary>Current progress from 0.0 to 1.0.</summary>
    public float Progress => TotalPieces > 0 ? (float)CompletedPieces / TotalPieces : 0f;

    /// <summary>Total number of pieces in the torrent.</summary>
    public int TotalPieces { get; init; }
}

/// <summary>
/// State transition information for state change events.
/// </summary>
public readonly struct StateTransition
{
    /// <summary>The new state after the transition.</summary>
    public TorrentState NewState { get; init; }

    /// <summary>The state before the transition.</summary>
    public TorrentState PreviousState { get; init; }
}

/// <summary>
/// Transfer statistics for stats update events.
/// </summary>
public readonly struct TransferStats
{
    /// <summary>Number of currently connected peers.</summary>
    public int ConnectedPeers { get; init; }

    /// <summary>Total bytes downloaded in this session.</summary>
    public long Downloaded { get; init; }

    /// <summary>Current download speed in bytes per second.</summary>
    public int DownloadSpeed { get; init; }

    /// <summary>Share ratio (Uploaded / Downloaded).</summary>
    public float Ratio => Downloaded > 0 ? (float)Uploaded / Downloaded : 0f;

    /// <summary>Total bytes uploaded in this session.</summary>
    public long Uploaded { get; init; }

    /// <summary>Current upload speed in bytes per second.</summary>
    public int UploadSpeed { get; init; }
}

/// <summary>
/// Builder for creating immutable <see cref="ITorrentEvents"/> instances.
/// </summary>
public sealed class TorrentEventsBuilder
{
    private Action<ITorrent, Exception>? _error;
    private Action<ITorrent, bool>? _finished;
    private Action<ITorrent, MetadataProgress>? _metadataProgress;
    private Action<ITorrent>? _metadataReceived;
    private Action<ITorrent, PieceProgress>? _pieceCompleted;
    private Action<ITorrent, DownloadProgress>? _progressChanged;
    private Action<ITorrent, StateTransition>? _stateChanged;
    private Action<ITorrent, TransferStats>? _transferStats;

    /// <summary>
    /// Builds an immutable <see cref="ITorrentEvents"/> instance.
    /// </summary>
    public ITorrentEvents Build()
    {
        return new TorrentEvents(
        _pieceCompleted,
        _progressChanged,
        _transferStats,
        _stateChanged,
        _error,
        _finished,
        _metadataProgress,
        _metadataReceived);
    }

    /// <summary>Sets the handler for error events.</summary>
    public TorrentEventsBuilder OnError(Action<ITorrent, Exception> handler)
    {
        _error = handler;
        return this;
    }

    /// <summary>Sets the handler for download finished events.</summary>
    public TorrentEventsBuilder OnFinished(Action<ITorrent, bool> handler)
    {
        _finished = handler;
        return this;
    }

    /// <summary>Sets the handler for metadata progress events.</summary>
    public TorrentEventsBuilder OnMetadataProgress(Action<ITorrent, MetadataProgress> handler)
    {
        _metadataProgress = handler;
        return this;
    }

    /// <summary>Sets the handler for metadata received events.</summary>
    public TorrentEventsBuilder OnMetadataReceived(Action<ITorrent> handler)
    {
        _metadataReceived = handler;
        return this;
    }

    /// <summary>Sets the handler for piece completion events.</summary>
    public TorrentEventsBuilder OnPieceCompleted(Action<ITorrent, PieceProgress> handler)
    {
        _pieceCompleted = handler;
        return this;
    }

    /// <summary>Sets the handler for progress change events.</summary>
    public TorrentEventsBuilder OnProgressChanged(Action<ITorrent, DownloadProgress> handler)
    {
        _progressChanged = handler;
        return this;
    }

    /// <summary>Sets the handler for state change events.</summary>
    public TorrentEventsBuilder OnStateChanged(Action<ITorrent, StateTransition> handler)
    {
        _stateChanged = handler;
        return this;
    }

    /// <summary>Sets the handler for transfer statistics events.</summary>
    public TorrentEventsBuilder OnTransferStats(Action<ITorrent, TransferStats> handler)
    {
        _transferStats = handler;
        return this;
    }
}

/// <summary>
/// Immutable implementation of <see cref="ITorrentEvents"/>.
/// </summary>
internal sealed class TorrentEvents : ITorrentEvents
{
    internal TorrentEvents(
        Action<ITorrent, PieceProgress>? pieceCompleted,
        Action<ITorrent, DownloadProgress>? progressChanged,
        Action<ITorrent, TransferStats>? transferStats,
        Action<ITorrent, StateTransition>? stateChanged,
        Action<ITorrent, Exception>? error,
        Action<ITorrent, bool>? finished,
        Action<ITorrent, MetadataProgress>? metadataProgress,
        Action<ITorrent>? metadataReceived)
    {
        PieceCompleted = pieceCompleted;
        ProgressChanged = progressChanged;
        TransferStats = transferStats;
        StateChanged = stateChanged;
        Error = error;
        Finished = finished;
        MetadataProgress = metadataProgress;
        MetadataReceived = metadataReceived;
    }

    public Action<ITorrent, Exception>? Error { get; }
    public Action<ITorrent, bool>? Finished { get; }
    public Action<ITorrent, MetadataProgress>? MetadataProgress { get; }
    public Action<ITorrent>? MetadataReceived { get; }
    public Action<ITorrent, PieceProgress>? PieceCompleted { get; }
    public Action<ITorrent, DownloadProgress>? ProgressChanged { get; }
    public Action<ITorrent, StateTransition>? StateChanged { get; }
    public Action<ITorrent, TransferStats>? TransferStats { get; }
}
