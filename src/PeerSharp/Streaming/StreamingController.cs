using Microsoft.Extensions.Logging;
using PeerSharp.Internals;

namespace PeerSharp.Streaming;

/// <summary>
/// Manages streaming functionality for a torrent, including download strategy,
/// piece prioritization, and active stream lifecycle.
/// </summary>
internal class StreamingController : IDisposable
{
    private static readonly HashSet<string> StreamableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv",
        ".ts", ".m2ts", ".mpg", ".mpeg", ".mp3", ".flac", ".ogg", ".wav",
        ".aac", ".m4a", ".opus"
    };

    private readonly ILogger<StreamingController> _logger = TorrentLoggerFactory.CreateLogger<StreamingController>();
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;

    // Accessed from multiple threads: consumer thread and torrent download thread
    private TorrentStream? _activeStream;

    private AtomicDisposal _disposal = new();

    // Accessed from consumer thread (write) and download thread (read)
    private int _downloadStrategy = (int)DownloadStrategy.RarestFirst;

    private List<int>? _priorityPieces;

    public StreamingController(Torrent torrent, TimeProvider timeProvider)
    {
        _torrent = torrent;
        _timeProvider = timeProvider;
    }

    public DownloadStrategy DownloadStrategy
    {
        get => (DownloadStrategy)Interlocked.CompareExchange(ref _downloadStrategy, 0, 0);
        set => Interlocked.Exchange(ref _downloadStrategy, (int)value);
    }

    public bool HasStreamableFiles => StreamableFileIndices.Count > 0;

    public List<int>? PriorityPieces
    {
        get => Interlocked.CompareExchange(ref _priorityPieces, null, null);
        internal set => Interlocked.Exchange(ref _priorityPieces, value);
    }

    public IReadOnlyList<int> StreamableFileIndices
    {
        get
        {
            var indices = new List<int>();
            var files = _torrent.InfoFile.Info.Files;
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].IsPadding)
                {
                    continue;
                }
                string ext = Path.GetExtension(files[i].Path);
                if (StreamableExtensions.Contains(ext)
                    && _torrent.InfoFile.Info.TryMapInternalIndexToVisible(i, out int visibleIndex))
                {
                    indices.Add(visibleIndex);
                }
            }
            return indices;
        }
    }

    /// <summary>
    /// Clears the active stream reference. Does not dispose the stream itself
    /// as the consumer owns its lifecycle.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Called by Torrent when a piece is verified. Notifies the active stream.
    /// </summary>
    public void OnPieceVerified(int pieceIndex)
    {
        // Capture to local to avoid race with OnStreamDisposed
        var stream = Interlocked.CompareExchange(ref _activeStream, null, null);
        if (stream == null)
        {
            return;
        }

        try
        {
            stream.OnPieceVerified(pieceIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying stream of piece {PieceIndex}", pieceIndex);
        }
    }

    public async Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default)
    {
        if (_torrent.State == TorrentState.Stopped)
        {
            throw new InvalidOperationException("Torrent must be running to open a stream.");
        }

        if (!_torrent.HasMetadata)
        {
            await WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        int internalIndex = _torrent.InfoFile.Info.MapVisibleIndexToInternal(fileIndex);
        var stream = new TorrentStream(this, _torrent, internalIndex, _timeProvider);
        Interlocked.Exchange(ref _activeStream, stream);
        return stream;
    }

    /// <summary>
    /// Called by TorrentStream when it is disposed.
    /// Returns true if this was the active stream.
    /// </summary>
    internal bool OnStreamDisposed(TorrentStream stream)
    {
        var previous = Interlocked.CompareExchange(ref _activeStream, null, stream);
        if (previous == stream)
        {
            // This was the active stream, reset state
            Interlocked.Exchange(ref _downloadStrategy, (int)DownloadStrategy.RarestFirst);
            Interlocked.Exchange(ref _priorityPieces, null);
            return true;
        }
        return false;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            Interlocked.Exchange(ref _activeStream, null);
        }
    }

    private async Task WaitForMetadataAsync(CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

        while (!_torrent.HasMetadata)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, linkedCts.Token).ConfigureAwait(false);
        }
    }
}
