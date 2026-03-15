using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class BlockCacheTests
{
    private class MockStorage : IStorage
    {
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        private readonly byte[] _data;

        public MockStorage(int size)
        {
            _data = new byte[size];
        }

        public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadCount++;
            if (offset < _data.Length)
            {
                int len = Math.Min(buffer.Length, _data.Length - (int)offset);
                _data.AsMemory((int)offset, len).CopyTo(buffer);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            WriteCount++;
            if (offset < _data.Length)
            {
                int len = Math.Min(data.Length, _data.Length - (int)offset);
                data.Slice(0, len).CopyTo(_data.AsMemory((int)offset, len));
            }
            return ValueTask.CompletedTask;
        }

        public void Init(IReadOnlyList<FileSelection>? selection = null) { }
        public Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
        public Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
        {
            return Task.FromResult(new byte[length]);
        }

        public void DeleteAll() { }
        public Task DeleteAllAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
        public static void Dispose() { }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_CachesAlignedBlocks()
    {
        var storage = new MockStorage(1024 * 1024); // 1MB
        var cache = new BlockCache(64 * 1024, readAheadBlocks: 0, readAheadEnabled: false, totalSize: 1024 * 1024); // 64KB cache
        cache.Initialize(storage);

        var buffer = new byte[16 * 1024]; // 16KB block
        long offset = 0;

        // First read: miss
        await cache.ReadAsync(offset, buffer);
        Assert.Equal(1, storage.ReadCount);

        // Second read: hit
        await cache.ReadAsync(offset, buffer);
        Assert.Equal(1, storage.ReadCount); // Storage count shouldn't increase
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_UnalignedBypassesCache()
    {
        var storage = new MockStorage(1024 * 1024);
        var cache = new BlockCache(64 * 1024, readAheadBlocks: 0, readAheadEnabled: false, totalSize: 1024 * 1024);
        cache.Initialize(storage);

        var buffer = new byte[100]; // Small read
        long offset = 0;

        await cache.ReadAsync(offset, buffer);
        Assert.Equal(1, storage.ReadCount);

        await cache.ReadAsync(offset, buffer);
        Assert.Equal(2, storage.ReadCount); // Bypass cache, count increases
    }

    [Fact(Timeout = 30000)]
    public async Task WriteAsync_PopulatesCache()
    {
        var storage = new MockStorage(1024 * 1024);
        var cache = new BlockCache(64 * 1024, readAheadBlocks: 0, readAheadEnabled: false, totalSize: 1024 * 1024);
        cache.Initialize(storage);

        var data = new byte[16 * 1024]; // 16KB
        Array.Fill(data, (byte)0xFF);
        long offset = 0;

        // Write through
        await cache.WriteAsync(offset, data);
        Assert.Equal(1, storage.WriteCount);

        // Read back - should hit cache
        var buffer = new byte[16 * 1024];
        await cache.ReadAsync(offset, buffer);

        Assert.Equal(0, storage.ReadCount); // Should be served from cache
        Assert.Equal(data[0], buffer[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task Eviction_Works()
    {
        // Cache size: 32KB (2 blocks)
        var storage = new MockStorage(1024 * 1024);
        var cache = new BlockCache(32 * 1024, readAheadBlocks: 0, readAheadEnabled: false, totalSize: 1024 * 1024);
        cache.Initialize(storage);

        var buffer = new byte[16 * 1024];

        // Fill cache (2 blocks)
        await cache.ReadAsync(0, buffer); // Block 0. Miss. StorageRead=1. Cache: [0]
        await cache.ReadAsync(16 * 1024, buffer); // Block 1. Miss. StorageRead=2. Cache: [0, 1]. MRU=1

        Assert.Equal(2, storage.ReadCount);

        // Access Block 0 (make it MRU)
        await cache.ReadAsync(0, buffer); // Block 0. Hit. StorageRead=2. Cache: [1, 0]. MRU=0.

        // Add Block 2 (should evict LRU = Block 1)
        await cache.ReadAsync(32 * 1024, buffer); // Block 2. Miss. StorageRead=3. Evict 1. Cache: [0, 2]. MRU=2.
        Assert.Equal(3, storage.ReadCount);

        // Read Block 1 (was evicted) -> Miss
        await cache.ReadAsync(16 * 1024, buffer); // Block 1. Miss. StorageRead=4. Evict 0? LRU is 0. Cache: [2, 1]. MRU=1.
        Assert.Equal(4, storage.ReadCount);

        // Read Block 0 (was MRU) -> Hit
        // Wait: In previous step (Read 16k), we added 16k. Cache was {0, 32k}. 
        // 0 was accessed in step 3. 32k in step 4. So 0 is LRU. 0 is evicted.
        // So reading 0 now is a MISS.
        await cache.ReadAsync(0, buffer);
        Assert.Equal(5, storage.ReadCount);
    }
}





