using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class FileHandleCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileHandleCache _cache;

    public FileHandleCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MtTorrentTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _cache = new FileHandleCache(maxOpenFiles: 32);
    }

    public void Dispose()
    {
        _cache.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact(Timeout = 30000)]
    public async Task GetHandleAsync_OpensNewFile()
    {
        string path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "hello");

        using var lease = await _cache.GetHandleAsync(path, false);

        Assert.NotNull(lease);
        Assert.False(lease.Handle.IsInvalid);
        Assert.False(lease.Handle.IsClosed);
    }

    [Fact(Timeout = 30000)]
    public async Task GetHandleAsync_ReusesHandle()
    {
        string path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "hello");

        using var lease1 = await _cache.GetHandleAsync(path, false);
        using var lease2 = await _cache.GetHandleAsync(path, false);

        Assert.Same(lease1.Handle, lease2.Handle);
    }

    [Fact(Timeout = 30000)]
    public async Task GetHandleAsync_UpgradesToWritable_LeavesOldOpenIfLeased()
    {
        string path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "hello");

        using var lease1 = await _cache.GetHandleAsync(path, false);
        using var lease2 = await _cache.GetHandleAsync(path, true);

        Assert.NotSame(lease1.Handle, lease2.Handle);
        // CRITICAL FIX: The old handle must NOT be closed while lease1 is active
        Assert.False(lease1.Handle.IsClosed);
        Assert.False(lease2.Handle.IsClosed);
    }

    [Fact(Timeout = 30000)]
    public async Task Eviction_ClosesOldestHandles_OnlyIfUnused()
    {
        var smallCache = new FileHandleCache(maxOpenFiles: 32);

        var leases = new List<IFileHandleLease>();
        for (int i = 0; i < 35; i++)
        {
            string path = Path.Combine(_tempDir, $"test{i}.txt");
            File.WriteAllText(path, "data");
            leases.Add(await smallCache.GetHandleAsync(path, false));
        }

        // All should be open because they are leased (Limit Exceeded behavior)
        Assert.False(leases[0].Handle.IsClosed);
        Assert.False(leases[34].Handle.IsClosed);

        // Now release the first one (LRU)
        leases[0].Dispose();

        // Trigger eviction attempt by adding another file
        string pathNew = Path.Combine(_tempDir, "testNew.txt");
        File.WriteAllText(pathNew, "data");
        using var leaseNew = await smallCache.GetHandleAsync(pathNew, false);

        // Now 0 should be closed (it was LRU and unused)
        Assert.True(leases[0].Handle.IsClosed);

        smallCache.Dispose();
        foreach (var l in leases) l.Dispose();
    }

    [Fact(Timeout = 30000)]
    public async Task CloseTorrentHandles_CleansUpPrefix()
    {
        string torrentDir = Path.Combine(_tempDir, "torrent1");
        Directory.CreateDirectory(torrentDir);
        string file1 = Path.Combine(torrentDir, "file1.txt");
        string file2 = Path.Combine(torrentDir, "file2.txt");
        string otherFile = Path.Combine(_tempDir, "other.txt");

        File.WriteAllText(file1, "data");
        File.WriteAllText(file2, "data");
        File.WriteAllText(otherFile, "data");

        // We use using blocks to ensure they are releasable
        var lease1 = await _cache.GetHandleAsync(file1, false);
        var lease2 = await _cache.GetHandleAsync(file2, false);
        var leaseOther = await _cache.GetHandleAsync(otherFile, false);

        // Dispose them so they are eligible for full closure
        lease1.Dispose();
        lease2.Dispose();
        leaseOther.Dispose();

        _cache.CloseTorrentHandles(torrentDir);

        Assert.True(lease1.Handle.IsClosed);
        Assert.True(lease2.Handle.IsClosed);
        Assert.False(leaseOther.Handle.IsClosed);
    }
}



