using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.PieceWriter;

internal sealed class Files : IInternalFiles, IAsyncDisposable
{
    private readonly BlockCache _blockCache;
    private readonly IStorage _storage;
    private AtomicDisposal _disposal = new();

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
        string torrentHash)
    {
        DownloadPath = path;
        var diskLimiter = new DiskBandwidthLimiter(bandwidth, torrentHash);
        _storage = new Storage(metadata, path, new PathValidator(path), handleCache, enableSparseFiles, diskLimiter);
        _blockCache = new BlockCache(cacheSizeBytes, readAheadBlocks, enableReadAhead, totalSize);
        _blockCache.Initialize(_storage);
    }

    public bool Checking { get; set; }

    /// <summary>
    /// The download path for this torrent's files.
    /// </summary>
    public string DownloadPath { get; }

    public bool IsDisposed => _disposal.IsDisposed;

    /// <summary>
    /// Creates a Files instance with optional custom download path.
    /// Path resolution: customPath → settings default → app base directory.
    /// </summary>
    /// <param name="torrent">The torrent.</param>
    /// <param name="handleCache">The global file handle cache.</param>
    /// <param name="customPath">Optional custom download path. If null, uses settings default.</param>
    public static Files Create(Torrent torrent, IFileHandleCache handleCache, string? customPath = null)
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
            torrent.Hash.ToHexStringUpper());
    }

    public Task DeleteFilesAsync(CancellationToken ct = default)
    {
        return _storage.DeleteAllAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            _blockCache.Dispose();
            await _storage.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
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
        await _blockCache.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
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
