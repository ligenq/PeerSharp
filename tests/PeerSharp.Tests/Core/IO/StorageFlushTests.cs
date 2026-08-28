using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

/// <summary>
/// The durability barrier under resume data.
///
/// <para>
/// Resume data is written durably - temp file, flush to device, atomic rename - while piece data
/// was handed to the operating system and never flushed. That ordering is backwards: after a power
/// loss the surviving half was the bitfield claiming the pieces, and the half that might not have
/// landed was the pieces themselves. Nothing downstream catches it, because verification runs when
/// a piece arrives and never again.
/// </para>
///
/// <para>
/// A test cannot pull the power out, so these assert the two things that are observable: that a
/// flush reports success for what it wrote, and that it is cheap enough to run on every save - only
/// files actually written since the last flush are touched.
/// </para>
/// </summary>
public class StorageFlushTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly TorrentFileMetadata _metadata;
    private readonly FileHandleCache _handleCache;
    private readonly Storage _storage;

    public StorageFlushTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PeerSharpFlushTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _metadata = new TorrentFileMetadata();
        _metadata.Info.Name = "flush_subject";
        _metadata.Info.PieceSize = 16384;
        _metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "first.bin", Size = 1000, Offset = 0 });
        _metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "second.bin", Size = 1000, Offset = 1000 });
        _metadata.Info.FullSize = 2000;

        _handleCache = new FileHandleCache();
        _storage = new Storage(_metadata, _tempDir, new PathValidator(_tempDir), _handleCache, enableSparseFiles: false);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _storage.DisposeAsync();
        _handleCache.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_AfterAWrite_Succeeds()
    {
        await _storage.InitAsync();
        await _storage.WriteAsync(0, new byte[500]);

        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_WithNothingWritten_Succeeds()
    {
        // Every save calls this, and most calls have nothing to do: a seeding or idle torrent must
        // not pay for a barrier it does not need.
        await _storage.InitAsync();

        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_BeforeInitialization_ReportsFailureRatherThanSuccess()
    {
        // Nothing has been written, but nothing can be promised about it either. Reporting success
        // here would let a caller record a claim it has no basis for.
        Assert.False(await _storage.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_IsIdempotent()
    {
        await _storage.InitAsync();
        await _storage.WriteAsync(0, new byte[500]);

        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));
        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_OnlyTouchesFilesWrittenSinceTheLastOne()
    {
        // The cost of a flush has to scale with what changed, not with how many files the torrent
        // has - a thousand-file torrent saving every minute otherwise fsyncs a thousand handles for
        // one 16 KiB block.
        await _storage.InitAsync();

        // A write spanning only the first file.
        await _storage.WriteAsync(0, new byte[500]);
        Assert.Equal(1, CountDirtyFiles());

        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, CountDirtyFiles());

        // A write spanning the boundary marks both.
        await _storage.WriteAsync(900, new byte[300]);
        Assert.Equal(2, CountDirtyFiles());

        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, CountDirtyFiles());
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_StillCoversAFileDeselectedAfterItWasWritten()
    {
        // Deselecting a file does not retract the completed pieces covering it - those stay in the
        // bitfield and get persisted as present. Clearing the dirty flag without the barrier would
        // therefore leave exactly the claim this mechanism exists to prevent: resume data saying the
        // bytes are on the disk when they were only ever handed to the write cache.
        await _storage.InitAsync();
        await _storage.WriteAsync(0, new byte[500]);
        Assert.Equal(1, CountDirtyFiles());

        // Selection is positional: the first file is dropped, the second kept.
        await _storage.UpdateFileSelectionAsync(
        [
            new FileSelection(Selected: false, Priority.DoNotDownload),
            new FileSelection(Selected: true, Priority.Normal)
        ], TestContext.Current.CancellationToken);

        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, CountDirtyFiles());
    }

    [Fact(Timeout = 30000)]
    public async Task WrittenBytesSurviveAFlushAndReread()
    {
        // The flush must not disturb the data it is pushing out - a barrier that corrupted the file
        // would be worse than no barrier.
        await _storage.InitAsync();

        byte[] payload = new byte[600];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await _storage.WriteAsync(700, payload);
        Assert.True(await _storage.FlushAsync(TestContext.Current.CancellationToken));

        byte[] readBack = await _storage.ReadAsync(700, payload.Length, TestContext.Current.CancellationToken);
        Assert.Equal(payload, readBack);
    }

    // ── When the barrier itself fails ────────────────────────────────────────
    //
    // The whole mechanism rests on this branch. A flush that cannot be completed must report failure,
    // because the caller's response is to skip the resume data that would have claimed those bytes -
    // and it must leave the file marked dirty, or the next save would believe the barrier had already
    // run and write the claim anyway.

    [Fact(Timeout = 30000)]
    public async Task Flush_WhenAFileCannotBeFlushed_ReportsFailure()
    {
        var cache = new FlakyHandleCache();
        var storage = new Storage(_metadata, _tempDir, new PathValidator(_tempDir), cache, enableSparseFiles: false);
        await using var _ = storage;

        await storage.InitAsync();
        await storage.WriteAsync(0, new byte[500]);

        cache.FailNextHandles = true;

        Assert.False(await storage.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_WhenAFileCannotBeFlushed_KeepsItDirtyForTheNextAttempt()
    {
        // Clearing the flag on a failed flush would be the quiet version of the original bug: the
        // next save would find nothing to do and write a bitfield for bytes that never reached the
        // device.
        var cache = new FlakyHandleCache();
        var storage = new Storage(_metadata, _tempDir, new PathValidator(_tempDir), cache, enableSparseFiles: false);
        await using var _ = storage;

        await storage.InitAsync();
        await storage.WriteAsync(0, new byte[500]);

        cache.FailNextHandles = true;
        Assert.False(await storage.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, CountDirtyFiles(storage));

        // The disk recovers; the retry succeeds and the file finally comes clean.
        cache.FailNextHandles = false;
        Assert.True(await storage.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, CountDirtyFiles(storage));
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_WhenOneFileFails_StillFlushesTheOthers()
    {
        // One bad file must not stop the rest reaching the device. It only has to stop the caller
        // claiming that it did.
        var cache = new FlakyHandleCache();
        var storage = new Storage(_metadata, _tempDir, new PathValidator(_tempDir), cache, enableSparseFiles: false);
        await using var _ = storage;

        await storage.InitAsync();
        await storage.WriteAsync(900, new byte[300]); // spans both files

        cache.FailForPathsContaining = "second.bin";
        Assert.False(await storage.FlushAsync(TestContext.Current.CancellationToken));

        // The healthy file was flushed and cleared; only the failing one is still outstanding.
        Assert.Equal(1, CountDirtyFiles(storage));
    }

    [Fact(Timeout = 30000)]
    public async Task Flush_WhenCancelledMidway_PropagatesRatherThanReportingFailure()
    {
        // Cancellation is not a flush failure, and the two must not be conflated: reporting false
        // would make the caller log a disk problem and skip resume data during an ordinary shutdown.
        var cache = new FlakyHandleCache();
        var storage = new Storage(_metadata, _tempDir, new PathValidator(_tempDir), cache, enableSparseFiles: false);
        await using var _ = storage;

        await storage.InitAsync();
        await storage.WriteAsync(0, new byte[500]);

        cache.CancelNextHandles = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.FlushAsync(CancellationToken.None));

        // The file is still outstanding, so the next flush will pick it up.
        Assert.Equal(1, CountDirtyFiles(storage));
    }

    /// <summary>
    /// A handle cache that can be told to fail, so the flush path's own error handling is reachable
    /// without needing a disk that actually breaks.
    /// </summary>
    private sealed class FlakyHandleCache : IFileHandleCache
    {
        private readonly FileHandleCache _inner = new();

        public bool FailNextHandles { get; set; }
        public bool CancelNextHandles { get; set; }
        public string? FailForPathsContaining { get; set; }

        public void CloseTorrentHandles(string rootPath) => _inner.CloseTorrentHandles(rootPath);

        public ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default)
        {
            if (CancelNextHandles)
            {
                throw new OperationCanceledException($"Simulated cancellation for {path}");
            }

            if (FailNextHandles
                || (FailForPathsContaining != null && path.Contains(FailForPathsContaining, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException($"Simulated handle failure for {path}");
            }

            return _inner.GetHandleAsync(path, writable, cancellationToken);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// Reads the private dirty-file flags. The alternative - asserting that a flush happened at all -
    /// is not observable from outside the process, so this checks the bookkeeping that decides which
    /// handles the barrier touches.
    /// </summary>
    private int CountDirtyFiles() => CountDirtyFiles(_storage);

    private static int CountDirtyFiles(Storage storage)
    {
        var field = typeof(Storage).GetField("_fileDirty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var flags = Assert.IsType<bool[]>(field.GetValue(storage));
        return flags.Count(dirty => dirty);
    }
}
