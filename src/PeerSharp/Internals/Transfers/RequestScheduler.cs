using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;
using PeerSharp.PiecePicking;

namespace PeerSharp.Internals.Transfers;

internal sealed class RequestScheduler
{
    /// <summary>
    /// How long one peer keeps the exclusive right to serve a piece that is being retried. Long enough
    /// that a peer of ordinary speed finishes a piece well inside it, short enough that a peer which
    /// goes quiet costs one interval rather than the download.
    /// </summary>
    private static readonly TimeSpan RetryClaimTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many of the pieces a stream needs soonest are urgent: offered to every peer ahead of any
    /// other piece, and asked of several peers at once, as the last pieces of a download are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A player waits on the piece at its read position and on nothing else. Left to the ordinary
    /// rules, that piece's blocks go to whichever peers started it, and are asked of a second peer only
    /// once one has been outstanding past its soft timeout - seconds, during which a player holding a
    /// second of video has stalled, while the fast peers fill their queues with pieces nobody needs yet.
    /// </para>
    /// <para>
    /// Two, so that the piece after the one being read is already under way when the reader reaches it.
    /// The duplicates cost little: each block is cancelled with the others once one copy lands, and only
    /// these pieces are asked of more than one peer from the start.
    /// </para>
    /// </remarks>
    internal const int UrgentStreamingPieces = 2;

