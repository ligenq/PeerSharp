using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal interface IBlockRequestStrategy
{
    bool IsBlockRequestable(PieceState state, int pieceIndex, int blockIndex, PeerCommunication peer, bool isPeerFast);
}

internal sealed class StandardBlockRequestStrategy : IBlockRequestStrategy
{
    /// <summary>
    /// How many peers may owe us the same block at once outside end game.
    ///
    /// <para>
    /// A stalled block is worth asking a second peer for, and that is the whole benefit - a third and
    /// fourth mostly buy duplicate data, since the cancel sent when the first copy lands races whatever
    /// is already in flight. Without a cap there was no limit at all: staleness is measured from the
    /// oldest outstanding request, whose age only grows until the block arrives, so once a block passed
    /// the soft timeout it stayed eligible forever and every fast peer kept qualifying.
    /// </para>
    /// </summary>
    private const int MaxConcurrentRequestsPerBlock = 2;

    private readonly BlockRequestTracker _requestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly Func<PeerCommunication, int> _getSoftTimeoutMs;
    private readonly int _blockSize;

    public StandardBlockRequestStrategy(
        BlockRequestTracker requestTracker,
        TimeProvider timeProvider,
        Func<PeerCommunication, int> getSoftTimeoutMs,
        int blockSize)
    {
        _requestTracker = requestTracker;
        _timeProvider = timeProvider;
        _getSoftTimeoutMs = getSoftTimeoutMs;
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

        // Nothing is logged here on purpose. This decides requestability and is called for every block
        // of every active piece on every scheduling pass, whether or not a request follows - the caller
        // builds spans from it and then sends only as many as the peer's queue allows. Logging the
        // decision reported work that mostly never happened, at a third of a million lines an hour.
        return _requestTracker.GetPendingRequestCount(pieceIndex, offset) < MaxConcurrentRequestsPerBlock;
    }
}

internal sealed class EndGameBlockRequestStrategy : IBlockRequestStrategy
{
    private const int MaxConcurrentRequestsPerBlock = 4;
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
        return !_requestTracker.HasPendingRequestFromPeer(pieceIndex, offset, peer)
            && _requestTracker.GetPendingRequestCount(pieceIndex, offset) < MaxConcurrentRequestsPerBlock;
    }
}
