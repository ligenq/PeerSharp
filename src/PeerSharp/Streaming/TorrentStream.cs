using Microsoft.Extensions.Logging;
using PeerSharp.Internals;

namespace PeerSharp.Streaming;

/// <summary>
/// A readable and seekable Stream that reads directly from a file within a torrent.
/// Handles piece prioritization and buffering automatically.
/// </summary>
internal class TorrentStream : Stream
{
    // Buffering configuration
    private const int BufferAheadBytes = 20 * 1024 * 1024;

    private const int FileEndPriorityBytes = 1 * 1024 * 1024;

    // 1MB at end for headers
    private const int PriorityUpdateIntervalBytes = 1024 * 1024;

    private readonly StreamingController _controller;
    private readonly SemaphoreSlim _dataSignal = new(0);
    private readonly long _fileSize;
    private readonly long _fileStartOffset;
    private readonly int _firstPieceIndex;
    private readonly int _lastPieceIndex;
    private readonly ILogger<TorrentStream> _logger = TorrentLoggerFactory.CreateLogger<TorrentStream>();
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private AtomicDisposal _disposal = new();
    private long _lastPriorityUpdatePosition;
    private long _position;
    private int _waitingEndPiece = -1;

    // Track the piece range we're currently waiting for (for efficient signaling)
    // Accessed from multiple threads: consumer thread (WaitForDataAsync) and torrent thread (OnPieceVerified)
    private int _waitingStartPiece = -1;

    // 20MB buffer ahead

    // Update priorities every 1MB

    internal TorrentStream(StreamingController controller, Torrent torrent, int fileIndex, TimeProvider timeProvider)
    {
        _controller = controller;
        _torrent = torrent;
        _timeProvider = timeProvider;

        // Validate file index
        if (fileIndex < 0 || fileIndex >= torrent.InfoFile.Info.Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }

        var file = torrent.InfoFile.Info.Files[fileIndex];
        _fileSize = file.Size;

        // Calculate absolute file offset in the torrent
        long offset = 0;
        for (int i = 0; i < fileIndex; i++)
        {
            offset += torrent.InfoFile.Info.Files[i].Size;
        }
        _fileStartOffset = offset;

        // Calculate piece range
        int pieceSize = (int)torrent.InfoFile.Info.PieceSize;
        _firstPieceIndex = (int)(_fileStartOffset / pieceSize);
        _lastPieceIndex = (int)((_fileStartOffset + _fileSize - 1) / pieceSize);

        // Configure for streaming
        _controller.DownloadStrategy = DownloadStrategy.Streaming;

        // Initial prioritization
        UpdatePriorities(0);

        _logger.LogDebug("Opened TorrentStream for {FileName} ({Size} bytes)", Path.GetFileName(file.Path), _fileSize);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
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
        // Only signal if this piece is in the range we're waiting for
        int startPiece = Interlocked.CompareExchange(ref _waitingStartPiece, 0, 0);
        int endPiece = Interlocked.CompareExchange(ref _waitingEndPiece, 0, 0);
        if (pieceIndex < startPiece || pieceIndex > endPiece)
        {
            return;
        }

        // Signal that relevant data is available
        if (_dataSignal.CurrentCount == 0)
        {
            try
            {
                _dataSignal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if semaphore is already disposed during shutdown
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
        if (available <= 0)
        {
            return 0;
        }
        long absoluteOffset = _fileStartOffset + _position;
        await _torrent.FilesInternal.ReadAsync(absoluteOffset, buffer.Slice(0, available), cancellationToken).ConfigureAwait(false);

        _position += available;

        // Look-ahead update: periodically update priorities as we read forward
        if (_position - _lastPriorityUpdatePosition >= PriorityUpdateIntervalBytes)
        {
            UpdatePriorities(_position);
        }

        return available;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _fileSize + offset,
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
        if (_disposal.MarkDisposed() && disposing)
        {
            // Controller handles resetting strategy if this was the active stream
            _controller.OnStreamDisposed(this);
            _dataSignal.Dispose();
        }
        base.Dispose(disposing);
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

    private void UpdatePriorities(long playheadPosition)
    {
        _lastPriorityUpdatePosition = playheadPosition;

        int pieceSize = (int)_torrent.InfoFile.Info.PieceSize;
        long absolutePlayhead = _fileStartOffset + playheadPosition;
        long bufferEnd = absolutePlayhead + BufferAheadBytes;

        int playheadPiece = (int)(absolutePlayhead / pieceSize);
        int bufferEndPiece = (int)(bufferEnd / pieceSize);

        // Clamp to file range
        playheadPiece = Math.Max(playheadPiece, _firstPieceIndex);
        bufferEndPiece = Math.Min(bufferEndPiece, _lastPieceIndex);

        var highPriority = new List<int>();
        var added = new HashSet<int>();

        // 1. Critical: Immediate playhead
        for (int i = playheadPiece; i <= bufferEndPiece; i++)
        {
            if (!_torrent.Pieces.HasPiece(i))
            {
                highPriority.Add(i);
                added.Add(i);
            }
        }

        // 2. Footer: Prioritize end of file (metadata/indexes)
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

        // 3. Header: First few pieces
        int headerPieces = Math.Min(3, _lastPieceIndex - _firstPieceIndex + 1);
        for (int i = _firstPieceIndex; i < _firstPieceIndex + headerPieces; i++)
        {
            if (!_torrent.Pieces.HasPiece(i) && added.Add(i))
            {
                highPriority.Insert(0, i);
            }
        }

        // Update streaming priorities
        // Note: This overrides priorities set by other streams if multiple are open.
        // This is a known limitation of the current per-Torrent strategy design.
        _controller.PriorityPieces = highPriority;
    }

    /// <summary>
    /// Waits for data to be available and returns the number of bytes that can be read.
    /// Returns 0 if cancelled or timed out with no data available.
    /// </summary>
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
            var timeout = TimeSpan.FromSeconds(60);

            while (!ct.IsCancellationRequested)
            {
                if (_timeProvider.GetUtcNow() - startTime > timeout)
                {
                    return 0;
                }

                availableBytes = CalculateAvailableBytes(absoluteOffset, requestedLength, pieceSize);
                if (availableBytes > 0)
                {
                    return availableBytes;
                }

                // Wait for signal from OnPieceVerified, or re-check after 1 second
                try
                {
                    await _dataSignal.WaitAsync(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }

            return 0;
        }
        finally
        {
            Interlocked.Exchange(ref _waitingStartPiece, -1);
            Interlocked.Exchange(ref _waitingEndPiece, -1);
        }
    }
}
