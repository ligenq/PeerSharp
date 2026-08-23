using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;
using PeerSharp.Internals.Utp;
using PeerSharp.Internals.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace PeerSharp.Internals;

internal sealed partial class ClientEngine : IClientEngine, IDhtCallback, ITorrentResolver
{
    private static readonly ProxySettings NoProxy = new();
    private readonly IAlertsManager _alerts;
    private readonly IBandwidthManager _bandwidth;
    private readonly IConnectionGovernor _connectionGovernor;
    private readonly IFileHandleCache _fileHandleCache;

    // Dependencies to be injected into Torrents
    private readonly IGeoIpService _geoIp;

    private readonly INetworkManager? _injectedNetworkManager;
    private readonly ILogger<ClientEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _ownsNetworkManager;
    private readonly IPeerCommunicationFactory _peerFactory;
    private readonly TorrentRegistry _registry;
    private readonly SessionManager? _sessionManager;
    private readonly TimeProvider _timeProvider;
    private readonly ITrackerFactory _trackerFactory;
    private AtomicDisposal _disposal = new();
    private int _bandwidthStarted;

    // Session-wide pause. The set records what was running so Resume puts back exactly that, rather
    // than starting everything the engine happens to hold.
    private readonly Lock _pauseLock = new();
    private readonly HashSet<InfoHash> _pausedTorrents = [];
    private int _paused;
    private int _initialized;
    private INetworkManager? _networkManager;
    private CancellationTokenSource? _dhtSaveCts;

    /// <summary>Drives the BEP 46 record keep-alive loop.</summary>
    private CancellationTokenSource? _republishCts;
    private Task? _dhtSaveTask;
    private CancellationTokenSource? _queueCts;
    private TorrentQueueManager? _queueManager;
    private Task? _queueTask;

    private ClientEngine(
        Settings settings,
        IBandwidthManager bandwidth,
        IAlertsManager alerts,
        INetworkManager? networkManager,
        bool ownsNetworkManager,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        TorrentRegistry registry,
        SessionManager? sessionManager)
    {
        Settings = settings;
        _bandwidth = bandwidth;
        _alerts = alerts;
        _injectedNetworkManager = networkManager;
        _ownsNetworkManager = networkManager == null || ownsNetworkManager;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ClientEngine>();
        _registry = registry;
        _sessionManager = sessionManager;

        _fileHandleCache = new FileHandleCache(loggerFactory: loggerFactory); // Default 200 handles
        _connectionGovernor = new ConnectionGovernor(settings);

        // Initialize dependencies
        _geoIp = new GeoIpService();
        _peerFactory = new PeerCommunicationFactory(loggerFactory);
        _trackerFactory = new TrackerFactory(loggerFactory);

        // Reads through GetStats, which refuses a disposed engine - hence the guard rather than the
        // call alone. Nothing is measured unless something subscribes to the meter.
        _metrics = new EngineMetrics(
            () => _disposal.IsDisposed ? null : GetStats(),
            () => _disposal.IsDisposed ? null : GetLifetimeTotals(),
            _engineId,
            scope: this);
    }

    /// <summary>
    /// Distinguishes this engine's measurements from another's in the same process. Not derived from
    /// settings: two engines can legitimately share a configuration.
    /// </summary>
    private readonly string _engineId = Guid.NewGuid().ToString("N")[..8];

    private readonly EngineMetrics _metrics;

    // GetStats sums the torrents currently registered, which is the right answer for "what is this
    // engine doing now" and the wrong one for a counter. These are the counter's figures, and the
    // removal and the read are kept indivisible - see LifetimeByteTotals for why that matters more
    // than the arithmetic does.
    private LifetimeByteTotals? _lifetimeTotals;

    private LifetimeByteTotals LifetimeTotals =>
        _lifetimeTotals ??= new LifetimeByteTotals(
            () => _registry.GetAll().Select(torrent => (torrent.TotalDownloaded, torrent.TotalUploaded)));

    /// <summary>
    /// Bytes moved over the engine's whole life, including by torrents that have been removed. Only
    /// ever increases, which is what a counter has to promise.
    /// </summary>
    internal (long Downloaded, long Uploaded) GetLifetimeTotals() => LifetimeTotals.Read();

    /// <summary>
    /// Takes a torrent out of the registry and folds its totals into the engine's lifetime figures,
    /// as one step. Returns whether this call is the one that removed it.
    /// </summary>
    private bool RemoveAndRetire(Torrent torrent, bool transient = false)
    {
        return LifetimeTotals.RemoveAndRetire(
            () => transient ? _registry.RemoveTransient(torrent) : _registry.Remove(torrent.Hash, out _),
            torrent.TotalDownloaded,
            torrent.TotalUploaded);
    }

    public IAlerts Alerts => _alerts;

    public IBandwidth Bandwidth => _bandwidth;

    public bool BlocklistEnabled
    {
        get => _networkManager?.Blocklist.Enabled ?? false;
        set
        {
            _networkManager?.Blocklist.Enabled = value;
        }
    }

    public int BoundTcpPort => _networkManager?.BoundTcpPort ?? 0;

    public int BoundUdpPort => _networkManager?.BoundUdpPort ?? 0;

    public IDhtManager? Dht => _networkManager?.Dht;

    public bool GeoIpEnabled
    {
        get => _geoIp.Enabled;
        set => _geoIp.Enabled = value;
    }

    public IPortListener? PortListener => _networkManager?.PortListener;

    /// <summary>
    /// Injectable settings instance. Initialized during Init() or can be set via constructor.
    /// </summary>
    public Settings Settings { get; private set; }

    public IUtpManager? Utp => _networkManager?.Utp;

    internal IpBlocklist? Blocklist => _networkManager?.Blocklist;

    /// <summary>
    /// Token that ties queue rebalancing to the engine's own lifetime. Reading
    /// <see cref="CancellationTokenSource.Token"/> throws once the source is disposed, and a
    /// concurrent shutdown can dispose it right after the caller's disposal check, so the
    /// already-shutting-down case is treated as "cancelled".
    /// </summary>
    private CancellationToken QueueToken
    {
        get
        {
            var cts = _queueCts;
            if (cts == null)
            {
                return CancellationToken.None;
            }

            try
            {
                return cts.Token;
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(canceled: true);
            }
        }
    }

    /// <summary>
    /// Creates a new ClientEngine with default settings and dependencies.
    /// </summary>
    public static ClientEngine Create()
    {
        return Create(new TorrentClientOptions());
    }

    /// <summary>
    /// Creates a new ClientEngine with the specified options.
    /// </summary>
    /// <param name="options">Configuration options for the client.</param>
    public static ClientEngine Create(TorrentClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var loggerFactory = options.EffectiveLoggerFactory;
        var timeProvider = TimeProvider.System;
        var settings = options.Settings ?? new Settings();

        // Create session persistence: use custom if provided, otherwise default if enabled
        ISessionPersistence? sessionPersistence = options.SessionPersistence;
        if (sessionPersistence == null && settings.Session.Enabled)
        {
            sessionPersistence = new SessionPersistence(
                settings.Session.SessionPath,
                loggerFactory.CreateLogger<SessionPersistence>());
        }

        var registry = new TorrentRegistry();
        SessionManager? sessionManager = null;
        if (sessionPersistence != null)
        {
            sessionManager = new SessionManager(sessionPersistence, registry, timeProvider, loggerFactory.CreateLogger<SessionManager>());
        }

        return new ClientEngine(
            settings,
            new BandwidthManager(10, timeProvider, loggerFactory),
            new AlertsManager(timeProvider),
            null,
            true,
            timeProvider,
            loggerFactory,
            registry,
            sessionManager);
    }

    public void ClearBlocklist()
    {
        _disposal.ThrowIfDisposed(this);
        _networkManager?.Blocklist.Clear();
    }

