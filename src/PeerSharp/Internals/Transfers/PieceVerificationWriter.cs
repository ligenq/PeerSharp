using System.Buffers;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Internals.Transfers;

internal sealed class PieceVerificationOutcome : IDisposable
{
    private readonly ArrayPool<byte>? _pool;
    private byte[]? _fullData;

    public PieceVerificationOutcome(bool hashSuccess, bool hashFailed, int pieceSize, byte[]? fullData, ArrayPool<byte>? pool)
    {
        HashSuccess = hashSuccess;
        HashFailed = hashFailed;
        PieceSize = pieceSize;
        _fullData = fullData;
        _pool = pool;
    }

    public bool HashSuccess { get; }
    public bool HashFailed { get; }
    public int PieceSize { get; }
    public byte[]? FullData => _fullData;

    public void Dispose()
    {
        var data = Interlocked.Exchange(ref _fullData, null);
        if (data != null)
        {
            _pool?.Return(data);
        }
    }
}

internal sealed class PieceVerificationWriter
{
    private static readonly ArrayPool<byte> PieceBufferPool =
        ArrayPool<byte>.Create(maxArrayLength: 16 * 1024 * 1024, maxArraysPerBucket: 4);

    private readonly int _blockSize;
    private readonly ILogger<PieceVerificationWriter> _logger;
    private readonly Action<int> _requestMerkleHashes;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;

    public PieceVerificationWriter(
        Torrent torrent,
        TimeProvider timeProvider,
        ILogger<PieceVerificationWriter> logger,
        int blockSize,
        Action<int> requestMerkleHashes)
    {
        _torrent = torrent;
        _timeProvider = timeProvider;
        _logger = logger;
        _blockSize = blockSize;
        _requestMerkleHashes = requestMerkleHashes;
    }

