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

internal sealed class ClientEngine : IClientEngine, IDhtCallback, ITorrentResolver
{
    private static readonly ConcurrentDictionary<ProxySettings, HttpClient> MagnetClientCache = new();
    private static readonly ProxySettings NoProxy = new();
    private readonly IAlertsManager _alerts;
    private readonly IBandwidthManager _bandwidth;
    private readonly IConnectionGovernor _connectionGovernor;
    private readonly IFileHandleCache _fileHandleCache;

    // Dependencies to be injected into Torrents
    private readonly IGeoIpService _geoIp;

    private readonly INetworkManager? _injectedNetworkManager;
    private readonly ILogger<ClientEngine> _logger;
    private readonly bool _ownsNetworkManager;
    private readonly IPeerCommunicationFactory _peerFactory;
    private readonly TorrentRegistry _registry;
    private readonly SessionManager? _sessionManager;
    private readonly TimeProvider _timeProvider;
    private readonly ITrackerFactory _trackerFactory;
    private AtomicDisposal _disposal = new();
    private int _initialized;
    private INetworkManager? _networkManager;
    private CancellationTokenSource? _dhtSaveCts;
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
        _logger = loggerFactory.CreateLogger<ClientEngine>();
        _registry = registry;
        _sessionManager = sessionManager;

        _fileHandleCache = new FileHandleCache(); // Default 200 handles
        _connectionGovernor = new ConnectionGovernor(settings);

        // Initialize dependencies
        _geoIp = new GeoIpService();
        _peerFactory = new PeerCommunicationFactory();
        _trackerFactory = new TrackerFactory();
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

        // Configure the static logger factory for components that need it
        TorrentLoggerFactory.Configure(loggerFactory);

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
            new BandwidthManager(10, timeProvider),
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
            if (_queueCts != null)
            {
                await _queueCts.CancelAsync().ConfigureAwait(false);
            }

            if (_queueTask is { } queueTask)
            {
                try { await queueTask.ConfigureAwait(false); } catch { /* Ignore cancellation */ }
            }

            _queueCts?.Dispose();

            // Save all resume data before shutting down
            if (_sessionManager != null)
            {
                try
                {
                    _logger.LogInformation("Saving session data before shutdown...");
                    await _sessionManager.SaveAllResumeDataAsync().ConfigureAwait(false);
                    _logger.LogInformation("Session data saved successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save session data during shutdown");
                }
                await _sessionManager.DisposeAsync().ConfigureAwait(false);
            }

