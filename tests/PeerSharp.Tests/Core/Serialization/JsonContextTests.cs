using PeerSharp.Internals;
using PeerSharp.PieceWriter;
using System.Text.Json;

namespace PeerSharp.Tests.Core.Serialization;

public class JsonContextTests
{
    [Fact]
    public void TorrentStateData_RoundTrip_PreservesAllFields()
    {
        var original = new TorrentStateData
        {
            AddedTime = 1_700_000_000L,
            Downloaded = 123456789UL,
            DownloadPath = "C:\\Downloads\\Test",
            LastStateTime = 1_700_000_100L,
            Pieces = new byte[] { 0b11001010, 0b01010101 },
            SeedTimeSeconds = 3600L,
            Started = true,
            Uploaded = 987654321UL,
            Version = 2,
            Selection =
            [
                new() { Selected = true, Priority = Priority.High },
                new() { Selected = false, Priority = Priority.Low }
            ],
            UnfinishedPieces =
            [
                new() { Index = 5, Blocks = new[] { true, false, true }, Data = new byte[] { 1, 2, 3 } }
            ],
            Info = new TorrentStateData.InfoData
            {
                FullSize = 50_000_000L,
                Name = "MyTorrent",
                PieceSize = 262144
            }
        };

        string json = JsonSerializer.Serialize(original, PeerSharpJsonContext.Default.TorrentStateData);
        var restored = JsonSerializer.Deserialize(json, PeerSharpJsonContext.Default.TorrentStateData);

        Assert.NotNull(restored);
        Assert.Equal(original.AddedTime, restored.AddedTime);
        Assert.Equal(original.Downloaded, restored.Downloaded);
        Assert.Equal(original.DownloadPath, restored.DownloadPath);
        Assert.Equal(original.LastStateTime, restored.LastStateTime);
        Assert.Equal(original.Pieces, restored.Pieces);
        Assert.Equal(original.SeedTimeSeconds, restored.SeedTimeSeconds);
        Assert.Equal(original.Started, restored.Started);
        Assert.Equal(original.Uploaded, restored.Uploaded);
        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(2, restored.Selection.Count);
        Assert.Equal(Priority.High, restored.Selection[0].Priority);
        Assert.Single(restored.UnfinishedPieces);
        Assert.Equal(5, restored.UnfinishedPieces[0].Index);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.UnfinishedPieces[0].Data);
        Assert.Equal("MyTorrent", restored.Info.Name);
        Assert.Equal(262144u, restored.Info.PieceSize);
    }

    [Fact]
    public void SavedTorrentOptions_RoundTrip_PreservesAllFields()
    {
        var original = new SavedTorrentOptions(
            DownloadPath: "D:\\Torrents",
            WasStarted: true,
            DownloadLimitBytesPerSecond: 1024 * 512,
            UploadLimitBytesPerSecond: 1024 * 256,
            QueuePriority: 3,
            RatioLimit: 2.5f,
            SeedTimeLimit: TimeSpan.FromHours(48),
            DownloadStrategy: DownloadStrategy.Sequential);

        string json = JsonSerializer.Serialize(original, PeerSharpJsonContext.Default.SavedTorrentOptions);
        var restored = JsonSerializer.Deserialize(json, PeerSharpJsonContext.Default.SavedTorrentOptions);

        Assert.NotNull(restored);
        Assert.Equal(original.DownloadPath, restored.DownloadPath);
        Assert.True(restored.WasStarted);
        Assert.Equal(original.DownloadLimitBytesPerSecond, restored.DownloadLimitBytesPerSecond);
        Assert.Equal(original.UploadLimitBytesPerSecond, restored.UploadLimitBytesPerSecond);
        Assert.Equal(original.QueuePriority, restored.QueuePriority);
        Assert.Equal(original.RatioLimit, restored.RatioLimit);
        Assert.Equal(original.SeedTimeLimit, restored.SeedTimeLimit);
        Assert.Equal(DownloadStrategy.Sequential, restored.DownloadStrategy);
    }

    [Fact]
    public void SavedTorrentOptions_Defaults_RoundTrip()
    {
        var original = new SavedTorrentOptions();

        string json = JsonSerializer.Serialize(original, PeerSharpJsonContext.Default.SavedTorrentOptions);
        var restored = JsonSerializer.Deserialize(json, PeerSharpJsonContext.Default.SavedTorrentOptions);

        Assert.NotNull(restored);
        Assert.Null(restored.DownloadPath);
        Assert.False(restored.WasStarted);
        Assert.Equal(0, restored.DownloadLimitBytesPerSecond);
        Assert.Null(restored.RatioLimit);
        Assert.Null(restored.SeedTimeLimit);
        Assert.Equal(DownloadStrategy.RarestFirst, restored.DownloadStrategy);
    }

    [Fact]
    public void DhtStateDto_RoundTrip_PreservesNodesAndId()
    {
        var original = new SessionPersistence.DhtStateDto
        {
            NodeId = "AABBCCDDEEFF00112233445566778899AABBCCDD",
            Nodes =
            [
                new() { Id = "1122334455667788990011223344556677889900", Ip = "192.168.1.1", Port = 6881 },
                new() { Id = "FFEEDDCCBBAA99887766554433221100FFEEDDCC", Ip = "10.0.0.1",   Port = 6882 }
            ]
        };

        string json = JsonSerializer.Serialize(original, PeerSharpJsonContext.Default.DhtStateDto);
        var restored = JsonSerializer.Deserialize(json, PeerSharpJsonContext.Default.DhtStateDto);

        Assert.NotNull(restored);
        Assert.Equal(original.NodeId, restored.NodeId);
        Assert.Equal(2, restored.Nodes.Count);
        Assert.Equal("192.168.1.1", restored.Nodes[0].Ip);
        Assert.Equal(6881, restored.Nodes[0].Port);
        Assert.Equal("FFEEDDCCBBAA99887766554433221100FFEEDDCC", restored.Nodes[1].Id);
    }

    [Fact]
    public void DhtStateDto_EmptyNodes_RoundTrip()
    {
        var original = new SessionPersistence.DhtStateDto { NodeId = null, Nodes = [] };

        string json = JsonSerializer.Serialize(original, PeerSharpJsonContext.Default.DhtStateDto);
        var restored = JsonSerializer.Deserialize(json, PeerSharpJsonContext.Default.DhtStateDto);

        Assert.NotNull(restored);
        Assert.Null(restored.NodeId);
        Assert.Empty(restored.Nodes);
    }

    [Fact]
    public void TorrentStateData_EmptyState_RoundTrip()
    {
        var original = new TorrentStateData();

        string json = JsonSerializer.Serialize(original, PeerSharpJsonContext.Default.TorrentStateData);
        var restored = JsonSerializer.Deserialize(json, PeerSharpJsonContext.Default.TorrentStateData);

        Assert.NotNull(restored);
        Assert.Empty(restored.Pieces);
        Assert.Empty(restored.Selection);
        Assert.Empty(restored.UnfinishedPieces);
        Assert.Equal(1u, restored.Version);
    }
}
