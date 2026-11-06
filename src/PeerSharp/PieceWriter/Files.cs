using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerSharp.PieceWriter;

internal sealed class Files : IInternalFiles, IAsyncDisposable
{
    private readonly BlockCache _blockCache;
    private readonly IStorage _storage;
    private AtomicDisposal _disposal = new();
    private List<TorrentStateData.FileSnapshotData>? _lastFileSnapshots;

    private Files(
        TorrentFileMetadata metadata,
        string path,
        IFileHandleCache handleCache,
        int cacheSizeBytes,
        bool enableSparseFiles,
        int readAheadBlocks,
        bool enableReadAhead,
        long totalSize,
        IBandwidthManager bandwidth,
        string torrentHash,
        ILoggerFactory loggerFactory,
        IReadOnlyDictionary<int, string>? renamedFiles,
        IReadOnlyList<TorrentStateData.FileSnapshotData>? resumeFileSnapshots,
        Func<PiecesProgress> getCompletedPieces)
    {
        DownloadPath = path;
        var diskLimiter = new DiskBandwidthLimiter(bandwidth, torrentHash);
        _storage = new Storage(metadata, path, new PathValidator(path), handleCache, enableSparseFiles, diskLimiter, loggerFactory)
        {
            RenamedFiles = renamedFiles,
            ResumeFileSnapshots = resumeFileSnapshots,
            GetCompletedPieces = getCompletedPieces
        };
        _blockCache = new BlockCache(cacheSizeBytes, readAheadBlocks, enableReadAhead, totalSize);
        _blockCache.Initialize(_storage);
    }

    private bool _flushedOnClose;

    private bool _checking;
    public bool Checking
    {
        get => Volatile.Read(ref _checking);
        set
        {
            Volatile.Write(ref _checking, value);
            _blockCache.Clear();
        }
    }

    /// <summary>
    /// The download path for this torrent's files.
    /// </summary>
    public string DownloadPath { get; }

    public bool IsDisposed => _disposal.IsDisposed;

    internal bool IsInitialized => _storage is Storage storage && storage.IsInitialized;

    /// <summary>See <see cref="Storage.FoundExistingData"/>.</summary>
    internal bool FoundExistingData => _storage is Storage storage && storage.FoundExistingData;

    /// <summary>
    /// Creates a Files instance with optional custom download path.
    /// Path resolution: customPath → settings default → app base directory.
    /// </summary>
    /// <param name="torrent">The torrent.</param>
    /// <param name="handleCache">The global file handle cache.</param>
    /// <param name="customPath">Optional custom download path. If null, uses settings default.</param>
    public static Files Create(Torrent torrent, IFileHandleCache handleCache, string? customPath = null)
    {
        return Create(torrent, handleCache, NullLoggerFactory.Instance, customPath);
    }

    internal static Files Create(Torrent torrent, IFileHandleCache handleCache, ILoggerFactory loggerFactory, string? customPath = null)
    {
        var path = ResolveDownloadPath(torrent, customPath);
        return new Files(
            torrent.InfoFile,
            path,
            handleCache,
            torrent.Settings.Files.BlockCacheSizeBytes,
            torrent.Settings.Files.EnableSparseFiles,
            torrent.Settings.Files.ReadAheadBlocks,
            torrent.Settings.Files.EnableReadAhead,
            torrent.InfoFile.Info.FullSize,
            torrent.Bandwidth,
            BandwidthManager.GetTorrentChannelKey(torrent),
            loggerFactory,
            torrent.GetRenamedFileMap(),
            torrent.LocalState.FileSnapshots,
            () => torrent.Pieces);
    }

    public Task DeleteFilesAsync(CancellationToken ct = default)
    {
        return _storage.DeleteAllAsync(ct);
    }

    public Task MoveFilesAsync(string newRootPath, CancellationToken ct = default)
    {
        return _storage.MoveAsync(newRootPath, ct);
    }

    public Task RenameFileAsync(int fileIndex, string newRelativePath, CancellationToken ct = default)
    {
        return _storage.RenameFileAsync(fileIndex, newRelativePath, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            _blockCache.Dispose();

            // The last chance to flush, and what lets a stopped torrent still be saved: its resume
            // data is written long after this, and has to be able to claim what was written here.
            bool opened = IsInitialized;
            bool flushed = !opened || await _storage.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await _storage.DisposeAsync().ConfigureAwait(false);

            // After closing, not before: a file's write time can move when its last handle closes,
            // and a snapshot from before that would read as the file having changed.
            if (opened && _storage is Storage storage)
            {
                _lastFileSnapshots = storage.GetFileSnapshots();
            }

            Volatile.Write(ref _flushedOnClose, flushed);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The files as they stand, or as this left them once disposed. A snapshot taken later would take
    /// in anything that changed them since, which is what the snapshot exists to catch.
    /// </summary>
    internal List<TorrentStateData.FileSnapshotData>? GetFileSnapshots() =>
        !_disposal.IsDisposed && _storage is Storage storage && storage.IsInitialized
            ? storage.GetFileSnapshots()
            : _lastFileSnapshots;

    /// <summary>
    /// Whether everything written through this instance has reached the disk. Unlike
    /// <see cref="IStorage.FlushAsync"/>, which can only answer for an open file, this is also true of
    /// files that were never opened, with nothing written, and of ones closed after a flush. Those are
    /// the stopped torrents, whose renames and file selections have to be saved all the same.
    /// </summary>
    public Task<bool> FlushAsync(CancellationToken ct = default)
    {
        // Writes are write-through (see BlockCache.WriteAsync), so there is no cache layer to drain
        // first - the storage flush is the whole barrier.
        if (_disposal.IsDisposed)
        {
            return Task.FromResult(Volatile.Read(ref _flushedOnClose));
        }

        return IsInitialized ? _storage.FlushAsync(ct) : Task.FromResult(true);
    }

    public Task InitializeAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
    {
        return _storage.InitAsync(selection, ct);
    }

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(length);
        await ReadAsync(offset, buffer.AsMemory(), ct).ConfigureAwait(false);
        return buffer;
    }

    public async Task ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (Checking)
        {
            await _storage.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
        }
        else
        {
            await _blockCache.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
        }
    }

    public Task StartAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
    {
        return _storage.InitAsync(selection, ct);
    }

    public async Task StopAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    public Task UpdateFileSelectionAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
    {
        // Guard inside the call so the (caller IsDisposed-check, callee dispatch) sequence isn't a TOCTOU race.
        if (_disposal.IsDisposed)
        {
            return Task.CompletedTask;
        }

        return _storage.UpdateFileSelectionAsync(selection, ct);
    }

    public async Task WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _blockCache.WriteAsync(offset, data, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the download path using fallback chain:
    /// customPath → settings default.
    /// </summary>
    private static string ResolveDownloadPath(Torrent torrent, string? customPath)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            return customPath;
        }

        if (!string.IsNullOrEmpty(torrent.Settings.Files.DefaultDownloadPath))
        {
            return torrent.Settings.Files.DefaultDownloadPath;
        }

        throw new ArgumentException("No download path provided. Please specify a download path in settings or AddTorrent options.");
    }
}
