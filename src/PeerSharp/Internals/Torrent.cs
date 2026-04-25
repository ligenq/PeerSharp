using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.PiecePicking;
using PeerSharp.Internals.Seeding;
using PeerSharp.Streaming;
using PeerSharp.Internals.Trackers;
using PeerSharp.Internals.Utilities;
using PeerSharp.Internals.Utp;
using System.Text.Json;

namespace PeerSharp.Internals;

internal sealed class Torrent : ITorrent, IPeerTransportHost, IAsyncDisposable, IFileSelectionObserver
{
    internal int _lastReportedDownloadSpeed;
    internal int _lastReportedUploadSpeed;
    private readonly IFileSelectionManager _fileSelectionManager;
    private readonly ILogger<Torrent> _logger = TorrentLoggerFactory.CreateLogger<Torrent>();

    // State
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private readonly ITimer _timer;

    private long _activityTimeTicks;

    private AtomicDisposal _disposal = new();

    private bool _finishedEventFired;

    // Progress tracking
    private float _lastReportedProgress = -1f;

    private TorrentState _previousState = TorrentState.Stopped;

    private TimeSpan _seededTime;

    private DateTimeOffset? _seedStartedAt;

    private bool _selectionFinishedEventFired;

    private int _started;

    private int _stopping;

    private int _timerTickCount;

    private readonly List<IPeerTransport> _peerTransports = new();
    private readonly Lock _peerTransportsLock = new();

