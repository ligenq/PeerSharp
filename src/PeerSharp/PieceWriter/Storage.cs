using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PeerSharp.Internals;
using System.Diagnostics;

namespace PeerSharp.PieceWriter;

/// <summary>
/// Exception thrown for storage-related errors with recovery information.
/// </summary>
public class StorageException : Exception
{
    /// <summary>Initializes a new instance with no message.</summary>
    public StorageException()
    {
    }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">A description of the failure.</param>
    public StorageException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and cause.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The underlying I/O error.</param>
    public StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance and records whether the failure can be retried.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="inner">The underlying I/O error, if any.</param>
    /// <param name="isRecoverable">
    /// <see langword="true"/> if retrying may succeed; <see langword="false"/> for terminal
    /// conditions such as a full disk or a file that has failed repeatedly.
    /// </param>
    public StorageException(string message, Exception? inner, bool isRecoverable)
        : base(message, inner)
    {
        IsRecoverable = isRecoverable;
    }

    /// <summary>
    /// Gets a value indicating whether the operation may succeed if retried. When
    /// <see langword="false"/>, the torrent stops rather than looping on a failure that cannot
    /// clear itself.
    /// </summary>
    public bool IsRecoverable { get; }
}

internal sealed class Storage : IStorage
{
    private const int MaxConsecutiveErrors = 10;
    private readonly SemaphoreSlim _fileSelectionLock = new(1, 1);
    private readonly IFileHandleCache _handleCache;
    private readonly TorrentFileMetadata _info;
    private readonly ILogger<Storage> _logger;
    private readonly Lock _writeTrackingLock = new();
    private readonly IPathValidator _pathValidator;
    private readonly string _rootPath;
    private readonly bool _enableSparseFiles;
    private readonly DiskBandwidthLimiter? _diskLimiter;
    private int _consecutiveErrors = 0;

    private AtomicDisposal _disposal = new();

    private bool[] _fileFailed = default!;

    private SemaphoreSlim[] _fileLocks = default!;

    // Tracks files that have encountered I/O errors
    private FileMapper? _fileMapper;

    // File arrays - protected by _fileSelectionLock for modifications in UpdateFileSelection
    private FileEntry[] _files = default!;

    private bool[] _fileSkipped = default!; // Tracks which files are skipped due to DoNotDownload

    // Files written since the last successful flush. Only these are pushed to the physical device,
    // so a periodic flush costs nothing on a torrent that is seeding or idle.
    private bool[] _fileDirty = default!;

    // Graceful shutdown tracking
    private int _inFlightWrites = 0;
    private TaskCompletionSource _writesDrained = CreateCompletedSignal();

    private int _shutdownRequested = 0;
    private int _initialized = 0;
    private readonly record struct FileEntry(long Length, string? FullPath);

    public Storage(TorrentFileMetadata info, string rootPath, IPathValidator pathValidator, IFileHandleCache handleCache, bool enableSparseFiles, DiskBandwidthLimiter? diskLimiter = null)
        : this(info, rootPath, pathValidator, handleCache, enableSparseFiles, diskLimiter, NullLoggerFactory.Instance)
    {
    }

    public Storage(TorrentFileMetadata info, string rootPath, IPathValidator pathValidator, IFileHandleCache handleCache, bool enableSparseFiles, DiskBandwidthLimiter? diskLimiter, ILoggerFactory loggerFactory)
    {
        _info = info;
        _rootPath = rootPath;
        _pathValidator = pathValidator;
        _handleCache = handleCache;
        _enableSparseFiles = enableSparseFiles;
        _diskLimiter = diskLimiter;
        _logger = loggerFactory.CreateLogger<Storage>();
    }

