using BenchmarkDotNet.Attributes;
using PeerSharp.Core;
using PeerSharp.PieceWriter;

namespace PeerSharp.Benchmarks;

/// <summary>
/// The cache layer above <see cref="Storage"/>. Its hit path runs at least as often as the
/// storage path by definition, so its per-call overhead compounds the same way.
///
/// The backing store here is an in-memory fake rather than real files: <see cref="Storage"/>
/// already has its own suite, and mixing disk latency in would drown the thing being measured.
/// What is left is exactly the cache's own cost - lookup, LRU bookkeeping, and eviction.
/// </summary>
[MemoryDiagnoser]
public class BlockCacheBenchmarks
{
    private const int BlockSize = 16 * 1024;
    private const long TotalSize = 64L * 1024 * 1024;

    private BlockCache _cache = null!;
    private byte[] _block = null!;
    private byte[] _readBuffer = null!;
    private long _missOffset;

    /// <summary>
    /// Cache capacity. 1 MiB holds 64 blocks, so the miss/eviction benchmarks thrash it;
    /// 32 MiB comfortably holds the working set so hits stay hits.
    /// </summary>
    [Params(1 * 1024 * 1024, 32 * 1024 * 1024)]
    public int CapacityBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _block = new byte[BlockSize];
        Random.Shared.NextBytes(_block);
        _readBuffer = new byte[BlockSize];

        _cache = new BlockCache(CapacityBytes, readAheadBlocks: 0, readAheadEnabled: false, TotalSize);
        _cache.Initialize(new InMemoryStorage(TotalSize));

        // Warm offset 0 so the hit benchmark actually hits.
        _cache.ReadAsync(0, _readBuffer).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => _cache.Dispose();

    [Benchmark(Description = "Read, cache hit")]
    public Task<bool> ReadHit() => _cache.ReadAsync(0, _readBuffer);

    /// <summary>
    /// Walks forward through the whole address space so every call is a fresh block. Once the
    /// walk exceeds capacity this is also the eviction path.
    /// </summary>
    [Benchmark(Description = "Read, cache miss (sequential walk)")]
    public Task<bool> ReadMiss()
    {
        _missOffset += BlockSize;
        if (_missOffset >= TotalSize)
        {
            _missOffset = 0;
        }
        return _cache.ReadAsync(_missOffset, _readBuffer);
    }

    [Benchmark(Description = "Write through cache")]
    public Task Write() => _cache.WriteAsync(0, _block);

    /// <summary>
    /// Backing store that serves zeroes and discards writes. Deliberately allocation-free and
    /// synchronous so the cache's own cost is what shows up in the results.
    /// </summary>
    private sealed class InMemoryStorage(long totalSize) : IStorage
    {
        public Task MoveAsync(string newRootPath, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameFileAsync(int fileIndex, string newRelativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAllAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> FlushAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            if (offset + buffer.Length > totalSize)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            buffer.Span.Clear();
            return ValueTask.CompletedTask;
        }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
            => Task.FromResult(new byte[length]);

        public Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
