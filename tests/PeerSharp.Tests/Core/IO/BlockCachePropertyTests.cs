using CsCheck;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

/// <summary>
/// The one thing a cache in front of storage must never do: answer with bytes that are no longer
/// there.
/// </summary>
/// <remarks>
/// <para>
/// The model is deliberately trivial - a byte array holding what the torrent's data should be after
/// every write so far. Whatever the cache does internally, a read has to agree with it. That covers
/// the failures worth catching in one statement: a stale block served after an overwrite, a block
/// admitted to the cache before it was complete, and an eviction that loses bytes storage never
/// received.
/// </para>
/// <para>
/// Writes are generated at arbitrary offsets and lengths rather than block-aligned ones, because
/// alignment is exactly the assumption the cache makes and therefore exactly the assumption worth
/// testing. The last block of any torrent is partial, and repair and end-game both rewrite bytes
/// that have already been read once.
/// </para>
/// </remarks>
public class BlockCachePropertyTests
{
    private const int BlockSize = 16 * 1024;
    private const int TotalSize = 4 * BlockSize;

    [Fact]
    public async Task AReadAlwaysAgreesWithEveryWriteBeforeIt()
    {
        await Operations().SampleAsync(async script =>
        {
            var storage = new ArrayStorage(TotalSize);
            // A capacity of two blocks against four blocks of data, so eviction happens constantly
            // rather than only in the rare long run.
            using var cache = new BlockCache(2 * BlockSize, readAheadBlocks: 2, readAheadEnabled: true, TotalSize);
            cache.Initialize(storage);

            byte[] model = new byte[TotalSize];

            foreach (var operation in script)
            {
                if (operation.IsWrite)
                {
                    byte[] data = new byte[operation.Length];
                    Array.Fill(data, operation.Fill);

                    data.CopyTo(model.AsSpan(operation.Offset));
                    await cache.WriteAsync(operation.Offset, data, TestContext.Current.CancellationToken);
                }
                else
                {
                    byte[] buffer = new byte[operation.Length];
                    await cache.ReadAsync(operation.Offset, buffer, TestContext.Current.CancellationToken);

                    Assert.True(
                        buffer.AsSpan().SequenceEqual(model.AsSpan(operation.Offset, operation.Length)),
                        $"read of {operation.Length} bytes at {operation.Offset} disagreed with the writes before it");
                }
            }
        }, iter: 2_000);
    }

    [Fact]
    public async Task ACacheMissFinishingAfterAnOverwriteCannotReinsertStaleBytes()
    {
        byte[] before = Enumerable.Repeat((byte)0x11, BlockSize).ToArray();
        byte[] after = Enumerable.Repeat((byte)0x22, BlockSize).ToArray();
        var storage = new PausedReadStorage(before);
        using var cache = new BlockCache(BlockSize, readAheadBlocks: 0, readAheadEnabled: false, BlockSize);
        cache.Initialize(storage);

        byte[] overlappingRead = new byte[BlockSize];
        Task<bool> read = cache.ReadAsync(0, overlappingRead, TestContext.Current.CancellationToken);
        await storage.ReadCaptured;

        await cache.WriteAsync(0, after, TestContext.Current.CancellationToken);
        storage.ReleaseRead();
        await read;

        // The overlapping read is allowed to have observed the old value. A later read is not: the
        // completed write must remain authoritative even though the older cache miss finished last.
        byte[] subsequentRead = new byte[BlockSize];
        await cache.ReadAsync(0, subsequentRead, TestContext.Current.CancellationToken);

        Assert.Equal(after, subsequentRead);
    }

    /// <summary>
    /// Scripts of reads and writes over a four-block torrent.
    /// </summary>
    /// <remarks>
    /// Offsets and lengths are drawn from block boundaries as often as from arbitrary positions:
    /// the aligned, exactly-block-sized case is the only one the cache serves from memory, so a
    /// script that never produces it would exercise nothing but the bypass path.
    /// </remarks>
    private static Gen<Operation[]> Operations()
    {
        var offset = Gen.OneOf(
            Gen.Int[0, 3].Select(block => block * BlockSize),
            Gen.Int[0, TotalSize - 1]);

        return Gen.Select(Gen.Bool, offset, Gen.OneOfConst(1, 100, BlockSize - 1, BlockSize, BlockSize + 1), Gen.Byte)
            .Select(t => new Operation(t.Item1, t.Item2, Math.Min(t.Item3, TotalSize - t.Item2), t.Item4))
            .Array[1, 24];
    }

    private readonly record struct Operation(bool IsWrite, int Offset, int Length, byte Fill);

    /// <summary>
    /// Storage that is simply an array, so any disagreement is the cache's doing.
    /// </summary>
    private sealed class ArrayStorage(int size) : IStorage
    {
        private readonly byte[] _data = new byte[size];

        public Task DeleteAllAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> FlushAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            int length = (int)Math.Min(buffer.Length, _data.Length - offset);
            if (length > 0)
            {
                _data.AsMemory((int)offset, length).CopyTo(buffer);
            }

            return ValueTask.CompletedTask;
        }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
        {
            byte[] result = new byte[length];
            int available = (int)Math.Min(length, _data.Length - offset);
            if (available > 0)
            {
                _data.AsSpan((int)offset, available).CopyTo(result);
            }

            return Task.FromResult(result);
        }

        public Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            int length = (int)Math.Min(data.Length, _data.Length - offset);
            if (length > 0)
            {
                data[..length].CopyTo(_data.AsMemory((int)offset, length));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PausedReadStorage(byte[] initial) : IStorage
    {
        private readonly TaskCompletionSource _readCaptured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] _data = initial;

        public Task ReadCaptured => _readCaptured.Task;

        public void ReleaseRead() => _releaseRead.TrySetResult();

        public Task DeleteAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> FlushAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            byte[] snapshot = _data;
            _readCaptured.TrySetResult();
            await _releaseRead.Task.WaitAsync(ct);
            snapshot.AsMemory((int)offset, buffer.Length).CopyTo(buffer);
        }

        public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
        {
            byte[] result = new byte[length];
            await ReadAsync(offset, result, ct);
            return result;
        }

        public Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            byte[] updated = (byte[])_data.Clone();
            data.CopyTo(updated.AsMemory((int)offset));
            _data = updated;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