    public void DeleteAll()
    {
        _handleCache.CloseTorrentHandles(_rootPath);

        if (_files == null || _files.Length == 0)
        {
            return;
        }

        try
        {
            // SAFETY: Only delete the files we know about.
            // Never delete _rootPath blindly as it might be a shared download directory.

            var directoriesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in _files)
            {
                if (file.FullPath != null)
                {
                    _logger.LogDebug("DeleteAll: Checking file {Path} (Exists={Exists})", file.FullPath, File.Exists(file.FullPath));
                }

                if (file.FullPath != null && File.Exists(file.FullPath))
                {
                    try
                    {
                        File.Delete(file.FullPath);
                        _logger.LogInformation("DeleteAll: Deleted file {Path}", file.FullPath);

                        var dir = Path.GetDirectoryName(file.FullPath);
                        if (!string.IsNullOrEmpty(dir) &&
                            dir.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase) &&
                            dir.Length > _rootPath.Length)
                        {
                            directoriesToCheck.Add(dir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete file {Path}", file.FullPath);
                    }
                }
            }

            // Attempt to delete empty parent directories, starting from deepest
            foreach (var dir in directoriesToCheck.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete empty directory {Path}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during storage deletion");
        }
    }

    public Task DeleteAllAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            DeleteAll();
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposal.MarkDisposed())
        {
            return;
        }

        Interlocked.Exchange(ref _shutdownRequested, 1);

        Task writesDrained;
        lock (_writeTrackingLock)
        {
            writesDrained = _inFlightWrites == 0 ? Task.CompletedTask : _writesDrained.Task;
        }

        try
        {
            await writesDrained.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Storage shutdown proceeded with {Count} in-flight writes remaining (timeout)", Volatile.Read(ref _inFlightWrites));
        }

        _handleCache.CloseTorrentHandles(_rootPath);

        bool lockAcquired = await _fileSelectionLock.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        try
        {
            if (_fileLocks != null)
            {
                foreach (var fileLock in _fileLocks)
                {
                    fileLock?.Dispose();
                }
            }
        }
        finally
        {
            if (lockAcquired)
            {
                try { _fileSelectionLock.Release(); }
                catch (SemaphoreFullException ex)
                {
                    _logger.LogTrace(ex, "Semaphore already full during release in DisposeAsync");
                }
            }
        }

        _fileSelectionLock.Dispose();
        // Clear _fileLocks because the semaphores above were just disposed; a stray post-dispose
        // access through the array would throw ObjectDisposedException. _files holds POCOs
        // (paths/sizes only — handles live in _handleCache, already closed via CloseTorrentHandles),
        // so it's safe to leave intact for any in-flight readers about to bail out.
        _fileLocks = [];

