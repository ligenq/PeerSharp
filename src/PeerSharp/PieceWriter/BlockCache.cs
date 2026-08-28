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
    private long _writeGeneration;
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

            long writeGeneration = Volatile.Read(ref _writeGeneration);

            // Cache miss - read from storage
            await _storage.ReadAsync(offset, buffer, ct).ConfigureAwait(false);

            // A write may have completed while storage was serving this miss. The bytes returned to
            // this overlapping read may legitimately be the old ones, but they must not replace the
            // writer's newer cache entry and poison later reads.
            AddToCacheIfUnchanged(offset, buffer.Span, writeGeneration);

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

        // Increment before touching the cache. Any miss or prefetch that started before this write
        // must either observe the increment and decline admission, or admit first and then be
        // refreshed/invalidated by the cache operations below.
        Interlocked.Increment(ref _writeGeneration);

        // Populate the cache, walking the aligned blocks this write touches rather than the write's
        // own chunks. Every touched block ends up in one of two states and never in between: fully
        // rewritten by this data and therefore refreshed, or only partly covered and therefore
        // dropped.
        //
        // Dropping the partial ones is the part that matters. Only whole aligned blocks are cached,
        // so an earlier version simply skipped a partial write here - leaving any block it overlapped
        // holding pre-write bytes, which the next aligned read served in preference to storage. The
        // last block of a torrent is partial, and repair and end-game rewrite blocks that have
        // already been read, so this is reachable and it hands stale data to peers.
        long end = offset + data.Length;
        for (long blockOffset = offset / BlockSize * BlockSize; blockOffset < end; blockOffset += BlockSize)
        {
            if (blockOffset >= offset && blockOffset + BlockSize <= end)
            {
                AddToCache(blockOffset, data.Slice((int)(blockOffset - offset), BlockSize).Span);
            }
            else
            {
                Invalidate(blockOffset);
            }
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

    /// <summary>
    /// Fills the cache ahead of the reader with a single contiguous read.
    /// </summary>
    /// <remarks>
    /// This used to issue one read per block: four separate sixteen-kilobyte reads, awaited one after
    /// another, each taking the read-ahead semaphore. That is not read-ahead so much as the same work
    /// moved earlier, and it added the semaphore traffic on top - reading four blocks individually
    /// costs four disk operations whether they are wanted now or later. Seeding at a gigabyte a second
    /// means tens of thousands of block reads a second, and a profile put most of PeerSharp's CPU in
    /// thread-pool parking rather than in the reads, which is what a queue of tiny asynchronous
    /// operations looks like from the outside.
    ///
    /// <para>
    /// One read covering the whole window costs one operation and one completion however many blocks
    /// it spans. The window stops at the first block already cached or already being fetched, because
    /// a single read has to be contiguous, and stopping is right anyway: someone else is bringing the
    /// rest in.
    /// </para>
    /// </remarks>
    private async Task PrefetchAsync(long startOffset, CancellationToken ct)
    {
        int blocks = 0;
        for (int i = 0; i < _readAheadBlocks; i++)
        {
            long offset = startOffset + (i * BlockSize);
            if (_totalSize > 0 && offset + BlockSize > _totalSize)
            {
                break;
            }

            if (IsCached(offset) || !_readAheadInFlight.TryAdd(offset, 0))
            {
                break;
            }

            blocks++;
        }

        if (blocks == 0)
        {
            return;
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

                int length = blocks * BlockSize;
                byte[] buffer = CachePool.Rent(length);
                try
                {
                    long writeGeneration = Volatile.Read(ref _writeGeneration);
                    await _storage.ReadAsync(startOffset, buffer.AsMemory(0, length), ct).ConfigureAwait(false);

                    for (int i = 0; i < blocks; i++)
                    {
                        AddToCacheIfUnchanged(
                            startOffset + (i * BlockSize),
                            buffer.AsSpan(i * BlockSize, BlockSize),
                            writeGeneration);
                    }
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
            // Shutdown during a read-ahead. There is nothing to cache and nothing to report; the
            // finally below still releases the in-flight markers.
        }
        finally
        {
            for (int i = 0; i < blocks; i++)
            {
                _readAheadInFlight.TryRemove(startOffset + (i * BlockSize), out _);
            }
        }
    }

    private void AddToCache(long offset, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            AddToCacheLocked(offset, data);
        }
    }

    private void AddToCacheIfUnchanged(long offset, ReadOnlySpan<byte> data, long writeGeneration)
    {
        lock (_lock)
        {
            // Make the generation check and admission indivisible with respect to the writer's
            // cache refresh. Otherwise a writer can update the cache after this check but before
            // AddToCache takes the lock, letting the older read overwrite it last.
            if (writeGeneration == Volatile.Read(ref _writeGeneration))
            {
                AddToCacheLocked(offset, data);
            }
        }
    }

    private void AddToCacheLocked(long offset, ReadOnlySpan<byte> data)
    {
        if (_blocks.TryGetValue(offset, out var existing))
        {
            // Refresh the cached contents: the same offset can be written again with
            // different data (e.g. a piece that failed verification and was re-downloaded).
            // Keeping the old block here would serve stale/corrupt data on later reads.
            data.CopyTo(existing.Data);
            _lruList.Remove(existing.Node);
            _lruList.AddLast(existing.Node);
            return;
        }

        byte[] buffer = CachePool.Rent(BlockSize);
        data.CopyTo(buffer);

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

    /// <summary>
    /// Drops one block, if it is held, so the next read for it goes to storage.
    /// </summary>
    private void Invalidate(long offset)
    {
        lock (_lock)
        {
            if (_blocks.Remove(offset, out var block))
            {
                _lruList.Remove(block.Node);
                _currentBytes -= BlockSize;
                CachePool.Return(block.Data);
            }
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
