using PeerSharp.Exceptions;
using PeerSharp.Internals;
using PeerSharp.Internals.Utilities;
using PeerSharp.PieceWriter;
using TorrentFileEntry = PeerSharp.Internals.TorrentFileEntry;

namespace PeerSharp.Tests.Core.IO;

public class StorageIntegrityRegressionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PeerSharpStorageIntegrity", Guid.NewGuid().ToString("N"));
    private readonly FileHandleCache _handles = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _handles.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(3000, 1)]
    [InlineData(2999, 2)]
    [InlineData(long.MaxValue, 1)]
    public async Task InvalidRangesFailBeforeAnyReadOrWrite(long offset, int length)
    {
        await using var storage = CreateStorage();
        await storage.InitAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.ReadAsync(offset, length));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.WriteAsync(offset, new byte[length]).AsTask());
        Assert.Equal(new byte[3000], await storage.ReadAsync(0, 3000));
    }

    [Fact]
    public async Task StorageCannotReportSuccessBeforeInitialization()
    {
        await using var storage = CreateStorage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ReadAsync(0, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.WriteAsync(0, new byte[1]).AsTask());
    }

    [Fact]
    public async Task TruncatedFilesFailRatherThanReturningTheUnreadTailOfABuffer()
    {
        await using var storage = CreateStorage();
        await storage.InitAsync();
        using (var file = new FileStream(Path.Combine(_root, "selected.bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            file.SetLength(10);
        }
        byte[] buffer = Enumerable.Repeat((byte)0xA5, 100).ToArray();
        var error = await Assert.ThrowsAsync<StorageException>(() => storage.ReadAsync(0, buffer).AsTask());
        Assert.IsType<EndOfStreamException>(error.InnerException);
    }

    [Fact]
    public async Task SharedPieceBytesInDeselectedFilesSurviveAStorageRestart()
    {
        var selection = new List<FileSelection>
        {
            new() { Selected = true, Priority = Priority.Normal },
            new() { Selected = false, Priority = Priority.DoNotDownload }
        };
        byte[] data = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
        await using (var storage = CreateStorage())
        {
            await storage.InitAsync(selection);
            await storage.WriteAsync(0, data);
            Assert.Equal(data, await storage.ReadAsync(0, data.Length));
            Assert.True(await storage.FlushAsync());
        }
        await using (var storage = CreateStorage())
        {
            await storage.InitAsync(selection);
            Assert.Equal(data, await storage.ReadAsync(0, data.Length));
        }
    }

    [Fact]
    public async Task DeselectingAnAlreadyDownloadedFilePreservesReadableVerifiedBytes()
    {
        await using var storage = CreateStorage();
        await storage.InitAsync();
        byte[] data = Enumerable.Repeat((byte)7, 3000).ToArray();
        await storage.WriteAsync(0, data);
        await storage.UpdateFileSelectionAsync([
            new() { Selected = true, Priority = Priority.Normal },
            new() { Selected = false, Priority = Priority.DoNotDownload }
        ]);
        Assert.Equal(data, await storage.ReadAsync(0, data.Length));
    }

    [Fact]
    public void MapperRejectsNegativeLengthsSizesAndOverflowingGeometry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMapper([-1]));
        Assert.Throws<OverflowException>(() => new FileMapper([long.MaxValue, 1]));
        var mapper = new FileMapper([10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.MapRange(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.MapRange(9, 2));
        Assert.Empty(mapper.MapRange(10, 0).ToList());
        Assert.Empty(new FileMapper([]).MapRange(0, 0).ToList());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeClaimsForMissingOrTruncatedFilesAreClearedBeforeAllocation(bool truncated)
    {
        Directory.CreateDirectory(_root);
        if (truncated)
        {
            await File.WriteAllBytesAsync(Path.Combine(_root, "selected.bin"), new byte[1000], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(_root, "skipped.bin"), new byte[1000], TestContext.Current.CancellationToken);
        }
        var pieces = new PiecesProgress(1);
        pieces.AddPiece(0);
        await using var storage = CreateStorage(pieces);
        await storage.InitAsync();
        Assert.False(pieces.HasPiece(0));
    }

    [Fact]
    public async Task ResumeKeepsClaimsWhoseFileRangesExist()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Combine(_root, "selected.bin"), new byte[1000], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "skipped.bin"), new byte[2000], TestContext.Current.CancellationToken);
        var pieces = new PiecesProgress(1);
        pieces.AddPiece(0);
        await using var storage = CreateStorage(pieces);
        await storage.InitAsync();
        Assert.True(pieces.HasPiece(0));
    }

    [Fact]
    public async Task RecheckingBypassesCachedDataAndInvalidatesEarlierUploads()
    {
        byte[] original = Enumerable.Repeat((byte)7, ProtocolConstants.BlockSize).ToArray();
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = original.Length;
        metadata.Info.Files.Add(new TorrentFileEntry { Path = "file.bin", Size = original.Length });
        metadata.Info.Pieces.Add(System.Security.Cryptography.SHA1.HashData(original));
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata, _root);
        await torrent.FilesInternal.InitializeAsync([], TestContext.Current.CancellationToken);
        await torrent.FilesInternal.WriteAsync(0, original, TestContext.Current.CancellationToken);
        Assert.Equal(1, await torrent.ForceRecheckAsync(cancellationToken: TestContext.Current.CancellationToken));
        // Prime the upload cache before changing the underlying bytes.
        Assert.Equal(original, await torrent.FilesInternal.ReadAsync(0, original.Length, TestContext.Current.CancellationToken));
        byte[] corrupted = Enumerable.Repeat((byte)9, original.Length).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_root, "file.bin"), corrupted, TestContext.Current.CancellationToken);
        Assert.Equal(0, await torrent.ForceRecheckAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(torrent.Pieces.HasPiece(0));
        Assert.Equal(corrupted, await torrent.FilesInternal.ReadAsync(0, original.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedPartialWritesInvalidatePreviouslyCachedBytes()
    {
        const int size = ProtocolConstants.BlockSize;
        var storage = new ControlledStorage(size);
        using var cache = new BlockCache(2 * size, 0, false, size);
        cache.Initialize(storage);
        byte[] buffer = new byte[size];
        await cache.ReadAsync(0, buffer, TestContext.Current.CancellationToken);
        storage.FailWrites = true;
        byte[] replacement = Enumerable.Repeat((byte)0x42, size).ToArray();
        await Assert.ThrowsAsync<IOException>(() => cache.WriteAsync(0, replacement, TestContext.Current.CancellationToken));
        await cache.ReadAsync(0, buffer, TestContext.Current.CancellationToken);
        Assert.Equal(0x42, buffer[0]);
        Assert.Equal(0, buffer[1]);
    }

    [Theory(Timeout = 10000)]
    [InlineData(-1L, 1)]
    [InlineData(16384L, 1)]
    [InlineData(long.MaxValue - 1, 1)]
    [InlineData(long.MaxValue, 2)]
    public async Task CacheRejectsInvalidRangesBeforeWalkingBlocks(long offset, int length)
    {
        var storage = new ControlledStorage(ProtocolConstants.BlockSize);
        using var cache = new BlockCache(ProtocolConstants.BlockSize, 0, false, ProtocolConstants.BlockSize);
        cache.Initialize(storage);
        byte[] data = new byte[length];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.WriteAsync(offset, data, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.ReadAsync(offset, data, TestContext.Current.CancellationToken));
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact(Timeout = 10000)]
    public async Task ConcurrentWritersKeepCacheAndDiskInTheSameOrder()
    {
        const int size = ProtocolConstants.BlockSize;
        var storage = new ControlledStorage(size) { HoldFirstWrite = true };
        using var cache = new BlockCache(2 * size, 0, false, size);
        cache.Initialize(storage);
        byte[] first = Enumerable.Repeat((byte)1, size).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, size).ToArray();
        Task firstWrite = cache.WriteAsync(0, first, TestContext.Current.CancellationToken);
        await storage.FirstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task secondWrite = cache.WriteAsync(0, second, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, storage.WriteCount);
        }
        finally
        {
            storage.ReleaseFirstWrite.TrySetResult();
            await Task.WhenAll(firstWrite, secondWrite);
        }
        byte[] buffer = new byte[size];
        await cache.ReadAsync(0, buffer, TestContext.Current.CancellationToken);
        Assert.Equal(second, buffer);
        Assert.Equal(second, storage.Bytes);
    }

    private Storage CreateStorage(PiecesProgress? pieces = null)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = 3000;
        metadata.Info.Files.Add(new TorrentFileEntry { Path = "selected.bin", Size = 1000, Offset = 0 });
        metadata.Info.Files.Add(new TorrentFileEntry { Path = "skipped.bin", Size = 2000, Offset = 1000 });
        return new Storage(metadata, _root, new PathValidator(_root), _handles, false)
        {
            GetCompletedPieces = pieces == null ? null : () => pieces
        };
    }

    private sealed class ControlledStorage(int size) : IStorage
    {
        public byte[] Bytes { get; } = new byte[size];
        public bool FailWrites { get; set; }
        public bool HoldFirstWrite { get; set; }
        public int WriteCount { get; private set; }
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            WriteCount++;
            if (FailWrites)
            {
                Bytes[(int)offset] = data.Span[0];
                throw new IOException("second file write failed");
            }
            data.CopyTo(Bytes.AsMemory((int)offset));
            if (HoldFirstWrite && WriteCount == 1)
            {
                FirstWriteStarted.TrySetResult();
                await ReleaseFirstWrite.Task.WaitAsync(ct);
            }
        }

        public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
        {
            Bytes.AsMemory((int)offset, buffer.Length).CopyTo(buffer);
            return ValueTask.CompletedTask;
        }
        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
            => Task.FromResult(Bytes.AsSpan((int)offset, length).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> FlushAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task MoveAsync(string newRootPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameFileAsync(int fileIndex, string newRelativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
