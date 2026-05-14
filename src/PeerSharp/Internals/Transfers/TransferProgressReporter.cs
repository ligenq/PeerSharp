using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals.Transfers;

internal sealed class TransferProgressReporter
{
    private readonly Torrent _torrent;
    private readonly ILogger<TransferProgressReporter> _logger;

    public TransferProgressReporter(Torrent torrent, ILogger<TransferProgressReporter> logger)
    {
        _torrent = torrent;
        _logger = logger;
    }

    public void ReportPieceCompleted(int pieceIndex)
    {
        var totalPieces = _torrent.Pieces.Count;
        var receivedPieces = _torrent.Pieces.ReceivedCount;
        var remaining = totalPieces - receivedPieces;
        var progress = receivedPieces * 100.0 / totalPieces;

        int progressPercent = (int)Math.Round(progress);
        int previousPercent = (int)Math.Round((receivedPieces - 1) * 100.0 / totalPieces);
        bool isMilestone = (progressPercent / 10 != previousPercent / 10) || // Every 10%
                           (receivedPieces % 100 == 0) ||                     // Every 100 pieces
                           receivedPieces == 1 ||                             // First piece
                           remaining == 0;                                    // Last piece

        if (isMilestone)
        {
            _logger.LogInformation("Download progress: {Received}/{Total} pieces ({Percent}%), remaining: {Remaining}",
                receivedPieces, totalPieces, progressPercent, remaining);
        }
        else
        {
            _logger.LogDebug("Piece {PieceIndex} complete - {Received}/{Total} ({Percent}%)",
                pieceIndex, receivedPieces, totalPieces, Math.Round(progress, 1));
        }

        if (remaining < 10 && remaining + 1 >= 10 && totalPieces > 10)
        {
            _logger.LogInformation("ENTERING END-GAME MODE - requesting remaining pieces from all peers");
        }

        if (_torrent.Finished)
        {
            _torrent.TrackerManager.AnnounceCompleted();
            _logger.LogInformation("Torrent finished!");
        }
    }
}