    public void ClearGeoIp()
    {
        _disposal.ThrowIfDisposed(this);
        _geoIp.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            var shutdownStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Cancel both background loops, wait for them to drain, and only then dispose their
            // token sources - a source must outlive every token still in flight.
            if (_queueCts != null)
            {
                await _queueCts.CancelAsync().ConfigureAwait(false);
            }

            if (_dhtSaveCts != null)
            {
                await _dhtSaveCts.CancelAsync().ConfigureAwait(false);
            }

            if (_republishCts != null)
            {
                await _republishCts.CancelAsync().ConfigureAwait(false);
            }

            if (_queueTask is { } queueTask)
            {
                try { await queueTask.ConfigureAwait(false); } catch { /* Ignore cancellation */ }
            }

            if (_dhtSaveTask is { } dhtSaveTask)
            {
                try { await dhtSaveTask.ConfigureAwait(false); } catch { /* Ignore cancellation */ }
            }

            if (_republishTask is { } republishTask)
            {
                try { await republishTask.ConfigureAwait(false); } catch { /* Ignore cancellation */ }
            }

            _queueCts?.Dispose();
            _queueCts = null;
            _dhtSaveCts?.Dispose();
            _dhtSaveCts = null;
            _republishCts?.Dispose();
            _republishCts = null;
            _logger.LogDebug("Shutdown phase queue completed in {ElapsedMs} ms", phaseStopwatch.ElapsedMilliseconds);

            // Save all resume data before shutting down
            phaseStopwatch.Restart();
            if (_sessionManager != null)
            {
                try
                {
                    _logger.LogInformation("Saving session data before shutdown...");
                    await _sessionManager.SaveAllResumeDataAsync(CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("Session data saved successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save session data during shutdown");
                }
                await _sessionManager.DisposeAsync().ConfigureAwait(false);
            }
            _logger.LogDebug("Shutdown phase session completed in {ElapsedMs} ms", phaseStopwatch.ElapsedMilliseconds);

            // Dispose all torrents to ensure they stop and release file handles
            phaseStopwatch.Restart();
            var torrents = _registry.GetAll();
            var disposeTasks = new List<Task>(torrents.Count);
            foreach (var torrent in torrents)
            {
                disposeTasks.Add(torrent.DisposeAsync().AsTask());
            }

            if (disposeTasks.Count > 0)
            {
                try
                {
                    // 15s: torrent disposal flushes the block cache and closes file handles,
                    // which can take several seconds on slow storage or large write queues.
                    await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Timed out or error waiting for torrents to dispose");
                }
            }
            _logger.LogDebug("Shutdown phase torrents completed in {ElapsedMs} ms for {TorrentCount} torrents", phaseStopwatch.ElapsedMilliseconds, disposeTasks.Count);

            // Stop and dispose network manager
            phaseStopwatch.Restart();
            if (_ownsNetworkManager && _networkManager != null)
            {
                // NetworkManager.DisposeAsync performs the stop. Calling both would
                // repeat slow best-effort cleanup such as NAT-PMP/UPnP unmapping.
                await _networkManager.DisposeAsync().ConfigureAwait(false);
            }
            _logger.LogDebug("Shutdown phase network completed in {ElapsedMs} ms", phaseStopwatch.ElapsedMilliseconds);

            phaseStopwatch.Restart();
            _metrics.Dispose();
            _fileHandleCache.Dispose();

            // Dispose bandwidth manager
            await _bandwidth.DisposeAsync().ConfigureAwait(false);
            _logger.LogDebug("Shutdown phase final resources completed in {ElapsedMs} ms", phaseStopwatch.ElapsedMilliseconds);
            _logger.LogDebug("ClientEngine shutdown completed in {ElapsedMs} ms", shutdownStopwatch.ElapsedMilliseconds);
        }
    }

    public IReadOnlyList<PortMappingStatus> GetPortMappingStatus()
    {
        _disposal.ThrowIfDisposed(this);
        return _networkManager?.GetPortMappingStatus() ?? [];
    }

    public EngineStats GetStats()
    {
        _disposal.ThrowIfDisposed(this);

        long dlSpeed = 0;
        long ulSpeed = 0;
        long totalDl = 0;
        long totalUl = 0;
        int active = 0;
        int peers = 0;

        var torrents = _registry.GetAll();
        int total = torrents.Count;

        foreach (var t in torrents)
        {
            if (t.State == TorrentState.Active || t.State == TorrentState.CheckingFiles || t.State == TorrentState.DownloadingMetadata)
            {
                active++;
            }

            peers += t.Peers.ConnectedCount;
            dlSpeed += t._lastReportedDownloadSpeed;
            ulSpeed += t._lastReportedUploadSpeed;
            totalDl += t.TotalDownloaded;
            totalUl += t.TotalUploaded;
        }

        return new EngineStats
        {
            DownloadSpeed = dlSpeed,
            UploadSpeed = ulSpeed,
            TotalDownloaded = totalDl,
            TotalUploaded = totalUl,
            TorrentCount = total,
            ActiveTorrents = active,
            TotalPeers = peers
        };
    }

    public ITorrent? GetTorrent(InfoHash hash)
    {
        _disposal.ThrowIfDisposed(this);
        _registry.TryGet(hash, out var torrent);
        return torrent;
    }

    public IReadOnlyList<ITorrent> GetTorrents()
    {
        _disposal.ThrowIfDisposed(this);
        return _registry.GetAll();
    }

    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        if (Interlocked.CompareExchange(ref _paused, 1, 0) == 1)
        {
            return;
        }

        foreach (var torrent in _registry.GetAll())
        {
            if (!torrent.Started)
            {
                continue;
            }

            lock (_pauseLock)
            {
                _pausedTorrents.Add(torrent.Hash);
            }

            // Cleared before stopping, so the queue cannot pick this torrent up again between the
            // stop and the next one - the queue only starts torrents that want auto-start.
            torrent.QueueAutoStart = false;

            try
            {
                await torrent.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One torrent failing to stop must not leave the rest running, which is what the
                // caller asked to prevent.
                Defect.ReportIfDefect(ex, $"pausing torrent {torrent.Hash}", _logger);
                _logger.LogWarning(ex, "Torrent {Hash} could not be stopped while pausing the session", torrent.Hash);
            }
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        if (Interlocked.CompareExchange(ref _paused, 0, 1) == 0)
        {
            return;
        }

        InfoHash[] toStart;
        lock (_pauseLock)
        {
            toStart = [.. _pausedTorrents];
            _pausedTorrents.Clear();
        }

        foreach (var hash in toStart)
        {
            if (!_registry.TryGet(hash, out var torrent) || torrent is null)
            {
                // Removed while the session was paused. Nothing to restore.
                continue;
            }

            try
            {
                // StartAsync restores QueueAutoStart, so nothing has to put it back here.
                await torrent.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Defect.ReportIfDefect(ex, $"resuming torrent {hash}", _logger);
                _logger.LogWarning(ex, "Torrent {Hash} could not be restarted while resuming the session", hash);
            }
        }
    }

    /// <summary>
    /// Lookups from background work, which answer "nothing" once the engine is gone rather than
    /// throwing.
    ///
    /// <para>
    /// Listeners, DHT callbacks and in-flight handshakes all resolve info hashes, and they keep
    /// arriving for a moment after disposal begins - a packet already in the socket buffer does not
    /// know the engine is shutting down. Throwing there turns an ordinary shutdown race into an
    /// exception on a background thread, when the honest answer is simply that no torrent matches.
    /// The public methods above still throw, which is the conventional behaviour a caller expects.
    /// </para>
    /// </summary>
    ITorrent? ITorrentResolver.GetTorrent(InfoHash hash)
    {
        return ResolveTorrentForBackgroundWork(hash);
    }

