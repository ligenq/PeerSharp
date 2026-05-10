using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core;

public class TorrentCreationTests
{
    [Fact]
    public async Task BuildAsync_V2_CreatesValidV2Torrent()
    {
        var fileName = "v2_async.bin";
        byte[] data = new byte[32 * 1024];
        Random.Shared.NextBytes(data);

        var builder = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .WithVersion(PeerSharp.Core.TorrentFileVersion.V2)
            .AddFile(fileName, data);

        var torrent = await builder.BuildAsync();

        Assert.Equal(fileName, torrent.Name);
        Assert.Equal(data.Length, torrent.TotalSize);
        Assert.Equal(16_384u, torrent.PieceSize);
        Assert.True(torrent.IsV2);
        Assert.True(torrent.InfoHash.IsEmpty); // A pure v2 torrent has an empty v1 info hash
        Assert.False(torrent.InfoHashV2.IsEmpty); // A pure v2 torrent has HashV2 populated
    }

    [Fact]
    public async Task BuildAsync_Hybrid_CreatesValidHybridTorrent()
    {
        var fileA = new byte[8 * 1024];
        var fileB = new byte[12 * 1024];
        Random.Shared.NextBytes(fileA);
        Random.Shared.NextBytes(fileB);

        var builder = new ApiTorrentFileBuilder()
            .WithName("HybridTest")
            .WithPieceLength(16_384)
            .WithVersion(PeerSharp.Core.TorrentFileVersion.Hybrid)
            .AddFile("Folder/a.bin", fileA)
            .AddFile("Folder/b.bin", fileB);

        var torrent = await builder.BuildAsync();

        Assert.Equal("HybridTest", torrent.Name);
        Assert.True(torrent.IsV2);
        Assert.False(torrent.InfoHash.IsEmpty);
        Assert.False(torrent.InfoHashV2.IsEmpty);
        Assert.True(torrent.PieceCount > 0);
    }
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
    public void BuildV2SmallFile_OmitsPieceLayers()
    {
        byte[] data = new byte[8 * 1024];
        Random.Shared.NextBytes(data);

        var torrent = new ApiTorrentFileBuilder()
            .WithName("small.bin")
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.V2)
            .AddFile("small.bin", data)
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        Assert.Null(root.Get("piece layers"));
    }

    [Fact]
    public void BuildV2LargeFile_EmitsValidPieceLayers()
    {
        byte[] data = new byte[32 * 1024];
        Random.Shared.NextBytes(data);

        var torrent = new ApiTorrentFileBuilder()
            .WithName("large.bin")
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.V2)
            .AddFile("large.bin", data)
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        Assert.NotNull(root.Get("piece layers"));
        Assert.Equal(2, torrent.Metadata.Info.Files[0].PieceLayers!.Count);
    }

    [Fact]
    public void BuildV2NonPowerOfTwoPieceCount_PieceLayerHasExactlyPieceCountHashes()
    {
        // Per BEP 52, "piece layers" must contain exactly file_num_pieces hashes — NOT the
        // padded-to-power-of-2 layer from the merkle tree. Pre-fix, GetPieceLayer returned 4
        // entries for a 3-piece file, which (a) inflated the .torrent and (b) broke roundtripping
        // because the parser's PieceCount/PieceLayers.Count consistency check rejected it.
        byte[] data = new byte[80 * 1024]; // 5 blocks => 3 pieces with 32KB pieces
        Random.Shared.NextBytes(data);

        var torrent = new ApiTorrentFileBuilder()
            .WithName("uneven.bin")
            .WithPieceLength(32 * 1024)
            .WithVersion(TorrentFileVersion.V2)
            .AddFile("uneven.bin", data)
            .Build();

        Assert.Equal(3, torrent.Metadata.Info.Files[0].PieceLayers!.Count);

        // Round-trip: the parser must accept what the builder emitted.
        var reparsed = TorrentFileParser.Parse(torrent.RawData.ToArray());
        Assert.Equal(3, reparsed.Info.Files[0].PieceLayers!.Count);
        Assert.Equal(
            torrent.Metadata.Info.Files[0].PiecesRoot,
            reparsed.Info.Files[0].PiecesRoot);
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

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("folder/../../escape.bin")]
    [InlineData("./file.bin")]
    [InlineData("folder/./file.bin")]
    public void AddFile_RejectsSpecialDirectorySegments(string torrentPath)
    {
        var builder = new ApiTorrentFileBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.AddFile(torrentPath, new byte[16]));

        Assert.Equal("torrentPath", ex.ParamName);
    }

    [Fact(Timeout = 30000)]
    public async Task AddFileFromPath_RejectsSpecialDirectorySegmentsInTorrentPath()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "MtTorrentBuilderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string filePath = Path.Combine(tempRoot, "disk.bin");
            await File.WriteAllBytesAsync(filePath, new byte[16]);

            var builder = new ApiTorrentFileBuilder();

            var ex = Assert.Throws<ArgumentException>(() => builder.AddFileFromPath(filePath, "../escape.bin"));

            Assert.Equal("torrentPath", ex.ParamName);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }
}





