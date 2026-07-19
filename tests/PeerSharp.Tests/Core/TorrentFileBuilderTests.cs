using System.Security.Cryptography;
using PeerSharp.BEncoding;
using CoreBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Core;

public class TorrentFileBuilderTests
{
    private static byte[] MakeData(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }

    // ── Validation ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoFiles_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new CoreBuilder().Build());
    }

    [Fact]
    public void AddFile_NullData_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new CoreBuilder().AddFile("file.bin", null!));
    }

    [Fact]
    public void AddFileFromPath_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new CoreBuilder().AddFileFromPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin")));
    }

    [Fact]
    public void AddFileFromPath_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CoreBuilder().AddFileFromPath(""));
    }

    [Fact]
    public void AddTracker_EmptyUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CoreBuilder().AddTracker("  "));
    }

    [Fact]
    public void AddTrackerTier_NullArgument_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new CoreBuilder().AddTrackerTier(null!));
    }

    [Fact]
    public void AddTrackerTier_EmptyCollection_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CoreBuilder().AddTrackerTier(Array.Empty<string>()));
    }

    [Fact]
    public void AddTrackerTier_AllWhitespace_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CoreBuilder().AddTrackerTier(["  ", "\t"]));
    }

    [Fact]
    public void AddWebSeed_EmptyUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CoreBuilder().AddWebSeed(""));
    }

    [Fact]
    public void WithAnnounce_EmptyUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CoreBuilder().WithAnnounce("  "));
    }

    [Fact]
    public void WithName_EmptyName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CoreBuilder().WithName(""));
    }

    [Fact]
    public void WithPieceLength_Zero_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreBuilder().WithPieceLength(0));
    }

    [Fact]
    public void Build_V2WithPaddingEnabled_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CoreBuilder()
                .AddFile("file.bin", MakeData(100))
                .WithVersion(TorrentFileVersion.V2)
                .WithPaddingFiles(true)
                .Build());
    }

    [Fact]
    public void Build_V2WithSmallPieceLength_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CoreBuilder()
                .AddFile("file.bin", MakeData(100))
                .WithVersion(TorrentFileVersion.V2)
                .WithPieceLength(1024)
                .Build());
    }

    [Fact]
    public void Build_V2WithNonPowerOfTwoMultiple_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CoreBuilder()
                .AddFile("file.bin", MakeData(100))
                .WithVersion(TorrentFileVersion.V2)
                .WithPieceLength(3 * 16384u)
                .Build());
    }

    // ── V1 single-file ─────────────────────────────────────────────────────

    [Fact]
    public void Build_V1_SingleFile_ProducesValidTorrent()
    {
        var torrent = new CoreBuilder()
            .AddFile("myfile.bin", MakeData(1000))
            .Build();

        Assert.True(torrent.IsV1);
        Assert.False(torrent.IsV2);
        Assert.Equal("myfile.bin", torrent.Name);
        Assert.Equal(1, torrent.FileCount);
        Assert.Equal(1000, torrent.TotalSize);
        Assert.Equal(1, torrent.PieceCount);
        Assert.False(torrent.InfoHash.IsEmpty);
    }

    [Fact]
    public void Build_V1_PieceHashMatchesSha1OfData()
    {
        byte[] data = MakeData(256);
        var torrent = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithPieceLength(256)
            .Build();

        Assert.Equal(1, torrent.PieceCount);
        byte[] expected = SHA1.HashData(data);
        Assert.Equal(expected, torrent.Metadata.Info.Pieces[0]);
    }

    [Fact]
    public void Build_V1_ExactlyTwoPieces_ProducesCorrectCount()
    {
        byte[] data = MakeData(512);
        var torrent = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithPieceLength(256)
            .Build();

        Assert.Equal(2, torrent.PieceCount);
        Assert.Equal(256u, torrent.PieceSize);
    }

    // ── V1 multi-file ──────────────────────────────────────────────────────

    [Fact]
    public void Build_V1_MultiFile_ProducesValidTorrent()
    {
        var torrent = new CoreBuilder()
            .AddFile("root/a.bin", MakeData(500))
            .AddFile("root/b.bin", MakeData(300))
            .Build();

        Assert.True(torrent.IsV1);
        Assert.Equal(2, torrent.FileCount);
        Assert.Equal(800, torrent.TotalSize);
        Assert.Equal("root", torrent.Name);
    }

    [Fact]
    public void Build_V1_MultiFile_CorrectFileSizes()
    {
        var torrent = new CoreBuilder()
            .AddFile("dir/a.bin", MakeData(300))
            .AddFile("dir/b.bin", MakeData(700))
            .Build();

        var files = torrent.GetFiles();
        Assert.Equal(2, files.Count);
        Assert.Equal(300, files[0].Size);
        Assert.Equal(700, files[1].Size);
    }

    // ── Name inference ─────────────────────────────────────────────────────

    [Fact]
    public void Build_V1_ExplicitName_UsesProvidedName()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .WithName("MyTorrent")
            .Build();

        Assert.Equal("MyTorrent", torrent.Name);
    }

    [Fact]
    public void Build_V1_SingleFileNoExplicitName_InfersFromFileName()
    {
        var torrent = new CoreBuilder()
            .AddFile("subdir/myfile.txt", MakeData(10))
            .Build();

        Assert.Equal("myfile.txt", torrent.Name);
    }

    [Fact]
    public void Build_V1_MultiFileNoCommonRoot_UsesTorrentFallback()
    {
        var torrent = new CoreBuilder()
            .AddFile("folder1/file.bin", MakeData(10))
            .AddFile("folder2/file.bin", MakeData(10))
            .Build();

        Assert.Equal("torrent", torrent.Name);
    }

    // ── Trackers and web seeds ─────────────────────────────────────────────

    [Fact]
    public void Build_V1_WithAnnounce_IncludesInTorrent()
    {
        const string url = "udp://tracker.example.com:6969/announce";
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .WithAnnounce(url)
            .Build();

        Assert.Equal(url, torrent.Announce);
        Assert.Contains(url, torrent.Trackers);
    }

    [Fact]
    public void Build_V1_AddTracker_PopulatesTrackers()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .AddTracker("udp://t1.example.com:6969/announce")
            .AddTracker("udp://t2.example.com:6969/announce")
            .Build();

        Assert.Contains("udp://t1.example.com:6969/announce", torrent.Trackers);
        Assert.Contains("udp://t2.example.com:6969/announce", torrent.Trackers);
    }

    [Fact]
    public void Build_V1_AddTrackerTier_PopulatesTrackers()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .AddTrackerTier(["udp://t1.example.com:6969/announce", "udp://t2.example.com:6969/announce"])
            .Build();

        Assert.Contains("udp://t1.example.com:6969/announce", torrent.Trackers);
        Assert.Contains("udp://t2.example.com:6969/announce", torrent.Trackers);
    }

    [Fact]
    public void Build_V1_WithAnnounceAndNoTier_AutoAddsToFirstTier()
    {
        const string url = "udp://tracker.example.com:6969/announce";
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .WithAnnounce(url)
            .Build();

        Assert.Contains(url, torrent.Trackers);
    }

    [Fact]
    public void Build_V1_WithSingleWebSeed_IncludesUrl()
    {
        const string seed = "http://seeds.example.com/files/";
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .AddWebSeed(seed)
            .Build();

        Assert.Single(torrent.WebSeeds);
        Assert.Equal(seed, torrent.WebSeeds[0]);
    }

    [Fact]
    public void Build_V1_WithMultipleWebSeeds_IncludesAll()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .AddWebSeed("http://seeds1.example.com/files/")
            .AddWebSeed("http://seeds2.example.com/files/")
            .Build();

        Assert.Equal(2, torrent.WebSeeds.Count);
    }

    // ── Flags ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_V1_WithPrivate_SetsPrivateFlag()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .WithPrivate(true)
            .Build();

        Assert.True(torrent.IsPrivate);
    }

    [Fact]
    public void Build_V1_WithoutPrivate_PrivateFlagFalse()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .Build();

        Assert.False(torrent.IsPrivate);
    }

    // ── Padding ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_V1_WithPadding_VisibleFileCountUnchanged()
    {
        var torrent = new CoreBuilder()
            .AddFile("root/a.bin", MakeData(100))
            .AddFile("root/b.bin", MakeData(100))
            .WithPieceLength(256)
            .WithPaddingFiles(true)
            .Build();

        Assert.Equal(2, torrent.FileCount);
    }

    [Fact]
    public void Build_V1_WithPadding_WritesBep47PaddingAttribute()
    {
        var torrent = new CoreBuilder()
            .AddFile("root/a.bin", MakeData(100))
            .AddFile("root/b.bin", MakeData(100))
            .WithPieceLength(256)
            .WithPaddingFiles(true)
            .Build();

        var paddingFile = GetRawV1Files(torrent).Single(f => GetPath(f).StartsWith(".pad/", StringComparison.Ordinal));

        Assert.Equal("p", paddingFile.GetString("attr"));
    }

    // ── V2 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_V2_SingleFile_ProducesValidTorrent()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .WithVersion(TorrentFileVersion.V2)
            .Build();

        Assert.True(torrent.IsV2);
        Assert.False(torrent.IsV1);
        Assert.False(torrent.InfoHashV2.IsEmpty);
        Assert.Equal(1, torrent.FileCount);
        // V2 FullSize is piece-aligned, so TotalSize >= actual file size
        Assert.True(torrent.TotalSize >= 100);
    }

    [Fact]
    public void Build_V2_MultiFile_ProducesValidTorrent()
    {
        var torrent = new CoreBuilder()
            .AddFile("dir/a.bin", MakeData(200))
            .AddFile("dir/b.bin", MakeData(300))
            .WithVersion(TorrentFileVersion.V2)
            .Build();

        Assert.True(torrent.IsV2);
        Assert.Equal(2, torrent.FileCount);
        Assert.True(torrent.TotalSize >= 500);
    }

    // ── Hybrid ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Hybrid_ProducesBothHashes()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .WithVersion(TorrentFileVersion.Hybrid)
            .Build();

        Assert.True(torrent.IsHybrid);
        Assert.False(torrent.InfoHash.IsEmpty);
        Assert.False(torrent.InfoHashV2.IsEmpty);
    }

    [Fact]
    public void Build_Hybrid_WritesV1PaddingFileWithBep47Attribute()
    {
        var torrent = new CoreBuilder()
            .AddFile("root/a.bin", MakeData(100))
            .AddFile("root/b.bin", MakeData(100))
            .WithPieceLength(16_384)
            .WithVersion(TorrentFileVersion.Hybrid)
            .Build();

        var rawFiles = GetRawV1Files(torrent);
        var paddingFile = rawFiles.Single(f => GetPath(f).StartsWith(".pad/", StringComparison.Ordinal));

        Assert.Equal(3, rawFiles.Count);
        Assert.Equal("p", paddingFile.GetString("attr"));
    }

    // ── Async ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_V1_MatchesSyncBuild()
    {
        byte[] data = MakeData(1000);
        var builder = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithPieceLength(256)
            .WithName("test");

        var sync = builder.Build();
        var async = await builder.BuildAsync();

        Assert.Equal(sync.InfoHash, async.InfoHash);
        Assert.Equal(sync.TotalSize, async.TotalSize);
        Assert.Equal(sync.PieceCount, async.PieceCount);
    }

    [Fact]
    public async Task BuildAsync_V2_MatchesSyncBuild()
    {
        byte[] data = MakeData(200);
        var builder = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithVersion(TorrentFileVersion.V2);

        var sync = builder.Build();
        var async = await builder.BuildAsync();

        Assert.Equal(sync.InfoHashV2, async.InfoHashV2);
        Assert.Equal(sync.TotalSize, async.TotalSize);
    }

    [Fact]
    public async Task BuildAsync_Cancelled_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var builder = new CoreBuilder().AddFile("file.bin", MakeData(1000));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(cts.Token));
    }

    // ── AddFileFromPath ────────────────────────────────────────────────────

    [Fact]
    public async Task AddFileFromPath_ExistingFile_BuildsCorrectly()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            byte[] data = MakeData(500);
            await File.WriteAllBytesAsync(tempFile, data);

            var torrent = new CoreBuilder()
                .AddFileFromPath(tempFile)
                .Build();

            Assert.Equal(500, torrent.TotalSize);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AddFileFromPath_WithCustomTorrentPath_UsesProvidedName()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, MakeData(100));

            var torrent = new CoreBuilder()
                .AddFileFromPath(tempFile, "custom/path.bin")
                .Build();

            Assert.Equal("path.bin", torrent.Name);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AddFileFromPath_WithAsyncIODisabled_MatchesResult()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            byte[] data = MakeData(300);
            await File.WriteAllBytesAsync(tempFile, data);

            var syncBuilder = new CoreBuilder()
                .AddFileFromPath(tempFile, "file.bin")
                .WithAsyncFileIO(false);

            var asyncBuilder = new CoreBuilder()
                .AddFileFromPath(tempFile, "file.bin")
                .WithAsyncFileIO(true);

            var syncResult = await syncBuilder.BuildAsync();
            var asyncResult = await asyncBuilder.BuildAsync();

            Assert.Equal(syncResult.InfoHash, asyncResult.InfoHash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── Fluent method chaining ─────────────────────────────────────────────

    [Fact]
    public void Build_FluentChain_ReturnsBuilder()
    {
        var builder = new CoreBuilder();
        var same = builder
            .WithName("test")
            .WithPieceLength(512)
            .WithPrivate(false)
            .WithPaddingFiles(false)
            .WithAnnounce("udp://t.example.com:6969/announce")
            .AddTracker("udp://t2.example.com:6969/announce")
            .AddTrackerTier(["udp://t3.example.com:6969/announce"])
            .AddWebSeed("http://seed.example.com/")
            .AddFile("file.bin", MakeData(100));

        Assert.Same(builder, same);
    }

    // ── Round-trip integrity ───────────────────────────────────────────────

    [Fact]
    public void Build_V1_ParsedTorrentMatchesOriginal()
    {
        byte[] data = MakeData(768);
        var torrent = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithPieceLength(256)
            .WithName("roundtrip")
            .Build();

        // Re-parse from raw bytes to confirm the output is a valid .torrent
        var reparsed = TorrentFile.Parse(torrent.RawData.ToArray());
        Assert.Equal(torrent.InfoHash, reparsed.InfoHash);
        Assert.Equal("roundtrip", reparsed.Name);
        Assert.Equal(3, reparsed.PieceCount);
    }

    // ── Path-validation edge cases ─────────────────────────────────────────

    [Fact]
    public void AddFile_AbsolutePath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new CoreBuilder().AddFile("/absolute/path.bin", MakeData(10)));
    }

    [Fact]
    public void AddFile_DotSegment_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new CoreBuilder().AddFile("dir/./file.bin", MakeData(10)));
    }

    [Fact]
    public void AddFile_DotDotSegment_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new CoreBuilder().AddFile("../outside.bin", MakeData(10)));
    }

    [Fact]
    public void AddFile_ZeroByteFile_BuildsSuccessfully()
    {
        var torrent = new CoreBuilder()
            .AddFile("empty.bin", [])
            .Build();

        Assert.Equal(0, torrent.TotalSize);
    }

    [Fact]
    public void Build_V1_PaddingAlignsPieceCount()
    {
        var torrent = new CoreBuilder()
            .AddFile("root/a.bin", MakeData(100))
            .AddFile("root/b.bin", MakeData(100))
            .WithPieceLength(256)
            .WithPaddingFiles(true)
            .Build();

        // With padding: a.bin(100) + pad(156) + b.bin(100) = 356 bytes; 2 pieces of 256 needed
        Assert.True(torrent.PieceCount >= 1);
        Assert.True(torrent.TotalSize < (long)torrent.PieceSize * torrent.PieceCount);
    }

    [Fact]
    public void Build_V1_LargeMultiPieceFile_CorrectPieceCount()
    {
        byte[] data = MakeData(5 * 256);
        var torrent = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithPieceLength(256)
            .Build();

        Assert.Equal(5, torrent.PieceCount);
    }

    [Fact]
    public void Build_Hybrid_InfoHashDifferentFromV2()
    {
        var torrent = new CoreBuilder()
            .AddFile("file.bin", MakeData(100))
            .WithVersion(TorrentFileVersion.Hybrid)
            .Build();

        Assert.True(torrent.IsHybrid);
        Assert.NotEqual(torrent.InfoHash, torrent.InfoHashV2);
    }

    [Fact]
    public void Build_WithPrivate_ProducesDistinctInfoHashFromPublic()
    {
        byte[] data = MakeData(200);

        var publicTorrent = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithPieceLength(256)
            .WithName("test")
            .Build();

        var privateTorrent = new CoreBuilder()
            .AddFile("file.bin", data)
            .WithPieceLength(256)
            .WithName("test")
            .WithPrivate(true)
            .Build();

        Assert.NotEqual(publicTorrent.InfoHash, privateTorrent.InfoHash);
    }

    [Fact]
    public async Task BuildAsync_FromDisk_SameHashAsBuildFromMemory()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            byte[] data = MakeData(512);
            await File.WriteAllBytesAsync(tempFile, data);

            var fromDisk = new CoreBuilder()
                .AddFileFromPath(tempFile, "file.bin")
                .WithPieceLength(256)
                .WithName("test")
                .Build();

            var fromMemory = new CoreBuilder()
                .AddFile("file.bin", data)
                .WithPieceLength(256)
                .WithName("test")
                .Build();

            Assert.Equal(fromMemory.InfoHash, fromDisk.InfoHash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static List<BDict> GetRawV1Files(TorrentFile torrent)
    {
        var root = Assert.IsType<BDict>(BencodeParser.Parse(torrent.RawData.ToArray()));
        var info = Assert.IsType<BDict>(root.Get("info"));
        var files = Assert.IsType<BList>(info.Get("files"));
        return files.List.Cast<BDict>().ToList();
    }

    private static string GetPath(BDict file)
    {
        var path = Assert.IsType<BList>(file.Get("path"));
        return string.Join("/", path.List.Select(part => Assert.IsType<BString>(part).Text));
    }
}
