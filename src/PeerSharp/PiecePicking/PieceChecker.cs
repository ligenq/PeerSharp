using Microsoft.Extensions.Logging;
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
    private readonly ILogger<PieceChecker> _logger = TorrentLoggerFactory.CreateLogger<PieceChecker>();
    private readonly IProgress<PieceCheckProgress>? _progress;
    private CancellationTokenSource? _cts;
    private AtomicDisposal _disposal = new();
    private int _isRunning;

    public PieceChecker(IInternalFiles files, IPieceCheckerContext context, IProgress<PieceCheckProgress>? progress = null)
    {
        _files = files;
        _context = context;
        _progress = progress;
    }

    public bool IsRunning => Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1;

    /// <summary>
    /// Cancels an ongoing piece check.
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Checks all pieces and updates the bitfield based on actual file content.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of valid pieces found.</returns>
    public async Task<int> CheckAllPiecesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            throw new InvalidOperationException("Piece check is already running");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            return await CheckPiecesInternalAsync(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Checks a specific range of pieces.
    /// </summary>
    public async Task<int> CheckPieceRangeAsync(int startPiece, int endPiece, CancellationToken ct = default)
    {
        int validPieces = 0;
        bool isMerkle = _context.IsMerkle;
        bool isV2 = _context.IsV2;

        for (int pieceIndex = startPiece; pieceIndex <= endPiece && pieceIndex < _context.PieceCount; pieceIndex++)
        {
            ct.ThrowIfCancellationRequested();

            long pieceSize = _context.GetPieceSize(pieceIndex);

            long pieceOffset = pieceIndex * _context.PieceSize;

            try
            {
                byte[] pieceBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)pieceSize);
                try
                {
                    var pieceData = pieceBuffer.AsMemory(0, (int)pieceSize);
                    await _files.ReadAsync(pieceOffset, pieceData, ct).ConfigureAwait(false);

                    bool isValid;
                    if (isMerkle || isV2)
                    {
                        isValid = _context.VerifyPiece(pieceIndex, pieceData.Span);
                    }
                    else
                    {
                        var expected = _context.GetExpectedHash(pieceIndex);
                        if (expected != null)
                        {
                            var computed = SHA1.HashData(pieceData.Span);
                            isValid = computed.SequenceEqual(expected);
                        }
                        else
                        {
                            isValid = false;
                        }
                    }

                    if (isValid)
                    {
                        _context.AddPiece(pieceIndex);
                        validPieces++;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(pieceBuffer);
                }
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
        if (_disposal.MarkDisposed() && _cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
        GC.SuppressFinalize(this);
    }

    private async Task<int> CheckPiecesInternalAsync(CancellationToken ct)
    {
        int totalPieces = _context.PieceCount;
        int validPieces = 0;
        int checkedPieces = 0;

        bool isMerkle = _context.IsMerkle;
        bool isV2 = _context.IsV2;

        // Reset pieces - we'll rebuild the bitfield based on actual data
        var newProgress = new PiecesProgress(totalPieces);

        _logger.LogInformation("Starting piece check for {TorrentName}: {TotalPieces} pieces", _context.TorrentName, totalPieces);

        for (int pieceIndex = 0; pieceIndex < totalPieces; pieceIndex++)
        {
            ct.ThrowIfCancellationRequested();

            long pieceSize = _context.GetPieceSize(pieceIndex);

            long pieceOffset = pieceIndex * _context.PieceSize;

            try
            {
                // Read piece data from storage
                byte[] pieceBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)pieceSize);
                try
                {
                    var pieceData = pieceBuffer.AsMemory(0, (int)pieceSize);
                    await _files.ReadAsync(pieceOffset, pieceData, ct).ConfigureAwait(false);

                    bool isValid;
                    if (isMerkle || isV2)
                    {
                        isValid = _context.VerifyPiece(pieceIndex, pieceData.Span);
                    }
                    else
                    {
                        var expected = _context.GetExpectedHash(pieceIndex);
                        if (expected != null)
                        {
                            // Standard SHA-1 verification
                            var computed = SHA1.HashData(pieceData.Span);
                            isValid = computed.SequenceEqual(expected);
                        }
                        else
                        {
                            // No hash available - skip
                            isValid = false;
                        }
                    }

                    if (isValid)
                    {
                        newProgress.AddPiece(pieceIndex);
                        validPieces++;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(pieceBuffer);
                }

                checkedPieces++;

                // Report progress
                _progress?.Report(new PieceCheckProgress
                {
                    CheckedPieces = checkedPieces,
                    TotalPieces = totalPieces,
                    ValidPieces = validPieces,
                    CurrentPiece = pieceIndex,
                    Progress = (float)checkedPieces / totalPieces
                });
            }
            catch (IOException)
            {
                // File doesn't exist or can't be read - piece is invalid
                checkedPieces++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking piece {PieceIndex}", pieceIndex);
                checkedPieces++;
            }
        }

        // Update the torrent's piece progress with verified data
        _context.UpdatePiecesFromBitfield(newProgress.ToBitfield());

        _logger.LogInformation("Piece check complete for {TorrentName}: {ValidPieces}/{TotalPieces} valid", _context.TorrentName, validPieces, totalPieces);

        return validPieces;
    }
}
