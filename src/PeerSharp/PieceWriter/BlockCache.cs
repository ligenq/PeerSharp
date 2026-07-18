using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace PeerSharp.PieceWriter;

internal class BlockCache : IBlockCache
{
    private const int BlockSize = 16 * 1024;
    private const int MaxReadAheadBlocks = 64;

    private static readonly ArrayPool<byte> CachePool =
        ArrayPool<byte>.Create(maxArrayLength: BlockSize, maxArraysPerBucket: 64);

    // Key: Global Torrent Offset (must be aligned to BlockSize)
    // Value: Cached Block
    private readonly Dictionary<long, CachedBlock> _blocks = [];

    private readonly int _capacityBytes;
    private readonly int _readAheadBlocks;
    private readonly bool _readAheadEnabled;
    private readonly long _totalSize;
    private readonly Lock _lock = new();
    private readonly LinkedList<long> _lruList = new();
    private int _currentBytes;
    private AtomicDisposal _disposal = new();

    private IStorage? _storage;
    private readonly ConcurrentDictionary<long, byte> _readAheadInFlight = new();
    private readonly SemaphoreSlim _readAheadSemaphore = new(2, 2);
    // 16KB

    public BlockCache(int capacityBytes, int readAheadBlocks, bool readAheadEnabled, long totalSize)
    {
        _capacityBytes = capacityBytes;
        _readAheadBlocks = Math.Clamp(readAheadBlocks, 0, MaxReadAheadBlocks);
        _readAheadEnabled = readAheadEnabled;
        _totalSize = totalSize;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void Initialize(IStorage storage)
    {
        _storage = storage;
    }

    public async Task<bool> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_storage == null)
        {
            throw new InvalidOperationException("BlockCache not initialized");
        }

        // We only cache strictly aligned 16KB blocks to keep logic simple and fast.
        // If request is not 16KB or not aligned, bypass cache (or handle partials).
        // Most peer requests are 16KB aligned.

        if (buffer.Length == BlockSize && offset % BlockSize == 0)
        {
            if (TryReadFromCache(offset, buffer.Span))
            {
                return true;
            }

            // Cache miss - read from storage
            await _storage.ReadAsync(offset, buffer, ct).ConfigureAwait(false);

            // Add to cache
            AddToCache(offset, buffer.Span);

            TriggerReadAhead(offset + BlockSize, ct);
            return true;
        }

        // Complex read (multi-block or unaligned)
        // For now, bypass cache for simplicity or implement scatter/gather?
        // Let's implement scatter/gather if it spans multiple blocks.

        await _storage.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
        return true;
    }

    public async Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_storage == null)
        {
            throw new InvalidOperationException("BlockCache not initialized");
        }

        // Write-Through: Write to storage first
        await _storage.WriteAsync(offset, data, ct).ConfigureAwait(false);

        // Populate cache
        // Data might be large (Piece Size e.g. 4MB). Slice it into blocks.
        int len = data.Length;
        int pos = 0;
        long currentOffset = offset;

        while (pos < len)
        {
            int chunkSize = Math.Min(BlockSize, len - pos);

            // Only cache full 16KB blocks to maintain alignment invariant
            if (chunkSize == BlockSize && currentOffset % BlockSize == 0)
            {
                AddToCache(currentOffset, data.Slice(pos, chunkSize).Span);
            }
            // If we strictly enforce 16KB, we skip partials at end of file/piece.
            // This is acceptable for a block cache.

            pos += chunkSize;
            currentOffset += chunkSize;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            _readAheadSemaphore.Dispose();
            lock (_lock)
            {
                foreach (var block in _blocks.Values)
                {
                    CachePool.Return(block.Data);
                }
                _blocks.Clear();
                _lruList.Clear();
                _currentBytes = 0;
            }
        }
    }

    private void TriggerReadAhead(long startOffset, CancellationToken ct)
    {
        if (!_readAheadEnabled || _readAheadBlocks == 0)
        {
            return;
        }

        if (_totalSize > 0 && startOffset + BlockSize > _totalSize)
        {
            return;
        }

        _ = PrefetchAsync(startOffset, ct).ContinueWith(
            t => Debug.WriteLine(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task PrefetchAsync(long startOffset, CancellationToken ct)
    {
        for (int i = 0; i < _readAheadBlocks; i++)
        {
            long offset = startOffset + (i * BlockSize);
            if (_totalSize > 0 && offset + BlockSize > _totalSize)
            {
                break;
            }

            if (IsCached(offset))
            {
                continue;
            }

            if (!_readAheadInFlight.TryAdd(offset, 0))
            {
                continue;
            }

            try
            {
                await _readAheadSemaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_storage == null)
                    {
                        return;
                    }

                    byte[] buffer = CachePool.Rent(BlockSize);
                    try
                    {
                        await _storage.ReadAsync(offset, buffer.AsMemory(0, BlockSize), ct).ConfigureAwait(false);
                        AddToCache(offset, buffer.AsSpan(0, BlockSize));
                    }
                    finally
                    {
                        CachePool.Return(buffer);
                    }
                }
                finally
                {
                    _readAheadSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _readAheadInFlight.TryRemove(offset, out _);
            }
        }
    }

    private void AddToCache(long offset, ReadOnlySpan<byte> data)
    {
        byte[] buffer = CachePool.Rent(BlockSize);
        data.CopyTo(buffer);

        lock (_lock)
        {
            if (_blocks.TryGetValue(offset, out var existing))
            {
                // Refresh the cached contents: the same offset can be written again with
                // different data (e.g. a piece that failed verification and was re-downloaded).
                // Keeping the old block here would serve stale/corrupt data on later reads.
                data.CopyTo(existing.Data);
                _lruList.Remove(existing.Node);
                _lruList.AddLast(existing.Node);
                CachePool.Return(buffer);
                return;
            }

            // Evict if needed
            while (_currentBytes + BlockSize > _capacityBytes && _lruList.Count > 0)
            {
                EvictLRU();
            }

            if (_currentBytes + BlockSize > _capacityBytes)
            {
                // Still no room (capacity too small?)
                CachePool.Return(buffer);
                return;
            }

            var node = _lruList.AddLast(offset);
            _blocks.Add(offset, new CachedBlock(buffer, node));
            _currentBytes += BlockSize;
        }
    }

    private void EvictLRU()
    {
        var node = _lruList.First;
        if (node != null)
        {
            long offset = node.Value;
            _lruList.RemoveFirst();

            if (_blocks.Remove(offset, out var evicted))
            {
                _currentBytes -= BlockSize;
                CachePool.Return(evicted.Data);
            }
        }
    }

    private bool IsCached(long offset)
    {
        lock (_lock)
        {
            return _blocks.ContainsKey(offset);
        }
    }

    private bool TryReadFromCache(long offset, Span<byte> destination)
    {
        lock (_lock)
        {
            if (_blocks.TryGetValue(offset, out var block))
            {
                // Move to MRU
                _lruList.Remove(block.Node);
                _lruList.AddLast(block.Node);

                block.Data.CopyTo(destination);
                return true;
            }
        }
        return false;
    }

    private sealed class CachedBlock
    {
        public CachedBlock(byte[] data, LinkedListNode<long> node)
        {
            Data = data;
            Node = node;
        }

        public byte[] Data { get; }
        public LinkedListNode<long> Node { get; }
    }
}
