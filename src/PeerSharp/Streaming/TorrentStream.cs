using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;

namespace PeerSharp.Streaming;

/// <summary>
/// A readable and seekable Stream that reads directly from a file within a torrent.
/// Handles piece prioritization and buffering automatically.
/// </summary>
internal class TorrentStream : Stream
{
    private const int FileEndPriorityBytes = 1 * 1024 * 1024;

    /// <summary>
    /// How much of the start of the file is fetched ahead of everything but the read position: where
    /// a container keeps what a player reads before anything else - Matroska's tracks, the index of an
    /// MP4 written for streaming.
    /// </summary>
    private const int FileStartPriorityBytes = 1 * 1024 * 1024;

    // Priorities are refreshed each time this much has been read since the last refresh.
    private const int PriorityUpdateIntervalBytes = 1024 * 1024;

    /// <summary>The most fetched ahead, however fast the stream is read: a copy of the file reads far faster than any film plays.</summary>
    private const long MaxReadAheadBytes = 512L * 1024 * 1024;

    /// <summary>How far back the reader's rate is measured.</summary>
    private static readonly TimeSpan ReadRateWindow = TimeSpan.FromSeconds(10);

    /// <summary>Seconds downloaded ahead of the reader, at its rate, below which the stream is buffering again.</summary>
    internal const int ThinBufferSeconds = 10;

    /// <summary>Seconds downloaded ahead of the reader, at its rate, from which the stream has enough.</summary>
    internal const int HealthyBufferSeconds = 20;

    /// <summary>How long a reader must have been read from before its rate is believed: its first reads are a player filling its buffer.</summary>
    private static readonly TimeSpan ReadRateSettles = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Fallback re-check interval, in case a piece-verified signal is missed - for example one that
    /// arrives between checking what is available and starting to wait.
    /// </summary>
    private static readonly TimeSpan DataPollInterval = TimeSpan.FromSeconds(1);

    private readonly StreamingController _controller;
    private readonly SemaphoreSlim _dataSignal = new(0);
    private readonly Lock _lifetimeLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CancellationToken _lifetimeToken;
    private int _readers;

    /// <summary>
    /// How long a read waits for the swarm to supply the pieces it needs before giving up, or null
    /// to wait for as long as the reader's token allows. Exceeding it throws
    /// <see cref="TimeoutException"/> rather than reporting end-of-stream, so a stalled swarm can
    /// never be mistaken for a complete file.
    /// </summary>
    private readonly TimeSpan? _dataWaitTimeout;
    private readonly long _fileSize;
    private readonly long _fileStartOffset;
    private readonly long _readAheadBytes;
    private readonly TimeSpan _readAheadTime;

    /// <summary>What was read in the last <see cref="ReadRateWindow"/>, oldest first, and when; read and written by the reader alone.</summary>
    private readonly Queue<(long Timestamp, int Bytes)> _reads = new();
    private long _readsBytes;
    private long _firstReadTimestamp = -1;

    /// <summary>The reader's rate, for other threads to read; zero while it is not yet known.</summary>
    private double _publishedRate;

    /// <summary>1 while little is downloaded ahead of the reader; see <see cref="IsBuffering"/>.</summary>
    private int _buffering = 1;
    private readonly int _firstPieceIndex;
    private readonly int _lastPieceIndex;
    private readonly ILogger<TorrentStream> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private AtomicDisposal _disposal = new();
    private long _lastPriorityUpdatePosition;
    private long _position;
    private int _waitingEndPiece = -1;

    // Track the piece range we're currently waiting for (for efficient signaling)
    // Accessed from multiple threads: consumer thread (WaitForDataAsync) and torrent thread (OnPieceVerified)
    private int _waitingStartPiece = -1;

    internal TorrentStream(StreamingController controller, Torrent torrent, int fileIndex, TimeProvider timeProvider)
        : this(controller, torrent, fileIndex, timeProvider, NullLogger<TorrentStream>.Instance)
    {
    }

