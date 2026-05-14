using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Transfers;

internal sealed class PieceCompletionHandler
{
    private readonly BlockRequestTracker _requestTracker;
    private readonly Action<int, int, PeerCommunication> _removeBlockRequest;
    private readonly Torrent _torrent;
    private readonly ILogger<PieceCompletionHandler> _logger;

    public PieceCompletionHandler(
        BlockRequestTracker requestTracker,
        Action<int, int, PeerCommunication> removeBlockRequest,
        Torrent torrent,
        ILogger<PieceCompletionHandler> logger)
    {
        _requestTracker = requestTracker;
        _removeBlockRequest = removeBlockRequest;
        _torrent = torrent;
        _logger = logger;
    }

    public async Task HandlePieceCompletedAsync(int pieceIndex, bool endGameMode)
    {
        // Collect requests to remove (avoids modification during iteration)
        var requestsToRemove = new List<(PeerCommunication Peer, (int Piece, int Offset) Key, BlockRequest Request)>();
        foreach (var kv in _requestTracker.EnumeratePeerRequests())
        {
            var peerRequests = kv.Value;
            var peer = kv.Key;

            foreach (var reqKv in peerRequests.AsEnumerable())
            {
                if (reqKv.Key.Piece == pieceIndex)
                {
                    requestsToRemove.Add((peer, reqKv.Key, reqKv.Value));
                }
            }
        }

        // Now remove and cancel
        List<Task>? cancelTasks = endGameMode ? [] : null;
        foreach (var (peer, key, r) in requestsToRemove)
        {
            if (_requestTracker.TryRemovePeerRequest(peer, key, out _))
            {
                _removeBlockRequest(r.PieceIndex, r.Offset, peer);
                if (endGameMode)
                {
                    cancelTasks!.Add(peer.SendMessageAsync(new PeerMessage(MessageId.Cancel)
                    {
                        PieceIndex = r.PieceIndex,
                        BlockOffset = r.Offset,
                        BlockLength = r.Length
                    }));
                }
            }
        }

        if (cancelTasks is { Count: > 0 })
        {
            await Task.WhenAll(cancelTasks).ConfigureAwait(false);
        }

        await _torrent.PeersInternal.BroadcastHaveAsync(pieceIndex).ConfigureAwait(false);
        _logger.LogDebug("Piece {PieceIndex} complete", pieceIndex);

        // Send Suggest messages (BEP 6) to peers who don't have this piece
        foreach (var connectedPeer in _torrent.PeersInternal.GetConnectedPeersInternal())
        {
            if (connectedPeer.RemoteSupportsExtensions && !connectedPeer.PeerPieces.HasPiece(pieceIndex))
            {
                await connectedPeer.SendSuggestAsync(pieceIndex).ConfigureAwait(false);
                _logger.LogDebug("Suggested piece {PieceIndex} to {PeerName}", pieceIndex, connectedPeer.Name);
            }
        }
    }
}