    public async Task<PieceVerificationOutcome> VerifyAsync(PieceState pieceToProcess, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        long pSize = _torrent.InfoFile.Info.GetPieceSize(pieceToProcess.Index);
        if (pSize <= 0)
        {
            return new PieceVerificationOutcome(hashSuccess: false, hashFailed: true, pieceSize: 0, fullData: null, pool: null);
        }
        int pieceSize = (int)pSize;

        bool hashSuccess = false;
        bool hashFailed = false;
        var hashStart = _timeProvider.GetUtcNow();

        // Always assemble a full piece buffer. This allows:
        // 1. Single disk write per piece (instead of 128 block-by-block writes)
        // 2. Unified verification path for both SHA1 and Merkle
        // The buffer is short-lived and GC-collected after write completes.
        byte[]? fullData = PieceBufferPool.Rent(pieceSize);
        double copyMs = 0;
        double hashMs = 0;

        bool valid = true;

        var copyStart = _timeProvider.GetUtcNow();
        for (int i = 0; i < pieceToProcess.BlockData.Length; i++)
        {
            var b = pieceToProcess.BlockData[i];
            if (b == null)
            {
                valid = false;
                break;
            }
            Array.Copy(b.Buffer, 0, fullData, i * _blockSize, b.Length);
        }
        copyMs = (_timeProvider.GetUtcNow() - copyStart).TotalMilliseconds;

        bool isMerkle = _torrent.InfoFile.Info.IsMerkle && _torrent.MerkleTree != null;
        bool isV2 = _torrent.InfoFile.Info.IsV2;

        // BEP 30: For Merkle hash torrents, use the Merkle tree for verification
        if (valid && isMerkle)
        {
            var hashCalcStart = _timeProvider.GetUtcNow();
            valid = _torrent.MerkleTree!.VerifyPiece(pieceToProcess.Index, fullData.AsSpan(0, pieceSize));
            hashMs = (_timeProvider.GetUtcNow() - hashCalcStart).TotalMilliseconds;

            if (!valid && !_torrent.MerkleTree!.CanVerifyPiece(pieceToProcess.Index))
            {
                _logger.LogDebug("BEP 30: Missing hashes for piece {PieceIndex}, requesting from peers", pieceToProcess.Index);
                _requestMerkleHashes(pieceToProcess.Index);
                valid = true; // Assume valid for now, will re-verify when hashes arrive
            }
            _logger.LogTrace("Piece {PieceIndex} Merkle verification: {Elapsed}ms, size={Size} bytes, valid={Valid}", pieceToProcess.Index, Math.Round(hashMs, 1), pieceSize, valid);
        }
        else if (valid && isV2)
        {
            var hashCalcStart = _timeProvider.GetUtcNow();
            var expected = _torrent.InfoFile.Info.GetV2ExpectedPieceHash(pieceToProcess.Index);
            bool padToPieceSize = _torrent.InfoFile.Info.ShouldPadV2PieceToPieceSize(pieceToProcess.Index);
            valid = expected != null && MerkleTree.VerifyPiece(fullData.AsSpan(0, pieceSize), pieceToProcess.Index, expected, _torrent.InfoFile.Info.PieceSize, padToPieceSize);
            hashMs = (_timeProvider.GetUtcNow() - hashCalcStart).TotalMilliseconds;
        }
        else if (valid && _torrent.InfoFile.Info.Pieces.Count > pieceToProcess.Index)
        {
            var hashCalcStart = _timeProvider.GetUtcNow();
            byte[] computed = System.Security.Cryptography.SHA1.HashData(fullData.AsSpan(0, pieceSize));
            hashMs = (_timeProvider.GetUtcNow() - hashCalcStart).TotalMilliseconds;
            var expected = _torrent.InfoFile.Info.Pieces[pieceToProcess.Index];
            if (!computed.SequenceEqual(expected))
            {
                valid = false;
            }
        }

        _logger.LogTrace("Piece {PieceIndex} hash verification: copy={CopyElapsed}ms, hash={HashElapsed}ms, size={Size} bytes, valid={Valid}",
            pieceToProcess.Index, Math.Round(copyMs, 1), Math.Round(hashMs, 1), pieceSize, valid);

        if (!valid)
        {
            hashFailed = true;
        }
        else
        {
            hashSuccess = true;
        }

        var totalMs = (_timeProvider.GetUtcNow() - hashStart).TotalMilliseconds;
        if (totalMs > 50)
        {
            _logger.LogTrace("Piece {PieceIndex} verification took {Elapsed}ms total", pieceToProcess.Index, Math.Round(totalMs, 1));
        }

        return new PieceVerificationOutcome(hashSuccess, hashFailed, pieceSize, fullData, PieceBufferPool);
    }

    public async Task<bool> WriteAsync(PieceState pieceToProcess, int pieceSize, byte[]? fullData, CancellationToken ct)
    {
        try
        {
            var writeStart = _timeProvider.GetUtcNow();
            long pieceOffset = pieceToProcess.Index * _torrent.InfoFile.Info.PieceSize;

            // Single write for the entire piece (fullData always provided now)
            await _torrent.FilesInternal.WriteAsync(pieceOffset, new ReadOnlyMemory<byte>(fullData!, 0, pieceSize), ct).ConfigureAwait(false);

            var writeMs = (_timeProvider.GetUtcNow() - writeStart).TotalMilliseconds;
            _logger.LogTrace("Piece {PieceIndex} written to disk in {Elapsed}ms", pieceToProcess.Index, Math.Round(writeMs, 1));
            return true;
        }
        // Non-recoverable storage failures (disk full, permanently failed file) deliberately
        // propagate: retrying is hopeless, and FileTransfer stops the torrent with an error
        // instead of re-downloading this piece forever.
        catch (Exception ex) when (ex is not PieceWriter.StorageException { IsRecoverable: false })
        {
            _logger.LogError(ex, "Write failed for piece {PieceIndex}", pieceToProcess.Index);
            return false;
        }
    }
}