    internal TorrentStream(StreamingController controller, Torrent torrent, int fileIndex, TimeProvider timeProvider, ILogger<TorrentStream> logger)
    {
        _controller = controller;
        _torrent = torrent;
        _logger = logger;
        _timeProvider = timeProvider;
        _lifetimeToken = _lifetimeCts.Token;

        // Validate file index
        if (fileIndex < 0 || fileIndex >= torrent.InfoFile.Info.Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }

        var file = torrent.InfoFile.Info.Files[fileIndex];
        _fileSize = file.Size;

        // Offsets include the implicit gaps between v2 files; summing sizes does not.
        _fileStartOffset = file.Offset;

        // Calculate piece range
        int pieceSize = (int)torrent.InfoFile.Info.PieceSize;
        _firstPieceIndex = (int)(_fileStartOffset / pieceSize);
        _lastPieceIndex = _fileSize == 0 ? _firstPieceIndex - 1 : (int)((_fileStartOffset + _fileSize - 1) / pieceSize);

        var settings = torrent.Settings.Streaming;
        _readAheadBytes = Math.Clamp(settings.ReadAheadBytes, 0, MaxReadAheadBytes);
        _readAheadTime = TimeSpan.FromSeconds(Math.Max(0, settings.ReadAheadSeconds));
        _dataWaitTimeout = settings.DataWaitTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(settings.DataWaitTimeoutSeconds)
            : null;

        // Being tracked is what puts the picker into streaming mode, for as long as this is open.
        _controller.OnStreamOpened(this);
        UpdatePriorities(0);

        _logger.LogDebug("Opened TorrentStream for {FileName} ({Size} bytes)", Path.GetFileName(file.Path), _fileSize);
    }

    /// <summary>
    /// Whether the stream has little downloaded ahead of where it is read: from when it opens or moves,
    /// or runs down below <see cref="ThinBufferSeconds"/> at the reader's rate, until
    /// <see cref="HealthyBufferSeconds"/> are. While its rate is unknown, it is taken as buffering.
    /// </summary>
    internal bool IsBuffering => Volatile.Read(ref _buffering) == 1;

    public override bool CanRead => !_disposal.IsDisposed;
    public override bool CanSeek => !_disposal.IsDisposed;
    public override bool CanWrite => false;
    public override long Length => _fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    { /* Read-only stream, nothing to flush */ }

