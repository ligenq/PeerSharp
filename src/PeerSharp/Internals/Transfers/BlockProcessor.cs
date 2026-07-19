using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal sealed class BlockProcessor
{
    private readonly PieceStateManager _pieceStateManager;
    private readonly int _blockSize;
    private readonly Func<PieceState, Task> _enqueuePeerPieceAsync;
    private readonly Func<PieceState, CancellationToken, Task> _enqueueWebSeedPieceAsync;
    private readonly TransferStats _downloader;
    private readonly RequestCompletionTracker _requestCompletionTracker;
    private readonly Torrent _torrent;
    private readonly ILogger<BlockProcessor> _logger;
    private readonly Func<int, int, PeerCommunication, Task> _cancelBlockRequestAsync;

    public BlockProcessor(BlockProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _pieceStateManager = options.PieceStateManager;
        _blockSize = options.BlockSize;
        _enqueuePeerPieceAsync = options.EnqueuePeerPiece;
        _enqueueWebSeedPieceAsync = options.EnqueueWebSeedPiece;
        _downloader = options.Downloader;
        _requestCompletionTracker = options.RequestCompletionTracker;
        _torrent = options.Torrent;
        _cancelBlockRequestAsync = options.CancelBlockRequest;
        _logger = options.Logger;
    }

    public async Task HandleWebSeedBlockReceivedAsync(Block block, CancellationToken ct)
    {
        // Ensure piece state exists for this piece
        long pieceSize = _torrent.InfoFile.Info.GetPieceSize(block.PieceIndex);
        int blocksPerPiece = (int)Math.Ceiling((double)pieceSize / _blockSize);

        var newState = new PieceState(block.PieceIndex, blocksPerPiece);
        _pieceStateManager.TryAddPiece(newState);

        try
        {
            await ProcessWebSeedBlockAsync(block, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSeed block processing failed");
        }
    }

    public async Task HandlePeerBlockAsync(PeerCommunication peer, Block block)
    {
        _logger.LogDebug("Block received: Piece {PieceIndex}, Offset {Offset}, Length {Length} from {RemoteEndPoint}",
            block.PieceIndex, block.Offset, block.Length, peer.RemoteEndPoint);

        PieceState? pieceToProcess = null;
        bool stored = false;

        _pieceStateManager.TryGetPiece(block.PieceIndex, out var state);

        if (state != null)
        {
            if (!IsValidRequestedBlock(peer, block, out _))
            {
                _logger.LogDebug(
                    "Rejected unsolicited or malformed block: Piece {PieceIndex}, Offset {Offset}, Length {Length} from {RemoteEndPoint}",
                    block.PieceIndex,
                    block.Offset,
                    block.Length,
                    peer.RemoteEndPoint);
                block.Dispose();
                return;
            }

            int blockIdx = block.Offset / _blockSize;

            if (state.TryAddBlock(blockIdx, block, peer))
            {
                stored = true;

                _downloader.AddDownloaded(block.Length);
                peer.AddDownloaded(block.Length);

                await _cancelBlockRequestAsync(block.PieceIndex, block.Offset, peer).ConfigureAwait(false);

                if (state.TryCompleteAndSetWriting())
                {
                    pieceToProcess = state;

                    _logger.LogDebug("Piece {PieceIndex} all blocks received ({BlockCount} blocks), starting hash verification", state.Index, state.Blocks.Length);
                }
            }
        }
        else
        {
            _logger.LogDebug("Received block for inactive piece {PieceIndex}", block.PieceIndex);
        }

        if (!stored)
        {
            block.Dispose();
            if (state == null)
            {
                return;
            }
        }

        if (pieceToProcess != null)
        {
            await _enqueuePeerPieceAsync(pieceToProcess).ConfigureAwait(false);
        }

        _requestCompletionTracker.HandleBlockReceived(peer, block);
    }

    private bool IsValidRequestedBlock(PeerCommunication peer, Block block, out BlockRequest request)
    {
        request = default!;
        if (block.PieceIndex < 0 ||
            block.Offset < 0 ||
            block.Length <= 0 ||
            block.Offset % _blockSize != 0)
        {
            return false;
        }

        if (!_requestCompletionTracker.TryGetPendingRequest(peer, block, out request))
        {
            return false;
        }

        return request.Length == block.Length;
    }

    private async Task ProcessWebSeedBlockAsync(Block block, CancellationToken ct)
    {
        _logger.LogDebug("WebSeed block received: Piece {PieceIndex}, Offset {Offset}, Length {Length}", block.PieceIndex, block.Offset, block.Length);

        PieceState? pieceToProcess = null;
        bool stored = false;

        _pieceStateManager.TryGetPiece(block.PieceIndex, out var state);

        if (state != null)
        {
            int blockIdx = block.Offset / _blockSize;

            if (state.TryAddBlockFromWebSeed(blockIdx, block))
            {
                stored = true;
                _downloader.AddDownloaded(block.Length);

                if (state.TryCompleteAndSetWriting())
                {
                    pieceToProcess = state;

                    _logger.LogDebug("WebSeed: Piece {PieceIndex} all blocks received ({BlockCount} blocks), starting hash verification", state.Index, state.Blocks.Length);
                }
            }
        }
        else
        {
            _logger.LogDebug("WebSeed: Received block for inactive piece {PieceIndex}", block.PieceIndex);
        }

        if (!stored)
        {
            block.Dispose();
        }

        if (pieceToProcess != null)
        {
            await _enqueueWebSeedPieceAsync(pieceToProcess, ct).ConfigureAwait(false);
        }
    }
}

internal sealed class BlockProcessorOptions
{
    public required PieceStateManager PieceStateManager { get; init; }
    public required int BlockSize { get; init; }
    public required Func<PieceState, Task> EnqueuePeerPiece { get; init; }
    public required Func<PieceState, CancellationToken, Task> EnqueueWebSeedPiece { get; init; }
    public required TransferStats Downloader { get; init; }
    public required RequestCompletionTracker RequestCompletionTracker { get; init; }
    public required Torrent Torrent { get; init; }
    public required Func<int, int, PeerCommunication, Task> CancelBlockRequest { get; init; }
    public required ILogger<BlockProcessor> Logger { get; init; }
}
