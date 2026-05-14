using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal interface IBlockRequestStrategy
{
    bool IsBlockRequestable(PieceState state, int pieceIndex, int blockIndex, PeerCommunication peer, bool isPeerFast);
}

internal sealed class StandardBlockRequestStrategy : IBlockRequestStrategy
{
    private readonly BlockRequestTracker _requestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly Func<PeerCommunication, int> _getSoftTimeoutMs;
    private readonly ILogger _logger;
    private readonly int _blockSize;

    public StandardBlockRequestStrategy(
        BlockRequestTracker requestTracker,
        TimeProvider timeProvider,
        Func<PeerCommunication, int> getSoftTimeoutMs,
        ILogger logger,
        int blockSize)
    {
        _requestTracker = requestTracker;
        _timeProvider = timeProvider;
        _getSoftTimeoutMs = getSoftTimeoutMs;
        _logger = logger;
        _blockSize = blockSize;
    }

    public bool IsBlockRequestable(PieceState state, int pieceIndex, int blockIndex, PeerCommunication peer, bool isPeerFast)
    {
        if (state.Blocks[blockIndex])
        {
            return false;
        }

        int offset = blockIndex * _blockSize;
        var existingRequest = _requestTracker.GetOldestPendingRequest(pieceIndex, offset, _timeProvider.GetUtcNow());
        if (!existingRequest.HasValue)
        {
            return true;
        }

        if (!isPeerFast)
        {
            return false;
        }

        int softTimeout = _getSoftTimeoutMs(peer);
        if (existingRequest.Value.AgeMs <= softTimeout)
        {
            return false;
        }

        if (_requestTracker.HasPendingRequestFromPeer(pieceIndex, offset, peer))
        {
            return false;
        }

        _logger.LogDebug("Duplicating stale request {PieceIndex}:{Offset} (age={Age}ms, timeout={Timeout}ms) to fast peer {RemoteEndPoint}",
            pieceIndex, offset, existingRequest.Value.AgeMs, softTimeout, peer.RemoteEndPoint);
        return true;
    }
}

internal sealed class EndGameBlockRequestStrategy : IBlockRequestStrategy
{
    private readonly BlockRequestTracker _requestTracker;
    private readonly int _blockSize;

    public EndGameBlockRequestStrategy(BlockRequestTracker requestTracker, int blockSize)
    {
        _requestTracker = requestTracker;
        _blockSize = blockSize;
    }

    public bool IsBlockRequestable(PieceState state, int pieceIndex, int blockIndex, PeerCommunication peer, bool isPeerFast)
    {
        if (state.Blocks[blockIndex])
        {
            return false;
        }

        int offset = blockIndex * _blockSize;
        return !_requestTracker.HasPendingRequestFromPeer(pieceIndex, offset, peer);
    }
}
