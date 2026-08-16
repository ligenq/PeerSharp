using PeerSharp.Internals;

namespace PeerSharp.Tests.Api;

public sealed class AlertModelsTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MtTorrentTests", Guid.NewGuid().ToString("N"));
    private ITorrent _torrent = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "alert_test";
        _torrent = TorrentTestUtility.CreateMinimal(metadata, _tempDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void AlertRecords_RetainAssignedProperties()
    {
        var timestamp = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var exception = new InvalidOperationException("boom");

        var simpleTorrent = new SimpleTorrentAlert
        {
            Id = AlertId.TorrentAdded,
            Timestamp = timestamp,
            Torrent = _torrent
        };

        var simpleMetadata = new SimpleMetadataAlert
        {
            Id = AlertId.MetadataInitialized,
            Timestamp = timestamp,
            Torrent = _torrent
        };

        var configAlert = new ConfigAlert
        {
            Id = AlertId.ConfigChanged,
            Timestamp = timestamp,
            ConfigType = "session"
        };

        var pieceAlert = new PieceCompletedAlert
        {
            Id = AlertId.PieceCompleted,
            Timestamp = timestamp,
            Torrent = _torrent,
            PieceIndex = 3,
            CompletedPieces = 4,
            TotalPieces = 10
        };

        var progressAlert = new ProgressChangedAlert
        {
            Id = AlertId.ProgressChanged,
            Timestamp = timestamp,
            Torrent = _torrent,
            Progress = 0.5f,
            SelectionProgress = 0.25f,
            FinishedBytes = 123,
            TotalBytes = 1000,
            CompletedPieces = 5,
            TotalPieces = 10
        };

        var transferAlert = new TransferStatsAlert
        {
            Id = AlertId.TransferStatsUpdated,
            Timestamp = timestamp,
            Torrent = _torrent,
            Downloaded = 100,
            Uploaded = 200,
            DownloadSpeed = 10,
            UploadSpeed = 20,
            ConnectedPeers = 7
        };

        var peerDisconnected = new PeerDisconnectedAlert
        {
            Id = AlertId.PeerDisconnected,
            Timestamp = timestamp,
            Torrent = _torrent,
            Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881),
            ClientName = "PeerSharp",
            Downloaded = 300,
            Uploaded = 400,
            ReasonCode = 2
        };

        var stateAlert = new StateChangedAlert
        {
            Id = AlertId.TorrentStateChanged,
            Timestamp = timestamp,
            Torrent = _torrent,
            PreviousState = TorrentState.Stopped,
            NewState = TorrentState.Active
        };

        var errorAlert = new TorrentErrorAlert
        {
            Id = AlertId.TorrentError,
            Timestamp = timestamp,
            Torrent = _torrent,
            Exception = exception
        };

        var metadataProgress = new MetadataProgressAlert
        {
            Id = AlertId.MetadataProgressChanged,
            Timestamp = timestamp,
            Torrent = _torrent,
            Progress = 0.75f,
            ReceivedPieces = 6,
            TotalPieces = 8
        };

        var metadataStalled = new MetadataDownloadStalledAlert
        {
            Id = AlertId.MetadataDownloadStalled,
            Timestamp = timestamp,
            Torrent = _torrent,
            CapablePeers = 6,
            RequestsSent = 110,
            Elapsed = TimeSpan.FromSeconds(111)
        };

        Assert.Equal(AlertId.TorrentAdded, simpleTorrent.Id);
        Assert.Equal(timestamp, simpleTorrent.Timestamp);
        Assert.Same(_torrent, simpleTorrent.Torrent);

        Assert.Equal(AlertId.MetadataInitialized, simpleMetadata.Id);
        Assert.Same(_torrent, simpleMetadata.Torrent);

        Assert.Equal("session", configAlert.ConfigType);
        Assert.Equal(AlertId.ConfigChanged, configAlert.Id);

        Assert.Equal(3, pieceAlert.PieceIndex);
        Assert.Equal(4, pieceAlert.CompletedPieces);
        Assert.Equal(10, pieceAlert.TotalPieces);

        Assert.Equal(0.5f, progressAlert.Progress);
        Assert.Equal(0.25f, progressAlert.SelectionProgress);
        Assert.Equal((ulong)123, progressAlert.FinishedBytes);
        Assert.Equal((ulong)1000, progressAlert.TotalBytes);

        Assert.Equal(100, transferAlert.Downloaded);
        Assert.Equal(200, transferAlert.Uploaded);
        Assert.Equal(7, transferAlert.ConnectedPeers);

        Assert.Equal(AlertId.PeerDisconnected, peerDisconnected.Id);
        Assert.Equal("PeerSharp", peerDisconnected.ClientName);
        Assert.Equal(300, peerDisconnected.Downloaded);
        Assert.Equal(400, peerDisconnected.Uploaded);
        Assert.Equal(2, peerDisconnected.ReasonCode);

        Assert.Equal(TorrentState.Stopped, stateAlert.PreviousState);
        Assert.Equal(TorrentState.Active, stateAlert.NewState);

        Assert.Same(exception, errorAlert.Exception);
        Assert.Same(_torrent, errorAlert.Torrent);

        Assert.Equal(0.75f, metadataProgress.Progress);
        Assert.Equal(6, metadataProgress.ReceivedPieces);
        Assert.Equal(8, metadataProgress.TotalPieces);

        Assert.Equal(6, metadataStalled.CapablePeers);
        Assert.Equal(110, metadataStalled.RequestsSent);
        Assert.Equal(TimeSpan.FromSeconds(111), metadataStalled.Elapsed);
    }
}




