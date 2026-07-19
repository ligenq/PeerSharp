using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PeerSharp.Internals;

namespace PeerSharp.PieceWriter;

/// <summary>
/// Exception thrown for storage-related errors with recovery information.
/// </summary>
public class StorageException : Exception
{
    public StorageException()
    {
    }

    public StorageException(string message)
        : base(message)
    {
    }

    public StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StorageException(string message, Exception? inner, bool isRecoverable)
        : base(message, inner)
    {
        IsRecoverable = isRecoverable;
    }

    public bool IsRecoverable { get; }
}

internal sealed class Storage : IStorage
{
    private const int MaxConsecutiveErrors = 10;
    private readonly SemaphoreSlim _fileSelectionLock = new(1, 1);
    private readonly IFileHandleCache _handleCache;
    private readonly TorrentFileMetadata _info;
    private readonly ILogger<Storage> _logger;
    private readonly ManualResetEventSlim _noWritesInFlight = new(true);
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

    // Graceful shutdown tracking
    private int _inFlightWrites = 0;

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

        if (Interlocked.CompareExchange(ref _inFlightWrites, 0, 0) > 0)
        {
            bool completed = await Task.Run(() => _noWritesInFlight.Wait(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (!completed)
            {
                _logger.LogWarning("Storage shutdown proceeded with {Count} in-flight writes remaining (timeout)", Interlocked.CompareExchange(ref _inFlightWrites, 0, 0));
            }
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

        _noWritesInFlight.Dispose();
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
                _fileLocks = new SemaphoreSlim[count];
                _fileMapper = new FileMapper(files.ConvertAll(f => f.Size));

                for (int i = 0; i < count; i++)
                {
                    _fileLocks[i] = new SemaphoreSlim(1, 1);
                }

                int skippedFiles = 0;
                int notSelectedFiles = 0;

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

                    if (!isSelected)
                    {
                        notSelectedFiles++;
                        _files[i] = new FileEntry(file.Size, fullPath);
                        _fileSkipped[i] = true;
                        continue;
                    }

                    await EnsureFileAllocatedAsync(fullPath, file.Size, ct).ConfigureAwait(false);

                    _files[i] = new FileEntry(file.Size, fullPath);
                    _fileSkipped[i] = false;
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

        var lockedFiles = new List<int>();
        try
        {
            foreach (var (FileIdx, _, _, _) in fileOperations)
            {
                if (!lockedFiles.Contains(FileIdx))
                {
                    await _fileLocks[FileIdx].WaitAsync(ct).ConfigureAwait(false);
                    lockedFiles.Add(FileIdx);
                }
            }

            foreach (var (fileIdx, fileOffset, readSize, bufferOffset) in fileOperations)
            {
                if (_fileSkipped[fileIdx] || _fileFailed[fileIdx])
                {
                    buffer.Slice(bufferOffset, readSize).Span.Clear();
                    continue;
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
                catch (Exception ex)
                {
                    var fileName = _info.Info.Files[fileIdx].Path;
                    _logger.LogError(ex, "Read error for file {FileName}", fileName);
                    buffer.Slice(bufferOffset, readSize).Span.Clear();
                }
            }
        }
        finally
        {
            for (int i = lockedFiles.Count - 1; i >= 0; i--)
            {
                _fileLocks[lockedFiles[i]].Release();
            }
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
        _fileLocks = [];
        _fileMapper = null;
        _initialized = 0;
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        int count = Interlocked.Increment(ref _inFlightWrites);
        if (count == 1)
        {
            _noWritesInFlight.Reset();
        }

        if (Interlocked.CompareExchange(ref _shutdownRequested, 0, 0) == 1)
        {
            if (Interlocked.Decrement(ref _inFlightWrites) == 0)
            {
                _noWritesInFlight.Set();
            }
            throw new ObjectDisposedException(nameof(Storage), "Storage is shutting down");
        }

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

        var lockedFiles = new List<int>();
        var acquiredLocks = new List<SemaphoreSlim>();
        try
        {
            foreach (var (FileIdx, _, _, _) in fileOperations)
            {
                if (!lockedFiles.Contains(FileIdx))
                {
                    // Check shutdown before accessing array which might be cleared
                    if (Interlocked.CompareExchange(ref _shutdownRequested, 0, 0) == 1)
                    {
                        throw new ObjectDisposedException(nameof(Storage));
                    }

                    var lockObj = _fileLocks[FileIdx];
                    await lockObj.WaitAsync(ct).ConfigureAwait(false);
                    lockedFiles.Add(FileIdx);
                    acquiredLocks.Add(lockObj);
                }
            }

            foreach (var (fileIdx, fileOffset, writeSize, dataOffset) in fileOperations)
            {
                if (_fileSkipped[fileIdx] || _fileFailed[fileIdx])
                {
                    continue;
                }

                var entry = _files[fileIdx];
                if (entry.FullPath != null)
                {
                    try
                    {
                        using var lease = await _handleCache.GetHandleAsync(entry.FullPath, true, ct).ConfigureAwait(false);
                        await WriteWithThrottleAsync(lease.Handle, data.Slice(dataOffset, writeSize), fileOffset, ct).ConfigureAwait(false);
                        Interlocked.Exchange(ref _consecutiveErrors, 0);
                    }
                    catch (IOException ex) when (ex.HResult == unchecked((int)0x80070070)) // ERROR_DISK_FULL
                    {
                        HandleDiskFull(fileIdx, ex);
                        throw new StorageException("Disk full", ex, isRecoverable: false);
                    }
                    catch (Exception ex)
                    {
                        HandleFileWriteError(fileIdx, ex);
                    }
                }
            }
        }
        finally
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
                    _logger.LogTrace(ex, "Error releasing file lock in WriteAsync");
                }
            }

            if (Interlocked.Decrement(ref _inFlightWrites) == 0)
            {
                _noWritesInFlight.Set();
            }
        }
    }

    private void HandleDiskFull(int fileIdx, Exception ex)
    {
        var fileName = _info.Info.Files[fileIdx].Path;
        _logger.LogCritical(ex, "DISK FULL while writing {FileName}", fileName);
        _logger.LogCritical("Disk full - cannot continue download");
    }

    private void HandleFileWriteError(int fileIdx, Exception ex)
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
                await Task.Yield();
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
                await Task.Yield();
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
                PathValidationError.InvalidCharacters => $"SECURITY: Invalid characters in torrent file path: {relativePath}",
                PathValidationError.WindowsReservedName => $"SECURITY: Reserved filename in torrent file path: {relativePath}",
                PathValidationError.EscapesRootDirectory => $"SECURITY: Path escapes root directory: {relativePath}",
                _ => $"Invalid file path in torrent: {relativePath}"
            };
            _logger.LogWarning("{ErrorMessage}", errorMessage);
            return null;
        }

        return result.SanitizedPath;
    }
}
