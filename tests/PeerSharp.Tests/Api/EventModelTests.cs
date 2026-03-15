namespace PeerSharp.Tests;

public class EventModelTests
{
    [Fact]
    public void DownloadProgress_RemainingBytes_UsesTotals()
    {
        var progress = new DownloadProgress
        {
            TotalBytes = 1000,
            FinishedBytes = 400
        };

        Assert.Equal(600, progress.RemainingBytes);
    }

    [Fact]
    public void PieceProgress_Progress_HandlesZeroTotal()
    {
        var zeroTotal = new PieceProgress { CompletedPieces = 5, TotalPieces = 0 };
        var nonZero = new PieceProgress { CompletedPieces = 5, TotalPieces = 10 };

        Assert.Equal(0f, zeroTotal.Progress);
        Assert.Equal(0.5f, nonZero.Progress);
    }

    [Fact]
    public void TransferStats_Ratio_HandlesZeroDownloaded()
    {
        var zeroDownloaded = new TransferStats { Downloaded = 0, Uploaded = 10 };
        var nonZero = new TransferStats { Downloaded = 100, Uploaded = 25 };

        Assert.Equal(0f, zeroDownloaded.Ratio);
        Assert.Equal(0.25f, nonZero.Ratio);
    }

    [Fact]
    public void TorrentFileInfo_Progress_HandlesZeroSize()
    {
        var zeroSize = new TorrentFileInfo("file.bin", 0, 0, 0);
        var partial = new TorrentFileInfo("file.bin", 100, 0, 25);

        Assert.Equal(1.0f, zeroSize.Progress);
        Assert.Equal(0.25f, partial.Progress);
    }

    [Fact]
    public void StateTransition_RetainsStates()
    {
        var transition = new StateTransition
        {
            PreviousState = TorrentState.Stopped,
            NewState = TorrentState.Active
        };

        Assert.Equal(TorrentState.Stopped, transition.PreviousState);
        Assert.Equal(TorrentState.Active, transition.NewState);
    }
}




