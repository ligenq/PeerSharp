namespace PeerSharp.Tests.Api;

public sealed class ApiModelTests
{
    [Fact]
    public void PeerInfo_Defaults_AreApplied()
    {
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234);
        var info = new PeerInfo(endpoint);

        Assert.Equal(endpoint, info.EndPoint);
        Assert.Equal(string.Empty, info.Country);
        Assert.Equal("Unknown", info.ClientName);
        Assert.Equal(0, info.DownloadSpeed);
        Assert.Equal(0, info.UploadSpeed);
        Assert.Equal(0, info.Downloaded);
        Assert.Equal(0, info.Uploaded);
        Assert.False(info.AmChoking);
        Assert.False(info.AmInterested);
        Assert.False(info.PeerChoking);
        Assert.False(info.PeerInterested);
        Assert.False(info.IsUtp);
        Assert.False(info.IsEncrypted);
        Assert.Equal(0f, info.Progress);
        Assert.Equal(0, info.RttMs);
    }

    [Fact]
    public void PortMappingStatus_Defaults_AreApplied()
    {
        var status = new PortMappingStatus("UPnP");

        Assert.Equal("UPnP", status.Protocol);
        Assert.Equal(PortMappingResult.NotAttempted, status.Result);
        Assert.Null(status.ExternalPort);
        Assert.Null(status.ErrorMessage);
    }

    [Fact]
    public void PieceCheckProgress_Values_AreStored()
    {
        var progress = new PieceCheckProgress
        {
            CheckedPieces = 5,
            CurrentPiece = 7,
            Progress = 0.5f,
            TotalPieces = 10,
            ValidPieces = 4
        };

        Assert.Equal(5, progress.CheckedPieces);
        Assert.Equal(7, progress.CurrentPiece);
        Assert.Equal(0.5f, progress.Progress);
        Assert.Equal(10, progress.TotalPieces);
        Assert.Equal(4, progress.ValidPieces);
    }

    [Fact]
    public void ClientEngineFactory_Create_ReturnsEngine()
    {
        var engine = ClientEngineFactory.Create();
        Assert.NotNull(engine);

        var options = new TorrentClientOptions();
        var engineWithOptions = ClientEngineFactory.Create(options);
        Assert.NotNull(engineWithOptions);
    }
}




