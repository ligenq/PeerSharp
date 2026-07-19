using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Utilities;
using System.Reflection;

namespace PeerSharp.Tests.Core;

public class TorrentCreationTests
{
    [Fact]
    public async Task BuildAsync_V2_CreatesValidV2Torrent()
    {
        const string fileName = "v2_async.bin";
        byte[] data = new byte[32 * 1024];
        Random.Shared.NextBytes(data);

        var builder = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.V2)
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
            .WithVersion(TorrentFileVersion.Hybrid)
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
        const string fileName = "single.bin";
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
        const string fileName = "v2.bin";
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
        const string fileName = "hybrid.bin";
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

    // ── InferName ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithoutExplicitName_SingleFile_InfersFilenameFromPath()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithPieceLength(16_384)
            .AddFile("subdir/data.bin", new byte[1024])
            .Build();

        Assert.Equal("data.bin", torrent.Name);
    }

    [Fact]
    public void Build_WithoutExplicitName_MultiFile_CommonRoot_UsesRootDirName()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithPieceLength(16_384)
            .AddFile("Album/track1.mp3", new byte[1024])
            .AddFile("Album/track2.mp3", new byte[1024])
            .Build();

        Assert.Equal("Album", torrent.Name);
    }

    [Fact]
    public void Build_WithoutExplicitName_MultiFile_NoCommonRoot_FallsBackToTorrent()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithPieceLength(16_384)
            .AddFile("a.bin", new byte[1024])
            .AddFile("b.bin", new byte[1024])
            .Build();

        Assert.Equal("torrent", torrent.Name);
    }

    // ── ValidateInputs ───────────────────────────────────────────────────────

    [Fact]
    public void Build_NoFiles_ThrowsInvalidOperation()
    {
        var builder = new ApiTorrentFileBuilder().WithPieceLength(16_384);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_PaddingFilesWithV2_ThrowsInvalidOperation()
    {
        var builder = new ApiTorrentFileBuilder()
            .WithVersion(TorrentFileVersion.V2)
            .WithPaddingFiles()
            .AddFile("f.bin", new byte[16]);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_V2_PieceLengthTooSmall_ThrowsInvalidOperation()
    {
        var builder = new ApiTorrentFileBuilder()
            .WithVersion(TorrentFileVersion.V2)
            .WithPieceLength(8_192)       // < MerkleTree.BlockSize (16 KiB)
            .AddFile("f.bin", new byte[16]);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_V2_PieceLengthNotMultipleOf16KiB_ThrowsInvalidOperation()
    {
        var builder = new ApiTorrentFileBuilder()
            .WithVersion(TorrentFileVersion.V2)
            .WithPieceLength(20_000)      // not a multiple of 16 384
            .AddFile("f.bin", new byte[16]);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_V2_PieceLengthNotPowerOfTwoMultiple_ThrowsInvalidOperation()
    {
        var builder = new ApiTorrentFileBuilder()
            .WithVersion(TorrentFileVersion.V2)
            .WithPieceLength(3 * 16_384)  // 3 × 16 KiB: multiple but not power-of-2 multiple
            .AddFile("f.bin", new byte[16]);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    // ── ZeroStream ───────────────────────────────────────────────────────────

    private static Stream CreateZeroStream(long length)
    {
        var type = typeof(ApiTorrentFileBuilder)
            .GetNestedType("ZeroStream", BindingFlags.NonPublic)!;
        return (Stream)Activator.CreateInstance(type, length)!;
    }

    [Fact]
    public void ZeroStream_Read_ReturnsZeroBytes()
    {
        using var s = CreateZeroStream(10);
        byte[] buf = new byte[10];
        buf[3] = 0xFF;
        int read = s.Read(buf, 0, 10);
        Assert.Equal(10, read);
        Assert.All(buf, b => Assert.Equal(0, b));
        Assert.Equal(10, s.Position);
    }

    [Fact]
    public void ZeroStream_Read_PastEnd_ReturnsZero()
    {
        using var s = CreateZeroStream(5);
        s.ReadExactly(new byte[5], 0, 5);
        int read = s.Read(new byte[1], 0, 1);
        Assert.Equal(0, read);
    }

    [Fact]
    public void ZeroStream_Seek_Begin_SetsPosition()
    {
        using var s = CreateZeroStream(100);
        s.ReadExactly(new byte[50], 0, 50);
        long pos = s.Seek(10, SeekOrigin.Begin);
        Assert.Equal(10, pos);
        Assert.Equal(10, s.Position);
    }

    [Fact]
    public void ZeroStream_Seek_Current_OffsetsFromCurrentPosition()
    {
        using var s = CreateZeroStream(100);
        s.ReadExactly(new byte[30], 0, 30);
        long pos = s.Seek(20, SeekOrigin.Current);
        Assert.Equal(50, pos);
        Assert.Equal(50, s.Position);
    }

    [Fact]
    public void ZeroStream_Seek_End_SetsPositionFromEnd()
    {
        using var s = CreateZeroStream(100);
        long pos = s.Seek(-10, SeekOrigin.End);
        Assert.Equal(90, pos);
        Assert.Equal(90, s.Position);
    }

    [Fact]
    public void ZeroStream_Seek_OutOfBounds_ThrowsIOException()
    {
        using var s = CreateZeroStream(100);
        Assert.Throws<IOException>(() => s.Seek(101, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => s.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void ZeroStream_Seek_InvalidOrigin_ThrowsArgumentOutOfRangeException()
    {
        using var s = CreateZeroStream(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Seek(0, (SeekOrigin)99));
    }

    [Fact]
    public void ZeroStream_Position_Setter_SetsPosition()
    {
        using var s = CreateZeroStream(100);
        s.Position = 50;
        Assert.Equal(50, s.Position);
        s.Position = 0;
        Assert.Equal(0, s.Position);
        s.Position = 100;
        Assert.Equal(100, s.Position);
    }

    [Fact]
    public void ZeroStream_Position_Setter_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        using var s = CreateZeroStream(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = 101);
    }

    // ── BuildRootDictionary / announce / url-list ────────────────────────────

    [Fact]
    public void Build_MultipleTrackerTiers_EmitsAllTiersInAnnounceList()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("tiers")
            .WithPieceLength(16_384)
            .AddTrackerTier(["http://t1a.com/announce", "http://t1b.com/announce"])
            .AddTrackerTier(["http://t2.com/announce"])
            .AddFile("f.bin", new byte[1024])
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var announceList = Assert.IsType<BList>(root.Get("announce-list"));
        Assert.Equal(2, announceList.List.Count);
        Assert.Equal(2, Assert.IsType<BList>(announceList.List[0]).List.Count);
        Assert.Single(Assert.IsType<BList>(announceList.List[1]).List);
    }

    [Fact]
    public void Build_TrackerTierOnly_NoWithAnnounce_FirstUrlBecomesAnnounce()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("tier-only")
            .WithPieceLength(16_384)
            .AddTrackerTier(["http://tracker.com/announce"])
            .AddFile("f.bin", new byte[1024])
            .Build();

        Assert.Equal("http://tracker.com/announce", torrent.Announce);
    }

    [Fact]
    public void Build_MultipleWebSeeds_EmitsBListNotBString()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("seeds")
            .WithPieceLength(16_384)
            .AddWebSeed("http://seed1.example/data")
            .AddWebSeed("http://seed2.example/data")
            .AddFile("f.bin", new byte[1024])
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        Assert.IsType<BList>(root.Get("url-list"));
        Assert.Equal(2, torrent.WebSeeds.Count);
    }

    // ── WithPrivate ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithPrivate_SetsPrivateFlagInParsedTorrent()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("private-test")
            .WithPieceLength(16_384)
            .WithPrivate()
            .AddFile("f.bin", new byte[1024])
            .Build();

        Assert.True(torrent.IsPrivate);
    }

    // ── AddFileFromPath ──────────────────────────────────────────────────────

    [Fact]
    public void AddFileFromPath_FileNotFound_ThrowsFileNotFoundException()
    {
        var builder = new ApiTorrentFileBuilder();
        Assert.Throws<FileNotFoundException>(() =>
            builder.AddFileFromPath(@"C:\nonexistent\ghost\file.bin"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddFileFromPath_EmptyOrWhitespacePath_ThrowsArgumentException(string path)
    {
        var builder = new ApiTorrentFileBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddFileFromPath(path));
    }

    [Fact(Timeout = 30000)]
    public async Task AddFileFromPath_WithExplicitTorrentPath_UsesProvidedPath()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "MtTorrentBuilderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string filePath = Path.Combine(tempRoot, "disk.bin");
            await File.WriteAllBytesAsync(filePath, new byte[1024]);

            // Multi-file mode so the path appears in the files list (not folded into name)
            var torrent = new ApiTorrentFileBuilder()
                .WithName("FromDisk")
                .WithPieceLength(16_384)
                .AddFileFromPath(filePath, "custom/path.bin")
                .AddFile("extra.bin", new byte[16])
                .Build();

            var paths = torrent.GetFiles().Select(f => f.Path).ToList();
            Assert.Contains(Path.Combine("custom", "path.bin"), paths);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    // ── GetV1FilesWithPadding ────────────────────────────────────────────────

    [Fact]
    public void Build_PaddingEnabled_AlignedFile_NoPaddingInserted()
    {
        var fileA = new byte[16_384]; // exactly one piece — no remainder
        var fileB = new byte[1_024];

        var torrent = new ApiTorrentFileBuilder()
            .WithName("aligned")
            .WithPieceLength(16_384)
            .WithPaddingFiles()
            .AddFile("a.bin", fileA)
            .AddFile("b.bin", fileB)
            .Build();

        // fileA fills a complete piece, so no pad entry is needed
        Assert.Equal(2, torrent.Metadata.Info.Files.Count);
        Assert.Equal(2, torrent.FileCount);
    }

    [Fact]
    public void Build_HybridVersion_AutoInsertsV1PaddingBetweenFiles()
    {
        var fileA = new byte[8_192];  // half a piece
        var fileB = new byte[4_096];

        var torrent = new ApiTorrentFileBuilder()
            .WithName("hybrid-pad")
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.Hybrid)
            .AddFile("a.bin", fileA)
            .AddFile("b.bin", fileB)
            .Build();

        // Hybrid mode enables padding: a.bin (8 KiB) + pad (8 KiB) + b.bin (4 KiB)
        Assert.Equal(2, torrent.FileCount); // user-visible: a.bin and b.bin only

        // Metadata.Info.Files parses V2 file tree (no padding); check the raw V1 files list in bencode
        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var v1Files = Assert.IsType<BList>(info.Get("files"));
        Assert.Equal(3, v1Files.List.Count); // a.bin + .pad/8192 + b.bin
    }

    // ── BEP 47 attr ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_PaddingEnabled_PaddingEntriesCarryBep47Attr()
    {
        var fileA = new byte[8_192];
        var fileB = new byte[4_096];

        var torrent = new ApiTorrentFileBuilder()
            .WithName("attr-pad")
            .WithPieceLength(16_384)
            .WithPaddingFiles()
            .AddFile("a.bin", fileA)
            .AddFile("b.bin", fileB)
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var v1Files = Assert.IsType<BList>(info.Get("files"));
        Assert.Equal(3, v1Files.List.Count); // a.bin + .pad/8192 + b.bin

        foreach (var node in v1Files.List)
        {
            var fDict = Assert.IsType<BDict>(node);
            var pathList = Assert.IsType<BList>(fDict.Get("path"));
            bool isPadPath = pathList.List[0].ToString() == ".pad";
            if (isPadPath)
            {
                Assert.Equal("p", fDict.GetString("attr"));
            }
            else
            {
                Assert.Null(fDict.Get("attr"));
            }
        }
    }

    [Fact]
    public void Build_Hybrid_PaddingEntriesCarryBep47Attr()
    {
        var fileA = new byte[8_192];
        var fileB = new byte[4_096];

        var torrent = new ApiTorrentFileBuilder()
            .WithName("hybrid-attr-pad")
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.Hybrid)
            .AddFile("a.bin", fileA)
            .AddFile("b.bin", fileB)
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var v1Files = Assert.IsType<BList>(info.Get("files"));

        var padEntries = v1Files.List
            .OfType<BDict>()
            .Where(f => f.Get("path") is BList p && p.List[0].ToString() == ".pad")
            .ToList();
        Assert.Single(padEntries);
        Assert.Equal("p", padEntries[0].GetString("attr"));
    }

    [Fact]
    public void Parse_V1AttrPadding_WithoutPadPath_DetectedAsPadding()
    {
        // A padding entry marked only by BEP 47 "attr":"p", not by the .pad/ path convention
        var metadata = TorrentFileParser.Parse(BuildV1TorrentBytes(padAttr: "p", padPath: ["filler.bin"]));

        Assert.Equal(3, metadata.Info.Files.Count);
        Assert.False(metadata.Info.Files[0].IsPadding);
        Assert.True(metadata.Info.Files[1].IsPadding);
        Assert.False(metadata.Info.Files[2].IsPadding);
    }

    [Fact]
    public void Parse_V1AttrWithoutPaddingFlag_NotDetectedAsPadding()
    {
        // BEP 47 attr "x" (executable) must not mark the file as padding
        var metadata = TorrentFileParser.Parse(BuildV1TorrentBytes(padAttr: "x", padPath: ["filler.bin"]));

        Assert.All(metadata.Info.Files, f => Assert.False(f.IsPadding));
    }

    [Fact]
    public void Parse_V2FileTreeAttrPadding_DetectedAsPadding()
    {
        var fileTree = new BDict();
        fileTree.Dict["data.bin"] = MakeV2FileNode(100, attr: null);
        fileTree.Dict["filler.bin"] = MakeV2FileNode(100, attr: "p");

        var info = new BDict();
        info.Dict["name"] = new BString("v2-attr"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16_384);
        info.Dict["meta version"] = new BNumber(2);
        info.Dict["file tree"] = fileTree;

        var metadata = TorrentFileParser.ParseInfoBytes(BencodeWriter.Write(info));

        Assert.Equal(2, metadata.Info.Files.Count);
        Assert.False(metadata.Info.Files.First(f => f.Path == "data.bin").IsPadding);
        Assert.True(metadata.Info.Files.First(f => f.Path == "filler.bin").IsPadding);
    }

    [Fact]
    public void Build_V1_ExecutableAndHiddenAttributes_EmittedAndRoundTrip()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("attrs")
            .WithPieceLength(16_384)
            .AddFile("run.sh", new byte[100], TorrentFileAttributes.Executable)
            .AddFile("secret.bin", new byte[100], TorrentFileAttributes.Executable | TorrentFileAttributes.Hidden)
            .AddFile("plain.bin", new byte[100])
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var v1Files = Assert.IsType<BList>(info.Get("files"));
        Assert.Equal("x", Assert.IsType<BDict>(v1Files.List[0]).GetString("attr"));
        Assert.Equal("xh", Assert.IsType<BDict>(v1Files.List[1]).GetString("attr"));
        Assert.Null(Assert.IsType<BDict>(v1Files.List[2]).Get("attr"));

        var files = torrent.GetFiles();
        Assert.True(files[0].IsExecutable);
        Assert.False(files[0].IsHidden);
        Assert.True(files[1].IsExecutable);
        Assert.True(files[1].IsHidden);
        Assert.Equal(TorrentFileAttributes.None, files[2].Attributes);
    }

    [Fact]
    public void Build_V1SingleFile_AttributesEmittedInInfoDictionary()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("single")
            .WithPieceLength(16_384)
            .AddFile("run.sh", new byte[100], TorrentFileAttributes.Executable)
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        Assert.Equal("x", info.GetString("attr"));

        Assert.True(torrent.GetFile(0).IsExecutable);
    }

    [Fact]
    public void Build_V1Symlink_EmittedAndRoundTrip()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("links")
            .WithPieceLength(16_384)
            .AddFile("data/target.bin", new byte[100])
            .AddSymlink("links/alias.bin", "data/target.bin")
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var v1Files = Assert.IsType<BList>(info.Get("files"));
        var linkDict = v1Files.List.OfType<BDict>().Single(f => f.GetString("attr") == "l");
        Assert.Equal(0, linkDict.GetLong("length"));
        var symlinkPath = Assert.IsType<BList>(linkDict.Get("symlink path"));
        Assert.Equal(new[] { "data", "target.bin" }, symlinkPath.List.Select(n => n.ToString()).ToArray());

        var link = torrent.GetFiles().Single(f => f.IsSymlink);
        Assert.Equal(0, link.Size);
        Assert.Equal(string.Join(Path.DirectorySeparatorChar, "data", "target.bin"), link.SymlinkTarget);
    }

    [Fact]
    public void Build_V2Symlink_NoPiecesRoot_AndRoundTrip()
    {
        var torrent = new ApiTorrentFileBuilder()
            .WithName("v2-links")
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.V2)
            .AddFile("data/target.bin", new byte[100])
            .AddSymlink("alias.bin", "data/target.bin")
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var fileTree = Assert.IsType<BDict>(info.Get("file tree"));
        var linkInfo = Assert.IsType<BDict>(Assert.IsType<BDict>(fileTree.Get("alias.bin")).Get(""));
        Assert.Equal("l", linkInfo.GetString("attr"));
        Assert.Equal(0, linkInfo.GetLong("length"));
        Assert.Null(linkInfo.Get("pieces root"));
        Assert.NotNull(linkInfo.Get("symlink path"));

        var link = torrent.GetFiles().Single(f => f.IsSymlink);
        Assert.Equal(string.Join(Path.DirectorySeparatorChar, "data", "target.bin"), link.SymlinkTarget);
    }

    [Fact]
    public void Build_WithPerFileSha1_EmitsDigests_SkipsPaddingAndSymlinks()
    {
        var dataA = new byte[8_192];
        var dataB = new byte[4_096];
        Random.Shared.NextBytes(dataA);
        Random.Shared.NextBytes(dataB);

        var torrent = new ApiTorrentFileBuilder()
            .WithName("sha1")
            .WithPieceLength(16_384)
            .WithPaddingFiles()
            .WithPerFileSha1()
            .AddFile("a.bin", dataA)
            .AddFile("b.bin", dataB)
            .AddSymlink("alias.bin", "a.bin")
            .Build();

        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var v1Files = Assert.IsType<BList>(info.Get("files"));

        foreach (var fDict in v1Files.List.OfType<BDict>())
        {
            var pathList = Assert.IsType<BList>(fDict.Get("path"));
            string name = pathList.List[^1].ToString()!;
            var sha1 = fDict.GetBytes("sha1");
            switch (name)
            {
                case "a.bin":
                    Assert.Equal(System.Security.Cryptography.SHA1.HashData(dataA), sha1!.Value.ToArray());
                    break;
                case "b.bin":
                    Assert.Equal(System.Security.Cryptography.SHA1.HashData(dataB), sha1!.Value.ToArray());
                    break;
                default:
                    Assert.Null(sha1); // padding and symlink entries carry no sha1
                    break;
            }
        }

        var parsedA = torrent.GetFiles().Single(f => f.Path == "a.bin");
        Assert.NotNull(parsedA.Sha1);
        Assert.Equal(System.Security.Cryptography.SHA1.HashData(dataA), parsedA.Sha1!.Value.ToArray());
    }

    [Fact]
    public async Task BuildAsync_WithPerFileSha1_MatchesSyncBuild()
    {
        var data = new byte[10_000];
        Random.Shared.NextBytes(data);

        var builder = new ApiTorrentFileBuilder()
            .WithName("sha1-async")
            .WithPieceLength(16_384)
            .WithPerFileSha1()
            .AddFile("a.bin", data);

        var sync = builder.Build();
        var async = await builder.BuildAsync(TestContext.Current.CancellationToken);

        Assert.Equal(sync.InfoHash, async.InfoHash);
    }

    [Fact]
    public void AddFile_PaddingOrSymlinkAttribute_Throws()
    {
        var builder = new ApiTorrentFileBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddFile("a.bin", new byte[16], TorrentFileAttributes.Padding));
        Assert.Throws<ArgumentException>(() => builder.AddFile("a.bin", new byte[16], TorrentFileAttributes.Symlink));
    }

    [Fact]
    public void AddSymlink_InvalidTargetPath_Throws()
    {
        var builder = new ApiTorrentFileBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.AddSymlink("alias.bin", "../escape.bin"));
        Assert.Equal("targetPath", ex.ParamName);
    }

    [Fact]
    public void Parse_V1AttrFlags_SymlinkPathAndSha1_Parsed()
    {
        byte[] sha1 = new byte[20];
        Random.Shared.NextBytes(sha1);

        var entry = new BDict();
        entry.Dict["length"] = new BNumber(0);
        entry.Dict["attr"] = new BString("lxh"u8.ToArray());
        var target = new BList();
        target.List.Add(new BString("dir"u8.ToArray()));
        target.List.Add(new BString("real.bin"u8.ToArray()));
        entry.Dict["symlink path"] = target;
        entry.Dict["sha1"] = new BString(sha1);
        var pathList = new BList();
        pathList.List.Add(new BString("alias.bin"u8.ToArray()));
        entry.Dict["path"] = pathList;

        var other = new BDict();
        other.Dict["length"] = new BNumber(100);
        var otherPath = new BList();
        otherPath.List.Add(new BString("real.bin"u8.ToArray()));
        other.Dict["path"] = otherPath;

        var files = new BList();
        files.List.Add(other);
        files.List.Add(entry);

        var info = new BDict();
        info.Dict["name"] = new BString("attr-full"u8.ToArray());
        info.Dict["piece length"] = new BNumber(256);
        info.Dict["pieces"] = new BString(new byte[20]);
        info.Dict["files"] = files;

        var root = new BDict();
        root.Dict["info"] = info;

        var metadata = TorrentFileParser.Parse(BencodeWriter.Write(root));

        var link = metadata.Info.Files[1];
        Assert.True(link.IsSymlink);
        Assert.True(link.IsExecutable);
        Assert.True(link.IsHidden);
        Assert.False(link.IsPadding);
        Assert.Equal(string.Join(Path.DirectorySeparatorChar, "dir", "real.bin"), link.SymlinkTarget);
        Assert.Equal(sha1, link.Sha1);

        var plain = metadata.Info.Files[0];
        Assert.False(plain.IsSymlink);
        Assert.Null(plain.Sha1);
    }

    [Fact]
    public void Parse_InvalidSha1Length_Ignored()
    {
        var metadata = TorrentFileParser.Parse(BuildV1TorrentBytes(padAttr: "p", padPath: ["filler.bin"], padSha1: new byte[7]));

        Assert.All(metadata.Info.Files, f => Assert.Null(f.Sha1));
    }

    private static byte[] BuildV1TorrentBytes(string padAttr, string[] padPath, byte[]? padSha1 = null)
    {
        static BDict MakeFileEntry(long length, string[] pathParts, string? attr, byte[]? sha1 = null)
        {
            var entry = new BDict();
            entry.Dict["length"] = new BNumber(length);
            var pathList = new BList();
            foreach (var part in pathParts)
            {
                pathList.List.Add(new BString(System.Text.Encoding.UTF8.GetBytes(part)));
            }
            entry.Dict["path"] = pathList;
            if (attr != null)
            {
                entry.Dict["attr"] = new BString(System.Text.Encoding.UTF8.GetBytes(attr));
            }
            if (sha1 != null)
            {
                entry.Dict["sha1"] = new BString(sha1);
            }
            return entry;
        }

        var files = new BList();
        files.List.Add(MakeFileEntry(100, ["a.bin"], attr: null));
        files.List.Add(MakeFileEntry(156, padPath, padAttr, padSha1));
        files.List.Add(MakeFileEntry(100, ["b.bin"], attr: null));

        var info = new BDict();
        info.Dict["name"] = new BString("attr-parse"u8.ToArray());
        info.Dict["piece length"] = new BNumber(256);
        info.Dict["pieces"] = new BString(new byte[40]); // 2 pieces of 20-byte SHA-1
        info.Dict["files"] = files;

        var root = new BDict();
        root.Dict["info"] = info;
        return BencodeWriter.Write(root);
    }

    private static BDict MakeV2FileNode(long length, string? attr)
    {
        var fileInfo = new BDict();
        fileInfo.Dict["length"] = new BNumber(length);
        fileInfo.Dict["pieces root"] = new BString(new byte[32]);
        if (attr != null)
        {
            fileInfo.Dict["attr"] = new BString(System.Text.Encoding.UTF8.GetBytes(attr));
        }
        var node = new BDict();
        node.Dict[string.Empty] = fileInfo;
        return node;
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





