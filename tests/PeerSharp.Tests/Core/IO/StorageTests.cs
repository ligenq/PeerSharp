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

        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
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
        byte[] f1Data = File.ReadAllBytes(Path.Combine(_tempDir, "file1.txt"));
        Assert.Equal(data[0], f1Data[800]);
        Assert.Equal(data[199], f1Data[999]);

        // Verify file2 content
        byte[] f2Data = File.ReadAllBytes(Path.Combine(_tempDir, "folder", "file2.txt"));
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

        Assert.True(File.Exists(file1));
        Assert.False(File.Exists(file2));

        // Update selection to enable file2
        selection[1] = new FileSelection { Selected = true, Priority = Priority.Normal };
        await _storage.UpdateFileSelectionAsync(selection);

        Assert.True(File.Exists(file2));
    }

    [Fact(Timeout = 30000)]
    public async Task Init_WhenAlreadyInitializedWithSelection_UpdatesFileSelection()
    {
        var selection = new List<FileSelection>
        {
            new() { Selected = true, Priority = Priority.Normal },
            new() { Selected = false, Priority = Priority.DoNotDownload }
        };

        await _storage.InitAsync(selection);

        string file2 = Path.Combine(_tempDir, "folder", "file2.txt");
        Assert.False(File.Exists(file2));

        selection[1] = new FileSelection { Selected = true, Priority = Priority.Normal };
        await _storage.InitAsync(selection);

        Assert.True(File.Exists(file2));
    }

    [Fact(Timeout = 30000)]
    public async Task Init_SkipsInvalidTorrentPathWithoutCreatingEscapedFile()
    {
        string storageRoot = Path.Combine(_tempDir, "invalid-path");
        string escapedPath = Path.GetFullPath(Path.Combine(storageRoot, "..", "escape.txt"));
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "invalid_path";
        metadata.Info.PieceSize = 16384;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "../escape.txt", Size = 32, Offset = 0 });
        metadata.Info.FullSize = 32;

        using var handleCache = new FileHandleCache();
        var validator = new PathValidator(storageRoot);
        var storage = new Storage(metadata, storageRoot, validator, handleCache, enableSparseFiles: false);
        try
        {
            await storage.InitAsync();

            Assert.False(File.Exists(escapedPath));
        }
        finally
        {
            await storage.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitAsync_Fails_ResetsInitializationState()
    {
        const string invalidRoot = "||invalid_root||";
        using var handleCache = new FileHandleCache();
        var validator = new PathValidator(invalidRoot);
        var storage = new Storage(_metadata, invalidRoot, validator, handleCache, enableSparseFiles: false);

        await Assert.ThrowsAnyAsync<Exception>(() => storage.InitAsync());

        // We can verify state by trying to write or read, which should fail saying not initialized,
        // or by using reflection to check _initialized
        var initField = typeof(Storage).GetField("_initialized", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        int initialized = (int)initField!.GetValue(storage)!;
        Assert.Equal(0, initialized);
    }

    private class ThrowingHandleCache : IFileHandleCache
    {
        public Exception ToThrow { get; set; } = new Exception("Generic error");
        public void CloseTorrentHandles(string rootPath) { }
        public ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default)
            => throw ToThrow;
        public void Dispose() { }
    }

    [Fact]
    public async Task WriteAsync_DiskFull_ThrowsStorageException()
    {
        var ioException = new IOException("Disk full");
        ioException.HResult = unchecked((int)0x80070070); // ERROR_DISK_FULL
        using var handleCache = new ThrowingHandleCache { ToThrow = ioException };
        var validator = new PathValidator(_tempDir);
        var storage = new Storage(_metadata, _tempDir, validator, handleCache, enableSparseFiles: false);
        await storage.InitAsync();

        var ex = await Assert.ThrowsAsync<StorageException>(() => storage.WriteAsync(0, new byte[10]).AsTask());
        Assert.Equal("Disk full", ex.Message);
        Assert.False(ex.IsRecoverable);
    }

    [Fact]
    public async Task WriteAsync_OtherException_CallsHandleFileWriteError()
    {
        using var handleCache = new ThrowingHandleCache { ToThrow = new IOException("Some other error") };
        var validator = new PathValidator(_tempDir);
        var storage = new Storage(_metadata, _tempDir, validator, handleCache, enableSparseFiles: false);
        await storage.InitAsync();

        // This won't throw StorageException immediately, but it logs and increments errors.
        await storage.WriteAsync(0, new byte[10]);

        var consecutiveErrorsField = typeof(Storage).GetField("_consecutiveErrors", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        int errors = (int)consecutiveErrorsField!.GetValue(storage)!;
        Assert.Equal(1, errors);
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

        byte[] file2Data = File.ReadAllBytes(Path.Combine(_tempDir, "folder", "file2.txt"));
        Assert.All(file2Data.Take(100), b => Assert.Equal(0, b));
    }
}





