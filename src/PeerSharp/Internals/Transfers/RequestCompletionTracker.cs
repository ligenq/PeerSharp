using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal sealed class RequestCompletionTracker
{
    private readonly BlockRequestTracker _requestTracker;
    private readonly TimeProvider _timeProvider;
    private readonly Action<int, int, PeerCommunication> _removeBlockRequest;

    public RequestCompletionTracker(
        BlockRequestTracker requestTracker,
        TimeProvider timeProvider,
        Action<int, int, PeerCommunication> removeBlockRequest)
    {
        _requestTracker = requestTracker;
        _timeProvider = timeProvider;
        _removeBlockRequest = removeBlockRequest;
    }

    public void HandleBlockReceived(PeerCommunication peer, Block block)
    {
        if (_requestTracker.TryGetPeerRequests(peer, out var requests))
        {
            var key = (block.PieceIndex, block.Offset);
            if (requests.TryRemove(key, out var r))
            {
                if (r.Timestamp != DateTimeOffset.MinValue)
                {
                    int rttMs = (int)(_timeProvider.GetUtcNow() - r.Timestamp).TotalMilliseconds;
                    if (rttMs > 0 && rttMs < 30000)
                    {
                        peer.RecordRtt(rttMs);
                    }
                }
                _removeBlockRequest(r.PieceIndex, r.Offset, peer);
            }
        }
    }
}
