using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal sealed class RequestCompletionTracker
{
    private readonly BlockRequestTracker _requestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly Action<int, int, PeerCommunication>? _removeBlockRequest;

    public RequestCompletionTracker(
        BlockRequestTracker requestTracker,
        TimeProvider timeProvider,
        Action<int, int, PeerCommunication>? removeBlockRequest = null)
    {
        _requestTracker = requestTracker;
        _timeProvider = timeProvider;
        _removeBlockRequest = removeBlockRequest;
    }

    public void HandleBlockReceived(PeerCommunication peer, Block block, BlockRequest? expected = null)
    {
        if (_requestTracker.TryGetPeerRequests(peer, out _))
        {
            var key = (block.PieceIndex, block.Offset);
            if (_requestTracker.TryRemovePeerRequest(peer, key, out var r, expected))
            {
                if (r.Timestamp != DateTimeOffset.MinValue)
                {
                    int rttMs = (int)(_timeProvider.GetUtcNow() - r.Timestamp).TotalMilliseconds;
                    if (rttMs > 0 && rttMs < 30000)
                    {
                        peer.RecordRtt(rttMs);
                    }
                }
                _removeBlockRequest?.Invoke(r.PieceIndex, r.Offset, peer);
            }
        }
    }

    public bool TryGetPendingRequest(PeerCommunication peer, Block block, out BlockRequest request)
    {
        request = default!;
        return _requestTracker.TryGetPeerRequests(peer, out var requests) &&
            requests.TryGetValue((block.PieceIndex, block.Offset), out request);
    }
}
