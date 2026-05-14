using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;
using PeerSharp.PiecePicking;

namespace PeerSharp.Internals.Transfers;

internal sealed class RequestScheduler
{
    private readonly PieceStateManager _pieceStateManager;
    private readonly int _blockSize;
    private readonly ILogger<RequestScheduler> _logger;
    private readonly PiecePicker _piecePicker;
    private readonly BlockRequestTracker _requestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private readonly IBlockRequestStrategy _standardStrategy;
    private readonly IBlockRequestStrategy _endGameStrategy;
    private readonly int _maxRequestsPerPeer;

    public RequestScheduler(RequestSchedulerOptions options, PiecePicker piecePicker)
    {
        ArgumentNullException.ThrowIfNull(options);

        _torrent = options.Torrent;
        _piecePicker = piecePicker ?? throw new ArgumentNullException(nameof(piecePicker));
        _requestTracker = options.RequestTracker;
        _pieceStateManager = options.PieceStateManager;
        _timeProvider = options.TimeProvider;
        _logger = options.Logger;
        _blockSize = options.BlockSize;
        _maxRequestsPerPeer = Math.Max(1, options.MaxRequestsPerPeer);
        _standardStrategy = new StandardBlockRequestStrategy(_requestTracker, _timeProvider, options.GetSoftTimeoutMs, _logger, _blockSize);
        _endGameStrategy = new EndGameBlockRequestStrategy(_requestTracker, _blockSize);
    }

    public async Task EvaluateNextRequestsAsync(PeerCommunication peer, bool endGameMode, Func<bool> isQueueFull)
    {
        if (isQueueFull())
        {
            return;
        }

        bool isChoked = peer.PeerChoking;
        if (isChoked)
        {
            if (!peer.AmInterested && HasWantedPieces(peer))
            {
                await peer.SetInterestedAsync(true).ConfigureAwait(false);
            }

            if (peer.AllowedFastCount == 0)
            {
                return;
            }
        }

        int maxRequests = peer.GetAdaptivePipelineDepth();
        if (maxRequests > _maxRequestsPerPeer)
        {
            maxRequests = _maxRequestsPerPeer;
        }
        int pending = 0;
        if (_requestTracker.TryGetPeerRequests(peer, out var existingReqs))
        {
            pending = existingReqs.Count;
        }

        if (pending >= maxRequests)
        {
            return;
        }

        int needed = maxRequests - pending;
        int sent = 0;

        var strategy = endGameMode ? _endGameStrategy : _standardStrategy;
        foreach (var kvp in _pieceStateManager.ActivePieces)
        {
            if (sent >= needed)
            {
                break;
            }

            var state = kvp.Value;

            if (isChoked && !peer.IsAllowedFast(state.Index))
            {
                continue;
            }

            if (!peer.PeerPieces.HasPiece(state.Index))
            {
                continue;
            }

            sent += await ProcessPieceForRequestsAsync(state, peer, needed - sent, strategy).ConfigureAwait(false);
        }

        if (sent < needed && _pieceStateManager.Count < _pieceStateManager.MaxActivePieces)
        {
            int loopLimit = 3;
            while (sent < needed && loopLimit > 0 && _pieceStateManager.Count < _pieceStateManager.MaxActivePieces)
            {
                if (_piecePicker.PickNextPiece(peer, out int pieceIndex))
                {
                    long pieceSize = _torrent.InfoFile.Info.GetPieceSize(pieceIndex);
                    int blocksCount = (int)((pieceSize + _blockSize - 1) / _blockSize);

                    var newState = new PieceState(pieceIndex, blocksCount);
                    if (_pieceStateManager.TryAddPiece(newState))
                    {
                        _logger.LogDebug("NEW PIECE {PieceIndex} started: {BlocksCount} blocks, {Size} bytes, active pieces now={ActiveCount}", pieceIndex, blocksCount, pieceSize, _pieceStateManager.Count);
                        sent += await ProcessPieceForRequestsAsync(newState, peer, needed - sent, strategy).ConfigureAwait(false);
                    }
                    loopLimit--;
                }
                else
                {
                    break;
                }
            }
        }

        if (sent > 0)
        {
            _logger.LogTrace("Sent {SentCount} requests to {RemoteEndPoint}", sent, peer.RemoteEndPoint);
        }
    }

    private bool HasWantedPieces(PeerCommunication peer)
    {
        var candidates = _piecePicker.GetCandidates();
        foreach (var idx in candidates)
        {
            if (peer.PeerPieces.HasPiece(idx) && _piecePicker.IsPieceNeeded(idx))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<int> ProcessPieceForRequestsAsync(PieceState state, PeerCommunication peer, int maxToSend, IBlockRequestStrategy strategy)
    {
        if (maxToSend <= 0 || state.IsWriting)
        {
            return 0;
        }

        int sent = 0;
        int pieceIndex = state.Index;
        var now = _timeProvider.GetUtcNow();

        long pSize = _torrent.InfoFile.Info.GetPieceSize(pieceIndex);

        bool isPeerFast = peer.SmoothedDownloadSpeed > 100_000;

        var spans = BuildRequestableSpans(state, peer, isPeerFast, strategy);
        foreach (var span in spans)
        {
            for (int b = span.StartBlock; b < span.EndBlock && sent < maxToSend; b++)
            {
                int offset = b * _blockSize;
                int length = (int)Math.Min(_blockSize, pSize - offset);

                var request = new BlockRequest
                {
                    PieceIndex = pieceIndex,
                    Offset = offset,
                    Length = length,
                    Timestamp = now,
                    Attempts = 1
                };

                _requestTracker.AddBlockRequest(pieceIndex, offset, peer, request);

                bool queued = await peer.SendRequestAsync(request).ConfigureAwait(false);
                if (!queued)
                {
                    if (_requestTracker.TryGetPeerRequests(peer, out var pending))
                    {
                        pending.TryRemove((pieceIndex, offset), out _);
                    }
                    _requestTracker.RemoveBlockRequest(pieceIndex, offset, peer);
                    return sent;
                }
                sent++;
            }
        }

        return sent;
    }

    private static List<BlockSpan> BuildRequestableSpans(PieceState state, PeerCommunication peer, bool isPeerFast, IBlockRequestStrategy strategy)
    {
        int pieceIndex = state.Index;
        int blocksCount = state.Blocks.Length;
        var spans = new List<BlockSpan>();

        int currentStart = -1;
        for (int b = 0; b < blocksCount; b++)
        {
            if (strategy.IsBlockRequestable(state, pieceIndex, b, peer, isPeerFast))
            {
                if (currentStart < 0)
                {
                    currentStart = b;
                }
            }
            else if (currentStart >= 0)
            {
                spans.Add(new BlockSpan(currentStart, b));
                currentStart = -1;
            }
        }

        if (currentStart >= 0)
        {
            spans.Add(new BlockSpan(currentStart, blocksCount));
        }

        return spans;
    }

    private readonly record struct BlockSpan(int StartBlock, int EndBlock);
}

internal sealed class RequestSchedulerOptions
{
    public required Torrent Torrent { get; init; }
    public required BlockRequestTracker RequestTracker { get; init; }
    public required PieceStateManager PieceStateManager { get; init; }
    public required TimeProvider TimeProvider { get; init; }
    public required ILogger<RequestScheduler> Logger { get; init; }
    public required int BlockSize { get; init; }
    public required int MaxRequestsPerPeer { get; init; }
    public required Func<PeerCommunication, int> GetSoftTimeoutMs { get; init; }
}