    public void OnPieceVerified(int pieceIndex)
    {
        if (pieceIndex >= _firstPieceIndex && pieceIndex <= _lastPieceIndex)
        {
            UpdateBuffering();
        }

        // Only signal if this piece is in the range we're waiting for
        int startPiece = Interlocked.CompareExchange(ref _waitingStartPiece, 0, 0);
        int endPiece = Interlocked.CompareExchange(ref _waitingEndPiece, 0, 0);
        if (pieceIndex < startPiece || pieceIndex > endPiece)
        {
            return;
        }

        // Notification and resource disposal share a lock so a late notification is harmless.
        lock (_lifetimeLock)
        {
            if (!_disposal.IsDisposed && _dataSignal.CurrentCount == 0)
            {
                _dataSignal.Release();
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Synchronous reads are not supported. Use ReadAsync.");
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_lifetimeLock)
        {
            _disposal.ThrowIfDisposed(this);
            if (_readers != 0) throw new InvalidOperationException("A read is already in progress on this stream.");
            _readers++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
            cancellationToken = linked.Token;
            cancellationToken.ThrowIfCancellationRequested();

            // A zero return is reserved for genuine end-of-file. Cancellation and stalled
            // downloads throw instead, so callers such as Stream.CopyToAsync cannot mistake
            // either one for a completely read file.
            if (_position >= _fileSize)
            {
                return 0;
            }

            int requested = (int)Math.Min(buffer.Length, _fileSize - _position);
            if (requested <= 0)
            {
                return 0;
            }

            // Wait for at least some data to be available, get how much we can read
            int available = await WaitForDataAsync(_position, requested, cancellationToken).ConfigureAwait(false);
            long absoluteOffset = _fileStartOffset + _position;
            await _torrent.FilesInternal.ReadAsync(absoluteOffset, buffer[..available], cancellationToken).ConfigureAwait(false);

            _position += available;
            RecordRead(available);

            // Look-ahead update: periodically update priorities as we read forward
            if (_position - _lastPriorityUpdatePosition >= PriorityUpdateIntervalBytes)
            {
                UpdatePriorities(_position);
            }

            return available;
        }
        finally
        {
            lock (_lifetimeLock)
            {
                _readers--;
                if (_disposal.IsDisposed) DisposeReadResources();
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _disposal.ThrowIfDisposed(this);
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => SaturatingAdd(_position, offset),
            SeekOrigin.End => SaturatingAdd(_fileSize, offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPos < 0)
        {
            newPos = 0;
        }

        if (newPos > _fileSize)
        {
            newPos = _fileSize;
        }

        if (newPos != _position)
        {
            _position = newPos;
            UpdatePriorities(_position);
            _logger.LogTrace("Seek to {Position}", _position);
        }

        return _position;
    }

    private static long SaturatingAdd(long position, long offset) =>
        offset > long.MaxValue - position ? long.MaxValue : position + offset;

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        bool closed;
        lock (_lifetimeLock)
        {
            closed = _disposal.MarkDisposed();
            if (closed && disposing)
            {
                _lifetimeCts.Cancel();
                if (_readers == 0) DisposeReadResources();
            }
        }
        if (closed && disposing) _controller.OnStreamDisposed(this);
        base.Dispose(disposing);
    }

    private void DisposeReadResources()
    {
        _dataSignal.Dispose();
        _lifetimeCts.Dispose();
    }

    /// <summary>
    /// Calculates how many contiguous bytes are available starting from the given offset.
    /// </summary>
    private int CalculateAvailableBytes(long absoluteOffset, int requestedLength, int pieceSize)
    {
        int startPiece = (int)(absoluteOffset / pieceSize);

        int available = 0;
        long currentOffset = absoluteOffset;
        long endOffset = absoluteOffset + requestedLength;

        int piece = startPiece;
        while (currentOffset < endOffset)
        {
            if (!_torrent.Pieces.HasPiece(piece))
            {
                break; // Gap in data
            }

            // Calculate how much of this piece we can use
            long pieceStart = (long)piece * pieceSize;
            long pieceEnd = pieceStart + pieceSize;

            // Clamp to our read range
            long readStart = Math.Max(pieceStart, currentOffset);
            long readEnd = Math.Min(pieceEnd, endOffset);

            available += (int)(readEnd - readStart);
            currentOffset = pieceEnd;
            piece++;
        }

        return available;
    }

    private void RecordRead(int bytes)
    {
        long now = _timeProvider.GetTimestamp();
        if (_firstReadTimestamp < 0)
        {
            _firstReadTimestamp = now;
        }

        _reads.Enqueue((now, bytes));
        _readsBytes += bytes;
        while (_reads.Count > 1 && _timeProvider.GetElapsedTime(_reads.Peek().Timestamp, now) > ReadRateWindow)
        {
            _readsBytes -= _reads.Dequeue().Bytes;
        }

        Volatile.Write(ref _publishedRate, ReadRate() ?? 0);
    }

    /// <summary>
    /// How far ahead to fetch: <see cref="StreamingSettings.ReadAheadSeconds"/> at the rate the stream
    /// has been read over the last <see cref="ReadRateWindow"/>, once that has settled, and never less
    /// than <see cref="StreamingSettings.ReadAheadBytes"/> nor more than <see cref="MaxReadAheadBytes"/>.
    /// </summary>
    private long ReadAheadBytes() =>
        _readAheadTime > TimeSpan.Zero && ReadRate() is { } rate
            ? Math.Max(_readAheadBytes, (long)Math.Min(MaxReadAheadBytes, rate * _readAheadTime.TotalSeconds))
            : _readAheadBytes;

    /// <summary>The rate the stream has been read at over the last <see cref="ReadRateWindow"/>, once it has settled; null before.</summary>
    private double? ReadRate()
    {
        if (_reads.Count == 0 || _timeProvider.GetElapsedTime(_firstReadTimestamp) < ReadRateSettles)
        {
            return null;
        }

        double seconds = Math.Max(1, _timeProvider.GetElapsedTime(_reads.Peek().Timestamp).TotalSeconds);
        return _readsBytes / seconds;
    }

    /// <summary>
    /// Works out <see cref="IsBuffering"/> afresh: how many seconds, at the reader's rate, are downloaded
    /// in one run from where it reads. Called as pieces arrive and as the reader moves, from either thread.
    /// </summary>
    private void UpdateBuffering()
    {
        double rate = Volatile.Read(ref _publishedRate);
        if (rate <= 0)
        {
            Volatile.Write(ref _buffering, 1);
            return;
        }

        bool buffering = IsBuffering;
        long wanted = (long)(rate * (buffering ? HealthyBufferSeconds : ThinBufferSeconds));
        long ahead = DownloadedAhead(Volatile.Read(ref _position), wanted);
        if (buffering && ahead >= wanted)
        {
            Volatile.Write(ref _buffering, 0);
        }
        else if (!buffering && ahead < wanted)
        {
            Volatile.Write(ref _buffering, 1);
        }
    }

    /// <summary>How much is downloaded in one run from <paramref name="position"/> in the file, counting no further than <paramref name="enough"/>.</summary>
    private long DownloadedAhead(long position, long enough)
    {
        int pieceSize = (int)_torrent.InfoFile.Info.PieceSize;
        long absolute = _fileStartOffset + position;
        long fileEnd = _fileStartOffset + _fileSize;
        long ahead = 0;
        for (int piece = (int)(absolute / pieceSize); piece <= _lastPieceIndex && ahead < enough; piece++)
        {
            if (!_torrent.Pieces.HasPiece(piece))
            {
                return ahead;
            }

            long pieceEnd = Math.Min((long)(piece + 1) * pieceSize, fileEnd);
            ahead += pieceEnd - Math.Max(absolute, (long)piece * pieceSize);
        }

        // The rest of the file, all here, is as much as any reader can want.
        return absolute + ahead >= fileEnd ? long.MaxValue : ahead;
    }

    private void UpdatePriorities(long playheadPosition)
    {
        UpdateBuffering();
        _lastPriorityUpdatePosition = playheadPosition;
        if (_fileSize == 0)
        {
            _controller.UpdatePriorities(this, []);
            return;
        }

        int pieceSize = (int)_torrent.InfoFile.Info.PieceSize;
        long absolutePlayhead = _fileStartOffset + playheadPosition;
        long bufferEnd = absolutePlayhead + Math.Min(_fileSize - playheadPosition, ReadAheadBytes());

        int playheadPiece = (int)(absolutePlayhead / pieceSize);
        int bufferEndPiece = (int)(bufferEnd / pieceSize);

        // Clamp to file range
        playheadPiece = Math.Max(playheadPiece, _firstPieceIndex);
        bufferEndPiece = Math.Min(bufferEndPiece, _lastPieceIndex);

        var highPriority = new List<int>();
        var added = new HashSet<int>();

        // 1. What the reader needs next, from where it is.
        for (int i = playheadPiece; i <= bufferEndPiece; i++)
        {
            if (!_torrent.Pieces.HasPiece(i))
            {
                highPriority.Add(i);
                added.Add(i);
            }
        }

        // 2. The start of the file, in order, then its end: what a player reads to open a file, and
        //    an index kept at the end. After the read position, not before it: a reader carrying on
        //    part-way in waits on where it is, and one opening the file is at its start already. This
        //    was the first three pieces, ahead of the read position and in reverse order - up to
        //    48 MB of a film fetched before the piece a resuming player was waiting for.
        long startEnd = _fileStartOffset + Math.Min(_fileSize, FileStartPriorityBytes) - 1;
        for (int i = _firstPieceIndex; i <= (int)(startEnd / pieceSize); i++)
        {
            if (!_torrent.Pieces.HasPiece(i) && added.Add(i))
            {
                highPriority.Add(i);
            }
        }

        long endStart = _fileStartOffset + _fileSize - FileEndPriorityBytes;
        int endPieceStart = (int)(endStart / pieceSize);
        endPieceStart = Math.Max(endPieceStart, _firstPieceIndex);

        for (int i = endPieceStart; i <= _lastPieceIndex; i++)
        {
            if (!_torrent.Pieces.HasPiece(i) && added.Add(i))
            {
                highPriority.Add(i);
            }
        }

        // Merged with every other open stream's by the controller, this one first.
        _controller.UpdatePriorities(this, highPriority);
    }

    /// <summary>
    /// Waits for data to be available and returns the number of bytes that can be read.
    /// Always returns a positive count: it throws rather than reporting "no data", because a
    /// zero-byte read on a <see cref="Stream"/> means end-of-file to every caller.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled the read.</exception>
    /// <exception cref="TimeoutException">
    /// No piece covering the requested offset arrived within <see cref="_dataWaitTimeout"/>.
    /// </exception>
    private async Task<int> WaitForDataAsync(long position, int requestedLength, CancellationToken ct)
    {
        long absoluteOffset = _fileStartOffset + position;
        int pieceSize = (int)_torrent.InfoFile.Info.PieceSize;

        // Quick check - if first piece is available, calculate how much we can read
        int availableBytes = CalculateAvailableBytes(absoluteOffset, requestedLength, pieceSize);
        if (availableBytes > 0)
        {
            return availableBytes;
        }

        // Ensure priorities are set correctly for this range
        UpdatePriorities(position);

        // Track which pieces we're waiting for (so OnPieceVerified only signals for relevant pieces)
        Interlocked.Exchange(ref _waitingStartPiece, (int)(absoluteOffset / pieceSize));
        Interlocked.Exchange(ref _waitingEndPiece, (int)((absoluteOffset + requestedLength - 1) / pieceSize));

        try
        {
            var startTime = _timeProvider.GetUtcNow();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                availableBytes = CalculateAvailableBytes(absoluteOffset, requestedLength, pieceSize);
                if (availableBytes > 0)
                {
                    return availableBytes;
                }

                if (_dataWaitTimeout is { } timeout && _timeProvider.GetUtcNow() - startTime > timeout)
                {
                    throw new TimeoutException(
                        $"Timed out after {timeout.TotalSeconds:0}s waiting for piece data at offset {absoluteOffset} of torrent '{_torrent.Name}'.");
                }

                // Wait for signal from OnPieceVerified, or re-check after the poll interval
                await _dataSignal.WaitAsync(DataPollInterval, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _waitingStartPiece, -1);
            Interlocked.Exchange(ref _waitingEndPiece, -1);
        }
    }
}