            // Dispose all torrents to ensure they stop and release file handles
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
                    await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Timed out or error waiting for torrents to dispose");
                }
            }

            // Stop and dispose network manager
            if (_ownsNetworkManager && _networkManager != null)
            {
                await _networkManager.StopAsync().ConfigureAwait(false);
                await _networkManager.DisposeAsync().ConfigureAwait(false);
            }

            _fileHandleCache.Dispose();

            // Dispose bandwidth manager
            await _bandwidth.DisposeAsync().ConfigureAwait(false);
        }
    }

    public IReadOnlyList<PortMappingStatus> GetPortMappingStatus()
    {
        _disposal.ThrowIfDisposed(this);
        return _networkManager?.GetPortMappingStatus() ?? Array.Empty<PortMappingStatus>();
    }

    public EngineStats GetStats()
    {
        _disposal.ThrowIfDisposed(this);

        int dlSpeed = 0;
        int ulSpeed = 0;
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
        var torrent = GetTorrent(infoHash);
        if (torrent is Torrent t)
        {
            t.PeersInternal.AddPeers(peers, PeerSourceKind.Dht, null);
            _logger.LogDebug("Found {PeerCount} peers for {TorrentName}", peers.Count, t.Name);
        }
    }

    public void OnScrapeResult(InfoHash infoHash, int estimatedSeeds, int estimatedPeers)
    {
        var torrent = GetTorrent(infoHash);
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

        if (_queueCts != null)
        {
            await _queueCts.CancelAsync().ConfigureAwait(false);
        }

        if (_queueTask is { } queueTask)
        {
            stopTasks.Add(queueTask);
        }

        _queueCts?.Dispose();
        _queueCts = null;

        if (_dhtSaveCts != null)
        {
            await _dhtSaveCts.CancelAsync().ConfigureAwait(false);
        }

        if (_dhtSaveTask is { } dhtSaveTask)
        {
            stopTasks.Add(dhtSaveTask);
        }

        _dhtSaveCts?.Dispose();
        _dhtSaveCts = null;

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
        }

        _logger.LogInformation("ClientEngine stopped");
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

        // Configure the static logger factory for components that need it
        TorrentLoggerFactory.Configure(actualLoggerFactory);

        var registry = new TorrentRegistry();

        return new ClientEngine(
            settings ?? new Settings(),
            bandwidth ?? new BandwidthManager(10, actualTimeProvider),
            alerts ?? new AlertsManager(actualTimeProvider),
            networkManager,
            takeOwnership,
            actualTimeProvider,
            actualLoggerFactory,
            registry,
            null);
    }

    private Torrent AddMagnetInternal(MagnetLink magnetLink, ITorrentEvents? events = null, TorrentResumeData? resumeData = null)
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
            metadata.AnnounceTiers.Add(new List<string>(magnetLink.Trackers));
            metadata.Announce = metadata.AnnounceList[0];
        }

        var fsm = new FileSelectionManager(metadata);
        var torrent = Torrent.Create(metadata, Settings, _bandwidth, _alerts, fsm, _peerFactory, _trackerFactory, _geoIp, _fileHandleCache, _connectionGovernor, _timeProvider, events, resumeData);

        torrent.DhtManager = Dht;
        torrent.UtpManager = Utp;
        torrent.LsdManager = _networkManager?.Lsd;
        torrent.Blocklist = Blocklist;
        torrent.MetadataDownload = new MetadataDownload(torrent);
        torrent.MetadataDownload.Start();
        torrent.Events = WrapMagnetEvents(torrent.Events);

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

        _registry.Add(torrent);

        return torrent;
    }

    private Torrent AddTorrentInternal(TorrentFileMetadata metadata, ITorrentEvents? events = null, TorrentResumeData? resumeData = null)
    {
        _disposal.ThrowIfDisposed(this);

        var fsm = new FileSelectionManager(metadata);
        var torrent = Torrent.Create(metadata, Settings, _bandwidth, _alerts, fsm, _peerFactory, _trackerFactory, _geoIp, _fileHandleCache, _connectionGovernor, _timeProvider, events, resumeData);

        torrent.DhtManager = Dht;
        torrent.UtpManager = Utp;
        torrent.LsdManager = _networkManager?.Lsd;
        torrent.Blocklist = Blocklist;

        _registry.Add(torrent);

        return torrent;
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
                await _sessionManager.SaveTorrentEntryAsync(torrent, bytes, null).ConfigureAwait(false);
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

            byte[] buffer = new byte[68];
            int read = 0;
            while (read < 68)
            {
                using var timeoutCts = new CancellationTokenSource(10000);
                int r = await stream.ReadAsync(buffer.AsMemory(read, 68 - read), timeoutCts.Token).ConfigureAwait(false);
                if (r == 0)
                {
                    _logger.LogWarning("uTP connection from {Remote} closed before handshake complete (read {Bytes}/68)", stream.RemoteEndPoint, read);
                    throw new IOException("Connection closed");
                }

                read += r;
            }

            if (buffer[0] != 19)
            {
                _logger.LogWarning("Invalid uTP handshake from {Remote}: first byte {Byte} (expected 19), hex: {Hex}",
                    stream.RemoteEndPoint, buffer[0], Convert.ToHexString(buffer, 0, read));
                throw new InvalidDataException("Invalid handshake");
            }

            var infoHash = new InfoHash(buffer.AsSpan(28, 20));

            var torrent = GetTorrent(infoHash);
            if (torrent is Torrent t)
            {
                _logger.LogDebug("Accepted uTP connection for {TorrentName} from {Remote}", t.Name, stream.RemoteEndPoint);
                await t.PeersInternal.AddIncomingPeerAsync(stream, buffer, stream.RemoteEndPoint).ConfigureAwait(false);
                ownershipTransferred = true;
                return; // Ownership transferred
            }
            else
            {
                _logger.LogWarning("uTP connection for unknown info hash {Hash} from {Remote}", infoHash, stream.RemoteEndPoint);
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
            var udpListener = new UdpListener(Settings.Connection.UdpPort, socketFactory, Settings);
            var utpManager = new UtpManager(_timeProvider);

            var dhtManager = new DhtManager(Settings.PeerId, udpListener, Settings, _timeProvider, this, new SystemDnsResolver());
            var portListener = new PortListener(this);
            var lsdManager = new LsdManager(Settings, this, _timeProvider, socketFactory);
            var portMapperFactory = new PortMapperFactory();

            var networkServices = new NetworkServices(dhtManager, utpManager, portListener, udpListener, lsdManager, portMapperFactory);

            _networkManager = new NetworkManager(Settings,
    HandleUtpConnection,
    networkServices
);
        }

        await _networkManager.StartAsync(cancellationToken).ConfigureAwait(false);

        // Update settings with actual bound ports (relevant if port 0 was used).
        // Preserve the configured port when no listener was bound (e.g. TCP disabled in
        // WebTorrent-only setups) so trackers don't receive port=0, which some reject as
        // "invalid port".
        if (_networkManager.BoundTcpPort > 0)
        {
            Settings.Connection.TcpPort = (ushort)_networkManager.BoundTcpPort;
        }
        if (_networkManager.BoundUdpPort > 0)
        {
            Settings.Connection.UdpPort = (ushort)_networkManager.BoundUdpPort;
        }

        // THROUGHPUT OPTIMIZATION: Configure bandwidth update interval
        // Lower interval = lower latency, higher throughput (10ms default for gigabit+)
        _bandwidth.Configure(Settings.Transfer.BandwidthUpdateIntervalMs);

        // Initialize Bandwidth Limits
        _bandwidth.SetGlobalLimits(
            (int)Settings.Transfer.MaxDownloadSpeed,
            (int)Settings.Transfer.MaxUploadSpeed);
        _bandwidth.SetGlobalDiskLimits(
            (int)Settings.Files.MaxDiskReadSpeed,
            (int)Settings.Files.MaxDiskWriteSpeed);

        _bandwidth.Start();

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
                DownloadStrategy = entry.Options?.DownloadStrategy ?? DownloadStrategy.RarestFirst,
                ResumeData = entry.ResumeData
            };
        }

        ITorrent torrent;
        if (entry.TorrentFileData is { Length: > 0 })
        {
            var torrentFile = TorrentFile.Parse(entry.TorrentFileData);
            torrent = await AddTorrentAsync(torrentFile, options, cancellationToken).ConfigureAwait(false);

            // Store raw data for future persistence
            _sessionManager?.RegisterTorrentData(torrent.Hash, entry.TorrentFileData, null);
        }
        else if (!string.IsNullOrEmpty(entry.MagnetLink))
        {
            var magnet = MagnetLink.Parse(entry.MagnetLink);
            torrent = await AddMagnetAsync(magnet, options, cancellationToken).ConfigureAwait(false);

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

            foreach (var entry in entries)
            {
                try
                {
                    await LoadPersistedEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                catch (TorrentAlreadyExistsException ex)
                {
                    _logger.LogDebug(ex, "Skipping duplicate torrent {Hash}", entry.Hash);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load persisted torrent {Hash}", entry.Hash);
                }
            }
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

    public async Task<ITorrent> AddMagnetAsync(
        MagnetLink magnetLink,
        AddTorrentOptions? options = null,
        CancellationToken cancellationToken = default)
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
                torrent = AddTorrentInternal(fetched.Value.Metadata, options?.Events, options?.ResumeData);
            }
        }

        torrent ??= AddMagnetInternal(magnetLink, options?.Events, options?.ResumeData);

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

        // Add additional trackers from options
        if (options?.AdditionalTrackers != null)
        {
            foreach (var tracker in options.AdditionalTrackers)
            {
                torrent.TrackerManager.AddTracker(tracker);
            }
        }

        // Apply options
        if (options != null)
        {
            if (options.DownloadPath != null)
            {
                await torrent.SetDownloadPathAsync(options.DownloadPath).ConfigureAwait(false);
            }
            torrent.DownloadStrategy = options.DownloadStrategy;
            torrent.DownloadLimitBytesPerSecond = options.DownloadLimitBytesPerSecond ?? 0;
            torrent.UploadLimitBytesPerSecond = options.UploadLimitBytesPerSecond ?? 0;
            torrent.QueuePriority = options.QueuePriority;
            torrent.RatioLimit = options.RatioLimit;
            torrent.SeedTimeLimit = options.SeedTimeLimit;
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

        // Start if requested
        if (options?.StartImmediately ?? true)
        {
            await torrent.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        // Save to session persistence if enabled
        if (_sessionManager != null)
        {
            var magnetString = magnetLink.OriginalString;
            _sessionManager.RegisterTorrentData(torrent.Hash, torrentBytes, magnetString);

            try
            {
                await _sessionManager.SaveTorrentEntryAsync(torrent, torrentBytes, magnetString, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist magnet {Hash}", torrent.Hash);
            }
        }

        await RebalanceQueueAsync(_queueCts?.Token ?? default).ConfigureAwait(false);

        return torrent;
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

                var metadata = TorrentFileParser.Parse(bytes);
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

        return new DefaultHttpClient(MagnetClientCache.GetOrAdd(settings, CreateMagnetClient));
    }

    private static HttpClient CreateMagnetClient(ProxySettings proxy)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        if (proxy.Type != ProxyType.None && !string.IsNullOrEmpty(proxy.Host))
        {
            string proxyUri = proxy.Type switch
            {
                ProxyType.Socks5 => $"socks5://{proxy.Host}:{proxy.Port}",
                ProxyType.Http => $"http://{proxy.Host}:{proxy.Port}",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(proxyUri))
            {
                var webProxy = new WebProxy(proxyUri);
                if (!string.IsNullOrEmpty(proxy.Username))
                {
                    webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                }
                handler.Proxy = webProxy;
            }
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<ITorrent> AddTorrentAsync(
        TorrentFile torrentFile,
        AddTorrentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentNullException.ThrowIfNull(torrentFile);
        cancellationToken.ThrowIfCancellationRequested();

        var torrent = AddTorrentInternal(torrentFile.Metadata, options?.Events, options?.ResumeData);

        // Apply options
        if (options != null)
        {
            if (options.DownloadPath != null)
            {
                await torrent.SetDownloadPathAsync(options.DownloadPath).ConfigureAwait(false);
            }
            torrent.DownloadStrategy = options.DownloadStrategy;
            torrent.DownloadLimitBytesPerSecond = options.DownloadLimitBytesPerSecond ?? 0;
            torrent.UploadLimitBytesPerSecond = options.UploadLimitBytesPerSecond ?? 0;
            torrent.QueuePriority = options.QueuePriority;
            torrent.RatioLimit = options.RatioLimit;
            torrent.SeedTimeLimit = options.SeedTimeLimit;
            torrent.QueueAutoStart = options.StartImmediately;
        }

        // Start if requested
        if (options?.StartImmediately ?? true)
        {
            await torrent.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        // Save to session persistence if enabled
        if (_sessionManager != null)
        {
            var rawData = torrentFile.RawData.IsEmpty ? null : torrentFile.RawData.ToArray();
            _sessionManager.RegisterTorrentData(torrent.Hash, rawData, null);

            try
            {
                await _sessionManager.SaveTorrentEntryAsync(torrent, rawData, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist torrent {Name}", torrent.Name);
            }
        }

        await RebalanceQueueAsync(_queueCts?.Token ?? default).ConfigureAwait(false);

        return torrent;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            throw new InvalidOperationException("Client engine is already initialized.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await InitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Reset initialized flag on failure so initialization can be retried
            Interlocked.Exchange(ref _initialized, 0);
            throw;
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

        if (!_registry.Contains(t.Hash))
        {
            throw new TorrentNotFoundException(t.Hash);
        }

        await t.StopAsync(cancellationToken).ConfigureAwait(false);

        if (options.HasFlag(RemoveOptions.DeleteFiles))
        {
            try
            {
                await t.FilesInternal.DeleteFilesAsync(cancellationToken).ConfigureAwait(false);
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

        if (_registry.Remove(t.Hash, out _))
        {
            _bandwidth.RemoveTorrentChannels(t);
            _alerts.TorrentAlert(AlertId.TorrentRemoved, t);
        }

        // Delete from session persistence if enabled
        if (_sessionManager != null)
        {
            try
            {
                await _sessionManager.DeleteAsync(t.Hash, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete persisted torrent {Hash}", t.Hash);
            }
        }

        await RebalanceQueueAsync(_queueCts?.Token ?? default).ConfigureAwait(false);

        await t.DisposeAsync().ConfigureAwait(false);
    }

    #endregion New Async API
}
