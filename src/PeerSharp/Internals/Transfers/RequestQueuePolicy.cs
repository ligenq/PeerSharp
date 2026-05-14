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
}
