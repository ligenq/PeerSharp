using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class StorageInterfaceTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly TorrentFileMetadata _metadata;
    private readonly FileHandleCache _handleCache;
    private readonly IStorage _storage; // Explicitly usage as interface

    public StorageInterfaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MtTorrentTests_IStorage", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _metadata = new TorrentFileMetadata();
        _metadata.Info.Name = "test_torrent";
        _metadata.Info.PieceSize = 16384;
        _metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file1.txt", Size = 1000, Offset = 0 });
        _metadata.Info.FullSize = 1000;

        _handleCache = new FileHandleCache();
        var validator = new PathValidator(_tempDir);
        // Cast to ensure it implements the interface
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
    public async Task DeleteAll_RemovesFilesButNotRoot()
    {
        await _storage.InitAsync();
        var filePath = Path.Combine(_tempDir, "file1.txt");
        Assert.True(Directory.Exists(_tempDir));
        Assert.True(File.Exists(filePath));

        await _storage.DeleteAllAsync();

        Assert.False(File.Exists(filePath), $"File {filePath} should have been deleted");
        Assert.True(Directory.Exists(_tempDir)); // Root path is preserved for safety
    }

    [Fact(Timeout = 30000)]
    public async Task InterfaceMethods_WorkAsExpected()
    {
        await _storage.InitAsync();

        var data = new byte[100];
        Array.Fill(data, (byte)0xAA);

        // Test ValueTasks
        await _storage.WriteAsync(0, data, CancellationToken.None);

        var buffer = new byte[100];
        await _storage.ReadAsync(0, buffer.AsMemory(), CancellationToken.None);

        Assert.Equal(data, buffer);

        // Test Task<byte[]>
        var bytes = await _storage.ReadAsync(0, 100, CancellationToken.None);
        Assert.Equal(data, bytes);
    }
}





