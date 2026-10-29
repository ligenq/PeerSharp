using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal static class RequestQueuePolicy
{
    public static int CalculateNewPieceStartLimit(int remainingRequestSlots, int activePieceSlotsAvailable, int blocksPerPiece)
    {
        if (remainingRequestSlots <= 0 || activePieceSlotsAvailable <= 0)
        {
            return 0;
        }

        int requestableBlocksPerPiece = Math.Max(1, blocksPerPiece);
        int piecesNeeded = (remainingRequestSlots + requestableBlocksPerPiece - 1) / requestableBlocksPerPiece;
        return Math.Clamp(piecesNeeded, 1, activePieceSlotsAvailable);
    }

    /// <summary>
    /// How many block requests one peer may have outstanding: the depth its rate earns, within the
    /// configured ceiling and the queue the peer advertised itself (BEP 10 <c>reqq</c>).
    /// </summary>
    public static int RequestQueueLimit(PeerCommunication peer, TransferSettings transfer)
    {
        int limit = Math.Min(peer.GetAdaptivePipelineDepth(), Math.Max(1, transfer.MaxRequestsPerPeer));
        if (peer.RemoteExtensions?.RequestQueueDepth is > 0 and var remoteCapacity)
        {
            limit = Math.Min(limit, remoteCapacity);
        }

        return limit;
    }

    /// <summary>
    /// How many pieces have to be open for one peer's request queue to be filled: as many as the queue
    /// spans, and one more for the piece it is finishing - still open while its last blocks land and
    /// it is verified - when the next is started. Never fewer than <paramref name="minimumPerPeer"/>.
    /// </summary>
    public static int PiecesToKeepOpen(int queueDepth, int blocksPerPiece, int minimumPerPeer)
    {
        int perPiece = Math.Max(1, blocksPerPiece);
        int spanned = (Math.Max(0, queueDepth) + perPiece - 1) / perPiece;
        return Math.Max(minimumPerPeer, spanned + 1);
    }
}