        GC.SuppressFinalize(this);
    }

    public async Task InitAsync(IReadOnlyList<FileSelection>? selection = null, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _initialized, 0, 0) == 1)
        {
            if (selection != null)
            {
                await UpdateFileSelectionAsync(selection, ct).ConfigureAwait(false);
            }
            return;
        }

        await _fileSelectionLock.WaitAsync(ct).ConfigureAwait(false);
        bool updateSelectionAfterInit = false;
        bool initializedAlready = false;
        try
        {
            if (_initialized == 1)
            {
                updateSelectionAfterInit = selection != null;
                initializedAlready = true;
            }
            else
            {
                if (!Directory.Exists(_rootPath))
                {
                    Directory.CreateDirectory(_rootPath);
                }

                var files = _info.Info.Files;
                int count = files.Count;

                _files = new FileEntry[count];
                _fileSkipped = new bool[count];
                _fileFailed = new bool[count];
                _fileDirty = new bool[count];
                _fileLocks = new SemaphoreSlim[count];
                _fileMapper = new FileMapper(files.ConvertAll(f => f.Size));

                for (int i = 0; i < count; i++)
                {
                    _fileLocks[i] = new SemaphoreSlim(1, 1);
                }

                int skippedFiles = 0;
                int notSelectedFiles = 0;

                // First pass: classify every file and record the paths that still need
                // physical allocation. Allocation itself is deferred so the (potentially
                // many) SetLength calls can run concurrently rather than one at a time.
                var toAllocate = new List<(string Path, long Size)>();

                // Entries can want the same path on disk, or require one path to be both a file and a
                // directory. A torrent may declare that directly, and rewriting unusable names makes
                // it reachable another way - "a|b" and "a?b/file.txt" collide on Windows. Sharing a
                // file would corrupt both entries, while a file/directory collision makes allocation
                // fail, so claim both kinds as the paths are assigned.
                var pathComparer = StringComparer.OrdinalIgnoreCase;
                var claimedFiles = new HashSet<string>(pathComparer);
                var claimedDirectories = new HashSet<string>(pathComparer);
                var directoryMappings = new Dictionary<string, string>(pathComparer);

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var file = files[i];

                    if (file.IsPadding)
                    {
                        _files[i] = new FileEntry(file.Size, null);
                        _fileSkipped[i] = true;
                        continue;
                    }

                    bool isSelected = true;
                    if (selection != null && i < selection.Count)
                    {
                        var sel = selection[i];
                        isSelected = sel.Selected && sel.Priority != Priority.DoNotDownload;
                    }

                    string? fullPath = SanitizeFilePath(file.Path);
                    if (fullPath == null)
                    {
                        _logger.LogWarning("Skipping malicious/invalid file path in torrent: {FilePath}", file.Path);
                        skippedFiles++;
                        _files[i] = new FileEntry(file.Size, null);
                        _fileSkipped[i] = true;
                        continue;
                    }

                    fullPath = ClaimUniquePath(
                        _rootPath,
                        fullPath,
                        claimedFiles,
                        claimedDirectories,
                        directoryMappings);

                    if (!isSelected)
                    {
                        notSelectedFiles++;
                        _files[i] = new FileEntry(file.Size, fullPath);
                        _fileSkipped[i] = true;
                        continue;
                    }

                    _files[i] = new FileEntry(file.Size, fullPath);
                    _fileSkipped[i] = false;
                    toAllocate.Add((fullPath, file.Size));
                }

                if (toAllocate.Count > 0)
                {
                    var allocationOptions = new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
                    };

                    await Parallel.ForEachAsync(toAllocate, allocationOptions, async (alloc, token) =>
                    {
                        await EnsureFileAllocatedAsync(alloc.Path, alloc.Size, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }

                if (skippedFiles > 0)
                {
                    _logger.LogWarning("Skipped {SkippedFiles} files with malicious/invalid paths in torrent", skippedFiles);
                }
                if (notSelectedFiles > 0)
                {
                    _logger.LogInformation("Skipped {NotSelectedFiles} files not selected for download", notSelectedFiles);
                }

                _initialized = 1;
            }
        }
        catch
        {
            if (!initializedAlready)
            {
                ResetInitializationState();
            }
            throw;
        }
        finally
        {
            _fileSelectionLock.Release();
        }

        if (updateSelectionAfterInit && selection != null && initializedAlready)
        {
            await UpdateFileSelectionAsync(selection, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        var fileOperations = new List<(int FileIdx, long FileOffset, int ReadSize, int BufferOffset)>();

        await _fileSelectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_fileMapper != null)
            {
                foreach (var (FileIndex, FileOffset, Length, BufferOffset) in _fileMapper.MapRange(offset, buffer.Length))
                {
                    fileOperations.Add((FileIndex, FileOffset, Length, BufferOffset));
                }
            }
        }
        finally
        {
            _fileSelectionLock.Release();
        }

        var acquiredLocks = new List<SemaphoreSlim>(fileOperations.Count);
        try
        {
            await AcquireFileLocksAsync(fileOperations, acquiredLocks, failIfShuttingDown: false, ct).ConfigureAwait(false);

            foreach (var (fileIdx, fileOffset, readSize, bufferOffset) in fileOperations)
            {
                if (_fileSkipped[fileIdx])
                {
                    // Deselected file - the data legitimately does not exist locally
                    buffer.Slice(bufferOffset, readSize).Span.Clear();
                    continue;
                }

                if (_fileFailed[fileIdx])
                {
                    // Serving zeroed data for a failed file would poison uploads: remote peers
                    // hash-fail the piece and penalize us. Failing the read lets callers drop
                    // the request instead.
                    throw new StorageException(
                        $"File '{_info.Info.Files[fileIdx].Path}' is marked failed after repeated I/O errors",
                        null,
                        isRecoverable: false);
                }

                var entry = _files[fileIdx];
                if (entry.FullPath == null)
                {
                    buffer.Slice(bufferOffset, readSize).Span.Clear();
                    continue;
                }

                try
                {
                    using var lease = await _handleCache.GetHandleAsync(entry.FullPath, false, ct).ConfigureAwait(false);
                    await ReadWithThrottleAsync(lease.Handle, buffer.Slice(bufferOffset, readSize), fileOffset, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Do NOT zero-fill and continue: returning fabricated data makes uploads
                    // serve garbage (and get this client banned) and makes rechecks loop.
                    var fileName = _info.Info.Files[fileIdx].Path;
                    _logger.LogError(ex, "Read error for file {FileName}", fileName);
                    throw new StorageException($"Read failed for file '{fileName}'", ex, isRecoverable: true);
                }
            }
        }
        finally
        {
            ReleaseFileLocks(acquiredLocks);
        }
    }

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
        await ReadAsync(offset, buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    public async Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
    {
        if (_files == null || _files.Length == 0)
        {
            return;
        }

        await _fileSelectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = _info.Info.Files;
            for (int i = 0; i < files.Count && i < _files.Length; i++)
            {
                if (files[i].IsPadding)
                {
                    continue;
                }
                bool shouldBeSelected = true;
                if (i < selection.Count)
                {
                    var sel = selection[i];
                    shouldBeSelected = sel.Selected && sel.Priority != Priority.DoNotDownload;
                }

                if (shouldBeSelected && _fileSkipped[i])
                {
                    var file = files[i];
                    var entry = _files[i];

                    if (entry.FullPath != null)
                    {
                        try
                        {
                            await EnsureFileAllocatedAsync(entry.FullPath, file.Size, ct).ConfigureAwait(false);

                            _fileSkipped[i] = false;
                            _logger.LogDebug("Enabled file for download: {FilePath}", file.Path);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to enable file {FilePath}", file.Path);
                        }
                    }
                }
                else if (!shouldBeSelected && !_fileSkipped[i])
                {
                    _fileSkipped[i] = true;
                    _logger.LogDebug("Disabled file for download: {FilePath}", files[i].Path);
                }
            }
        }
        finally
        {
            _fileSelectionLock.Release();
        }
    }

    private Task EnsureFileAllocatedAsync(string fullPath, long size, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length < size)
            {
                using var fs = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                TryEnableSparse(fs);
                if (fs.Length < size)
                {
                    fs.SetLength(size);
                }
            }
        }, ct);
    }

    private void ResetInitializationState()
    {
        if (_fileLocks != null)
        {
            foreach (var fileLock in _fileLocks)
            {
                fileLock?.Dispose();
            }
        }

        _files = [];
        _fileSkipped = [];
        _fileFailed = [];
        _fileDirty = [];
        _fileLocks = [];
        _fileMapper = null;
        _initialized = 0;
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        // Everything after this point must run inside the try: leaking the in-flight count
        // (for example when the token is already cancelled, or _fileSelectionLock has been
        // disposed by a racing shutdown) would leave _writesDrained permanently uncompleted and
        // make every later ShutdownAsync wait out its full timeout.
        BeginWrite();
        try
        {
            var fileOperations = new List<(int FileIdx, long FileOffset, int WriteSize, int DataOffset)>();

            await _fileSelectionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_fileMapper != null)
                {
                    foreach (var (FileIndex, FileOffset, Length, BufferOffset) in _fileMapper.MapRange(offset, data.Length))
                    {
                        fileOperations.Add((FileIndex, FileOffset, Length, BufferOffset));
                    }
                }
            }
            finally
            {
                _fileSelectionLock.Release();
            }

            var acquiredLocks = new List<SemaphoreSlim>(fileOperations.Count);
            try
            {
                await AcquireFileLocksAsync(fileOperations, acquiredLocks, failIfShuttingDown: true, ct).ConfigureAwait(false);

                foreach (var (fileIdx, fileOffset, writeSize, dataOffset) in fileOperations)
                {
                    if (_fileSkipped[fileIdx])
                    {
                        // Deselected file - dropping this span is intentional
                        continue;
                    }

                    if (_fileFailed[fileIdx])
                    {
                        // Never pretend the write succeeded: silently skipping would let the piece
                        // be recorded as complete while its bytes were dropped, corrupting the
                        // download and later poisoning uploads to other peers.
                        throw new StorageException(
                            $"File '{_info.Info.Files[fileIdx].Path}' is marked failed after repeated I/O errors",
                            null,
                            isRecoverable: false);
                    }

                    var entry = _files[fileIdx];
                    if (entry.FullPath != null)
                    {
                        try
                        {
                            using var lease = await _handleCache.GetHandleAsync(entry.FullPath, true, ct).ConfigureAwait(false);
                            await WriteWithThrottleAsync(lease.Handle, data.Slice(dataOffset, writeSize), fileOffset, ct).ConfigureAwait(false);
                            // Set under the file lock that FlushAsync also takes, so a flush cannot
                            // observe the write and clear the flag before this marks it.
                            _fileDirty[fileIdx] = true;
                            Interlocked.Exchange(ref _consecutiveErrors, 0);
                        }
                        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070070)) // ERROR_DISK_FULL
                        {
                            HandleDiskFull(fileIdx, ex);
                            throw new StorageException("Disk full", ex, isRecoverable: false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Shutdown/cancellation is not a disk error - don't count it toward file failure
                            throw;
                        }
                        catch (Exception ex)
                        {
                            bool fileNowFailed = HandleFileWriteError(fileIdx, ex);
                            throw new StorageException(
                                $"Write failed for file '{_info.Info.Files[fileIdx].Path}'",
                                ex,
                                isRecoverable: !fileNowFailed);
                        }
                    }
                }
            }
            finally
            {
                ReleaseFileLocks(acquiredLocks);
            }
        }
        finally
        {
            EndWrite();
        }
    }

    /// <summary>
    /// Forces every file written since the last flush out to the physical device, and reports
    /// whether all of them made it.
    ///
    /// <para>
    /// This exists to order two things that were previously unordered. Resume data is written
    /// durably - temp file, flush, atomic rename - while piece data was handed to the operating
    /// system and never flushed, so a power loss could leave a bitfield claiming pieces whose bytes
    /// were still in the write cache. Verification runs on the way in, so nothing downstream would
    /// have caught it: the engine would restart, believe the bitfield, and serve whatever the disk
    /// happened to contain. Flushing before the claim is written is what makes the bitfield a
    /// statement about the disk rather than about the cache.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="false"/> rather than throwing when a file cannot be flushed. The
    /// caller's correct response is to skip this round of resume saving and leave the older copy in
    /// place - older resume data claims fewer pieces, so it is always the safe direction - and a
    /// flush failure is not by itself a reason to fault the torrent.
    /// </para>
    /// </summary>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (_disposal.IsDisposed || Volatile.Read(ref _initialized) == 0)
        {
            return false;
        }

        // Snapshot the arrays: a concurrent shutdown may replace them while this runs, and taking
        // the references once means the loop cannot see a torn pair of dirty flags and locks.
        var dirty = _fileDirty;
        var locks = _fileLocks;
        var files = _files;
        bool allFlushed = true;

        for (int i = 0; i < dirty.Length && i < locks.Length && i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (!Volatile.Read(ref dirty[i]))
            {
                continue;
            }

            var fileLock = locks[i];
            try
            {
                await fileLock.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down underneath us; nothing left to promise about this file.
                return false;
            }

            try
            {
                // Re-check under the lock: another flush may have cleared it in between.
                if (!dirty[i])
                {
                    continue;
                }

                // Deliberately not conditioned on _fileSkipped. A file can be written while selected
                // and deselected before the next save, and deselecting it does not retract the
                // completed pieces covering it - those stay in the bitfield and get persisted as
                // present. Clearing the flag without the barrier would leave exactly the claim this
                // whole mechanism exists to prevent. Only a padding entry, which has no path and is
                // never written, can be cleared for free.
                string? path = files[i].FullPath;
                if (path == null)
                {
                    dirty[i] = false;
                    continue;
                }

                using var lease = await _handleCache.GetHandleAsync(path, true, ct).ConfigureAwait(false);
                RandomAccess.FlushToDisk(lease.Handle);
                dirty[i] = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Leave the flag set so the next flush retries this file.
                allFlushed = false;
                _logger.LogWarning(ex, "Failed to flush '{FilePath}' to disk", files[i].FullPath);
            }
            finally
            {
                try { fileLock.Release(); }
                catch (ObjectDisposedException) { /* Ignored - storage shutting down */ }
            }
        }

        return allFlushed;
    }

    /// <summary>
    /// Acquires the per-file locks covering <paramref name="fileOperations"/>, in ascending file
    /// order. Reads and writes must agree on that order or they deadlock against each other.
    /// <see cref="FileMapper.MapRange"/> walks a contiguous byte range forward, so operations
    /// already arrive strictly ascending and unique - sorting or de-duplicating them would add
    /// allocations to a path that runs once per 16 KiB block.
    /// </summary>
    private async ValueTask AcquireFileLocksAsync(
        List<(int FileIdx, long FileOffset, int Length, int BufferOffset)> fileOperations,
        List<SemaphoreSlim> acquiredLocks,
        bool failIfShuttingDown,
        CancellationToken ct)
    {
        int previousFileIdx = -1;
        foreach (var operation in fileOperations)
        {
            int fileIdx = operation.FileIdx;
            Debug.Assert(fileIdx > previousFileIdx, "MapRange must yield strictly ascending, unique file indices.");
            if (fileIdx <= previousFileIdx)
            {
                continue;
            }
            previousFileIdx = fileIdx;

            // Check shutdown before accessing array which might be cleared
            if (failIfShuttingDown && Volatile.Read(ref _shutdownRequested) == 1)
            {
                throw new ObjectDisposedException(nameof(Storage));
            }

            var lockObj = _fileLocks[fileIdx];
            await lockObj.WaitAsync(ct).ConfigureAwait(false);
            acquiredLocks.Add(lockObj);
        }
    }

    /// <summary>
    /// Releases locks in reverse acquisition order. Holding the <see cref="SemaphoreSlim"/>
    /// references rather than indices matters: a concurrent shutdown may already have cleared
    /// <c>_fileLocks</c> by the time the caller unwinds.
    /// </summary>
    private void ReleaseFileLocks(List<SemaphoreSlim> acquiredLocks)
    {
        for (int i = acquiredLocks.Count - 1; i >= 0; i--)
        {
            try
            {
                acquiredLocks[i].Release();
            }
            catch (ObjectDisposedException) { /* Ignored - storage shutting down */ }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Error releasing file lock");
            }
        }
    }

    private void BeginWrite()
    {
        lock (_writeTrackingLock)
        {
            if (Volatile.Read(ref _shutdownRequested) == 1)
            {
                throw new ObjectDisposedException(nameof(Storage), "Storage is shutting down");
            }

            if (_inFlightWrites == 0)
            {
                _writesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _inFlightWrites++;
        }
    }

    private void EndWrite()
    {
        TaskCompletionSource? drained = null;
        lock (_writeTrackingLock)
        {
            _inFlightWrites--;
            if (_inFlightWrites == 0)
            {
                drained = _writesDrained;
            }
        }
        drained?.TrySetResult();
    }

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private void HandleDiskFull(int fileIdx, Exception ex)
    {
        var fileName = _info.Info.Files[fileIdx].Path;
        _logger.LogCritical(ex, "DISK FULL while writing {FileName}", fileName);
        _logger.LogCritical("Disk full - cannot continue download");
    }

    /// <summary>
    /// Records a write error. Returns true when the file has just crossed the consecutive-error
    /// threshold and is now marked failed, meaning further writes are hopeless and the caller
    /// should treat the failure as non-recoverable.
    /// </summary>
    private bool HandleFileWriteError(int fileIdx, Exception ex)
    {
        var fileName = _info.Info.Files[fileIdx].Path;
        _logger.LogError(ex, "Write error for file {FileName}", fileName);

        int errors = Interlocked.Increment(ref _consecutiveErrors);
        if (errors >= MaxConsecutiveErrors)
        {
            _fileFailed[fileIdx] = true;
            _logger.LogError("File {FileName} marked as failed after {Errors} consecutive errors", fileName, errors);
        }

        if (errors >= MaxConsecutiveErrors * _files.Length)
        {
            _logger.LogCritical("CRITICAL: Too many storage errors ({Errors}), possible disk failure", errors);
        }

        return _fileFailed[fileIdx];
    }

    private void TryEnableSparse(FileStream stream)
    {
        if (!_enableSparseFiles)
        {
            return;
        }

        if (!SparseFileHelper.TrySetSparse(stream.SafeFileHandle, out int error) && error != 0)
        {
            _logger.LogTrace("Sparse file enable failed (code {Error}) for {Path}", error, stream.Name);
        }
    }

    private async Task ReadWithThrottleAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken ct)
    {
        if (_diskLimiter == null)
        {
            await RandomAccess.ReadAsync(handle, buffer, fileOffset, ct).ConfigureAwait(false);
            return;
        }

        int remaining = buffer.Length;
        int localOffset = 0;

        while (remaining > 0)
        {
            int request = Math.Min(remaining, DiskBandwidthLimiter.MaxChunkBytes);
            int granted = await _diskLimiter.RequestReadAsync(request, ct).ConfigureAwait(false);
            if (granted <= 0)
            {
                // Yield before retrying so a limiter that grants nothing cannot spin hot.
                // Task.Yield is banned - it posts back to the caller's SynchronizationContext -
                // and Task.Delay would need a TimeProvider this type does not take. A thread-pool
                // hop gives the same "let something else run" effect with neither problem.
                await Task.Run(static () => { }, ct).ConfigureAwait(false);
                continue;
            }

            int bytesRead = 0;
            try
            {
                bytesRead = await RandomAccess.ReadAsync(handle, buffer.Slice(localOffset, granted), fileOffset + localOffset, ct).ConfigureAwait(false);
            }
            catch
            {
                _diskLimiter.ReturnRead(granted);
                throw;
            }

            if (bytesRead < granted)
            {
                _diskLimiter.ReturnRead(granted - bytesRead);
            }

            remaining -= bytesRead;
            localOffset += bytesRead;

            if (bytesRead == 0)
            {
                break;
            }
        }
    }

    private async Task WriteWithThrottleAsync(SafeFileHandle handle, ReadOnlyMemory<byte> data, long fileOffset, CancellationToken ct)
    {
        if (_diskLimiter == null)
        {
            await RandomAccess.WriteAsync(handle, data, fileOffset, ct).ConfigureAwait(false);
            return;
        }

        int remaining = data.Length;
        int localOffset = 0;

        while (remaining > 0)
        {
            int request = Math.Min(remaining, DiskBandwidthLimiter.MaxChunkBytes);
            int granted = await _diskLimiter.RequestWriteAsync(request, ct).ConfigureAwait(false);
            if (granted <= 0)
            {
                // Yield before retrying so a limiter that grants nothing cannot spin hot.
                // Task.Yield is banned - it posts back to the caller's SynchronizationContext -
                // and Task.Delay would need a TimeProvider this type does not take. A thread-pool
                // hop gives the same "let something else run" effect with neither problem.
                await Task.Run(static () => { }, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RandomAccess.WriteAsync(handle, data.Slice(localOffset, granted), fileOffset + localOffset, ct).ConfigureAwait(false);
            }
            catch
            {
                _diskLimiter.ReturnWrite(granted);
                throw;
            }

            remaining -= granted;
            localOffset += granted;
        }
    }
    /// <summary>
    /// Claims a physical path for one torrent file, numbering any component that an earlier entry
    /// needs as the other filesystem kind. For example, a file at "a_b" and a later entry under
    /// "a_b/file.txt" become "a_b" and "a_b.1/file.txt" rather than making initialization fail
    /// because one path must be both a file and a directory. Repeated logical directories reuse the
    /// same mapping, while duplicate leaf paths get their own numbered files.
    /// </summary>
    private static string ClaimUniquePath(
        string rootPath,
        string fullPath,
        HashSet<string> claimedFiles,
        HashSet<string> claimedDirectories,
        Dictionary<string, string> directoryMappings)
    {
        string root = Path.GetFullPath(rootPath);
        string relativePath = Path.GetRelativePath(root, fullPath);
        string[] parts = relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            throw new InvalidOperationException($"File path '{fullPath}' has no component below '{root}'.");
        }

        string logicalDirectory = root;
        string physicalDirectory = root;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            logicalDirectory = Path.Combine(logicalDirectory, parts[i]);
            if (!directoryMappings.TryGetValue(logicalDirectory, out string? mappedDirectory))
            {
                string candidate = Path.Combine(physicalDirectory, parts[i]);
                mappedDirectory = ClaimUniqueDirectory(
                    candidate,
                    claimedFiles,
                    claimedDirectories);
                directoryMappings.Add(logicalDirectory, mappedDirectory);
            }

            physicalDirectory = mappedDirectory;
        }

        string fileCandidate = Path.Combine(physicalDirectory, parts[^1]);
        return ClaimUniqueFile(fileCandidate, claimedFiles, claimedDirectories);
    }

    private static string ClaimUniqueDirectory(
        string path,
        HashSet<string> claimedFiles,
        HashSet<string> claimedDirectories)
    {
        if (!claimedFiles.Contains(path) && claimedDirectories.Add(path))
        {
            return path;
        }

        string parent = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileName(path);
        int attempts = claimedFiles.Count + claimedDirectories.Count + 1;

        for (int suffix = 1; suffix <= attempts; suffix++)
        {
            string candidate = Path.Combine(parent, $"{name}.{suffix}");
            if (!claimedFiles.Contains(candidate) && claimedDirectories.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find a free directory name for '{path}' in {attempts} attempts.");
    }

    private static string ClaimUniqueFile(
        string path,
        HashSet<string> claimedFiles,
        HashSet<string> claimedDirectories)
    {
        if (!claimedDirectories.Contains(path) && claimedFiles.Add(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        // Bounded rather than open-ended: only as many variants can already be taken as there are
        // paths claimed, so one more attempt than that always finds a free name.
        int attempts = claimedFiles.Count + claimedDirectories.Count + 1;
        for (int suffix = 1; suffix <= attempts; suffix++)
        {
            string candidate = Path.Combine(directory, $"{stem}.{suffix}{extension}");
            if (!claimedDirectories.Contains(candidate) && claimedFiles.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find a free file name for '{path}' in {attempts} attempts.");
    }

    /// <summary>
    /// Sanitizes a file path from torrent metadata to prevent path traversal attacks.
    /// </summary>
    private string? SanitizeFilePath(string relativePath)
    {
        var result = _pathValidator.ValidatePath(relativePath);

        if (!result.IsValid)
        {
            string errorMessage = result.Error switch
            {
                PathValidationError.PathTraversalAttempt => $"SECURITY: Blocked path traversal attempt in torrent file path: {relativePath}",
                PathValidationError.EscapesRootDirectory => $"SECURITY: Path escapes root directory: {relativePath}",
                _ => $"Invalid file path in torrent: {relativePath}"
            };
            _logger.LogWarning("{ErrorMessage}", errorMessage);
            return null;
        }

        return result.SanitizedPath;
    }
}