    private Torrent(
            TorrentFileMetadata infoFile,
            Settings settings,
            TorrentServices services,
            IFileSelectionManager fileSelectionManager)
    {
        InfoFile = infoFile;
        Settings = settings;
        Services = services;
        Configuration = new TorrentConfiguration(this, services.Bandwidth);
        _fileSelectionManager = fileSelectionManager;
        _fileSelectionManager.SetObserver(this);
        TimeAdded = Services.TimeProvider.GetUtcNow();
        _timer = Services.TimeProvider.CreateTimer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public IAlertsManager Alerts => Services.Alerts;

    // Service Passthrough (for internal use mostly)
    public IBandwidthManager Bandwidth => Services.Bandwidth;

    public IpBlocklist? Blocklist { get => Network.Blocklist; set => Network.Blocklist = value; }

    public TorrentConfiguration Configuration { get; }

    public long DataLeft
    {
        get
        {
            if (!HasMetadata)
            {
                return 1;
            }

            long finished = (long)Math.Min(FinishedBytes, (ulong)long.MaxValue);
            return Math.Max(0, InfoFile.Info.FullSize - finished);
        }
    }

    public long DataDownloaded => TotalDownloaded;

    public long DataUploaded => TotalUploaded;

    // Public Facade Properties
    public IDhtManager? DhtManager { get => Network.Dht; set => Network.Dht = value; }

    public int DownloadLimitBytesPerSecond { get => Configuration.DownloadLimitBytesPerSecond; set => Configuration.DownloadLimitBytesPerSecond = value; }

    public int DiskReadLimitBytesPerSecond { get => Configuration.DiskReadLimitBytesPerSecond; set => Configuration.DiskReadLimitBytesPerSecond = value; }

    public int DiskWriteLimitBytesPerSecond { get => Configuration.DiskWriteLimitBytesPerSecond; set => Configuration.DiskWriteLimitBytesPerSecond = value; }

    // Configuration Passthrough
    public DownloadStrategy DownloadStrategy
    {
        get => Streaming?.DownloadStrategy ?? DownloadStrategy.RarestFirst;
        set
        {
            Streaming?.DownloadStrategy = value;
            Configuration.DownloadStrategy = value;
        }
    }

    public ITorrentEvents? Events { get; set; }

    // File selection API
    public int FileCount => InfoFile.Info.GetVisibleFileCount();

    public IFiles Files => FilesInternal;

    public IFileTransfer FileTransfer => FileTransferInternal;

    public bool Finished => Pieces?.ReceivedCount == Pieces?.Count;

    public ulong FinishedBytes => GetFinishedBytes();

    public ulong FinishedSelectedBytes => _fileSelectionManager.CalculateFinishedSelectedBytes();

    public InfoHash Hash => InfoFile.Info.Hash;

    public InfoHash HashV2 => InfoFile.Info.HashV2;

    public bool HasMetadata => (InfoFile.Info.Pieces?.Count > 0 && InfoFile.Info.FullSize > 0)
                                 || (InfoFile.Info.IsMerkle && InfoFile.Info.FullSize > 0);

    public bool HasStreamableFiles => Streaming.HasStreamableFiles;

    // Core Components
    public TorrentFileMetadata InfoFile { get; set; }

    public Exception? LastException { get; private set; }
    public TorrentStateData LocalState { get; set; } = new();
    public ILsdManager? LsdManager { get => Network.Lsd; set => Network.Lsd = value; }
    public MerkleTreeSha1? MerkleTree { get; private set; }

    public IMetadataDownload? MetadataDownload
    {
        get => MetadataDownloadInternal;
        set => MetadataDownloadInternal = (MetadataDownload?)value;
    }

    public string Name => InfoFile.Info.Name;
    public IPeers Peers => PeersInternal;
    public ReadOnlyMemory<byte> PeerId => Settings.PeerId;
    public int PieceCount => Pieces?.Count ?? 0;

    // Subsystems
    public PiecesProgress Pieces { get; private set; } = null!;

    public uint PieceSize => InfoFile.Info.PieceSize;
    public int PiecesReceived => Pieces?.ReceivedCount ?? 0;

    public float Progress
    {
        get
        {
            if (Pieces == null)
            {
                return 0.0f;
            }

            float progress = Pieces.Progress;
            if (InfoFile.Info.PieceSize > 0 && Pieces.Count > 0)
            {
                progress += (FileTransferInternal?.GetUnfinishedBytes() ?? 0) / (float)InfoFile.Info.PieceSize / Pieces.Count;
            }
            if (progress > 1.0f)
            {
                progress = 1.0f;
            }

            return progress;
        }
    }

    public bool QueueAutoStart { get => Configuration.QueueAutoStart; set => Configuration.QueueAutoStart = value; }
    public int QueuePriority { get => Configuration.QueuePriority; set => Configuration.QueuePriority = value; }
    public float? RatioLimit { get => Configuration.RatioLimit; set => Configuration.RatioLimit = value; }
    public TimeSpan? SeedTimeLimit { get => Configuration.SeedTimeLimit; set => Configuration.SeedTimeLimit = value; }
    public bool SelectionFinished => _fileSelectionManager.IsSelectionFinished;
    public float SelectionProgress => _fileSelectionManager.CalculateSelectionProgress();
    public Settings Settings { get; }
    public bool Started => Interlocked.CompareExchange(ref _started, 0, 0) == 1;

    public TorrentState State
    {
        get
        {
            if (FilesInternal?.Checking == true)
            {
                return TorrentState.CheckingFiles;
            }

            // Atomic reads for thread-safe state checks
            bool stopping = Interlocked.CompareExchange(ref _stopping, 0, 0) == 1;
            if (stopping)
            {
                return TorrentState.Stopping;
            }

            bool started = Interlocked.CompareExchange(ref _started, 0, 0) == 1;
            if (!started)
            {
                return TorrentState.Stopped;
            }

            if (MetadataDownloadInternal?.Finished == false)
            {
                return TorrentState.DownloadingMetadata;
            }

            return TorrentState.Active;
        }
    }

    public DateTimeOffset StateTimestamp => new DateTimeOffset(Interlocked.Read(ref _activityTimeTicks), TimeSpan.Zero);
    public IReadOnlyList<int> StreamableFileIndices => Streaming.StreamableFileIndices;
    public SuperSeedManager SuperSeedManager { get; private set; } = null!;
    public DateTimeOffset TimeAdded { get; set; }
    public long TotalSize => InfoFile.Info.FullSize;
    public TrackerManager TrackerManager { get; private set; } = null!;
    public ITrackers Trackers => TrackerManager;
    public int UploadLimitBytesPerSecond { get => Configuration.UploadLimitBytesPerSecond; set => Configuration.UploadLimitBytesPerSecond = value; }
    public IUtpManager? UtpManager { get => Network.Utp; set => Network.Utp = value; }
    public WebSeedManager? WebSeedManager { get; private set; }

    // Internal Modules
    internal Files FilesInternal { get; private set; } = null!;

    internal FileTransfer FileTransferInternal { get; private set; } = null!;
    internal MetadataDownload? MetadataDownloadInternal { get; private set; }
    internal TorrentNetworkManager Network { get; } = new();
    internal PeerManager PeersInternal { get; private set; } = null!;
    internal TorrentServices Services { get; }
    internal StreamingController Streaming { get; private set; } = null!;
    internal List<int>? StreamingPriorityPieces => Streaming?.PriorityPieces;

    internal long TotalDownloaded => FileTransferInternal?.Downloader.Downloaded ?? 0;
    internal long TotalUploaded => FileTransferInternal?.Uploader.Uploaded ?? 0;

    public static Torrent Create(
        TorrentFileMetadata infoFile,
        Settings settings,
        IBandwidthManager bandwidth,
        IAlertsManager alerts,
        IFileSelectionManager fileSelectionManager,
        IPeerCommunicationFactory peerFactory,
        ITrackerFactory trackerFactory,
        IGeoIpService geoIpService,
        IFileHandleCache fileHandleCache,
        IConnectionGovernor connectionGovernor,
        TimeProvider? timeProvider = null,
        ITorrentEvents? events = null,
        TorrentResumeData? resumeData = null)
    {
        var factories = new TorrentFactories(peerFactory, trackerFactory);
        var services = new TorrentServices(bandwidth, alerts, fileHandleCache, connectionGovernor, geoIpService, factories, timeProvider ?? TimeProvider.System);
        var torrent = new Torrent(infoFile, settings, services, fileSelectionManager);
        torrent.Events = events;
        if (resumeData != null)
        {
            torrent.ApplyResumeData(resumeData);
        }
        torrent.Initialize();
        return torrent;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            try
            {
                await StopInternalAsync(true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StopInternalAsync failed during DisposeAsync");
            }

            _timer.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public async Task<int> ForceRecheckAsync(IProgress<PieceCheckProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        if (Started)
        {
            throw new InvalidOperationException("Torrent must be stopped before force recheck");
        }

        if (!HasMetadata)
        {
            throw new InvalidOperationException("Cannot recheck torrent without metadata");
        }

        FilesInternal.Checking = true;
        Alerts.TorrentAlert(AlertId.TorrentCheckStarted, this);
        FireStateChangedEvent(TorrentState.CheckingFiles);

        try
        {
            await FilesInternal.InitializeAsync(GetFileSelectionSnapshot(), cancellationToken).ConfigureAwait(false);
            await using var checker = new PieceChecker(FilesInternal, new TorrentPieceCheckerContext(this), progress);
            return await checker.CheckAllPiecesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FilesInternal.Checking = false;
            FireStateChangedEvent(TorrentState.Stopped);
        }
    }

    public IReadOnlyList<Core.TorrentFileInfo> GetAllFileInfo()
    {
        var indices = InfoFile.Info.GetVisibleFileIndices();
        var result = new Core.TorrentFileInfo[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            var file = InfoFile.Info.Files[indices[i]];
            result[i] = new Core.TorrentFileInfo(
                file.Path,
                file.Size,
                i,
                GetDownloadedBytesForFile(indices[i]));
        }
        return result;
    }

    public IReadOnlyList<FileSelection> GetAllFileSelections()
    {
        var selections = _fileSelectionManager.GetAllFileSelections();
        var indices = InfoFile.Info.GetVisibleFileIndices();
        var result = new FileSelection[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            result[i] = selections[indices[i]];
        }
        return result;
    }

    public Core.TorrentFileInfo GetFileInfo(int fileIndex)
    {
        int internalIndex = InfoFile.Info.MapVisibleIndexToInternal(fileIndex);
        var file = InfoFile.Info.Files[internalIndex];
        return new Core.TorrentFileInfo(
            file.Path,
            file.Size,
            fileIndex,
            GetDownloadedBytesForFile(internalIndex));
    }

    public FileSelection GetFileSelection(int fileIndex)
    {
        int internalIndex = InfoFile.Info.MapVisibleIndexToInternal(fileIndex);
        return _fileSelectionManager.GetFileSelection(internalIndex);
    }

    public byte[] GetPieceBitfield()
    {
        return Pieces?.ToBitfield() ?? Array.Empty<byte>();
    }

    public TorrentResumeData GetResumeData()
    {
        var state = new TorrentStateData
        {
            Pieces = Pieces.ToBitfield(),
            UnfinishedPieces = FileTransferInternal?.GetUnfinishedPiecesState() ?? new(),
            Downloaded = (ulong)(FileTransferInternal?.Downloader?.Downloaded ?? 0),
            Uploaded = (ulong)(FileTransferInternal?.Uploader?.Uploaded ?? 0),
            SeedTimeSeconds = (long)GetSeedingTime(Services.TimeProvider.GetUtcNow()).TotalSeconds,
            Started = Started,
            LastStateTime = Services.TimeProvider.GetUtcNow().ToUnixTimeSeconds(),
            AddedTime = TimeAdded.ToUnixTimeSeconds(),
            DownloadPath = FilesInternal?.DownloadPath ?? Settings.Files.DefaultDownloadPath,
            Selection = new List<FileSelection>(_fileSelectionManager.GetAllFileSelections()),
            Info =
            {
                Name = Name,
                PieceSize = InfoFile.Info.PieceSize,
                FullSize = InfoFile.Info.FullSize
            }
        };

        // Use MemoryStream instead of SerializeToUtf8Bytes to avoid
        // ArrayPool<byte>.Shared retention of large intermediate buffers
        using var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, state, PeerSharpJsonContext.Default.TorrentStateData);

        return new TorrentResumeData
        {
            Hash = Hash,
            Data = ms.ToArray(),
            Timestamp = Services.TimeProvider.GetUtcNow()
        };
    }

    public async Task OnSelectionChangedAsync(IReadOnlyList<FileSelection> selection, CancellationToken ct = default)
    {
        FileTransferInternal?.InvalidateSelection();
        if (FilesInternal != null)
        {
            await FilesInternal.UpdateFileSelectionAsync(selection, ct).ConfigureAwait(false);
        }
    }

    public Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        return Streaming.OpenStreamAsync(fileIndex, cancellationToken);
    }

    public Task AttachPeerTransportAsync(Stream stream, bool initiator, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PeersInternal.AddConnectedPeerAsync(stream, initiator, remote: null, sourceKind: PeerSourceKind.WebTorrent);
    }

    public async Task ReinitializeAfterMetadataAsync(CancellationToken ct = default)
    {
        bool wasStarted = Started;
        await StopAsync(ct).ConfigureAwait(false);
        Initialize();
        if (wasStarted)
        {
            await StartAsync(ct).ConfigureAwait(false);
        }
        else
        {
            FireAndForgetLsdAnnounce();
        }
    }

    public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        return _fileSelectionManager.SetAllFilesPriorityAsync(priority, cancellationToken);
    }

