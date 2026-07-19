using System.Text;

namespace PeerSharp.Tests.Integration;

internal static class IntegrationTestDiagnostics
{
    public static string DescribeTorrent(ITorrent torrent)
    {
        var sb = new StringBuilder();

        sb.Append("State=").Append(torrent.State)
            .Append(", Started=").Append(torrent.Started)
            .Append(", HasMetadata=").Append(torrent.HasMetadata)
            .Append(", Progress=").Append(torrent.Progress.ToString("P1"))
            .Append(", SelectionProgress=").Append(torrent.SelectionProgress.ToString("P1"))
            .Append(", Pieces=").Append(torrent.PiecesReceived).Append('/').Append(torrent.PieceCount)
            .Append(", DataLeft=").Append(torrent.DataLeft)
            .Append(", FinishedBytes=").Append(torrent.FinishedBytes)
            .Append(", FinishedSelectedBytes=").Append(torrent.FinishedSelectedBytes);

        AppendTransfer(sb, torrent);
        AppendMetadata(sb, torrent);
        AppendPeers(sb, torrent);
        AppendPieceAvailability(sb, torrent);
        AppendTrackers(sb, torrent);

        if (torrent.LastException != null)
        {
            sb.Append(", LastException=")
                .Append(torrent.LastException.GetType().Name)
                .Append(": ")
                .Append(torrent.LastException.Message);
        }

        return sb.ToString();
    }

    private static void AppendTransfer(StringBuilder sb, ITorrent torrent)
    {
        try
        {
            sb.Append(", TransferDownloaded=").Append(torrent.FileTransfer.Downloaded)
                .Append(", TransferUploaded=").Append(torrent.FileTransfer.Uploaded)
                .Append(", EndGame=").Append(torrent.FileTransfer.EndGameMode);
        }
        catch (Exception ex)
        {
            sb.Append(", TransferDiagnosticsError=").Append(ex.GetType().Name);
        }
    }

    private static void AppendMetadata(StringBuilder sb, ITorrent torrent)
    {
        try
        {
            if (torrent.MetadataDownload is { } metadata)
            {
                sb.Append(", MetadataFinished=").Append(metadata.Finished)
                    .Append(", MetadataProgress=").Append(metadata.Progress.ToString("P1"));
            }
        }
        catch (Exception ex)
        {
            sb.Append(", MetadataDiagnosticsError=").Append(ex.GetType().Name);
        }
    }

    private static void AppendPeers(StringBuilder sb, ITorrent torrent)
    {
        try
        {
            var peers = torrent.Peers.GetConnectedPeers();
            sb.Append(", Peers=").Append(torrent.Peers.ConnectedCount)
                .Append(", PeerSnapshots=").Append(peers.Count);

            if (peers.Count == 0)
            {
                return;
            }

            sb.Append(", PeerDetails=[");
            for (int i = 0; i < Math.Min(peers.Count, 4); i++)
            {
                var peer = peers[i];
                if (i > 0)
                {
                    sb.Append("; ");
                }

                sb.Append(peer.EndPoint)
                    .Append(" client=").Append(peer.ClientName)
                    .Append(" utp=").Append(peer.IsUtp)
                    .Append(" encrypted=").Append(peer.IsEncrypted)
                    .Append(" peerProgress=").Append(peer.Progress.ToString("P1"))
                    .Append(" dl=").Append(peer.Downloaded)
                    .Append(" ul=").Append(peer.Uploaded)
                    .Append(" downBps=").Append(peer.DownloadSpeed)
                    .Append(" upBps=").Append(peer.UploadSpeed)
                    .Append(" amChoking=").Append(peer.AmChoking)
                    .Append(" peerChoking=").Append(peer.PeerChoking)
                    .Append(" amInterested=").Append(peer.AmInterested)
                    .Append(" peerInterested=").Append(peer.PeerInterested);
            }

            if (peers.Count > 4)
            {
                sb.Append("; +").Append(peers.Count - 4).Append(" more");
            }

            sb.Append(']');
        }
        catch (Exception ex)
        {
            sb.Append(", PeerDiagnosticsError=").Append(ex.GetType().Name);
        }
    }

    private static void AppendPieceAvailability(StringBuilder sb, ITorrent torrent)
    {
        try
        {
            var availability = torrent.Peers.GetPieceAvailability();
            if (availability.Length == 0)
            {
                sb.Append(", AvailablePieces=0/0");
                return;
            }

            int available = availability.Count(x => x > 0);
            int maxAvailability = availability.Max();
            sb.Append(", AvailablePieces=").Append(available).Append('/').Append(availability.Length)
                .Append(", MaxPieceAvailability=").Append(maxAvailability);
        }
        catch (Exception ex)
        {
            sb.Append(", PieceAvailabilityDiagnosticsError=").Append(ex.GetType().Name);
        }
    }

    private static void AppendTrackers(StringBuilder sb, ITorrent torrent)
    {
        try
        {
            var trackers = torrent.Trackers.GetTrackers();
            sb.Append(", Trackers=").Append(trackers.Count);
            if (trackers.Count == 0)
            {
                return;
            }

            sb.Append(", TrackerDetails=[");
            for (int i = 0; i < Math.Min(trackers.Count, 4); i++)
            {
                var tracker = trackers[i];
                if (i > 0)
                {
                    sb.Append("; ");
                }

                sb.Append(tracker.Url)
                    .Append(" status=").Append(tracker.Status)
                    .Append(" seeds=").Append(tracker.SeedCount)
                    .Append(" leeches=").Append(tracker.LeechCount)
                    .Append(" failures=").Append(tracker.ConsecutiveFailures);

                if (!string.IsNullOrWhiteSpace(tracker.LastError))
                {
                    sb.Append(" lastError=").Append(tracker.LastError);
                }
            }

            if (trackers.Count > 4)
            {
                sb.Append("; +").Append(trackers.Count - 4).Append(" more");
            }

            sb.Append(']');
        }
        catch (Exception ex)
        {
            sb.Append(", TrackerDiagnosticsError=").Append(ex.GetType().Name);
        }
    }
}
