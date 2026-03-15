using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Core;

public class TorrentCreationTests
{
    [Fact]
    public void BuildSingleFile_CreatesValidTorrent()
    {
        var fileName = "single.bin";
        byte[] data = new byte[48 * 1024];
        Random.Shared.NextBytes(data);

        var torrent = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .AddFile(fileName, data)
            .Build();

        Assert.Equal(fileName, torrent.Name);
        Assert.Equal(data.Length, torrent.TotalSize);
        Assert.Equal(16_384u, torrent.PieceSize);
        Assert.Equal(1, torrent.FileCount);
        Assert.True(torrent.RawData.Length > 0);

        var file = torrent.GetFile(0);
        Assert.Equal(fileName, file.Path);
        Assert.Equal(data.Length, file.Size);

        var reparsed = TorrentFile.Parse(torrent.RawData.ToArray());
        Assert.Equal(torrent.InfoHash, reparsed.InfoHash);
        Assert.Equal(torrent.PieceCount, reparsed.PieceCount);
    }

    [Fact]
    public void BuildMultiFile_SetsTrackersAndWebSeeds()
    {
        var fileA = new byte[8 * 1024];
        var fileB = new byte[12 * 1024];
        Random.Shared.NextBytes(fileA);
        Random.Shared.NextBytes(fileB);

        var torrent = new ApiTorrentFileBuilder()
            .WithName("MultiTest")
            .WithPieceLength(16_384)
            .WithAnnounce("http://tracker.example/announce")
            .AddTracker("http://tracker-backup.example/announce")
            .AddWebSeed("http://seed.example/data")
            .AddFile("Folder/a.bin", fileA)
            .AddFile("Folder/b.bin", fileB)
            .Build();

        Assert.Equal("MultiTest", torrent.Name);
        Assert.Equal(2, torrent.FileCount);
        Assert.Equal(2, torrent.Trackers.Count);
        Assert.Equal("http://tracker.example/announce", torrent.Announce);
        Assert.Single(torrent.WebSeeds);

        var files = torrent.GetFiles().ToList();
        Assert.Equal(Path.Combine("Folder", "a.bin"), files[0].Path);
        Assert.Equal(fileA.Length, files[0].Size);
        Assert.Equal(Path.Combine("Folder", "b.bin"), files[1].Path);
        Assert.Equal(fileB.Length, files[1].Size);
    }

    [Fact]
    public void BuildMultiFile_WithPadding_HidesPaddingFiles()
    {
        var fileA = new byte[8 * 1024];
        var fileB = new byte[12 * 1024];

        var torrent = new ApiTorrentFileBuilder()
            .WithName("PaddingTest")
            .WithPieceLength(16_384)
            .WithPaddingFiles()
            .AddFile("Folder/a.bin", fileA)
            .AddFile("Folder/b.bin", fileB)
            .Build();

        Assert.Equal(2, torrent.FileCount);
        Assert.Equal(28 * 1024, torrent.TotalSize);

        var files = torrent.GetFiles().ToList();
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => f.Path.StartsWith(".pad", StringComparison.Ordinal));

        Assert.NotNull(torrent.Metadata);
        Assert.True(torrent.Metadata.Info.Files.Count > files.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task BuildFromDiskAsync_ComputesPieceCount()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "MtTorrentBuilderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string filePath = Path.Combine(tempRoot, "disk.bin");
            byte[] data = new byte[40 * 1024];
            Random.Shared.NextBytes(data);
            await File.WriteAllBytesAsync(filePath, data);

            var torrent = await new ApiTorrentFileBuilder()
                .WithName("DiskTest")
                .WithPieceLength(16_384)
                .AddFileFromPath(filePath)
                .BuildAsync();

            int expectedPieces = (data.Length + 16_384 - 1) / 16_384;
            Assert.Equal(expectedPieces, torrent.PieceCount);
            Assert.Equal(data.Length, torrent.TotalSize);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void BuildV2SingleFile_EmitsV2Metadata()
    {
        var fileName = "v2.bin";
        byte[] data = new byte[24 * 1024];
        Random.Shared.NextBytes(data);

        var torrent = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.V2)
            .AddFile(fileName, data)
            .Build();

        Assert.True(torrent.IsV2);
        Assert.False(torrent.IsV1);
        Assert.True(torrent.InfoHash.IsEmpty);
        Assert.False(torrent.InfoHashV2.IsEmpty);
        Assert.Equal(fileName, torrent.GetFile(0).Path);
    }

    [Fact]
    public void BuildHybridSingleFile_EmitsBothHashes()
    {
        var fileName = "hybrid.bin";
        byte[] data = new byte[32 * 1024];
        Random.Shared.NextBytes(data);

        var torrent = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.Hybrid)
            .AddFile(fileName, data)
            .Build();

        Assert.True(torrent.IsHybrid);
        Assert.True(torrent.IsV1);
        Assert.True(torrent.IsV2);
        Assert.False(torrent.InfoHash.IsEmpty);
        Assert.False(torrent.InfoHashV2.IsEmpty);
    }
}





