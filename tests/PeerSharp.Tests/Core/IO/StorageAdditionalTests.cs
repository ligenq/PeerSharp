using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

/// <summary>
/// Covers Storage paths not reached by existing tests:
/// StorageException(message, inner) constructor, DeleteAll() sync,
/// InitAsync with pre-existing files, sparse file path, InitAsync cancellation.
/// </summary>
public class StorageAdditionalTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly FileHandleCache _handleCache;

    public StorageAdditionalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "StorageAdditional", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _handleCache = new FileHandleCache();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _handleCache.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
        await Task.CompletedTask;
    }

    private Storage CreateStorage(TorrentFileMetadata? meta = null, bool sparse = false)
    {
        meta ??= MakeMetadata();
        var validator = new PathValidator(_tempDir);
        return new Storage(meta, _tempDir, validator, _handleCache, enableSparseFiles: sparse);
    }

    private static TorrentFileMetadata MakeMetadata()
    {
        var meta = new TorrentFileMetadata();
        meta.Info.Name = "test";
        meta.Info.PieceSize = 16384;
        meta.Info.Files.Add(new Internals.TorrentFileEntry { Path = "a.txt", Size = 512, Offset = 0 });
        meta.Info.Files.Add(new Internals.TorrentFileEntry { Path = "sub/b.txt", Size = 256, Offset = 512 });
        meta.Info.FullSize = 768;
        return meta;
    }

    // ── StorageException constructors ─────────────────────────────────────────

    [Fact]
    public void StorageException_MessageAndInner_StoresBoth()
    {
        var inner = new IOException("disk");
        var ex = new StorageException("failed", inner);

        Assert.Equal("failed", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.False(ex.IsRecoverable); // default
    }

    // ── DeleteAll sync ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAll_Sync_RemovesFiles()
    {
        var storage = CreateStorage();
        await storage.InitAsync();

        string fileA = Path.Combine(_tempDir, "a.txt");
        Assert.True(File.Exists(fileA));

        storage.DeleteAll();

        Assert.False(File.Exists(fileA));
        await storage.DisposeAsync();
    }

    [Fact]
    public async Task DeleteAll_Sync_AlsoRemovesEmptySubdirectory()
    {
        var storage = CreateStorage();
        await storage.InitAsync();

        string subDir = Path.Combine(_tempDir, "sub");
        Assert.True(Directory.Exists(subDir));

        storage.DeleteAll();

        Assert.False(Directory.Exists(subDir), "Empty subdirectory should be removed");
        await storage.DisposeAsync();
    }

    [Fact]
    public void DeleteAll_BeforeInit_DoesNotThrow()
    {
        var storage = CreateStorage();
        storage.DeleteAll(); // _files is null/empty — should be a no-op
    }

    // ── InitAsync with pre-existing file ─────────────────────────────────────

    [Fact]
    public async Task InitAsync_PreExistingFileExactSize_DoesNotTruncate()
    {
        var storage = CreateStorage();
        await storage.InitAsync();

        // Write some data
        byte[] data = new byte[512];
        data[0] = 0xAB;
        await storage.WriteAsync(0, data);

        // Re-init with a fresh storage on the same path (simulates resume)
        await storage.DisposeAsync();
        var storage2 = CreateStorage();
        await storage2.InitAsync();

        // Data should be preserved
        var readBack = await storage2.ReadAsync(0, 512);
        Assert.Equal(0xAB, readBack[0]);
        await storage2.DisposeAsync();
    }

    // ── Sparse file path ──────────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_SparseEnabled_CreatesFiles()
    {
        var storage = CreateStorage(sparse: true);
        try
        {
            await storage.InitAsync();

            Assert.True(File.Exists(Path.Combine(_tempDir, "a.txt")));
            Assert.True(File.Exists(Path.Combine(_tempDir, "sub", "b.txt")));
        }
        finally
        {
            await storage.DisposeAsync();
        }
    }

    // ── InitAsync cancellation ────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var storage = CreateStorage();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.InitAsync(ct: cts.Token));

        await storage.DisposeAsync();
    }

    // ── DeleteAllAsync with cancellation ─────────────────────────────────────

    [Fact]
    public async Task DeleteAllAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var storage = CreateStorage();
        await storage.InitAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.DeleteAllAsync(cts.Token));

        await storage.DisposeAsync();
    }

    // ── UpdateFileSelection ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFileSelection_DeselectedFile_SkipsWrites()
    {
        var storage = CreateStorage();
        await storage.InitAsync();

        // Deselect first file
        var selection = new List<FileSelection>
        {
            new() { Selected = false, Priority = Priority.DoNotDownload },
            new() { Selected = true,  Priority = Priority.Normal }
        };
        await storage.UpdateFileSelectionAsync(selection);

        // Writing to offset 0 (file 1's region) should silently skip
        await storage.WriteAsync(0, new byte[512]); // no exception

        await storage.DisposeAsync();
    }
}
