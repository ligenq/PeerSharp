using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals;

/// <summary>
/// Manages the persistence of torrent sessions, including auto-saving and loading resume data.
/// </summary>
internal sealed class SessionManager : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly Dictionary<InfoHash, string> _magnetLinks = [];
    private readonly ISessionPersistence _persistence;
    private readonly TorrentRegistry _registry;
    private readonly TimeProvider _timeProvider;

    // Store raw .torrent bytes and magnet links for persistence
    // These are needed because the active Torrent object might only have parsed metadata
    private readonly Dictionary<InfoHash, byte[]> _torrentRawData = [];

    /// <summary>
    /// One save at a time per torrent. The persistence layer already serialises the file writes, but
    /// that is not enough on its own: two saves that captured their snapshots in one order can reach
    /// the writes in the other, leaving the older bitfield on disk. Holding this across capture,
    /// flush and write makes the sequence indivisible, so the copy that lands is always the newer.
    /// </summary>
    private readonly Dictionary<InfoHash, SemaphoreSlim> _saveGates = [];

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
            var autoSaveCts = _autoSaveCts;
            if (autoSaveCts != null)
            {
                await autoSaveCts.CancelAsync().ConfigureAwait(false);
            }

            bool drained = true;
            if (_autoSaveTask != null)
            {
                try
                {
                    // Resume data is persisted explicitly before this point, so the wait
                    // only drains the already-cancelled auto-save loop. Bound it so a
                    // stalled in-flight save (e.g. on hung storage) cannot block shutdown
                    // indefinitely.
                    await _autoSaveTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    drained = false;
                    _logger.LogWarning(ex, "Auto-save loop did not drain within the shutdown grace period");
                }
                catch (OperationCanceledException)
                {
                    // Expected on disposal
                }
            }

            // Dispose only once the loop can no longer touch the token. If it is still stuck on
            // a hung save, the source is left to the finalizer rather than pulled out from under
            // the running loop.
            if (drained)
            {
                autoSaveCts?.Dispose();
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

        // Retire any previous loop completely before starting a new one: cancel, wait for it to
        // exit, then dispose its source. Without the wait, two loops could briefly save
        // concurrently, and the old one could be holding a token whose source we just disposed.
        var previousCts = _autoSaveCts;
        var previousTask = _autoSaveTask;
        _autoSaveCts = null;
        _autoSaveTask = null;

        if (previousCts != null)
        {
            await previousCts.CancelAsync().ConfigureAwait(false);
        }

        if (previousTask != null)
        {
            try
            {
                await previousTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                previousCts?.Dispose();
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Previous auto-save loop did not drain before restart");
            }
            catch (OperationCanceledException)
            {
                previousCts?.Dispose();
            }
        }
        else
        {
            previousCts?.Dispose();
        }

        _autoSaveCts = new CancellationTokenSource();
        _autoSaveTask = AutoSaveLoopAsync(TimeSpan.FromSeconds(intervalSeconds), _autoSaveCts.Token);
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
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
        };

        await Parallel.ForEachAsync(torrents, options, async (torrent, ct) =>
        {
            try
            {
                await SaveTorrentEntryAsync(torrent, null, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to save resume data for {Name}", torrent.Name);
            }
        }).ConfigureAwait(false);
    }

    public async Task SaveTorrentEntryAsync(Torrent torrent, byte[]? torrentFileData = null, string? magnetLink = null, CancellationToken cancellationToken = default)
    {
        var gate = GetSaveGate(torrent.SessionHash);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveTorrentEntryCoreAsync(torrent, torrentFileData, magnetLink, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetSaveGate(InfoHash hash)
    {
        lock (_lock)
        {
            if (!_saveGates.TryGetValue(hash, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _saveGates[hash] = gate;
            }

            return gate;
        }
    }

    private async Task SaveTorrentEntryCoreAsync(Torrent torrent, byte[]? torrentFileData, string? magnetLink, CancellationToken cancellationToken)
    {
        // Get stored raw data or magnet link
        lock (_lock)
        {
            torrentFileData ??= _torrentRawData.GetValueOrDefault(torrent.SessionHash);
            magnetLink ??= _magnetLinks.GetValueOrDefault(torrent.SessionHash);
        }

        // The bitfield about to be written is a claim that those pieces are on the disk, so the
        // pieces have to reach the disk first. Resume data is itself written durably, which is
        // exactly what makes the ordering matter: without this the durable half would be the claim
        // and the volatile half would be the data it claims.
        //
        // Snapshot first, then flush - not the other way round. A piece is marked complete only
        // after its bytes have been handed to storage, so everything this snapshot claims has
        // already dirtied a file and the flush that follows necessarily covers it. Flushing first
        // leaves a window instead: a piece finishing between the flush and the snapshot is claimed
        // by a resume file that was never durable.
        TorrentResumeData? resumeData = torrent.HasMetadata ? torrent.GetResumeData() : null;
        if (resumeData != null
            && torrent.FilesInternal is { } files
            && !await files.FlushAsync(cancellationToken).ConfigureAwait(false))
        {
            // Leave the previous resume file alone. It is older, so it claims no more than this one
            // would have, and the next save will try again.
            _logger.LogWarning(
                "Skipping resume data for {Name}: piece data could not be flushed to disk",
                torrent.Name);
            resumeData = null;
        }

        var entry = new SavedTorrentEntry(
            torrent.SessionHash,
            torrentFileData,
            magnetLink,
            resumeData,
            new SavedTorrentOptions(
                torrent.FilesInternal?.DownloadPath ?? torrent.Settings.Files.DefaultDownloadPath,
                torrent.Started,
                torrent.DownloadLimitBytesPerSecond,
                torrent.UploadLimitBytesPerSecond,
                torrent.QueuePriority,
                torrent.RatioLimit,
                torrent.SeedTimeLimit,
                torrent.DownloadStrategy)
            {
                PeerPreferences = torrent.PeersInternal.ExportConnectionPreferences(),
                SuperSeeding = torrent.SuperSeeding
            });

        await _persistence.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public void UnregisterTorrentData(InfoHash hash)
    {
        lock (_lock)
        {
            _torrentRawData.Remove(hash);
            _magnetLinks.Remove(hash);

            // Dropped rather than disposed: a save for this torrent may still be holding it, and
            // disposing a semaphore out from under its owner throws on release. Letting it go
            // unreferenced costs nothing and cannot race.
            _saveGates.Remove(hash);
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