    private readonly PieceStateManager _pieceStateManager;
    private readonly int _blockSize;
    private readonly ILogger<RequestScheduler> _logger;
    private readonly PiecePicker _piecePicker;
    private readonly BlockRequestTracker _requestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly Torrent _torrent;
    private readonly IBlockRequestStrategy _standardStrategy;
    private readonly IBlockRequestStrategy _endGameStrategy;
    private readonly IBlockRequestStrategy _urgentStrategy;
    private readonly Func<IReadOnlyList<int>?> _streamingPriorityPieces;

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
        _standardStrategy = new StandardBlockRequestStrategy(_requestTracker, _timeProvider, options.GetSoftTimeoutMs, _blockSize);
        _endGameStrategy = new EndGameBlockRequestStrategy(_requestTracker, _blockSize);
        _urgentStrategy = new UrgentBlockRequestStrategy(_requestTracker, _timeProvider, _blockSize);
        _streamingPriorityPieces = options.GetStreamingPriorityPieces ?? (() => _torrent.StreamingPriorityPieces);
    }

    public async Task EvaluateNextRequestsAsync(PeerCommunication peer, bool endGameMode, Func<bool> isQueueFull)
    {
        if (peer.Connected == 0 || isQueueFull())
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

        int maxRequests = Math.Min(peer.GetAdaptivePipelineDepth(), Math.Max(1, _torrent.Settings.Transfer.MaxRequestsPerPeer));
        if (peer.RemoteExtensions?.RequestQueueDepth is > 0 and var remoteCapacity)
        {
            maxRequests = Math.Min(maxRequests, remoteCapacity);
        }
        int pending = 0;
        if (_requestTracker.TryGetPeerRequests(peer, out var existingReqs))
        {
            pending = existingReqs.Count;
        }

        var streaming = _streamingPriorityPieces();
        var urgent = UrgentPieces(streaming);

        if (pending >= maxRequests)
        {
            return;
        }

        int needed = maxRequests - pending;
        int sent = 0;

        // The pieces streams need come first, in the order they need them - started here if they have
        // not been, rather than after every piece already under way has had its turn.
        var streamingIndices = new HashSet<int>();
        foreach (int index in streaming ?? [])
        {
            if (sent >= needed)
            {
                break;
            }

            if (!streamingIndices.Add(index) || (isChoked && !peer.IsAllowedFast(index)) || !peer.PeerPieces.HasPiece(index))
            {
                continue;
            }

            if (ActiveOrStarted(index) is { } state)
            {
                sent += await ProcessPieceForRequestsAsync(state, peer, needed - sent, StrategyFor(index, endGameMode, urgent)).ConfigureAwait(false);
            }
        }

        foreach (var kvp in _pieceStateManager.ActivePieces)
        {
            var state = kvp.Value;
            if (sent >= needed)
            {
                break;
            }

            if (streamingIndices.Contains(state.Index) || (isChoked && !peer.IsAllowedFast(state.Index)) || !peer.PeerPieces.HasPiece(state.Index))
            {
                continue;
            }

            sent += await ProcessPieceForRequestsAsync(state, peer, needed - sent, StrategyFor(state.Index, endGameMode, urgent)).ConfigureAwait(false);
        }

        if (sent < needed && _pieceStateManager.Count < _pieceStateManager.MaxActivePieces)
        {
            int activePieceSlotsAvailable = _pieceStateManager.MaxActivePieces - _pieceStateManager.Count;
            int loopLimit = RequestQueuePolicy.CalculateNewPieceStartLimit(
                needed - sent,
                activePieceSlotsAvailable,
                GetTypicalBlocksPerPiece());

            while (sent < needed && loopLimit > 0 && _pieceStateManager.Count < _pieceStateManager.MaxActivePieces)
            {
                if (_piecePicker.PickNextPiece(peer, out int pieceIndex))
                {
                    long pieceSize = _torrent.InfoFile.Info.GetPieceSize(pieceIndex);
                    int blocksCount = (int)((pieceSize + _blockSize - 1) / _blockSize);

                    var newState = new PieceState(pieceIndex, blocksCount);
                    if (_pieceStateManager.TryAddPiece(newState))
                    {
                        _logger.LogTrace("NEW PIECE {PieceIndex} started: {BlocksCount} blocks, {Size} bytes, active pieces now={ActiveCount}", pieceIndex, blocksCount, pieceSize, _pieceStateManager.Count);
                        sent += await ProcessPieceForRequestsAsync(newState, peer, needed - sent, StrategyFor(pieceIndex, endGameMode, urgent)).ConfigureAwait(false);
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

    private IBlockRequestStrategy StrategyFor(int pieceIndex, bool endGameMode, HashSet<int>? urgent)
    {
        if (urgent?.Contains(pieceIndex) == true)
        {
            return _urgentStrategy;
        }

        return endGameMode ? _endGameStrategy : _standardStrategy;
    }

    /// <summary>The first <see cref="UrgentStreamingPieces"/> pieces streams need that are not here yet; null when nothing streams.</summary>
    private HashSet<int>? UrgentPieces(IReadOnlyList<int>? streaming)
    {
        if (streaming is null)
        {
            return null;
        }

        var urgent = new HashSet<int>();
        foreach (int index in streaming)
        {
            if (urgent.Count == UrgentStreamingPieces)
            {
                break;
            }

            // The list is refreshed as the reader moves, so a piece in it may have arrived since.
            if (index >= 0 && index < _torrent.Pieces.Count && !_torrent.Pieces.HasPiece(index))
            {
                urgent.Add(index);
            }
        }

        return urgent;
    }

    /// <summary>
    /// The piece under way at <paramref name="index"/>, or the same started now; null when it cannot be
    /// started - it is here, not wanted, or every slot for a piece under way is taken.
    /// </summary>
    private PieceState? ActiveOrStarted(int index)
    {
        if (_pieceStateManager.ActivePieces.TryGetValue(index, out var state))
        {
            return state;
        }

        if (!CanStart(index))
        {
            return null;
        }

        long pieceSize = _torrent.InfoFile.Info.GetPieceSize(index);
        var started = new PieceState(index, (int)((pieceSize + _blockSize - 1) / _blockSize));
        if (_pieceStateManager.TryAddPiece(started))
        {
            _logger.LogTrace("NEW PIECE {PieceIndex} started for a stream, active pieces now={ActiveCount}", index, _pieceStateManager.Count);
            return started;
        }

        // Another peer's pass started it first.
        return _pieceStateManager.ActivePieces.TryGetValue(index, out state) ? state : null;
    }

    private bool CanStart(int index) =>
        !_torrent.Pieces.HasPiece(index)
        && _pieceStateManager.Count < _pieceStateManager.MaxActivePieces
        && _piecePicker.IsPieceNeeded(index);

    private int GetTypicalBlocksPerPiece()
    {
        long pieceSize = Math.Max(1, _torrent.InfoFile.Info.PieceSize);
        return (int)Math.Max(1, (pieceSize + _blockSize - 1) / _blockSize);
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
        if (maxToSend <= 0 || state.IsWriting || peer.Connected == 0)
        {
            return 0;
        }

        int sent = 0;
        int pieceIndex = state.Index;
        var now = _timeProvider.GetUtcNow();

        // A piece that has already failed its hash is asked of one peer at a time. Restricting who is
        // asked, rather than whose blocks are accepted, is what keeps this safe: a peer that stops
        // answering has its requests time out and reassigned as usual, where refusing its blocks would
        // have left the piece unable to complete from anyone.
        if (!state.TryClaimForRetry(peer, now, RetryClaimTimeout))
        {
            return 0;
        }

        // Disconnect cleanup releases retry claims, but an evaluation already in flight can race it:
        // observe Connected before cleanup, claim after cleanup, and leave the dead peer as owner for
        // another thirty seconds. Recheck after claiming and give it back immediately.
        if (peer.Connected == 0)
        {
            state.ReleaseRetryClaim(peer);
            return 0;
        }

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

                // Read before adding our own, so this counts the peers that already owed us this block.
                int alreadyOutstanding = _requestTracker.GetPendingRequestCount(pieceIndex, offset);

                _requestTracker.AddBlockRequest(pieceIndex, offset, peer, request);

                bool queued = await peer.SendRequestAsync(request).ConfigureAwait(false);
                if (!queued)
                {
                    _requestTracker.TryRemovePeerRequest(peer, (pieceIndex, offset), out _, request);
                    if (peer.Connected == 0)
                    {
                        state.ReleaseRetryClaim(peer);
                    }
                    return sent;
                }

                if (alreadyOutstanding > 0)
                {
                    // Logged here rather than where requestability is decided, so a line means a
                    // duplicate request genuinely went out. Trace because on a healthy transfer this is
                    // a routine reaction to one slow peer, not a fault.
                    _logger.LogTrace(
                        "Duplicate request {PieceIndex}:{Offset} sent to {RemoteEndPoint}; {Outstanding} peer(s) already owed it",
                        pieceIndex, offset, peer.RemoteEndPoint, alreadyOutstanding);
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
    public required Func<PeerCommunication, int> GetSoftTimeoutMs { get; init; }

    /// <summary>The pieces open streams need soonest, first first; the torrent's own when not given.</summary>
    public Func<IReadOnlyList<int>?>? GetStreamingPriorityPieces { get; init; }
}
