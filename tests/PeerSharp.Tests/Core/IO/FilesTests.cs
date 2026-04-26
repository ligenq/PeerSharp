using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class FilesTests
{
    [Fact]
    public async Task Create_UsesCustomPath()
    {
        var metadata = CreateMetadata(16 * 1024);
        string defaultPath = CreateTempPath();
        string customPath = CreateTempPath();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, defaultPath);
        var files = Files.Create(torrent, torrent.Services.FileHandleCache, customPath);

        Assert.Equal(customPath, files.DownloadPath);

        await files.DisposeAsync();
        await torrent.DisposeAsync();

        CleanupPath(defaultPath);
        CleanupPath(customPath);
    }

    [Fact]
    public async Task Create_UsesSettingsDefault_WhenCustomNull()
    {
        var metadata = CreateMetadata(16 * 1024);
        string defaultPath = CreateTempPath();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, defaultPath);
        var files = Files.Create(torrent, torrent.Services.FileHandleCache);

        Assert.Equal(defaultPath, files.DownloadPath);

        await files.DisposeAsync();
        await torrent.DisposeAsync();

        CleanupPath(defaultPath);
    }

    [Fact]
    public async Task Create_Throws_WhenNoPath()
    {
        var metadata = CreateMetadata(16 * 1024);
        string defaultPath = CreateTempPath();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, defaultPath);
        torrent.Settings.Files.DefaultDownloadPath = string.Empty;

        Assert.Throws<ArgumentException>(() => Files.Create(torrent, torrent.Services.FileHandleCache));

        await torrent.DisposeAsync();
        CleanupPath(defaultPath);
    }

    [Fact]
    public async Task ReadWrite_RoundTrip()
    {
        var metadata = CreateMetadata(32 * 1024);
        string defaultPath = CreateTempPath();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, defaultPath);
        var files = Files.Create(torrent, torrent.Services.FileHandleCache, defaultPath);

        await files.InitializeAsync(CreateSelection(metadata), CancellationToken.None);

        byte[] data = new byte[ProtocolConstants.BlockSize];
        Random.Shared.NextBytes(data);

        await files.WriteAsync(0, data, CancellationToken.None);

        byte[] read = await files.ReadAsync(0, data.Length, CancellationToken.None);

        Assert.Equal(data, read);

        await files.DisposeAsync();
        await torrent.DisposeAsync();

        CleanupPath(defaultPath);
    }

    [Fact]
    public async Task DeleteFilesAsync_AfterStop_RemovesAllocatedFiles()
    {
        var metadata = CreateMetadata(16 * 1024);
        string defaultPath = CreateTempPath();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, defaultPath);
        var files = Files.Create(torrent, torrent.Services.FileHandleCache, defaultPath);

        await files.InitializeAsync(CreateSelection(metadata), CancellationToken.None);
        string filePath = Path.Combine(defaultPath, "file.bin");
        Assert.True(System.IO.File.Exists(filePath));

        await files.StopAsync();
        await files.DeleteFilesAsync(CancellationToken.None);

        Assert.False(System.IO.File.Exists(filePath));

        await torrent.DisposeAsync();
        CleanupPath(defaultPath);
    }

    [Fact]
    public async Task UpdateFileSelectionAsync_AfterDispose_IsNoOp()
    {
        // Closes the TOCTOU window between Torrent.OnSelectionChangedAsync and the call:
        // selection updates that race with shutdown must not throw or hit disposed storage.
        var metadata = CreateMetadata(16 * 1024);
        string defaultPath = CreateTempPath();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, defaultPath);
        var files = Files.Create(torrent, torrent.Services.FileHandleCache, defaultPath);

        await files.InitializeAsync(CreateSelection(metadata), CancellationToken.None);
        await files.DisposeAsync();

        // Calling on a disposed Files must complete without throwing.
        await files.UpdateFileSelectionAsync(CreateSelection(metadata));

        await torrent.DisposeAsync();
        CleanupPath(defaultPath);
    }

    private static TorrentFileMetadata CreateMetadata(long size)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "test";
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = size;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry {
            Path = "file.bin",
            Size = size,
            Offset = 0
        });
        return metadata;
    }

    private static IReadOnlyList<FileSelection> CreateSelection(TorrentFileMetadata metadata)
    {
        var selection = new List<FileSelection>(metadata.Info.Files.Count);
        for (int i = 0; i < metadata.Info.Files.Count; i++)
        {
            selection.Add(new FileSelection { Selected = true, Priority = Priority.Normal });
        }
        return selection;
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "MtTorrentTests_Files", Guid.NewGuid().ToString("N"));
    }

    private static void CleanupPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup for temp artifacts.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}





