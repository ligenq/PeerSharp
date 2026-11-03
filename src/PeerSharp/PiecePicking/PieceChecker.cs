using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;
using System.Security.Cryptography;

namespace PeerSharp.PiecePicking;

/// <summary>
/// Verifies downloaded pieces against their hashes.
/// Used for checking existing data on resume and force recheck.
/// </summary>
internal class PieceChecker : IAsyncDisposable
{
    private readonly IPieceCheckerContext _context;
    private readonly IInternalFiles _files;
    private readonly ILogger<PieceChecker> _logger;
    private readonly IProgress<PieceCheckProgress>? _progress;
    // Bytes of piece buffers a check may hold at once, and a cap on pieces in flight whatever the
    // piece size. Four is enough to keep a fast disk busy without taking every core from the rest of
    // the engine.
    private const long CheckMemoryBudget = 64L * 1024 * 1024;
    private const int MaxCheckConcurrency = 4;
    private CancellationTokenSource? _cts;
    private AtomicDisposal _disposal = new();
    private int _isRunning;

    public PieceChecker(IInternalFiles files, IPieceCheckerContext context, IProgress<PieceCheckProgress>? progress = null)
        : this(files, context, progress, NullLoggerFactory.Instance)
    {
    }

    public PieceChecker(IInternalFiles files, IPieceCheckerContext context, IProgress<PieceCheckProgress>? progress, ILoggerFactory loggerFactory)
    {
        _files = files;
        _context = context;
        _progress = progress;
        _logger = loggerFactory.CreateLogger<PieceChecker>();
    }

    public bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

    /// <summary>
    /// Cancels an ongoing piece check.
    /// </summary>
    public void Cancel()
    {
        try
        {
            Volatile.Read(ref _cts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The check already finished and disposed its cancellation source.
        }
    }

    /// <summary>
    /// Checks all pieces and updates the bitfield based on actual file content.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of valid pieces found.</returns>
    public async Task<int> CheckAllPiecesAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            throw new InvalidOperationException("Piece check is already running");
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = source.Token;
        Volatile.Write(ref _cts, source);
        if (_disposal.IsDisposed)
        {
            await source.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            return await CheckPiecesInternalAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, source);
            source.Dispose();
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    /// <summary>
    /// Checks a specific range of pieces.
    /// </summary>
    public async Task<int> CheckPieceRangeAsync(int startPiece, int endPiece, CancellationToken ct = default)
    {
        int validPieces = 0;

        for (int pieceIndex = startPiece; pieceIndex <= endPiece && pieceIndex < _context.PieceCount; pieceIndex++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await IsPieceValidAsync(pieceIndex, ct).ConfigureAwait(false))
                {
                    _context.AddPiece(pieceIndex);
                    validPieces++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Piece is invalid
            }
        }

        return validPieces;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed() && Volatile.Read(ref _cts) is { } source)
        {
            try
            {
                await source.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The check completed concurrently; it owns disposal of its source.
            }
        }
        GC.SuppressFinalize(this);
    }

    private async Task<int> CheckPiecesInternalAsync(CancellationToken ct)
    {
        int totalPieces = _context.PieceCount;
        int validPieces = 0;
        int checkedPieces = 0;
        var progressLock = new Lock();

        // Reset pieces - we'll rebuild the bitfield based on actual data
        var newProgress = new PiecesProgress(totalPieces);

        int workers = GetCheckConcurrency(_context.PieceSize, Environment.ProcessorCount);
        _logger.LogInformation("Starting piece check for {TorrentName}: {TotalPieces} pieces, {Workers} at a time", _context.TorrentName, totalPieces, workers);

        // Several pieces at once, so that one piece is being read while another is hashed. Checking them
        // one after the other left the disk idle during every hash and the CPU idle during every read,
        // which is why a recheck ran well under either's speed. Pieces are handed out in index order,
        // so reads stay close to sequential.
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, totalPieces), options, async (pieceIndex, token) =>
        {
            bool isValid = false;
            try
            {
                isValid = await IsPieceValidAsync(pieceIndex, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                // File doesn't exist or can't be read - piece is invalid
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking piece {PieceIndex}", pieceIndex);
            }

            if (isValid)
            {
                newProgress.AddPiece(pieceIndex);
                Interlocked.Increment(ref validPieces);
            }

            // Reported under a lock: a caller's IProgress need not be thread-safe, and CheckedPieces
            // should only ever go up.
            lock (progressLock)
            {
                checkedPieces++;
                _progress?.Report(new PieceCheckProgress
                {
                    CheckedPieces = checkedPieces,
                    TotalPieces = totalPieces,
                    ValidPieces = Volatile.Read(ref validPieces),
                    CurrentPiece = pieceIndex,
                    Progress = (float)checkedPieces / totalPieces
                });
            }
        }).ConfigureAwait(false);

        // Update the torrent's piece progress with verified data
        _context.UpdatePiecesFromBitfield(newProgress.ToBitfield());

        _logger.LogInformation("Piece check complete for {TorrentName}: {ValidPieces}/{TotalPieces} valid", _context.TorrentName, validPieces, totalPieces);

        return validPieces;
    }

    /// <summary>
    /// How many pieces to check at once: enough to overlap reading with hashing and to use a few cores,
    /// bounded so the buffers in flight stay within <see cref="CheckMemoryBudget"/>. libtorrent bounds
    /// its checking the same way, by <c>checking_mem_usage</c>.
    /// </summary>
    internal static int GetCheckConcurrency(long pieceSize, int processorCount)
    {
        int byMemory = (int)Math.Clamp(CheckMemoryBudget / Math.Max(1, pieceSize), 1, int.MaxValue);
        return Math.Clamp(Math.Min(processorCount, byMemory), 1, MaxCheckConcurrency);
    }

    private async Task<bool> IsPieceValidAsync(int pieceIndex, CancellationToken ct)
    {
        long pieceSize = _context.GetPieceSize(pieceIndex);
        long pieceOffset = pieceIndex * _context.PieceSize;

        byte[] pieceBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)pieceSize);
        try
        {
            var pieceData = pieceBuffer.AsMemory(0, (int)pieceSize);
            await _files.ReadAsync(pieceOffset, pieceData, ct).ConfigureAwait(false);

            if (_context.IsMerkle || _context.IsV2)
            {
                return _context.VerifyPiece(pieceIndex, pieceData.Span);
            }

            var expected = _context.GetExpectedHash(pieceIndex);
            if (expected == null)
            {
                // No hash available - skip
                return false;
            }

            // AsSpan first: byte[].SequenceEqual(byte[]) binds to LINQ's Enumerable.SequenceEqual,
            // which allocates two enumerators and compares element by element. The span overload is
            // vectorised.
            var computed = SHA1.HashData(pieceData.Span);
            return computed.AsSpan().SequenceEqual(expected);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pieceBuffer);
        }
    }
}
