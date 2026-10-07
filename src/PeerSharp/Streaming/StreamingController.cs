using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;

namespace PeerSharp.Streaming;

/// <summary>
/// Manages streaming functionality for a torrent: the streams open on it, the pieces they need
/// first, and when the piece picker should favour them.
/// </summary>
/// <remarks>
/// <para>
/// Every open stream is tracked, not only the latest. A media player - and above all a Chromecast
/// fetching over HTTP - routinely holds several overlapping range requests: one reading from the
/// playhead, another fetching an MP4's index from the end of the file. Tracking only the latest
/// meant that when the short request finished it switched the whole torrent back to rarest-first,
/// and the playhead's reader, still open, stalled until it timed out.
/// </para>
/// <para>
/// Streaming is an override on top of the torrent's configured strategy, never a replacement for
/// it. While any stream is open the picker favours their pieces; once the last one closes the
/// torrent goes back to whatever it was configured with, which the controller never writes.
/// </para>
/// </remarks>
internal class StreamingController : IDisposable
{
    private readonly ILogger<StreamingController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;

    private readonly Lock _streamsLock = new();

    /// <summary>
    /// Open streams, most recently active first, so that the reader somebody is watching gets the
    /// first claim on every peer. Guarded by <see cref="_streamsLock"/>.
    /// </summary>
    private readonly List<StreamPriorities> _streams = [];

    /// <summary>The open streams, published for the piece-verified path to read without locking.</summary>
    private TorrentStream[] _openStreams = [];

    /// <summary>Every open stream's pieces, merged in order, published for the piece picker.</summary>
    private IReadOnlyList<int>? _priorityPieces;

    private AtomicDisposal _disposal = new();

    public StreamingController(Torrent torrent, TimeProvider timeProvider)
        : this(torrent, timeProvider, NullLoggerFactory.Instance)
    {
    }

    internal StreamingController(Torrent torrent, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _torrent = torrent;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StreamingController>();
        _timeProvider = timeProvider;
    }

    public bool HasStreamableFiles => StreamableFileIndices.Count > 0;

    /// <summary>
    /// Whether an open stream has little downloaded ahead of its reader - just opened, just moved, or
    /// running low - for which peers are given a shorter queue of work.
    /// </summary>
    public bool IsBuffering
    {
        get
        {
            foreach (var stream in Volatile.Read(ref _openStreams))
            {
                if (stream.IsBuffering)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether any stream is open, which is what puts the picker into streaming mode.</summary>
    public bool IsStreaming => Volatile.Read(ref _openStreams).Length > 0;

    /// <summary>
    /// The pieces every open stream needs soonest, the most recently active stream's first, or null
    /// when nothing is streaming.
    /// </summary>
    public IReadOnlyList<int>? PriorityPieces => Volatile.Read(ref _priorityPieces);

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

                if (StreamMediaTypes.IsStreamable(files[i].Path)
                    && _torrent.InfoFile.Info.TryMapInternalIndexToVisible(i, out int visibleIndex))
                {
                    indices.Add(visibleIndex);
                }
            }
            return indices;
        }
    }

    /// <summary>
    /// Forgets open streams. Each stream belongs to whoever opened it.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Called by Torrent when a piece is verified. Tells every open stream, since any of them may be
    /// waiting for it.
    /// </summary>
    public void OnPieceVerified(int pieceIndex)
    {
        foreach (var stream in Volatile.Read(ref _openStreams))
        {
            try
            {
                stream.OnPieceVerified(pieceIndex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error notifying stream of piece {PieceIndex}", pieceIndex);
            }
        }
    }

    public async Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_torrent.State == TorrentState.Stopped)
        {
            throw new InvalidOperationException("Torrent must be running to open a stream.");
        }

        if (!_torrent.HasMetadata)
        {
            await WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        _disposal.ThrowIfDisposed(this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_torrent.State == TorrentState.Stopped)
        {
            throw new InvalidOperationException("Torrent must be running to open a stream.");
        }

        int internalIndex = _torrent.InfoFile.Info.MapVisibleIndexToInternal(fileIndex);
        return new TorrentStream(this, _torrent, internalIndex, _timeProvider, _loggerFactory.CreateLogger<TorrentStream>());
    }

    /// <summary>Starts tracking a stream. Called once, as the stream is constructed.</summary>
    internal void OnStreamOpened(TorrentStream stream)
    {
        lock (_streamsLock)
        {
            _disposal.ThrowIfDisposed(this);
            _streams.Insert(0, new StreamPriorities(stream));
            PublishLocked();
        }
    }

    /// <summary>
    /// Records the pieces <paramref name="stream"/> needs next and moves it to the front, since a
    /// stream updating its window is the one being read.
    /// </summary>
    /// <remarks>
    /// A stream that has already been closed is ignored rather than tracked again: a read racing
    /// its own disposal must not leave the torrent streaming for a reader that has gone.
    /// </remarks>
    internal void UpdatePriorities(TorrentStream stream, IReadOnlyList<int> pieces)
    {
        lock (_streamsLock)
        {
            int index = _streams.FindIndex(entry => ReferenceEquals(entry.Stream, stream));
            if (index < 0)
            {
                return;
            }

            var entry = _streams[index];
            entry.Pieces = pieces;
            _streams.RemoveAt(index);
            _streams.Insert(0, entry);
            PublishLocked();
        }
    }

    /// <summary>
    /// Stops tracking a stream. When it was the last one, the torrent's configured strategy applies
    /// again.
    /// </summary>
    /// <returns>Whether the stream was being tracked; false when it had already been forgotten.</returns>
    internal bool OnStreamDisposed(TorrentStream stream)
    {
        lock (_streamsLock)
        {
            if (_streams.RemoveAll(entry => ReferenceEquals(entry.Stream, stream)) == 0)
            {
                return false;
            }

            PublishLocked();
            return true;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            CloseStreams(closeReaders: false);
        }
    }

    internal void CloseStreams(bool closeReaders = true)
    {
        TorrentStream[] streams;
        lock (_streamsLock)
        {
            streams = [.. _streams.Select(entry => entry.Stream)];
            _streams.Clear();
            PublishLocked();
        }
        if (closeReaders)
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }

    private void PublishLocked()
    {
        if (_streams.Count == 0)
        {
            Volatile.Write(ref _priorityPieces, null);
            Volatile.Write(ref _openStreams, []);
            return;
        }

        var merged = new List<int>();
        var seen = new HashSet<int>();
        foreach (var entry in _streams)
        {
            foreach (int piece in entry.Pieces)
            {
                if (seen.Add(piece))
                {
                    merged.Add(piece);
                }
            }
        }

        Volatile.Write(ref _priorityPieces, merged);
        Volatile.Write(ref _openStreams, _streams.Select(entry => entry.Stream).ToArray());
    }

    private async Task WaitForMetadataAsync(CancellationToken cancellationToken)
    {
        int timeoutSeconds = _torrent.Settings.Streaming.MetadataWaitTimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            await _torrent.WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            await _torrent.WaitForMetadataAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Distinct from the caller cancelling: nobody asked for this to stop, the swarm simply
            // never supplied the metadata.
            throw new TimeoutException(
                $"Timed out after {timeout.TotalSeconds:0}s waiting for the metadata of torrent '{_torrent.Name}'.");
        }
    }

    private sealed class StreamPriorities(TorrentStream stream)
    {
        public TorrentStream Stream { get; } = stream;

        public IReadOnlyList<int> Pieces { get; set; } = [];
    }
}
