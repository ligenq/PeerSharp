using PeerSharp.Internals;
using PeerSharp.Internals.Utilities;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Covers the magnet preview API surface: WaitForMetadataAsync completion semantics,
/// the StopAfterMetadata flag, and ExportTorrentFile metadata reuse.
/// </summary>
public class TorrentMetadataPreviewTests
{
    [Fact]
    public async Task WaitForMetadataAsync_TorrentCreatedWithMetadata_CompletesImmediately()
    {
        var (torrent, path) = CreateTorrentWithMetadata();
        try
        {
            var task = torrent.WaitForMetadataAsync(TestContext.Current.CancellationToken);

            Assert.True(task.IsCompleted);
            await task;
        }
        finally
        {
            await CleanupAsync(torrent, path);
        }
    }

    [Fact]
    public async Task WaitForMetadataAsync_MagnetStyleTorrent_PendsUntilMetadataApplied()
    {
        var (torrent, path) = CreateMagnetStyleTorrent();
        try
        {
            var task = torrent.WaitForMetadataAsync(TestContext.Current.CancellationToken);
            Assert.False(task.IsCompleted);

            // Simulate the metadata download completing (what MetadataDownload does)
            ApplyMetadata(torrent);
            await torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

            await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await CleanupAsync(torrent, path);
        }
    }

    [Fact]
    public async Task WaitForMetadataAsync_Cancelled_Throws()
    {
        var (torrent, path) = CreateMagnetStyleTorrent();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => torrent.WaitForMetadataAsync(cts.Token));
        }
        finally
        {
            await CleanupAsync(torrent, path);
        }
    }

    [Fact]
    public async Task ReinitializeAfterMetadata_StopAfterMetadata_LeavesTorrentStopped()
    {
        var (torrent, path) = CreateTorrentWithMetadata();
        try
        {
            await torrent.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(torrent.Started);

            torrent.StopAfterMetadata = true;
            await torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

            // The race-free preview window: no restart, so no blocks can be requested
            Assert.False(torrent.Started);
            Assert.True(torrent.WaitForMetadataAsync(TestContext.Current.CancellationToken).IsCompleted);
        }
        finally
        {
            await CleanupAsync(torrent, path);
        }
    }

    [Fact]
    public async Task ReinitializeAfterMetadata_WithoutFlag_RestartsStartedTorrent()
    {
        var (torrent, path) = CreateTorrentWithMetadata();
        try
        {
            await torrent.StartAsync(TestContext.Current.CancellationToken);

            await torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

            Assert.True(torrent.Started);
            await torrent.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await CleanupAsync(torrent, path);
        }
    }

    [Fact]
    public async Task ExportTorrentFile_RoundTripsMetadata()
    {
        byte[] data = new byte[16_384];
        Random.Shared.NextBytes(data);

        var built = new ApiTorrentFileBuilder()
            .WithName("preview")
            .WithPieceLength(16_384)
            .AddFile("a.bin", data)
            .Build();

        var metadata = TorrentFileParser.Parse(built.RawData.ToArray());
        string path = TempPath();
        var torrent = TorrentTestUtility.CreateMinimal(metadata, path);
        try
        {
            var exported = torrent.ExportTorrentFile();

            Assert.Equal(built.InfoHash, exported.InfoHash);
            Assert.Equal(1, exported.FileCount);

            // The exported RawData must be independently parseable - this is the byte[]
            // an application caches to skip the metadata download next time
            var reparsed = TorrentFile.Parse(exported.RawData.ToArray());
            Assert.Equal(built.InfoHash, reparsed.InfoHash);
        }
        finally
        {
            await CleanupAsync(torrent, path);
        }
    }

    [Fact]
    public async Task ExportTorrentFile_WithoutMetadata_Throws()
    {
        var (torrent, path) = CreateMagnetStyleTorrent();
        try
        {
            Assert.Throws<InvalidOperationException>(() => torrent.ExportTorrentFile());
        }
        finally
        {
            await CleanupAsync(torrent, path);
        }
    }

    private static (Torrent Torrent, string Path) CreateTorrentWithMetadata()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.Name = "preview";
        metadata.Info.PieceSize = 16_384;
        metadata.Info.FullSize = 16_384;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = 16_384, Offset = 0 });
        metadata.Info.Pieces.Add(new byte[20]);

        string path = TempPath();
        return (TorrentTestUtility.CreateMinimal(metadata, path), path);
    }

    private static (Torrent Torrent, string Path) CreateMagnetStyleTorrent()
    {
        // Magnet-style: hash only, no piece hashes yet
        var metadata = new TorrentFileMetadata();
        metadata.Info.Version = TorrentVersion.V1;
        metadata.Info.Hash = new InfoHash(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        metadata.Info.PieceSize = 16_384;

        string path = TempPath();
        return (TorrentTestUtility.CreateMinimal(metadata, path), path);
    }

    private static void ApplyMetadata(Torrent torrent)
    {
        torrent.InfoFile.Info.Name = "preview";
        torrent.InfoFile.Info.FullSize = 16_384;
        torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = 16_384, Offset = 0 });
        torrent.InfoFile.Info.Pieces.Add(new byte[20]);
    }

    private static string TempPath()
    {
        return Path.Combine(Path.GetTempPath(), "PeerSharpTests_MetadataPreview", Guid.NewGuid().ToString("N"));
    }

    private static async Task CleanupAsync(Torrent torrent, string path)
    {
        await torrent.DisposeAsync();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp artifacts.
        }
    }
}
