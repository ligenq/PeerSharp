using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class StorageTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly TorrentFileMetadata _metadata;
    private readonly FileHandleCache _handleCache;
    private readonly Storage _storage;

    public StorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MtTorrentTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _metadata = new TorrentFileMetadata();
        _metadata.Info.Name = "test_torrent";
        _metadata.Info.PieceSize = 16384;
        _metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file1.txt", Size = 1000, Offset = 0 });
        _metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "folder/file2.txt", Size = 2000, Offset = 1000 });
        _metadata.Info.FullSize = 3000;

        _handleCache = new FileHandleCache();
        var validator = new PathValidator(_tempDir);
        _storage = new Storage(_metadata, _tempDir, validator, _handleCache, enableSparseFiles: false);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _storage.DisposeAsync();
        _handleCache.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task Init_CreatesFilesAndDirectories()
    {
        await _storage.InitAsync();

        string file1 = Path.Combine(_tempDir, "file1.txt");
        string file2 = Path.Combine(_tempDir, "folder", "file2.txt");

        Assert.True(System.IO.File.Exists(file1));
        Assert.True(System.IO.File.Exists(file2));
        Assert.Equal(1000, new FileInfo(file1).Length);
        Assert.Equal(2000, new FileInfo(file2).Length);
    }

    [Fact(Timeout = 30000)]
    public async Task WriteAndRead_WorksAcrossFiles()
    {
        await _storage.InitAsync();

        // Write 500 bytes starting at offset 800 (spans file1 and file2)
        // file1: 800-999 (200 bytes)
        // file2: 0-299 (300 bytes)
        byte[] data = new byte[500];
        for (int i = 0; i < 500; i++)
        {
            data[i] = (byte)(i % 256);
        }

        await _storage.WriteAsync(800, data);

        // Read it back
        byte[] readBack = await _storage.ReadAsync(800, 500);
        Assert.Equal(data, readBack);

        // Close handles so we can use ReadAllBytes
        _handleCache.CloseTorrentHandles(_tempDir);

        // Verify file1 content
        byte[] f1Data = System.IO.File.ReadAllBytes(Path.Combine(_tempDir, "file1.txt"));
        Assert.Equal(data[0], f1Data[800]);
        Assert.Equal(data[199], f1Data[999]);

        // Verify file2 content
        byte[] f2Data = System.IO.File.ReadAllBytes(Path.Combine(_tempDir, "folder", "file2.txt"));
        Assert.Equal(data[200], f2Data[0]);
        Assert.Equal(data[499], f2Data[299]);
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateFileSelection_EnablesFiles()
    {
        // Initially only select first file
        var selection = new List<FileSelection>
        {
            new() { Selected = true, Priority = Priority.Normal },
            new() { Selected = false, Priority = Priority.DoNotDownload }
        };

        await _storage.InitAsync(selection);

        string file1 = Path.Combine(_tempDir, "file1.txt");
        string file2 = Path.Combine(_tempDir, "folder", "file2.txt");

        Assert.True(System.IO.File.Exists(file1));
        Assert.False(System.IO.File.Exists(file2));

        // Update selection to enable file2
        selection[1] = new FileSelection { Selected = true, Priority = Priority.Normal };
        await _storage.UpdateFileSelectionAsync(selection);

        Assert.True(System.IO.File.Exists(file2));
    }

    [Fact(Timeout = 30000)]
    public async Task Read_SkippedFile_ReturnsZeros()
    {
        var selection = new List<FileSelection>
        {
            new() { Selected = true, Priority = Priority.Normal },
            new() { Selected = false, Priority = Priority.DoNotDownload }
        };

        await _storage.InitAsync(selection);

        // Read from file2 which is skipped
        byte[] data = await _storage.ReadAsync(1500, 100);

        Assert.All(data, b => Assert.Equal(0, b));
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateFileSelection_DisablesWrites()
    {
        var selection = new List<FileSelection>
        {
            new() { Selected = true, Priority = Priority.Normal },
            new() { Selected = true, Priority = Priority.Normal }
        };

        await _storage.InitAsync(selection);

        selection[1] = new FileSelection { Selected = false, Priority = Priority.DoNotDownload };
        await _storage.UpdateFileSelectionAsync(selection);

        byte[] data = Enumerable.Repeat((byte)0xAB, 100).ToArray();
        await _storage.WriteAsync(1000, data);

        _handleCache.CloseTorrentHandles(_tempDir);

        byte[] file2Data = System.IO.File.ReadAllBytes(Path.Combine(_tempDir, "folder", "file2.txt"));
        Assert.All(file2Data.Take(100), b => Assert.Equal(0, b));
    }
}





