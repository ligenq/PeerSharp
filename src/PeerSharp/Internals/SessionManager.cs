using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals;

/// <summary>
/// Manages the persistence of torrent sessions, including auto-saving and loading resume data.
/// </summary>
internal sealed class SessionManager : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly Dictionary<InfoHash, string> _magnetLinks = new();
    private readonly ISessionPersistence _persistence;
    private readonly TorrentRegistry _registry;
    private readonly TimeProvider _timeProvider;

    // Store raw .torrent bytes and magnet links for persistence
    // These are needed because the active Torrent object might only have parsed metadata
    private readonly Dictionary<InfoHash, byte[]> _torrentRawData = new();

    private CancellationTokenSource? _autoSaveCts;
    private Task? _autoSaveTask;
    private AtomicDisposal _disposal = new();

    public SessionManager(
        ISessionPersistence persistence,
        TorrentRegistry registry,
        TimeProvider timeProvider,
        ILogger<SessionManager> logger)
    {
        _persistence = persistence;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken)
    {
        UnregisterTorrentData(hash);
        await _persistence.DeleteAsync(hash, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            if (_autoSaveCts != null)
            {
                await _autoSaveCts.CancelAsync().ConfigureAwait(false);
                _autoSaveCts.Dispose();
            }

            if (_autoSaveTask != null)
            {
                try
                {
                    await _autoSaveTask.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on disposal
                }
            }
        }
        GC.SuppressFinalize(this);
    }

    public async Task InitializeAutoSaveAsync(int intervalSeconds)
    {
        if (intervalSeconds <= 0)
        {
            return;
        }

        if (_autoSaveCts != null)
        {
            await _autoSaveCts.CancelAsync().ConfigureAwait(false);
            _autoSaveCts.Dispose();
        }

        _autoSaveCts = new CancellationTokenSource();

        _autoSaveTask = AutoSaveLoopAsync(TimeSpan.FromSeconds(intervalSeconds), _autoSaveCts.Token);
        await Task.CompletedTask.ConfigureAwait(false); // Keep async signature for future proofing
    }

    public Task<IReadOnlyList<SavedTorrentEntry>> LoadAllAsync(CancellationToken cancellationToken)
    {
        return _persistence.LoadAllAsync(cancellationToken);
    }

    public Task<DhtState?> LoadDhtStateAsync(CancellationToken cancellationToken)
    {
        return _persistence.LoadDhtStateAsync(cancellationToken);
    }

    public Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken)
    {
        return _persistence.SaveDhtStateAsync(state, cancellationToken);
    }

    public void RegisterTorrentData(InfoHash hash, byte[]? rawData, string? magnetLink)
    {
        lock (_lock)
        {
            if (rawData is { Length: > 0 })
            {
                _torrentRawData[hash] = rawData;
            }
            if (magnetLink != null)
            {
                _magnetLinks[hash] = magnetLink;
            }
        }
    }

    public async Task SaveAllResumeDataAsync(CancellationToken cancellationToken = default)
    {
        var torrents = _registry.GetAll();
        foreach (var torrent in torrents)
        {
            try
            {
                await SaveTorrentEntryAsync(torrent, null, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save resume data for {Name}", torrent.Name);
            }
        }
    }

    public async Task SaveTorrentEntryAsync(Torrent torrent, byte[]? torrentFileData = null, string? magnetLink = null, CancellationToken cancellationToken = default)
    {
        // Get stored raw data or magnet link
        lock (_lock)
        {
            torrentFileData ??= _torrentRawData.GetValueOrDefault(torrent.Hash);
            magnetLink ??= _magnetLinks.GetValueOrDefault(torrent.Hash);
        }

        var entry = new SavedTorrentEntry(
            torrent.Hash,
            torrentFileData,
            magnetLink,
            torrent.HasMetadata ? torrent.GetResumeData() : null,
            new SavedTorrentOptions(
                torrent.FilesInternal?.DownloadPath ?? torrent.Settings.Files.DefaultDownloadPath,
                torrent.Started,
                torrent.DownloadLimitBytesPerSecond,
                torrent.UploadLimitBytesPerSecond,
                torrent.QueuePriority,
                torrent.RatioLimit,
                torrent.SeedTimeLimit,
                torrent.DownloadStrategy));

        await _persistence.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public void UnregisterTorrentData(InfoHash hash)
    {
        lock (_lock)
        {
            _torrentRawData.Remove(hash);
            _magnetLinks.Remove(hash);
        }
    }

    private async Task AutoSaveLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
                await SaveAllResumeDataAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-save loop failed");
        }
    }
}