    IReadOnlyList<ITorrent> ITorrentResolver.GetTorrents()
    {
        return _disposal.IsDisposed ? [] : _registry.GetAll();
    }

    private ITorrent? ResolveTorrentForBackgroundWork(InfoHash hash)
    {
        if (_disposal.IsDisposed)
        {
            return null;
        }

        _registry.TryGetForRouting(hash, out var torrent);
        return torrent;
    }

    public void LoadBlocklist(Stream stream)
    {
        _disposal.ThrowIfDisposed(this);
        _networkManager?.Blocklist.LoadFromStream(stream);
    }

    public Task LoadBlocklistAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        return _networkManager?.Blocklist.LoadFromStreamAsync(stream, cancellationToken) ?? Task.CompletedTask;
    }

    public void LoadGeoIp(Stream stream)
    {
        _disposal.ThrowIfDisposed(this);
        _geoIp.Load(stream);
    }

    public Task LoadGeoIpAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        return _geoIp.LoadAsync(stream, cancellationToken);
    }

    public void OnPeersFound(InfoHash infoHash, List<IPEndPoint> peers)
    {
        var torrent = ResolveTorrentForBackgroundWork(infoHash);

        // Started, because a lookup answered after the torrent stopped would otherwise queue dials for
        // a torrent nobody is waiting on.
        if (torrent is Torrent { Started: true } t)
        {
            t.PeersInternal.AddPeers(peers, PeerSourceKind.Dht, null);

            // Split by family. BEP 32 is two overlaid DHTs and the IPv6 half is easy to have wired up
            // and never actually reaching: ours parsed values6 correctly for months while bootstrapping
            // only over IPv4, so there was never an IPv6 node to hear it from. A count here says which
            // half is working without having to read packet dumps.
            int ipv6Count = 0;
            foreach (var peer in peers)
            {
                if (peer.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    && !peer.Address.IsIPv4MappedToIPv6)
                {
                    ipv6Count++;
                }
            }

            _logger.LogDebug(
                "Found {PeerCount} peers for {TorrentName} ({IPv6Count} over IPv6)",
                peers.Count,
                t.Name,
                ipv6Count);
        }
    }

    public void OnScrapeResult(InfoHash infoHash, int estimatedSeeds, int estimatedPeers)
    {
        var torrent = ResolveTorrentForBackgroundWork(infoHash);
        if (torrent is Torrent t)
        {
            _logger.LogDebug("DHT scrape for {TorrentName}: ~{Seeds} seeds, ~{Peers} peers", t.Name, estimatedSeeds, estimatedPeers);
        }
    }

    public async Task SaveSessionAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        if (_sessionManager != null)
        {
            await _sessionManager.SaveAllResumeDataAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _disposal.ThrowIfDisposed(this);

        _logger.LogInformation("Stopping ClientEngine...");

        var stopTasks = new List<Task>();
        if (_networkManager != null && _ownsNetworkManager)
        {
            stopTasks.Add(_networkManager.StopAsync(ct));
        }

        var torrents = _registry.GetAll();
        foreach (var torrent in torrents)
        {
            stopTasks.Add(torrent.StopAsync(ct));
        }

        // Cancel the background loops here but dispose their token sources only after the loops
        // have actually drained (below). Disposing a source whose token is still in use can make
        // the loop fault with ObjectDisposedException on its way out.
        var queueCts = _queueCts;
        var dhtSaveCts = _dhtSaveCts;
        _queueCts = null;
        _dhtSaveCts = null;

        if (queueCts != null)
        {
            await queueCts.CancelAsync().ConfigureAwait(false);
        }

        var backgroundLoops = new List<Task>();
        if (_queueTask is { } queueTask)
        {
            backgroundLoops.Add(queueTask);
        }

        if (dhtSaveCts != null)
        {
            await dhtSaveCts.CancelAsync().ConfigureAwait(false);
        }

        if (_dhtSaveTask is { } dhtSaveTask)
        {
            backgroundLoops.Add(dhtSaveTask);
        }

        stopTasks.AddRange(backgroundLoops);

        // Save all resume data before shutting down
        if (_sessionManager != null)
        {
            try
            {
                _logger.LogInformation("Saving session data before shutdown...");
                await _sessionManager.SaveAllResumeDataAsync(ct).ConfigureAwait(false);
                await SaveDhtStateIfNeededAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Session data saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save session data during shutdown");
            }
        }

        if (stopTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(stopTasks).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ClientEngine stop encountered errors");
            }
            finally
            {
                // Safe now: either the loops completed, or the caller's own token fired and we
                // are abandoning them - in which case they are already cancelled and will not
                // register anything new on these sources.
                DisposeDrainedLoop(queueCts, backgroundLoops);
                DisposeDrainedLoop(dhtSaveCts, backgroundLoops);
            }
        }
        else
        {
            queueCts?.Dispose();
            dhtSaveCts?.Dispose();
        }

        _logger.LogInformation("ClientEngine stopped");
    }

    private static void DisposeDrainedLoop(CancellationTokenSource? cts, List<Task> loops)
    {
        if (cts == null)
        {
            return;
        }

        // Only dispose once every loop that could still be holding this token has finished.
        // If one is still running (the caller's token cut the wait short), leaking the source
        // is the lesser evil - it is cancelled, so the loop will exit on its own.
        if (loops.TrueForAll(static t => t.IsCompleted))
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Creates a new ClientEngine with the specified settings and optional dependencies.
    /// Uses a factory method pattern to avoid circular dependency issues.
    /// </summary>
    internal static ClientEngine Create(
        Settings? settings,
        IBandwidthManager? bandwidth = null,
        IAlertsManager? alerts = null,
        INetworkManager? networkManager = null,
        bool takeOwnership = true,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        var actualTimeProvider = timeProvider ?? TimeProvider.System;
        var actualLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

        var registry = new TorrentRegistry();

        return new ClientEngine(
            settings ?? new Settings(),
            bandwidth ?? new BandwidthManager(10, actualTimeProvider, actualLoggerFactory),
            alerts ?? new AlertsManager(actualTimeProvider),
            networkManager,
            takeOwnership,
            actualTimeProvider,
            actualLoggerFactory,
            registry,
            null);
    }

    private Torrent AddMagnetInternal(MagnetLink magnetLink, ITorrentEvents? events = null, TorrentResumeData? resumeData = null, bool transient = false)
    {
        _disposal.ThrowIfDisposed(this);

        var metadata = new TorrentFileMetadata
        {
            Info = new TorrentFileInfo
            {
                Hash = magnetLink.InfoHash,
                HashV2 = magnetLink.InfoHashV2,
                Name = magnetLink.DisplayName ?? string.Empty
            }
        };

        // Determine version
        if (!magnetLink.InfoHash.IsEmpty && !magnetLink.InfoHashV2.IsEmpty)
        {
            metadata.Info.Version = TorrentVersion.Hybrid;
        }
        else if (!magnetLink.InfoHashV2.IsEmpty)
        {
            metadata.Info.Version = TorrentVersion.V2;
        }
        else
        {
            metadata.Info.Version = TorrentVersion.V1;
        }

        if (magnetLink.Trackers.Count > 0)
        {
            metadata.AnnounceList.AddRange(magnetLink.Trackers);
            metadata.AnnounceTiers.Add([.. magnetLink.Trackers]);
            metadata.Announce = metadata.AnnounceList[0];
        }

        var fsm = new FileSelectionManager(metadata);
        var alerts = transient ? NullAlertsManager.Instance : _alerts;
        var torrent = Torrent.Create(metadata, Settings, _bandwidth, alerts, fsm, _peerFactory, _trackerFactory, _geoIp, _fileHandleCache, _connectionGovernor, _timeProvider, events, resumeData, _loggerFactory);

        torrent.DhtManager = Dht;
        torrent.UtpManager = Utp;
        torrent.LsdManager = _networkManager?.Lsd;
        torrent.PortListener = _networkManager?.PortListener;
        torrent.Blocklist = Blocklist;
        torrent.MetadataDownload = new MetadataDownload(torrent, _loggerFactory);
        torrent.MetadataDownload.Start();

        // A transient torrent never reaches the session, so the proxy that would register it there
        // has nothing to do.
        if (!transient)
        {
            torrent.Events = WrapMagnetEvents(torrent.Events);
        }

        if (magnetLink.Peers.Count > 0)
        {
            try
            {
                torrent.PeersInternal.AddPeers(magnetLink.Peers, PeerSourceKind.Resume, null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to add magnet peers");
            }
        }

        Register(torrent, transient);

        return torrent;
    }

    private Torrent AddTorrentInternal(TorrentFileMetadata metadata, ITorrentEvents? events = null, TorrentResumeData? resumeData = null, bool transient = false)
    {
        _disposal.ThrowIfDisposed(this);

        var fsm = new FileSelectionManager(metadata);
        var alerts = transient ? NullAlertsManager.Instance : _alerts;
        var torrent = Torrent.Create(metadata, Settings, _bandwidth, alerts, fsm, _peerFactory, _trackerFactory, _geoIp, _fileHandleCache, _connectionGovernor, _timeProvider, events, resumeData, _loggerFactory);

        torrent.DhtManager = Dht;
        torrent.UtpManager = Utp;
        torrent.LsdManager = _networkManager?.Lsd;
        torrent.PortListener = _networkManager?.PortListener;
        torrent.Blocklist = Blocklist;

        Register(torrent, transient);

        return torrent;
    }

    /// <summary>
    /// Puts a newly built torrent into the registry. A transient one goes into the side list, so it
    /// stays resolvable for inbound connections without joining the caller's torrent list or
    /// tripping the duplicate guard against a hash they may already hold.
    /// </summary>
    private void Register(Torrent torrent, bool transient)
    {
        if (transient)
        {
            _registry.AddTransient(torrent);
        }
        else
        {
            _registry.Add(torrent);
        }
    }

    private ITorrentEvents? WrapMagnetEvents(ITorrentEvents? events)
    {
        if (_sessionManager == null)
        {
            return events;
        }

        return new TorrentEventsProxy(events ?? NullTorrentEvents.Instance, torrent =>
        {
            _ = PersistMagnetMetadataAsync(torrent).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    _logger.LogError(t.Exception.GetBaseException(), "Unhandled exception persisting magnet metadata");
                }
            }, TaskScheduler.Default);
        });
    }

    private async Task PersistMagnetMetadataAsync(Torrent torrent)
    {
        try
        {
            var bytes = TorrentFileSerializer.BuildTorrentBytes(torrent.InfoFile);
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            _sessionManager?.RegisterTorrentData(torrent.Hash, bytes, null);
            if (Settings.Session.Enabled && _sessionManager != null)
            {
                await _sessionManager.SaveTorrentEntryAsync(torrent, bytes, null, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist magnet metadata for {Hash}", torrent.Hash);
        }
    }

    [ExcludeFromCodeCoverage]
    private sealed class TorrentEventsProxy : ITorrentEvents
    {
        private readonly ITorrentEvents _inner;
        private readonly Action<Torrent> _onMetadataReceived;

        public TorrentEventsProxy(ITorrentEvents inner, Action<Torrent> onMetadataReceived)
        {
            _inner = inner;
            _onMetadataReceived = onMetadataReceived;
        }

        public Action<ITorrent, Exception>? Error => _inner?.Error;
        public Action<ITorrent, bool>? Finished => _inner?.Finished;
        public Action<ITorrent, MetadataProgress>? MetadataProgress => _inner?.MetadataProgress;
        public Action<ITorrent>? MetadataReceived => t =>
        {
            _inner?.MetadataReceived?.Invoke(t);
            if (t is Torrent torrent)
            {
                _onMetadataReceived(torrent);
            }
        };
        public Action<ITorrent, PieceProgress>? PieceCompleted => _inner?.PieceCompleted;
        public Action<ITorrent, DownloadProgress>? ProgressChanged => _inner?.ProgressChanged;
        public Action<ITorrent, StateTransition>? StateChanged => _inner?.StateChanged;
        public Action<ITorrent, Interfaces.TransferStats>? TransferStats => _inner?.TransferStats;
    }

    private async Task HandleIncomingUtpAsync(UtpStream stream)
    {
        bool ownershipTransferred = false;
        try
        {
            // Wait for connection state? UtpStream usually starts in Connected if accepted.

            // One deadline for the whole handshake. Creating it per read would restart the clock on
            // every chunk, letting a peer that drips a byte every few seconds hold the connection open
            // indefinitely.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // uTP peers negotiate MSE exactly as TCP peers do. This path used to read 68 bytes and
            // insist the first was 19, so every encrypted inbound uTP peer was rejected - its
            // Diffie-Hellman key looks like noise, which is what the old "Invalid uTP handshake ...
            // first byte" warnings were actually reporting. Both libtorrent and Transmission decide
            // encryption from policy alone with no reference to the transport, and our own measurements
            // show encrypted uTP is the common case rather than an oddity.
            var negotiated = await IncomingHandshakeNegotiator.NegotiateAsync(
                stream,
                this,
                _logger,
                timeoutCts.Token).ConfigureAwait(false);

            if (!negotiated.Success)
            {
                // Returned rather than thrown. This is the ordinary outcome for an inbound peer that
                // hangs up or never finishes the handshake, the catch that received it is twenty
                // lines below in this same method, and it was costing an exception per attempt on a
                // path that runs for every incoming connection.
                _logger.LogDebug("uTP handshake from {Remote} did not complete", stream.RemoteEndPoint);
                return;
            }

            var torrent = ResolveTorrentForBackgroundWork(negotiated.InfoHash);
            if (torrent is Torrent { Started: false } stopped)
            {
                // See PortListener: a stopped torrent must not take connections, or the capacity the
                // queue freed by stopping it goes straight back out of the door.
                _logger.LogDebug(
                    "Refusing incoming uTP connection for {TorrentName}: the torrent is not running",
                    stopped.Name);
                return;
            }

            if (torrent is Torrent t)
            {
                _logger.LogDebug(
                    "Accepted {Kind} uTP connection for {TorrentName} from {Remote}",
                    negotiated.Encryption != null ? "encrypted" : "plaintext",
                    t.Name,
                    stream.RemoteEndPoint);

                await t.PeersInternal.AddIncomingPeerAsync(
                    stream,
                    negotiated.Handshake,
                    stream.RemoteEndPoint,
                    negotiated.Encryption).ConfigureAwait(false);
                ownershipTransferred = true;
                return; // Ownership transferred
            }
            else
            {
                _logger.LogWarning("uTP connection for unknown info hash {Hash} from {Remote}", negotiated.InfoHash, stream.RemoteEndPoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "uTP incoming connection error from {Remote}", stream.RemoteEndPoint);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                try
                {
                    stream.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to close uTP stream from {Remote}", stream.RemoteEndPoint);
                }
            }
        }
    }

    private void HandleUtpConnection(UtpStream stream)
    {
        // Need to read handshake similar to TCP
        // Observe exceptions from fire-and-forget task
        _ = HandleIncomingUtpAsync(stream).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                _logger.LogError(t.Exception.GetBaseException(), "Unhandled exception in uTP connection handler");
                // Best-effort cleanup - stream may already be closed/disposed
                try { stream.Close(); }
                catch (IOException) { /* Stream already closed by peer - expected */ }
                catch (ObjectDisposedException) { /* Stream already disposed - expected during shutdown */ }
            }
        }, TaskScheduler.Default);
    }

    private async Task InitAsync(CancellationToken cancellationToken)
    {
        var bindAddress = Settings.Connection.BindAddress;
        if (bindAddress != null &&
            (bindAddress.Equals(IPAddress.Any) || bindAddress.Equals(IPAddress.IPv6Any)))
        {
            throw new InvalidOperationException(
                "Connection.BindAddress must be a specific local address; use null to listen on all interfaces.");
        }

        // Gateway discovery and mapping open their own interface-selected sockets. They cannot honor a
        // strict bind without changing the meaning of gateway discovery, and a VPN-facing address
        // normally has no mappable NAT gateway anyway. Keep the bind guarantee by not starting them.
        if (bindAddress != null &&
            (Settings.Connection.UpnpPortMapping || Settings.Connection.NatPmpPortMapping))
        {
            _logger.LogInformation(
                "A local bind address is configured; disabling UPnP and NAT-PMP port mapping to keep all traffic bound");
            Settings.Connection.UpnpPortMapping = false;
            Settings.Connection.NatPmpPortMapping = false;
        }

        // Disable UPnP if ForceProxy is enabled
        if (Settings.Proxy.ForceProxy && Settings.Proxy.Type != ProxyType.None)
        {
            _logger.LogInformation("ForceProxy is enabled, disabling UPnP port mapping");
            Settings.Connection.UpnpPortMapping = false;
        }

        if (Settings.PeerId.All(b => b == 0))
        {
            // BEP 20: Generate peer ID using Azureus-style format
            // Format: -MT0100-xxxxxxxxxxxx (20 bytes)
            var peerId = ProtocolConstants.GeneratePeerId();
            Array.Copy(peerId, Settings.PeerId, 20);
            // NOTE: Client application is responsible for persisting Settings if it wants to keep PeerID
        }

        if (_sessionManager != null && Settings.Session.Enabled && Settings.Dht.Enabled)
        {
            try
            {
                var dhtState = await _sessionManager.LoadDhtStateAsync(cancellationToken).ConfigureAwait(false);
                if (dhtState != null)
                {
                    Settings.Dht.InitialState = dhtState;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load initial DHT state");
            }
        }

        if (_injectedNetworkManager != null)
        {
            _networkManager = _injectedNetworkManager;
        }
        else
        {
            // Create NetworkManager and its dependencies
            // Note: socketFactory is now required for UdpListener
            var socketFactory = new UdpSocketFactory();
            var udpListener = new UdpListener(Settings.Connection.UdpPort, socketFactory, Settings, _loggerFactory, _timeProvider);
            var utpManager = new UtpManager(_timeProvider, _loggerFactory);

            var dhtManager = DhtManager.Create(Settings.PeerId, udpListener, Settings, _timeProvider, this, new SystemDnsResolver(), _loggerFactory);
            var portListener = new PortListener(this, _loggerFactory, Settings.Connection.BindAddress);
            var lsdManager = new LsdManager(Settings, this, _timeProvider, socketFactory, _loggerFactory);
            var portMapperFactory = new PortMapperFactory(_loggerFactory);

            var networkServices = new NetworkServices(dhtManager, utpManager, portListener, udpListener, lsdManager, portMapperFactory);

            _networkManager = new NetworkManager(
                Settings,
                HandleUtpConnection,
                networkServices,
                _loggerFactory);
        }

        await _networkManager.StartAsync(cancellationToken).ConfigureAwait(false);

        // Update settings with actual bound ports (relevant if port 0 was used).
        // Preserve the configured port when no listener was bound (e.g. TCP disabled in
        // WebTorrent-only setups) so trackers don't receive port=0, which some reject as
        // "invalid port".
        // Captured before the settings are overwritten below, which is what makes the comparison
        // possible at all - afterwards the configured port and the bound one are the same value.
        int requestedTcpPort = Settings.Connection.TcpPort;
        int requestedUdpPort = Settings.Connection.UdpPort;

        if (_networkManager.BoundTcpPort > 0)
        {
            Settings.Connection.TcpPort = (ushort)_networkManager.BoundTcpPort;
        }
        if (_networkManager.BoundUdpPort > 0)
        {
            Settings.Connection.UdpPort = (ushort)_networkManager.BoundUdpPort;
        }

        // Port 0 asks for whatever is free, so being given something else is the answer, not a
        // surprise worth an alert.
        if (requestedTcpPort != 0 && _networkManager.BoundTcpPort > 0 && _networkManager.BoundTcpPort != requestedTcpPort)
        {
            _alerts.ListenPortChangedAlert(requestedTcpPort, _networkManager.BoundTcpPort, ListenTransport.Tcp);
        }
        if (requestedUdpPort != 0 && _networkManager.BoundUdpPort > 0 && _networkManager.BoundUdpPort != requestedUdpPort)
        {
            _alerts.ListenPortChangedAlert(requestedUdpPort, _networkManager.BoundUdpPort, ListenTransport.Udp);
        }

        // BandwidthManager is intentionally kept alive across a failed initialization because it
        // has no restart operation. Configure and start it only once; subsequent initialization
        // attempts reuse the same timer and channels.
        if (Interlocked.CompareExchange(ref _bandwidthStarted, 0, 0) == 0)
        {
            // THROUGHPUT OPTIMIZATION: Configure bandwidth update interval
            // Lower interval = lower latency, higher throughput (10ms default for gigabit+)
            _bandwidth.Configure(Settings.Transfer.BandwidthUpdateIntervalMs);

            // Initialize Bandwidth Limits
            _bandwidth.SetGlobalLimits(
                Settings.Transfer.MaxDownloadSpeed,
                Settings.Transfer.MaxUploadSpeed);
            _bandwidth.SetGlobalDiskLimits(
                Settings.Files.MaxDiskReadSpeed,
                Settings.Files.MaxDiskWriteSpeed);

            _bandwidth.Start();
            Interlocked.Exchange(ref _bandwidthStarted, 1);
        }

        InitializeQueueManager();

        // Load persisted torrents if session persistence is enabled
        if (_sessionManager != null)
        {
            await LoadPersistedTorrentsAsync(cancellationToken).ConfigureAwait(false);
            await _sessionManager.InitializeAutoSaveAsync(Settings.Session.AutoSaveIntervalSeconds).ConfigureAwait(false);
            InitializeDhtAutoSave();
        }
    }

    [ExcludeFromCodeCoverage]
    private sealed class NullTorrentEvents : ITorrentEvents
    {
        public static readonly NullTorrentEvents Instance = new();

        public Action<ITorrent, StateTransition>? StateChanged => null;
        public Action<ITorrent, Interfaces.TransferStats>? TransferStats => null;
        public Action<ITorrent, Exception>? Error => null;
        public Action<ITorrent, DownloadProgress>? ProgressChanged => null;
        public Action<ITorrent, PieceProgress>? PieceCompleted => null;
        public Action<ITorrent, bool>? Finished => null;
        public Action<ITorrent, MetadataProgress>? MetadataProgress => null;
        public Action<ITorrent>? MetadataReceived => null;
    }

    private void InitializeQueueManager()
    {
        _queueManager = new TorrentQueueManager(Settings.Queue, _timeProvider);

        if (!Settings.Queue.Enabled && !Settings.Queue.EnforceAutoStop)
        {
            return;
        }

        int intervalSeconds = Math.Clamp(Settings.Queue.RecheckIntervalSeconds, 1, 60);

        _queueCts?.Dispose();
        _queueCts = new CancellationTokenSource();
        _queueTask = QueueLoopAsync(TimeSpan.FromSeconds(intervalSeconds), _queueCts.Token);
    }

    private void InitializeDhtAutoSave()
    {
        if (_sessionManager == null || !Settings.Session.Enabled || !Settings.Dht.Enabled)
        {
            return;
        }

        int intervalSeconds = Settings.Session.AutoSaveIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            return;
        }

        intervalSeconds = Math.Max(30, intervalSeconds);

        _dhtSaveCts?.Dispose();
        _dhtSaveCts = new CancellationTokenSource();
        _dhtSaveTask = DhtAutoSaveLoopAsync(TimeSpan.FromSeconds(intervalSeconds), _dhtSaveCts.Token);
    }

    private async Task DhtAutoSaveLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
                await SaveDhtStateIfNeededAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DHT auto-save loop failed");
        }
    }

    private async Task SaveDhtStateIfNeededAsync(CancellationToken ct)
    {
        if (_sessionManager == null || !Settings.Session.Enabled || !Settings.Dht.Enabled)
        {
            return;
        }

        var dht = _networkManager?.Dht;
        if (dht == null)
        {
            return;
        }

        var state = dht.ConsumeStateSnapshot();
        if (state == null)
        {
            return;
        }

        try
        {
            await _sessionManager.SaveDhtStateAsync(state, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save DHT state");
        }
    }

    private async Task LoadPersistedEntryAsync(SavedTorrentEntry entry, CancellationToken cancellationToken)
    {
        AddTorrentOptions? options = null;
        if (entry.Options != null || entry.ResumeData != null)
        {
            options = new AddTorrentOptions
            {
                DownloadPath = entry.Options?.DownloadPath,
                StartImmediately = entry.Options?.WasStarted ?? false,
                DownloadLimitBytesPerSecond = entry.Options?.DownloadLimitBytesPerSecond,
                UploadLimitBytesPerSecond = entry.Options?.UploadLimitBytesPerSecond,
                QueuePriority = entry.Options?.QueuePriority ?? 0,
                RatioLimit = entry.Options?.RatioLimit,
                SeedTimeLimit = entry.Options?.SeedTimeLimit,
                SuperSeeding = entry.Options?.SuperSeeding ?? false,
                DownloadStrategy = entry.Options?.DownloadStrategy ?? DownloadStrategy.RarestFirst,
                ResumeData = entry.ResumeData
            };
        }

        // Restore path: suppress the redundant disk write-back (the entry was just read from
        // disk) and defer queue rebalancing to a single pass after the whole batch loads.
        ITorrent torrent;
        if (entry.TorrentFileData is { Length: > 0 })
        {
            var torrentFile = TorrentFile.Parse(entry.TorrentFileData);
            torrent = await AddTorrentCoreAsync(torrentFile, options, persistToDisk: false, rebalanceQueue: false, entry.Options?.PeerPreferences, cancellationToken).ConfigureAwait(false);

            // Store raw data for future persistence
            _sessionManager?.RegisterTorrentData(torrent.Hash, entry.TorrentFileData, null);
        }
        else if (!string.IsNullOrEmpty(entry.MagnetLink))
        {
            var magnet = MagnetLink.Parse(entry.MagnetLink);
            torrent = await AddMagnetCoreAsync(magnet, options, persistToDisk: false, rebalanceQueue: false, transient: false, entry.Options?.PeerPreferences, cancellationToken).ConfigureAwait(false);

            // Store magnet for future persistence
            _sessionManager?.RegisterTorrentData(torrent.Hash, null, entry.MagnetLink);
        }
        else
        {
            _logger.LogWarning("Persisted entry {Hash} has no torrent file or magnet link", entry.Hash);
            return;
        }

        _logger.LogInformation("Loaded persisted torrent: {Name}", torrent.Name);
    }

    private async Task LoadPersistedTorrentsAsync(CancellationToken cancellationToken)
    {
        if (_sessionManager == null)
        {
            return;
        }

        try
        {
            var entries = await _sessionManager.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return;
            }

            // Load torrents concurrently. Each entry allocates files and spins up its
            // per-torrent managers independently, so a bounded fan-out overlaps that work
            // instead of paying for it serially. Queue rebalancing is deferred to a single
            // pass below so bulk restore is O(n) rather than O(n^2).
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
            };

            await Parallel.ForEachAsync(entries, options, async (entry, ct) =>
            {
                try
                {
                    await LoadPersistedEntryAsync(entry, ct).ConfigureAwait(false);
                }
                catch (TorrentAlreadyExistsException ex)
                {
                    _logger.LogDebug(ex, "Skipping duplicate torrent {Hash}", entry.Hash);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to load persisted torrent {Hash}", entry.Hash);
                }
            }).ConfigureAwait(false);

            // Single rebalance pass once every torrent is registered.
            await RebalanceQueueAsync(QueueToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled InitializeAsync. Restoring a partial session and reporting
            // success would hide that, so let cancellation reach the caller instead of being
            // swallowed by the generic handler below.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted session");
        }
    }

    private async Task QueueLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
                await RebalanceQueueAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queue loop failed");
        }
    }

    private async Task RebalanceQueueAsync(CancellationToken ct)
    {
        if (_queueManager == null)
        {
            return;
        }

        var torrents = _registry.GetAll();
        var plan = _queueManager.BuildPlan(torrents);
        if (plan.Stop.Count == 0 && plan.Start.Count == 0)
        {
            return;
        }

        var byHash = torrents.ToDictionary(t => t.Hash, t => t);

        foreach (var hash in plan.Stop)
        {
            if (byHash.TryGetValue(hash, out var torrent))
            {
                try
                {
                    await torrent.StopAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Queue stop failed for {TorrentName}", torrent.Name);
                }
            }
        }

        foreach (var hash in plan.Start)
        {
            if (byHash.TryGetValue(hash, out var torrent))
            {
                try
                {
                    await torrent.StartAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Queue start failed for {TorrentName}", torrent.Name);
                }
            }
        }
    }

    #region New Async API

    public Task<ITorrent> AddMagnetAsync(
        MagnetLink magnetLink,
        AddTorrentOptions? options = null,
        CancellationToken cancellationToken = default)
        => AddMagnetCoreAsync(magnetLink, options, persistToDisk: true, rebalanceQueue: true, transient: false, restoredPeerPreferences: null, cancellationToken);

    private async Task<ITorrent> AddMagnetCoreAsync(
        MagnetLink magnetLink,
        AddTorrentOptions? options,
        bool persistToDisk,
        bool rebalanceQueue,
        bool transient,
        IReadOnlyList<SavedPeerPreference>? restoredPeerPreferences,
        CancellationToken cancellationToken)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentNullException.ThrowIfNull(magnetLink);
        cancellationToken.ThrowIfCancellationRequested();

        Torrent? torrent = null;
        byte[]? torrentBytes = null;

        if (magnetLink.ExactSources.Count > 0)
        {
            var fetched = await TryFetchTorrentFromMagnetAsync(magnetLink, cancellationToken).ConfigureAwait(false);
            if (fetched != null)
            {
                torrentBytes = fetched.Value.Bytes;
                torrent = AddTorrentInternal(fetched.Value.Metadata, options?.Events, options?.ResumeData, transient);
            }
        }

        torrent ??= AddMagnetInternal(magnetLink, options?.Events, options?.ResumeData, transient);

        // The torrent is registered above, so from here on any failure - most commonly the
        // caller's token tripping during StartAsync - has to unregister it again. Leaving a
        // half-configured torrent behind would make the add look like it did nothing while
        // still failing the next add of the same hash with TorrentAlreadyExistsException.
        try
        {
            torrent.PeersInternal.ImportConnectionPreferences(restoredPeerPreferences);

            if (magnetLink.Peers.Count > 0)
            {
                try
                {
                    torrent.PeersInternal.AddPeers(magnetLink.Peers, PeerSourceKind.Resume, null);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to add magnet peers");
                }
            }

            AddOptionPeers(torrent, options);

            // Add additional trackers from options
            if (options?.AdditionalTrackers != null)
            {
                foreach (var tracker in options.AdditionalTrackers)
                {
                    torrent.TrackerManager.AddTracker(tracker);
                }
            }

            if (options?.AdditionalWebSeeds != null)
            {
                foreach (var webSeed in options.AdditionalWebSeeds)
                {
                    torrent.WebSeeds.Add(webSeed);
                }
            }

            // Apply options
            if (options != null)
            {
                if (options.DownloadPath != null)
                {
                    await torrent.SetDownloadPathAsync(options.DownloadPath, cancellationToken).ConfigureAwait(false);
                }
                torrent.DownloadStrategy = options.DownloadStrategy;
                torrent.DownloadLimitBytesPerSecond = options.DownloadLimitBytesPerSecond ?? 0;
                torrent.UploadLimitBytesPerSecond = options.UploadLimitBytesPerSecond ?? 0;
                torrent.QueuePriority = options.QueuePriority;
                torrent.RatioLimit = options.RatioLimit;
                torrent.SeedTimeLimit = options.SeedTimeLimit;
                torrent.SuperSeeding = options.SuperSeeding;
                torrent.QueueAutoStart = options.StartImmediately;
            }

            // BEP 53: Remember the magnet's select-only file indices. If metadata is already
            // available (fetched via xs=), the selection applies immediately; otherwise it is
            // applied when the metadata download completes.
            if (magnetLink.SelectOnlyFileIndices.Count > 0)
            {
                torrent.PendingSelectOnlyFileIndices = magnetLink.SelectOnlyFileIndices;
                await torrent.ApplyPendingSelectOnlyFileIndicesAsync(cancellationToken).ConfigureAwait(false);
            }

            // Preview mode: leave the torrent stopped once metadata has been downloaded so the
            // application can inspect the file list and adjust selections before starting.
            torrent.StopAfterMetadata = options?.StopAfterMetadata ?? false;

            // Start if requested
            if (options?.StartImmediately ?? true)
            {
                if (IsPaused)
                {
                    // A torrent added while the session is paused waits with the rest, rather than
                    // being the one thing transferring in a paused engine.
                    torrent.QueueAutoStart = false;
                    lock (_pauseLock)
                    {
                        _pausedTorrents.Add(torrent.Hash);
                    }
                }
                else
                {
                    await torrent.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await DiscardFailedAddAsync(torrent, transient).ConfigureAwait(false);
            throw;
        }

        // Save to session persistence if enabled.
        // During restore, only re-register in memory and skip the redundant disk write-back.
        // A transient torrent is not the caller's and must not outlive the operation, so it is
        // kept out of the session entirely - including the in-memory registration.
        if (_sessionManager != null && !transient)
        {
            var magnetString = magnetLink.OriginalString;
            _sessionManager.RegisterTorrentData(torrent.Hash, torrentBytes, magnetString);

            if (persistToDisk)
            {
                try
                {
                    await _sessionManager.SaveTorrentEntryAsync(torrent, torrentBytes, magnetString, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist magnet {Hash}", torrent.Hash);
                }
            }
        }

        if (rebalanceQueue)
        {
            await RebalanceQueueAsync(QueueToken).ConfigureAwait(false);
        }

        return torrent;
    }

    public Task<TorrentFile> GetMagnetMetadataAsync(
        MagnetLink magnetLink,
        CancellationToken cancellationToken = default)
        => GetMagnetMetadataCoreAsync(magnetLink, progress: null, cancellationToken);

    public Task<TorrentFile> GetMagnetMetadataWithProgressAsync(
        MagnetLink magnetLink,
        IProgress<MetadataProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return GetMagnetMetadataCoreAsync(magnetLink, progress, cancellationToken);
    }

    private async Task<TorrentFile> GetMagnetMetadataCoreAsync(
        MagnetLink magnetLink,
        IProgress<MetadataProgress>? progress,
        CancellationToken cancellationToken)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentNullException.ThrowIfNull(magnetLink);

        // Transient fetch: add the magnet in preview mode, wait for its metadata, export it
        // as a TorrentFile, and discard the transient torrent again. The caller can then show
        // a file-selection UI and add the returned TorrentFile like a regular .torrent
        // (its RawData can also be cached to skip the metadata download entirely next time).
        //
        // The add is transient in the engine's own sense: no alerts, no session entry, no queue
        // participation, and no claim on the hash. A caller watching the alert stream sees nothing
        // happen, and one that already holds this hash - or adds it while the fetch is running -
        // is unaffected.
        var torrent = (Torrent)await AddMagnetCoreAsync(
            magnetLink,
            new AddTorrentOptions
            {
                StopAfterMetadata = true,
                Events = progress == null
                    ? null
                    : new TorrentEventsBuilder()
                        .OnMetadataProgress((_, value) => progress.Report(value))
                        .Build()
            },
            persistToDisk: false,
            rebalanceQueue: false,
            transient: true,
            restoredPeerPreferences: null,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await torrent.WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);
            return torrent.ExportTorrentFile();
        }
        finally
        {
            await DiscardTransientAsync(torrent).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tears down a torrent the engine added for its own purposes. Deliberately not
    /// <see cref="RemoveTorrentAsync(ITorrent, RemoveOptions, CancellationToken)"/>: that resolves by hash, raises
    /// <see cref="AlertId.TorrentRemoved"/> and deletes a session entry, none of which are correct
    /// for a torrent the caller was never shown. Runs uncancellably so a cancelled fetch still
    /// cleans up after itself.
    /// </summary>
    private async Task DiscardTransientAsync(Torrent torrent)
    {
        try
        {
            _registry.RemoveTransient(torrent);
            await torrent.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _bandwidth.RemoveTorrentChannels(torrent);
            await torrent.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discard transient metadata-fetch torrent {Hash}", torrent.Hash);
        }
    }

    /// <summary>
    /// Removes and disposes a torrent whose add did not complete, so a failed or cancelled add
    /// leaves the engine exactly as it found it. Runs uncancellably and never throws: the
    /// original add failure is what the caller needs to see.
    /// </summary>
    private async Task DiscardFailedAddAsync(Torrent torrent, bool transient = false)
    {
        try
        {
            // By identity for a transient one: removing by hash could take a real torrent the
            // caller holds for the same hash.
            RemoveAndRetire(torrent, transient);
            _bandwidth.RemoveTorrentChannels(torrent);
            await torrent.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up torrent {Hash} after an unsuccessful add", torrent.Hash);
        }
    }

    private async Task<(TorrentFileMetadata Metadata, byte[] Bytes)?> TryFetchTorrentFromMagnetAsync(
        MagnetLink magnetLink,
        CancellationToken ct)
    {
        foreach (var source in magnetLink.ExactSources)
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                continue;
            }

            try
            {
                var client = GetMagnetHttpClient();
                var bytes = await client.GetByteArrayAsync(uri.ToString(), ct).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    continue;
                }

                var metadata = TorrentFileParser.Parse(bytes, _loggerFactory);
                if (!MagnetMatchesMetadata(magnetLink, metadata))
                {
                    continue;
                }

                MagnetTrackerMerger.Merge(metadata, magnetLink);
                return (metadata, bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch torrent from {Source}", source);
            }
        }

        return null;
    }

    private static bool MagnetMatchesMetadata(MagnetLink magnetLink, TorrentFileMetadata metadata)
    {
        if (magnetLink.IsV1 && !magnetLink.InfoHash.IsEmpty && metadata.Info.Hash.Equals(magnetLink.InfoHash))
        {
            return true;
        }

        if (magnetLink.IsV2 && !magnetLink.InfoHashV2.IsEmpty && metadata.Info.HashV2.Equals(magnetLink.InfoHashV2))
        {
            return true;
        }

        return false;
    }

    private IHttpClient GetMagnetHttpClient()
    {
        var settings = Settings.Proxy;
        if (!settings.ProxyTrackers || settings.Type == ProxyType.None)
        {
            settings = NoProxy;
        }

        return new DefaultHttpClient(new HttpClientFactory().CreateClient(
            settings,
            isTracker: true,
            Settings.Connection.BindAddress));
    }

    public Task<ITorrent> AddTorrentAsync(
        TorrentFile torrentFile,
        AddTorrentOptions? options = null,
        CancellationToken cancellationToken = default)
        => AddTorrentCoreAsync(torrentFile, options, persistToDisk: true, rebalanceQueue: true, restoredPeerPreferences: null, cancellationToken);

    private async Task<ITorrent> AddTorrentCoreAsync(
        TorrentFile torrentFile,
        AddTorrentOptions? options,
        bool persistToDisk,
        bool rebalanceQueue,
        IReadOnlyList<SavedPeerPreference>? restoredPeerPreferences,
        CancellationToken cancellationToken)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentNullException.ThrowIfNull(torrentFile);
        cancellationToken.ThrowIfCancellationRequested();

        var torrent = AddTorrentInternal(torrentFile.Metadata, options?.Events, options?.ResumeData);

        // See AddMagnetCoreAsync: the torrent is already registered, so a failure or a tripped
        // token from here on has to unregister it again.
        try
        {
            torrent.PeersInternal.ImportConnectionPreferences(restoredPeerPreferences);

            AddOptionPeers(torrent, options);

            // Apply options
            if (options != null)
            {
                if (options.DownloadPath != null)
                {
                    await torrent.SetDownloadPathAsync(options.DownloadPath, cancellationToken).ConfigureAwait(false);
                }
                torrent.DownloadStrategy = options.DownloadStrategy;
                torrent.DownloadLimitBytesPerSecond = options.DownloadLimitBytesPerSecond ?? 0;
                torrent.UploadLimitBytesPerSecond = options.UploadLimitBytesPerSecond ?? 0;
                torrent.QueuePriority = options.QueuePriority;
                torrent.RatioLimit = options.RatioLimit;
                torrent.SeedTimeLimit = options.SeedTimeLimit;
                torrent.SuperSeeding = options.SuperSeeding;
                torrent.QueueAutoStart = options.StartImmediately;
            }

            // Start if requested
            if (options?.StartImmediately ?? true)
            {
                if (IsPaused)
                {
                    // A torrent added while the session is paused waits with the rest, rather than
                    // being the one thing transferring in a paused engine.
                    torrent.QueueAutoStart = false;
                    lock (_pauseLock)
                    {
                        _pausedTorrents.Add(torrent.Hash);
                    }
                }
                else
                {
                    await torrent.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await DiscardFailedAddAsync(torrent).ConfigureAwait(false);
            throw;
        }

        // Save to session persistence if enabled.
        // During session restore the entry was just read from disk, so we only re-register
        // the raw data in memory (needed for later auto-saves) and skip the redundant write-back.
        if (_sessionManager != null)
        {
            var rawData = torrentFile.RawData.IsEmpty ? null : torrentFile.RawData.ToArray();
            _sessionManager.RegisterTorrentData(torrent.Hash, rawData, null);

            if (persistToDisk)
            {
                try
                {
                    await _sessionManager.SaveTorrentEntryAsync(torrent, rawData, null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist torrent {Name}", torrent.Name);
                }
            }
        }

        if (rebalanceQueue)
        {
            await RebalanceQueueAsync(QueueToken).ConfigureAwait(false);
        }

        return torrent;
    }

    private void AddOptionPeers(Torrent torrent, AddTorrentOptions? options)
    {
        if (options?.AdditionalPeers is not { Count: > 0 })
        {
            return;
        }

        try
        {
            torrent.PeersInternal.AddPeers(options.AdditionalPeers, PeerSourceKind.Resume, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to add option peers");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            throw new InvalidOperationException("Client engine is already initialized.");
        }

        var torrentsBeforeInitialization = _registry.GetAll().Select(static t => t.Hash).ToHashSet();

        try
        {
            await InitAsync(cancellationToken).ConfigureAwait(false);
            StartRepublishLoop();
        }
        catch
        {
            await RollbackInitializationAsync(torrentsBeforeInitialization).ConfigureAwait(false);
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    /// <summary>
    /// Returns the engine to its pre-initialization state after a failed or cancelled attempt.
    /// Initialization starts the network and queue loop before restoring the session, so merely
    /// resetting <see cref="_initialized"/> would leave live resources behind for the retry.
    /// </summary>
    private async Task RollbackInitializationAsync(HashSet<InfoHash> torrentsBeforeInitialization)
    {
        var queueCts = _queueCts;
        var queueTask = _queueTask;
        _queueCts = null;
        _queueTask = null;
        _queueManager = null;

        if (queueCts != null)
        {
            try
            {
                await queueCts.CancelAsync().ConfigureAwait(false);
                if (queueTask != null)
                {
                    await queueTask.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to drain queue loop after unsuccessful initialization");
            }
            finally
            {
                queueCts.Dispose();
            }
        }

        foreach (var torrent in _registry.GetAll())
        {
            if (torrentsBeforeInitialization.Contains(torrent.Hash))
            {
                continue;
            }

            RemoveAndRetire(torrent);
            _bandwidth.RemoveTorrentChannels(torrent);
            try
            {
                await torrent.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose restored torrent {Hash} after unsuccessful initialization", torrent.Hash);
            }
        }

        if (_networkManager != null)
        {
            try
            {
                await _networkManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop network after unsuccessful initialization");
            }
        }
    }

    public Task RemoveTorrentAsync(
        InfoHash hash,
        RemoveOptions options = RemoveOptions.None,
        CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryGet(hash, out var torrent))
        {
            throw new TorrentNotFoundException(hash);
        }

        return RemoveTorrentAsync(torrent, options, cancellationToken);
    }

    public async Task RemoveTorrentAsync(
        ITorrent torrent,
        RemoveOptions options = RemoveOptions.None,
        CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentNullException.ThrowIfNull(torrent);
        cancellationToken.ThrowIfCancellationRequested();

        if (torrent is not Torrent t)
        {
            throw new ArgumentException("Torrent must be a valid instance from this engine.", nameof(torrent));
        }

        // Deregistering is the single point of no return: after it the torrent is gone from the
        // engine's point of view, and it doubles as the atomic guard against two concurrent
        // removals both running the teardown. Everything past it is uncancellable, so a tripped
        // token can never leave a torrent that is stopped but still registered, or removed but
        // still holding bandwidth channels and its session entry.
        if (!RemoveAndRetire(t))
        {
            throw new TorrentNotFoundException(t.Hash);
        }

        await t.StopAsync(CancellationToken.None).ConfigureAwait(false);

        if (options.HasFlag(RemoveOptions.DeleteFiles))
        {
            try
            {
                await t.FilesInternal.DeleteFilesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Failed to delete some files - not fatal
            }
            catch (UnauthorizedAccessException)
            {
                // Access denied - not fatal
            }
        }

        _bandwidth.RemoveTorrentChannels(t);
        _alerts.TorrentAlert(AlertId.TorrentRemoved, t);

        // Delete from session persistence if enabled
        if (_sessionManager != null)
        {
            try
            {
                await _sessionManager.DeleteAsync(t.Hash, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete persisted torrent {Hash}", t.Hash);
            }
        }

        await RebalanceQueueAsync(QueueToken).ConfigureAwait(false);

        await t.DisposeAsync().ConfigureAwait(false);
    }

    #endregion New Async API
}