    public async Task SetDownloadPathAsync(string path)
    {
        _disposal.ThrowIfDisposed(this);

        if (Started)
        {
            throw new InvalidOperationException("Torrent must be stopped before changing download path");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or empty", nameof(path));
        }

        LocalState.DownloadPath = path;

        if (FilesInternal != null)
        {
            await FilesInternal.DisposeAsync().ConfigureAwait(false);
        }
        FilesInternal = PeerSharp.PieceWriter.Files.Create(this, Services.FileHandleCache, path);

        _logger.LogInformation("Download path changed to: {Path}", path);
    }

    public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        int internalIndex = InfoFile.Info.MapVisibleIndexToInternal(fileIndex);
        return _fileSelectionManager.SetFilePriorityAsync(internalIndex, priority, cancellationToken);
    }

    public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        int internalIndex = InfoFile.Info.MapVisibleIndexToInternal(fileIndex);
        return _fileSelectionManager.SetFileSelectionAsync(internalIndex, selection, cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Configuration.QueueAutoStart = true;
            if (Interlocked.CompareExchange(ref _started, 0, 0) == 1)
            {
                throw new InvalidOperationException($"Torrent '{Name}' is already started.");
            }

            LastException = null;

            if (FilesInternal == null)
            {
                Initialize();
            }
            else if (FilesInternal.IsDisposed)
            {
                string? downloadPath = !string.IsNullOrEmpty(LocalState.DownloadPath) ? LocalState.DownloadPath : null;
                FilesInternal = PeerSharp.PieceWriter.Files.Create(this, Services.FileHandleCache, downloadPath);
            }

            if (FilesInternal == null)
            {
                throw new TorrentException($"Failed to initialize torrent '{Name}'.", Hash);
            }

            EnsureFileTransferInitialized();

            await FilesInternal.StartAsync(GetFileSelectionSnapshot(), cancellationToken).ConfigureAwait(false);

            Interlocked.Exchange(ref _started, 1);
            Interlocked.Exchange(ref _activityTimeTicks, Services.TimeProvider.GetUtcNow().Ticks);

            _timerTickCount = 0;
            _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            await PeersInternal.StartAsync().ConfigureAwait(false);
            if (ShouldStartClassicTrackers())
            {
                await TrackerManager.StartAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Classic trackers disabled because TCP/uTP transports are disabled");
            }
            await StartPeerTransportsAsync(cancellationToken).ConfigureAwait(false);

            FireAndForgetLsdAnnounce();

            if (Network.Dht != null && !InfoFile.Info.IsPrivate)
            {
                Network.Dht.FindPeers(Hash);
                Network.Dht.Announce(Hash, Settings.Connection.TcpPort);
            }
            else if (InfoFile.Info.IsPrivate)
            {
                _logger.LogDebug("DHT disabled for private torrent {TorrentName}", Name);
            }

            if (Settings.Connection.EnableWebSeeds && InfoFile.WebSeedUrls.Count > 0)
            {
                WebSeedManager ??= new WebSeedManager(this, InfoFile.WebSeedUrls, Services.TimeProvider);
                WebSeedManager.Start();
                _logger.LogInformation("Started WebSeedManager with {UrlCount} URLs", InfoFile.WebSeedUrls.Count);
            }
            else if (!Settings.Connection.EnableWebSeeds && InfoFile.WebSeedUrls.Count > 0)
            {
                _logger.LogInformation("Web seeds disabled; ignoring {UrlCount} URLs from torrent metadata", InfoFile.WebSeedUrls.Count);
            }

            Alerts.TorrentAlert(AlertId.TorrentCheckStarted, this);
            Alerts.TorrentAlert(AlertId.TorrentStarted, this);
            FireStateChangedEvent(TorrentState.Active);

            _logger.LogInformation("Torrent {TorrentName} started", Name);
        }
        finally
        {
            _stateLock.Release();
        }

        StartSeedingTimerIfNeeded();
    }

    public void RegisterPeerTransport(IPeerTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_peerTransportsLock)
        {
            _disposal.ThrowIfDisposed(this);
            if (_peerTransports.Contains(transport))
            {
                throw new InvalidOperationException("This peer transport is already registered with this torrent.");
            }
            _peerTransports.Add(transport);
        }
    }

    private async Task StartPeerTransportsAsync(CancellationToken cancellationToken)
    {
        IPeerTransport[] snapshot;
        lock (_peerTransportsLock)
        {
            snapshot = _peerTransports.ToArray();
        }

        foreach (var transport in snapshot)
        {
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopPeerTransportsAsync(bool disposing, CancellationToken cancellationToken)
    {
        IPeerTransport[] snapshot;
        lock (_peerTransportsLock)
        {
            snapshot = _peerTransports.ToArray();
            if (disposing)
            {
                _peerTransports.Clear();
            }
        }

        foreach (var transport in snapshot)
        {
            try
            {
                await transport.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (disposing)
                {
                    _logger.LogDebug(ex, "Peer transport StopAsync threw during dispose");
                }
                else
                {
                    _logger.LogWarning(ex, "Peer transport StopAsync threw");
                }
            }

            if (disposing)
            {
                try
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Peer transport DisposeAsync threw");
                }
            }
        }
    }

    private bool ShouldStartClassicTrackers()
    {
        bool anyClassicEnabled = Settings.Connection.EnableTcpIn
            || Settings.Connection.EnableTcpOut
            || Settings.Connection.EnableUtpIn
            || Settings.Connection.EnableUtpOut;
        if (anyClassicEnabled)
        {
            return true;
        }

        lock (_peerTransportsLock)
        {
            return _peerTransports.Count == 0;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return StopInternalAsync(false, cancellationToken);
    }

    internal void ApplyResumeData(TorrentResumeData resumeData)
    {
        try
        {
            var state = JsonSerializer.Deserialize(resumeData.Data, PeerSharpJsonContext.Default.TorrentStateData);
            if (state != null)
            {
                LocalState = state;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply resume data for {TorrentName}", Name);
        }
    }

    internal void FireErrorEvent(Exception exception)
    {
        LastException = exception;
        Events?.Error?.Invoke(this, exception);
        Alerts.TorrentErrorAlert(this, exception);
    }

    internal void FireStateChangedEvent(TorrentState newState)
    {
        if (newState == _previousState) return;

        var previousState = _previousState;
        _previousState = newState;

        Events?.StateChanged?.Invoke(this, new StateTransition
        {
            PreviousState = previousState,
            NewState = newState
        });
        Alerts.StateChangedAlert(this, previousState, newState);
    }

    internal void FireTransferStatsEvent()
    {
        if (FileTransferInternal == null) return;

        int downloadSpeed = 0;
        int uploadSpeed = 0;

        foreach (var peer in PeersInternal?.GetConnectedPeersInternal() ?? Enumerable.Empty<PeerCommunication>())
        {
            downloadSpeed += peer.DownloadSpeed;
            uploadSpeed += peer.UploadSpeed;
        }

        if (downloadSpeed == _lastReportedDownloadSpeed && uploadSpeed == _lastReportedUploadSpeed) return;

        _lastReportedDownloadSpeed = downloadSpeed;
        _lastReportedUploadSpeed = uploadSpeed;

        long downloaded = FileTransferInternal.Downloader.Downloaded;
        long uploaded = FileTransferInternal.Uploader.Uploaded;
        int connectedPeers = PeersInternal?.ConnectedCount ?? 0;

        var stats = new Interfaces.TransferStats
        {
            Downloaded = downloaded,
            Uploaded = uploaded,
            DownloadSpeed = downloadSpeed,
            UploadSpeed = uploadSpeed,
            ConnectedPeers = connectedPeers
        };

        Events?.TransferStats?.Invoke(this, stats);
        Alerts.TransferStatsAlert(this, downloaded, uploaded, downloadSpeed, uploadSpeed, connectedPeers);
    }

    internal IReadOnlyList<FileSelection> GetFileSelectionSnapshot() => _fileSelectionManager.GetAllFileSelections();

    internal double GetRatio()
    {
        long downloaded = TotalDownloaded;
        long uploaded = TotalUploaded;
        if (downloaded <= 0) return uploaded > 0 ? double.PositiveInfinity : 0.0;
        return (double)uploaded / downloaded;
    }

    internal TimeSpan GetSeedingTime(DateTimeOffset now)
    {
        UpdateSeedingTime(now);
        if (_seedStartedAt != null)
        {
            return _seededTime + (now - _seedStartedAt.Value);
        }
        return _seededTime;
    }

    internal void OnPieceVerified(int pieceIndex)
    {
        _fileSelectionManager.OnPieceVerified(pieceIndex);

        int completedPieces = Pieces.ReceivedCount;
        int totalPieces = Pieces.Count;

        Streaming.OnPieceVerified(pieceIndex);
        FirePieceCompletedEvent(pieceIndex, completedPieces, totalPieces);

        float currentProgress = Progress;
        if (ShouldReportProgress(currentProgress))
        {
            _lastReportedProgress = currentProgress;
            FireProgressChangedEvent();
        }

        if (!_finishedEventFired && Finished)
        {
            _finishedEventFired = true;
            StartSeedingTimerIfNeeded();
            FireFinishedEvent(false);
            Alerts.TorrentAlert(AlertId.TorrentFinished, this);
        }
        else if (!_selectionFinishedEventFired && SelectionFinished)
        {
            _selectionFinishedEventFired = true;
            FireFinishedEvent(true);
        }
    }

    internal void UpdateSeedingTime(DateTimeOffset now)
    {
        bool seeding = Started && Finished;
        if (seeding)
        {
            _seedStartedAt ??= now;
            return;
        }

        if (_seedStartedAt != null)
        {
            _seededTime += now - _seedStartedAt.Value;
            _seedStartedAt = null;
        }
    }

    private void ApplyLoadedState()
    {
        if (LocalState.Pieces?.Length > 0)
        {
            Pieces.FromBitfield(LocalState.Pieces);
        }

        if (LocalState.UnfinishedPieces != null)
        {
            FileTransferInternal?.LoadUnfinishedPiecesState(LocalState.UnfinishedPieces);
            // Free the large byte[] data now that it's been copied into PieceState blocks
            LocalState.UnfinishedPieces.Clear();
        }

        _fileSelectionManager.Initialize(LocalState.Selection, Pieces);

        TimeAdded = DateTimeOffset.FromUnixTimeSeconds(LocalState.AddedTime == 0 ? Services.TimeProvider.GetUtcNow().ToUnixTimeSeconds() : LocalState.AddedTime);
        _seededTime = TimeSpan.FromSeconds(LocalState.SeedTimeSeconds);
    }

    private void EnsureFileTransferInitialized()
    {
        if (FileTransferInternal?.IsDisposed != false)
        {
            FileTransferInternal = new FileTransfer(this, Services.TimeProvider);
            _fileSelectionManager.SetBytesProvider(FileTransferInternal);
        }
    }

    private void FireAndForgetLsdAnnounce()
    {
        var lsd = Network.Lsd;
        if (lsd == null) return;

        var announceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = lsd.AnnounceAsync(Hash, announceCts.Token).ContinueWith(t =>
        {
            announceCts.Dispose();
            if (t.IsFaulted && t.Exception != null)
            {
                _logger.LogDebug(t.Exception, "LSD announce failed for {TorrentName}", Name);
            }
        }, TaskScheduler.Default);
    }

    private void FireFinishedEvent(bool selectionOnly)
    {
        Events?.Finished?.Invoke(this, selectionOnly);
    }

    private void FirePieceCompletedEvent(int pieceIndex, int completedPieces, int totalPieces)
    {
        Events?.PieceCompleted?.Invoke(this, new PieceProgress
        {
            PieceIndex = pieceIndex,
            CompletedPieces = completedPieces,
            TotalPieces = totalPieces
        });
        Alerts.PieceCompletedAlert(this, pieceIndex, completedPieces, totalPieces);
    }

    private void FireProgressChangedEvent()
    {
        var progressInfo = new DownloadProgress
        {
            Progress = Progress,
            SelectionProgress = SelectionProgress,
            FinishedBytes = FinishedBytes,
            TotalBytes = (ulong)TotalSize,
            CompletedPieces = PiecesReceived,
            TotalPieces = PieceCount
        };

        Events?.ProgressChanged?.Invoke(this, progressInfo);
        Alerts.ProgressChangedAlert(this, progressInfo.Progress, progressInfo.SelectionProgress,
            progressInfo.FinishedBytes, progressInfo.TotalBytes, progressInfo.CompletedPieces, progressInfo.TotalPieces);
    }

    private long GetDownloadedBytesForFile(int fileIndex)
    {
        if (Pieces == null || fileIndex < 0 || fileIndex >= InfoFile.Info.Files.Count) return 0;

        var file = InfoFile.Info.Files[fileIndex];
        var (firstPiece, lastPiece) = InfoFile.Info.GetPieceRangeForFile(fileIndex);
        if (firstPiece == -1) return 0;

        long downloaded = 0;
        uint pieceSize = InfoFile.Info.PieceSize;
        long fullSize = InfoFile.Info.FullSize;

        for (int i = firstPiece; i <= lastPiece; i++)
        {
            if (Pieces.HasPiece(i))
            {
                long pieceStart = i * pieceSize;
                long pieceEnd = pieceStart + pieceSize;
                if (pieceEnd > fullSize) pieceEnd = fullSize;

                long overlapStart = Math.Max(pieceStart, file.Offset);
                long overlapEnd = Math.Min(pieceEnd, file.Offset + file.Size);

                if (overlapEnd > overlapStart)
                {
                    downloaded += overlapEnd - overlapStart;
                }
            }
        }

        if (FileTransferInternal != null)
        {
            downloaded += FileTransferInternal.GetUnfinishedBytesForFile(fileIndex);
        }

        return downloaded;
    }

    private ulong GetFinishedBytes()
    {
        if (Pieces == null || InfoFile.Info.PieceSize == 0)
        {
            return (ulong)(FileTransferInternal?.GetUnfinishedBytes() ?? 0);
        }

        ulong completedBytes = (ulong)Pieces.ReceivedCount * InfoFile.Info.PieceSize;
        if (Pieces.Count > 0 && Pieces.HasPiece(Pieces.Count - 1))
        {
            long lastPieceSize = InfoFile.Info.FullSize % InfoFile.Info.PieceSize;
            if (lastPieceSize > 0)
            {
                completedBytes -= (ulong)(InfoFile.Info.PieceSize - lastPieceSize);
            }
        }

        return completedBytes + (ulong)(FileTransferInternal?.GetUnfinishedBytes() ?? 0);
    }

    private void Initialize()
    {
        // Create Files with persisted download path if available (from applied resume data)
        string? downloadPath = !string.IsNullOrEmpty(LocalState.DownloadPath) ? LocalState.DownloadPath : null;

        FilesInternal = PeerSharp.PieceWriter.Files.Create(this, Services.FileHandleCache, downloadPath);
        Streaming = new StreamingController(this, Services.TimeProvider);

        PeersInternal = new PeerManager(this, Services.GeoIp, Services.PeerFactory, Services.TimeProvider, Services.ConnectionGovernor);
        TrackerManager = new TrackerManager(this, Services.TrackerFactory, Services.TimeProvider);

        int piecesCount = 0;
        if (InfoFile.Info.PieceSize > 0)
        {
            piecesCount = (int)((InfoFile.Info.FullSize + InfoFile.Info.PieceSize - 1) / InfoFile.Info.PieceSize);
        }

        Pieces = new PiecesProgress(piecesCount);
        FileTransferInternal = new FileTransfer(this, Services.TimeProvider);
        _fileSelectionManager.SetBytesProvider(FileTransferInternal);
        SuperSeedManager = new SuperSeedManager(this);

        // BEP 30: Initialize Merkle tree for Merkle hash torrents
        if (InfoFile.Info.IsMerkle && InfoFile.Info.MerkleRootHash != null)
        {
            MerkleTree = new MerkleTreeSha1(piecesCount, InfoFile.Info.MerkleRootHash);
            _logger.LogInformation("BEP 30: Initialized Merkle tree for {TorrentName} with {Pieces} pieces", Name, piecesCount);
        }

        ApplyLoadedState();

        if (InfoFile.AnnounceTiers.Count > 0)
        {
            TrackerManager.AddTrackers(InfoFile.AnnounceTiers);
        }
        else
        {
            if (!string.IsNullOrEmpty(InfoFile.Announce))
            {
                TrackerManager.AddTracker(InfoFile.Announce);
            }

            foreach (var t in InfoFile.AnnounceList)
            {
                TrackerManager.AddTracker(t);
            }
        }

        if (InfoFile.InfoBytes?.Length > 0)
        {
            MetadataDownloadInternal = new MetadataDownload(this);
            MetadataDownloadInternal.SetMetadata(InfoFile.InfoBytes);
        }

        Alerts.TorrentAlert(AlertId.TorrentAdded, this);
    }

    private void OnTimerTick(object? state)
    {
        try
        {
            FileTransferInternal?.Update();
            MetadataDownloadInternal?.Update();
            FireTransferStatsEvent();

            _timerTickCount++;
            if (_timerTickCount >= 30)
            {
                _timerTickCount = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in torrent timer tick");
        }
    }

    private bool ShouldReportProgress(float currentProgress)
    {
        if (_lastReportedProgress < 0) return true;
        return (currentProgress - _lastReportedProgress) >= 0.01f;
    }

    private void StartSeedingTimerIfNeeded()
    {
        if (Started && Finished)
        {
            _seedStartedAt ??= Services.TimeProvider.GetUtcNow();
        }
    }

    private async Task StopInternalAsync(bool disposing, CancellationToken ct = default)
    {
        if (!disposing)
        {
            _disposal.ThrowIfDisposed(this);
        }

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.CompareExchange(ref _started, 0, 0) == 0 && !disposing)
            {
                return;
            }

            Interlocked.Exchange(ref _stopping, 1);
            FireStateChangedEvent(TorrentState.Stopping);

            UpdateSeedingTime(Services.TimeProvider.GetUtcNow());

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Interlocked.Exchange(ref _started, 0);

            try
            {
                if (PeersInternal != null)
                {
                    await PeersInternal.StopAsync().ConfigureAwait(false);
                }
                if (TrackerManager != null)
                {
                    await TrackerManager.StopAsync().ConfigureAwait(false);
                }
                await StopPeerTransportsAsync(disposing, ct).ConfigureAwait(false);
                if (WebSeedManager != null)
                {
                    await WebSeedManager.DisposeAsync().ConfigureAwait(false);
                }
                if (FileTransferInternal != null)
                {
                    await FileTransferInternal.DisposeAsync().ConfigureAwait(false);
                }
                if (disposing && FilesInternal != null)
                {
                    await FilesInternal.StopAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _stopping, 0);
                Interlocked.Exchange(ref _activityTimeTicks, Services.TimeProvider.GetUtcNow().Ticks);
            }

            Alerts.TorrentAlert(AlertId.TorrentStopped, this);
            FireStateChangedEvent(TorrentState.Stopped);

            _logger.LogInformation("Torrent {TorrentName} stopped", Name);
        }
        finally
        {
            _stateLock.Release();
        }
    }
}
