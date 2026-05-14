using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal sealed class RequestTimeoutManager
{
    private readonly BlockRequestTracker _requestTracker;
    private readonly Action<int, int, PeerCommunication> _removeBlockRequest;
    private readonly Func<PeerCommunication, int> _getHardTimeoutMs;
    private readonly ILogger<RequestTimeoutManager> _logger;
    private readonly int _maxRequestAttempts;

    public RequestTimeoutManager(
        BlockRequestTracker requestTracker,
        Action<int, int, PeerCommunication> removeBlockRequest,
        Func<PeerCommunication, int> getHardTimeoutMs,
        ILogger<RequestTimeoutManager> logger,
        int maxRequestAttempts)
    {
        _requestTracker = requestTracker;
        _removeBlockRequest = removeBlockRequest;
        _getHardTimeoutMs = getHardTimeoutMs;
        _logger = logger;
        _maxRequestAttempts = maxRequestAttempts;
    }

    public void ProcessTimeouts(DateTimeOffset now, bool endGameMode)
    {
        var timedOutRequests = new List<(PeerCommunication Peer, (int Piece, int Offset) Key, BlockRequest Request)>();

        foreach (var kv in _requestTracker.EnumeratePeerRequests().ToArray())
        {
            var peer = kv.Key;
            var list = kv.Value;
            int hardTimeout = _getHardTimeoutMs(peer);

            foreach (var reqKvp in list.AsEnumerable().ToArray())
            {
                var req = reqKvp.Value;
                if ((now - req.Timestamp).TotalMilliseconds > hardTimeout)
                {
                    timedOutRequests.Add((peer, reqKvp.Key, req));
                }
            }
        }

        foreach (var (peer, key, req) in timedOutRequests)
        {
            if (_requestTracker.TryRemovePeerRequest(peer, key, out _))
            {
                _removeBlockRequest(req.PieceIndex, req.Offset, peer);

                if (req.Attempts >= _maxRequestAttempts && !endGameMode)
                {
                    _logger.LogDebug("Request for piece {PieceIndex} offset {Offset} gave up after {Attempts} attempts", req.PieceIndex, req.Offset, req.Attempts);
                }
                else
                {
                    _logger.LogDebug("Request for piece {PieceIndex} offset {Offset} timed out", req.PieceIndex, req.Offset);
                }
            }
        }
    }
}
