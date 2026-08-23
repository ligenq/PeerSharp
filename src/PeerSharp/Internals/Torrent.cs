using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using System.Net;
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
    internal long _lastReportedDownloadSpeed;
    internal long _lastReportedUploadSpeed;
    private readonly IFileSelectionManager _fileSelectionManager;
    private readonly ILogger<Torrent> _logger;

    // State
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly TorrentWebSeeds _webSeeds;

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

    private int _rollbackIncomplete;

    private int _stopping;

    private int _timerTickCount;

    private readonly List<IPeerTransport> _peerTransports = [];
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
        _logger = services.LoggerFactory.CreateLogger<Torrent>();
        Configuration = new TorrentConfiguration(this, services.Bandwidth);
        _webSeeds = new TorrentWebSeeds(this);
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

    public long DownloadLimitBytesPerSecond { get => Configuration.DownloadLimitBytesPerSecond; set => Configuration.DownloadLimitBytesPerSecond = value; }

    public long DownloadSpeed => Volatile.Read(ref _lastReportedDownloadSpeed);

    public long DiskReadLimitBytesPerSecond { get => Configuration.DiskReadLimitBytesPerSecond; set => Configuration.DiskReadLimitBytesPerSecond = value; }

    public long DiskWriteLimitBytesPerSecond { get => Configuration.DiskWriteLimitBytesPerSecond; set => Configuration.DiskWriteLimitBytesPerSecond = value; }

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

    /// <summary>
    /// Whether every piece has been received.
    ///
    /// <para>
    /// The metadata check is not redundant. A magnet has no piece count until its metadata arrives, so
    /// "received every piece" is satisfied by an empty collection and a torrent that has not yet
    /// learned what it is downloading would report itself finished - at the one moment it wants the
    /// swarm most. Every caller reading this as "wants nothing further" got the opposite of the truth,
    /// and the connection manager acted on it: it marked the seeds holding the metadata as peers worth
    /// nothing and stopped dialling them.
    /// </para>
    /// </summary>
    public bool Finished => HasMetadata && Pieces?.ReceivedCount == Pieces?.Count;

    public ulong FinishedBytes => GetFinishedBytes();

    public ulong FinishedSelectedBytes => _fileSelectionManager.CalculateFinishedSelectedBytes();

    public InfoHash Hash => InfoFile.Info.Hash;

    public InfoHash HashV2 => InfoFile.Info.HashV2;

    public bool HasMetadata => (InfoFile.Info.Pieces?.Count > 0 && InfoFile.Info.FullSize > 0)
                                 || (InfoFile.Info.IsMerkle && InfoFile.Info.FullSize > 0);

    public bool HasSameIdentity(ITorrent? other)
    {
        if (other == null)
        {
            return false;
        }

        return (!Hash.IsEmpty && !other.Hash.IsEmpty && Hash == other.Hash)
            || (!HashV2.IsEmpty && !other.HashV2.IsEmpty && HashV2 == other.HashV2);
    }

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

    public int MaxConnections
    {
        get => Configuration.MaxConnections;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Configuration.MaxConnections = value;
        }
    }

    public int MaxUploadSlots
    {
        get => Configuration.MaxUploadSlots;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Configuration.MaxUploadSlots = value;
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

    public long UploadSpeed => Volatile.Read(ref _lastReportedUploadSpeed);

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
            bool rollbackIncomplete = Interlocked.CompareExchange(ref _rollbackIncomplete, 0, 0) == 1;
            if (stopping || rollbackIncomplete)
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

    public DateTimeOffset StateTimestamp => new(Interlocked.Read(ref _activityTimeTicks), TimeSpan.Zero);
    public IReadOnlyList<int> StreamableFileIndices => Streaming.StreamableFileIndices;
    public SuperSeedManager SuperSeedManager { get; private set; } = null!;

    public bool SuperSeeding
    {
        get => Configuration.SuperSeeding;
        set
        {
            Configuration.SuperSeeding = value;

            // The manager is null until Initialize runs and is replaced when a magnet's metadata
            // arrives, so the configuration is the authority and this only mirrors it.
            if (SuperSeedManager is not null)
            {
                SuperSeedManager.Enabled = value;
            }
        }
    }
    public DateTimeOffset TimeAdded { get; set; }
    public long TotalSize => InfoFile.Info.FullSize;
    public TrackerManager TrackerManager { get; private set; } = null!;
    public ITrackers Trackers => TrackerManager;
    public IWebSeeds WebSeeds => _webSeeds;
    public long UploadLimitBytesPerSecond { get => Configuration.UploadLimitBytesPerSecond; set => Configuration.UploadLimitBytesPerSecond = value; }
    public IUtpManager? UtpManager { get => Network.Utp; set => Network.Utp = value; }

    /// <summary>
    /// The peer ids we have presented on outgoing connections and not yet finished with.
    ///
    /// <para>
    /// A connection that loops back to us - a tracker handing us our own address, PEX echoing us,
    /// local discovery - arrives at our listener carrying the id we just sent. Recognising it is the
    /// only reliable way to tell ourselves apart from a stranger, because the address alone does not:
    /// behind NAT our external address is not one we can compare against.
    /// </para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _outgoingPeerIds = new();

    /// <summary>Issues a fresh peer id for one outgoing connection and remembers it.</summary>
    public byte[] IssueOutgoingPeerId()
    {
        var id = ProtocolConstants.GeneratePeerId();
        _outgoingPeerIds[Convert.ToHexString(id)] = 0;
        return id;
    }

    /// <summary>Forgets an issued id once its connection is done, so the set cannot grow unbounded.</summary>
    public void ReleaseOutgoingPeerId(byte[]? id)
    {
        if (id is { Length: 20 })
        {
            _outgoingPeerIds.TryRemove(Convert.ToHexString(id), out _);
        }
    }

    /// <summary>Whether this id is one we presented on an outgoing connection, meaning it is us.</summary>
    public bool IsOurOutgoingPeerId(ReadOnlySpan<byte> id)
    {
        return id.Length == 20 && _outgoingPeerIds.ContainsKey(Convert.ToHexString(id));
    }

    /// <summary>Where we accept peer connections, or null when we are not listening.</summary>
    public IPortListener? PortListener { get => Network.PortListener; set => Network.PortListener = value; }

    /// <summary>
    /// BEP 10 <c>yourip</c>: one peer's opinion of our external address.
    ///
    /// <para>
    /// Joins the same vote pool as the DHT's own reports and BEP 24 tracker replies rather than being
    /// believed outright. A single peer is not evidence - it can be misconfigured, behind a different
    /// view of the network, or lying deliberately to move our node ID - and the existing threshold is
    /// exactly the mechanism for that. Silently ignored when DHT is off, since the vote pool is where
    /// the answer is kept.
    /// </para>
    /// </summary>
    public void ReportExternalAddress(ReadOnlySpan<byte> addressBytes)
    {
        if (DhtManager is not { } dht)
        {
            return;
        }

        // Length was checked by the caller, but a malformed address still throws rather than parsing.
        IPAddress parsed;
        try
        {
            parsed = new IPAddress(addressBytes);
        }
        catch (ArgumentException)
        {
            return;
        }

        dht.ReportExternalIp(parsed);
    }
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
        TorrentResumeData? resumeData = null,
        ILoggerFactory? loggerFactory = null)
    {
        var factories = new TorrentFactories(peerFactory, trackerFactory, loggerFactory ?? NullLoggerFactory.Instance);
        var services = new TorrentServices(bandwidth, alerts, fileHandleCache, connectionGovernor, geoIpService, factories, timeProvider ?? TimeProvider.System);
        var torrent = new Torrent(infoFile, settings, services, fileSelectionManager)
        {
            Events = events
        };
        if (resumeData != null)
        {
            torrent.ApplyResumeData(resumeData);
        }
        torrent.Initialize();
        if (torrent.HasMetadata)
        {
            torrent._metadataApplied.TrySetResult();
        }

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

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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

            EnsureFilesInitialized();

            FilesInternal.Checking = true;
            Alerts.TorrentAlert(AlertId.TorrentCheckStarted, this);
            FireStateChangedEvent(TorrentState.CheckingFiles);

            try
            {
                await FilesInternal.InitializeAsync(GetFileSelectionSnapshot(), cancellationToken).ConfigureAwait(false);
                await using var checker = new PieceChecker(FilesInternal, new TorrentPieceCheckerContext(this), progress, Services.LoggerFactory);
                return await checker.CheckAllPiecesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                FilesInternal.Checking = false;
                FireStateChangedEvent(TorrentState.Stopped);
            }
        }
        finally
        {
            _stateLock.Release();
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
        return Pieces?.ToBitfield() ?? [];
    }

    public TorrentResumeData GetResumeData()
    {
        var state = new TorrentStateData
        {
            Pieces = Pieces.ToBitfield(),
            UnfinishedPieces = FileTransferInternal?.GetUnfinishedPiecesState() ?? [],
            Downloaded = (ulong)(FileTransferInternal?.Downloader?.Downloaded ?? 0),
            Uploaded = (ulong)(FileTransferInternal?.Uploader?.Uploaded ?? 0),
            SeedTimeSeconds = (long)GetSeedingTime(Services.TimeProvider.GetUtcNow()).TotalSeconds,
            Started = Started,
            LastStateTime = Services.TimeProvider.GetUtcNow().ToUnixTimeSeconds(),
            AddedTime = TimeAdded.ToUnixTimeSeconds(),
            DownloadPath = FilesInternal?.DownloadPath ?? Settings.Files.DefaultDownloadPath,
            Selection = [.. _fileSelectionManager.GetAllFileSelections()],
            RenamedFiles = [.. LocalState.RenamedFiles],
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
            // UpdateFileSelectionAsync is itself a no-op when disposed; that avoids a TOCTOU race
            // between a separate IsDisposed check here and a concurrent shutdown.
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
        _disposal.ThrowIfDisposed(this);
        ArgumentNullException.ThrowIfNull(stream);
        return PeersInternal.AddConnectedPeerAsync(stream, initiator, remote: null, sourceKind: PeerSourceKind.WebTorrent, cancellationToken);
    }

    /// <summary>
    /// BEP 53: File indices from a magnet link's "so=" parameter, waiting for metadata to
    /// arrive so they can be applied to the file selection. Null when no restriction is pending.
    /// </summary>
    internal IReadOnlyList<int>? PendingSelectOnlyFileIndices { get; set; }

    // Completed once metadata is available and applied (immediately for torrents created
    // from a .torrent file; after the post-download reinitialize for magnet torrents).
    private readonly TaskCompletionSource _metadataApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// When true, the torrent is left stopped after magnet metadata has been downloaded and
    /// applied, instead of resuming into the download. This gives applications a race-free
    /// window to preview the file list and adjust selections before calling StartAsync.
    /// Set via <see cref="AddTorrentOptions.StopAfterMetadata"/>.
    /// </summary>
    internal bool StopAfterMetadata { get; set; }

    public Task WaitForMetadataAsync(CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        return _metadataApplied.Task.WaitAsync(cancellationToken);
    }

    public Core.TorrentFile ExportTorrentFile()
    {
        _disposal.ThrowIfDisposed(this);
        if (!HasMetadata)
        {
            throw new InvalidOperationException("Metadata has not been downloaded yet. Await WaitForMetadataAsync first.");
        }

        byte[]? bytes = TorrentFileSerializer.BuildTorrentBytes(InfoFile);
        if (bytes == null || bytes.Length == 0)
        {
            throw new InvalidOperationException("Failed to serialize torrent metadata.");
        }

        return TorrentFile.Parse(bytes);
    }

    public async Task ReinitializeAfterMetadataAsync(CancellationToken ct = default)
    {
        bool wasStarted = Started;
        var peerPreferences = PeersInternal.ExportConnectionPreferences();
        IReadOnlyList<PeerCommunication> retainedPeers = [];

        // Only preserve peers when the torrent will resume. Preview mode intentionally leaves the
        // torrent stopped, so keeping live sockets there would violate its public state contract.
        bool preservePeers = wasStarted && !StopAfterMetadata;
        if (preservePeers)
        {
            retainedPeers = await PeersInternal.DetachConnectedPeersForMetadataRebuildAsync().ConfigureAwait(false);
        }

        // Not a stop, a rebuild: the info hash was verified against the metadata we just received, so
        // the tracker session is still valid and a started announce follows immediately. Sending the
        // courtesy stopped announce here would claim otherwise, and it is bounded by a timeout that one
        // unresponsive UDP tracker runs out in full - 2.5 of the 5.75 seconds a real magnet spent
        // between its last metadata byte and its first block.
        await StopInternalAsync(
            disposing: false,
            sendStoppedAnnounce: false,
            peerManagerAlreadyStopped: preservePeers,
            ct).ConfigureAwait(false);
        Initialize();
        PeersInternal.ImportConnectionPreferences(peerPreferences);
        await ApplyPendingSelectOnlyFileIndicesAsync(ct).ConfigureAwait(false);
        if (retainedPeers.Count > 0)
        {
            await PeersInternal.AdoptPeersAfterMetadataRebuildAsync(retainedPeers).ConfigureAwait(false);
        }
        if (wasStarted && !StopAfterMetadata)
        {
            await StartAsync(ct).ConfigureAwait(false);
        }
        else
        {
            FireAndForgetLsdAnnounce();
        }

        // Signal only after selections are applied and the start/stop decision is final,
        // so WaitForMetadataAsync waiters observe a settled torrent.
        if (HasMetadata)
        {
            _metadataApplied.TrySetResult();
        }
    }

    /// <summary>
    /// BEP 53: Applies a pending magnet "so=" restriction by deselecting every file whose
    /// index is not in the requested set. No-ops until metadata is available. A file
    /// selection restored from resume data reflects an explicit user choice and wins over
    /// the magnet link's restriction.
    /// </summary>
    internal async Task ApplyPendingSelectOnlyFileIndicesAsync(CancellationToken ct = default)
    {
        var indices = PendingSelectOnlyFileIndices;
        if (indices == null || !HasMetadata)
        {
            return;
        }

        PendingSelectOnlyFileIndices = null;

        if (indices.Count == 0)
        {
            return;
        }

        if (LocalState.Selection is { Count: > 0 })
        {
            _logger.LogDebug("BEP 53: Ignoring magnet 'so=' selection - resume data already defines a file selection");
            return;
        }

        int fileCount = InfoFile.Info.GetVisibleFileCount();
        var selected = new HashSet<int>(indices);
        int deselected = 0;
        for (int i = 0; i < fileCount; i++)
        {
            if (!selected.Contains(i))
            {
                await SetFilePriorityAsync(i, Priority.DoNotDownload, ct).ConfigureAwait(false);
                deselected++;
            }
        }

        _logger.LogInformation("BEP 53: Magnet 'so=' selection applied - downloading {Selected} of {Total} files", fileCount - deselected, fileCount);
    }

    public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        return _fileSelectionManager.SetAllFilesPriorityAsync(priority, cancellationToken);
    }

    public async Task SetDownloadPathAsync(string path, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        // The lock can be held by a long recheck or a stop that is flushing the block cache, so
        // the wait has to be cancellable - otherwise callers have no way out of it at all.
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
            FilesInternal = PieceWriter.Files.Create(this, Services.FileHandleCache, Services.LoggerFactory, path);

            _logger.LogInformation("Download path changed to: {Path}", path);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// The renames, keyed by the internal file index Storage works in. Null when there are none, so
    /// the common case costs no dictionary.
    /// </summary>
    internal IReadOnlyDictionary<int, string>? GetRenamedFileMap()
    {
        var renamed = LocalState.RenamedFiles;
        if (renamed.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<int, string>(renamed.Count);
        foreach (var entry in renamed)
        {
            map[entry.Index] = entry.Path;
        }

        return map;
    }

    public IReadOnlyDictionary<int, string> GetRenamedFiles()
    {
        var result = new Dictionary<int, string>();
        foreach (var entry in LocalState.RenamedFiles)
        {
            if (InfoFile.Info.TryMapInternalIndexToVisible(entry.Index, out int visibleIndex))
            {
                result[visibleIndex] = entry.Path;
            }
        }

        return result;
    }

    public async Task RenameFileAsync(int fileIndex, string newPath, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        if (!HasMetadata)
        {
            throw new InvalidOperationException("Files cannot be renamed before the torrent's metadata is known");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(fileIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fileIndex, FileCount);

        string normalized = newPath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 ||
            Path.IsPathRooted(newPath) ||
            normalized.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "A file name must be relative to the torrent's download path and may not walk outside it.",
                nameof(newPath));
        }

        int internalIndex = InfoFile.Info.MapVisibleIndexToInternal(fileIndex);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _disposal.ThrowIfDisposed(this);

            if (Started)
            {
                throw new InvalidOperationException("Torrent must be stopped before renaming a file");
            }

            var renamed = LocalState.RenamedFiles;
            int existing = renamed.FindIndex(r => r.Index == internalIndex);
            if (existing >= 0)
            {
                renamed[existing].Path = normalized;
            }
            else
            {
                renamed.Add(new PieceWriter.TorrentStateData.RenamedFileData
                {
                    Index = internalIndex,
                    Path = normalized
                });
            }

            // Storage assigns paths at construction, so the rename only takes effect on a rebuild. It
            // carries the data across on the way, otherwise the torrent would start again looking for
            // a file that is still sitting under its old name.
            if (FilesInternal != null)
            {
                string downloadPath = FilesInternal.DownloadPath;
                await FilesInternal.RenameFileAsync(internalIndex, normalized, cancellationToken).ConfigureAwait(false);
                await FilesInternal.DisposeAsync().ConfigureAwait(false);
                FilesInternal = PieceWriter.Files.Create(this, Services.FileHandleCache, Services.LoggerFactory, downloadPath);
            }

            _logger.LogInformation("File {FileIndex} renamed to {NewPath}", fileIndex, normalized);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    internal bool TryGetPiecePriority(int pieceIndex, out Priority priority)
    {
        var overrides = Configuration.PiecePriorities;
        if (overrides.IsEmpty)
        {
            priority = default;
            return false;
        }

        return overrides.TryGetValue(pieceIndex, out priority);
    }

    public void SetPiecePriority(int pieceIndex, Priority priority)
    {
        _disposal.ThrowIfDisposed(this);

        if (!HasMetadata)
        {
            throw new InvalidOperationException("Piece priorities cannot be set before the torrent's metadata is known");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(pieceIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pieceIndex, PieceCount);

        Configuration.PiecePriorities[pieceIndex] = priority;
    }

    public Priority GetPiecePriority(int pieceIndex)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentOutOfRangeException.ThrowIfNegative(pieceIndex);

        if (TryGetPiecePriority(pieceIndex, out var overridden))
        {
            return overridden;
        }

        return InfoFile.Info.GetPiecePriority(pieceIndex, GetFileSelectionSnapshot());
    }

    public void ClearPiecePriorities()
    {
        _disposal.ThrowIfDisposed(this);
        Configuration.PiecePriorities.Clear();
    }

    public async Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);

        if (!HasMetadata)
        {
            throw new InvalidOperationException("Pieces cannot be read before the torrent's metadata is known");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(pieceIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pieceIndex, PieceCount);

        if (!Pieces.HasPiece(pieceIndex))
        {
            throw new InvalidOperationException(
                $"Piece {pieceIndex} has not been downloaded and verified yet.");
        }

        var files = FilesInternal ?? throw new InvalidOperationException(
            "The torrent's storage is not open. Start the torrent before reading pieces from it.");

        long offset = (long)pieceIndex * InfoFile.Info.PieceSize;
        int length = (int)InfoFile.Info.GetPieceSize(pieceIndex);

        return await files.ReadAsync(offset, length, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveStorageAsync(string path, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _disposal.ThrowIfDisposed(this);

            if (Started)
            {
                throw new InvalidOperationException("Torrent must be stopped before moving its storage");
            }

            if (FilesInternal != null)
            {
                // Move first, then repoint. The other order would leave the data unreachable if the
                // move failed, which is the situation this method exists to avoid.
                await FilesInternal.MoveFilesAsync(path, cancellationToken).ConfigureAwait(false);
                await FilesInternal.DisposeAsync().ConfigureAwait(false);
            }

            LocalState.DownloadPath = path;
            FilesInternal = PieceWriter.Files.Create(this, Services.FileHandleCache, Services.LoggerFactory, path);

            _logger.LogInformation("Storage moved to: {Path}", path);
        }
        finally
        {
            _stateLock.Release();
        }
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
            if (Interlocked.CompareExchange(ref _rollbackIncomplete, 0, 0) == 1)
            {
                throw new InvalidOperationException(
                    $"Torrent '{Name}' did not finish rolling back its previous start. Stop it before starting again.");
            }

            LastException = null;

            if (FilesInternal == null)
            {
                Initialize();
            }
            EnsureFilesInitialized();

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

            // Past this point the torrent already reports Started, so any failure - including
            // cancellation from a peer transport that honours the token - must roll the state
            // back. Otherwise the caller sees a failed StartAsync on a torrent that claims to be
            // active, and every retry throws "already started".
            try
            {
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
                    var dhtHash = InfoFile.Info.GetTrackerInfoHash();
                    Network.Dht.FindPeers(dhtHash);
                    Network.Dht.Announce(dhtHash, Settings.Connection.TcpPort);
                }
                else if (InfoFile.Info.IsPrivate)
                {
                    _logger.LogDebug("DHT disabled for private torrent {TorrentName}", Name);
                }

                var webSeedUrls = _webSeeds.GetAll();
                if (Settings.Connection.EnableWebSeeds && webSeedUrls.Count > 0)
                {
                    WebSeedManager ??= new WebSeedManager(this, webSeedUrls, Services.TimeProvider, Services.LoggerFactory.CreateLogger<WebSeedManager>());
                    WebSeedManager.Start();
                    _logger.LogInformation("Started WebSeedManager with {UrlCount} URLs", webSeedUrls.Count);
                }
                else if (!Settings.Connection.EnableWebSeeds && webSeedUrls.Count > 0)
                {
                    _logger.LogInformation("Web seeds disabled; ignoring {UrlCount} URLs", webSeedUrls.Count);
                }
            }
            catch
            {
                await RollbackStartAsync().ConfigureAwait(false);
                throw;
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

    /// <summary>
    /// Undoes a partially completed <see cref="StartAsync"/>. Must be called with
    /// <see cref="_stateLock"/> held. Runs uncancellably and swallows teardown failures so the
    /// original start failure is what reaches the caller.
    /// </summary>
    private async Task RollbackStartAsync()
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _started, 0);
        Interlocked.Exchange(ref _activityTimeTicks, Services.TimeProvider.GetUtcNow().Ticks);

        bool complete = true;
        complete &= await TryTeardownAsync(
            () => StopPeerTransportsAsync(
                disposing: false,
                throwOnFailure: true,
                cancellationToken: CancellationToken.None),
            "peer transports").ConfigureAwait(false);
        complete &= await TryTeardownAsync(() => TrackerManager?.StopAsync() ?? Task.CompletedTask, "trackers").ConfigureAwait(false);
        complete &= await TryTeardownAsync(() => PeersInternal?.StopAsync() ?? Task.CompletedTask, "peers").ConfigureAwait(false);

        if (WebSeedManager is { } webSeeds)
        {
            // Cleared as well as disposed: StartAsync only creates one when the field is null,
            // so leaving a disposed instance behind would break the next start.
            WebSeedManager = null;
            complete &= await TryTeardownAsync(() => webSeeds.DisposeAsync().AsTask(), "web seeds").ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _rollbackIncomplete, complete ? 0 : 1);
        TorrentState state = complete ? TorrentState.Stopped : TorrentState.Stopping;
        FireStateChangedEvent(state);
        _logger.LogWarning(
            complete
                ? "Torrent {TorrentName} failed to start; rolled back to stopped"
                : "Torrent {TorrentName} failed to start and its rollback is incomplete",
            Name);
    }

    private async Task<bool> TryTeardownAsync(Func<Task> teardown, string what)
    {
        try
        {
            await teardown().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to roll back {Component} after a failed start of {TorrentName}", what, Name);
            return false;
        }
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
            snapshot = [.. _peerTransports];
        }

        foreach (var transport in snapshot)
        {
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopPeerTransportsAsync(
        bool disposing,
        bool throwOnFailure,
        CancellationToken cancellationToken)
    {
        IPeerTransport[] snapshot;
        List<Exception>? failures = throwOnFailure ? [] : null;
        lock (_peerTransportsLock)
        {
            snapshot = [.. _peerTransports];
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
                failures?.Add(ex);
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

        if (failures is { Count: > 0 })
        {
            throw new AggregateException("One or more peer transports failed to stop.", failures);
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

    /// <summary>
    /// The highest <see cref="TorrentStateData.Version"/> this build understands. Resume data
    /// written by a newer version is discarded rather than half-read: the fields we recognise may
    /// no longer mean what they did, and a bitfield interpreted under the wrong rules is worse than
    /// no bitfield at all.
    /// </summary>
    private const uint SupportedResumeVersion = 1;

    internal void ApplyResumeData(TorrentResumeData resumeData)
    {
        // Identity before anything else. The geometry checks below can only catch resume data from a
        // torrent shaped differently to this one - two torrents of the same total size and piece size
        // pass every one of them, and the bitfield would then be believed, advertised and served from
        // whatever bytes happen to be on disk. An empty hash means "not recorded", which is what
        // resume data hand-built by a caller looks like.
        if (ResumeHashConflicts(resumeData.Hash))
        {
            _logger.LogWarning(
                "Discarding resume data for {TorrentName}: it was saved for torrent {SavedHash}. The torrent starts from an empty bitfield instead.",
                Name,
                resumeData.Hash);
            return;
        }

        TorrentStateData? state;
        try
        {
            state = JsonSerializer.Deserialize(resumeData.Data, PeerSharpJsonContext.Default.TorrentStateData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse resume data for {TorrentName}", Name);
            return;
        }

        if (state == null)
        {
            return;
        }

        if (!IsResumeDataUsable(state, out string? rejection))
        {
            // Starting from nothing costs a re-download. Adopting a bitfield that does not describe
            // this torrent costs correctness: the engine would claim pieces it never verified and
            // then serve them.
            _logger.LogWarning(
                "Discarding resume data for {TorrentName}: {Reason}. The torrent starts from an empty bitfield instead.",
                Name,
                rejection);
            return;
        }

        LocalState = state;
    }

    /// <summary>
    /// Whether a saved hash positively names a <em>different</em> torrent.
    ///
    /// <para>
    /// Only a genuine conflict counts. An empty saved hash means the field was not recorded, and a
    /// torrent with no hash of its own has nothing to compare against - answering "conflict" in
    /// either case would reject resume data over missing information rather than over disagreement.
    /// Either hash of a hybrid torrent identifies it, which is the rule
    /// <see cref="HasSameIdentity"/> already applies: resume data saved before the other form was
    /// known still describes the same content.
    /// </para>
    /// </summary>
    private bool ResumeHashConflicts(InfoHash savedHash)
    {
        if (savedHash.IsEmpty || (Hash.IsEmpty && HashV2.IsEmpty))
        {
            return false;
        }

        bool matches = (!Hash.IsEmpty && Hash == savedHash)
            || (!HashV2.IsEmpty && HashV2 == savedHash);

        return !matches;
    }

    /// <summary>
    /// Checks resume data against the torrent it claims to describe. The fields being compared here
    /// were written on every save and read by nothing, which is the same as not having them.
    /// </summary>
    private bool IsResumeDataUsable(TorrentStateData state, out string? reason)
    {
        if (state.Version > SupportedResumeVersion)
        {
            reason = $"it was written by a newer version (format {state.Version}, this build understands {SupportedResumeVersion})";
            return false;
        }

        // A magnet has no metadata to check against yet. Its resume data is validated when the
        // metadata arrives and the torrent is rebuilt around it.
        if (!HasMetadata)
        {
            reason = null;
            return true;
        }

        var info = InfoFile.Info;

        // Zero means "not recorded" rather than "recorded as zero": resume data from before a field
        // was written should not be thrown away for lacking it.
        if (state.Info.PieceSize != 0 && state.Info.PieceSize != info.PieceSize)
        {
            reason = $"it was saved for a {state.Info.PieceSize}-byte piece size, this torrent uses {info.PieceSize}";
            return false;
        }

        if (state.Info.FullSize != 0 && state.Info.FullSize != info.FullSize)
        {
            reason = $"it was saved for {state.Info.FullSize} bytes of content, this torrent has {info.FullSize}";
            return false;
        }

        if (state.Pieces.Length > 0)
        {
            int expectedPieces = info.PieceSize > 0
                ? (int)((info.FullSize + info.PieceSize - 1) / info.PieceSize)
                : 0;
            int expectedBytes = (expectedPieces + 7) / 8;
            if (state.Pieces.Length != expectedBytes)
            {
                reason = $"its bitfield is {state.Pieces.Length} bytes, but {expectedPieces} pieces need {expectedBytes}";
                return false;
            }
        }

        // A name change on its own cannot make the bitfield wrong, so it is worth saying out loud
        // without refusing the data over it.
        if (!string.IsNullOrEmpty(state.Info.Name) && state.Info.Name != info.Name)
        {
            _logger.LogInformation(
                "Resume data for {TorrentName} was saved under the name '{SavedName}'",
                Name,
                state.Info.Name);
        }

        reason = null;
        return true;
    }

    internal void FireErrorEvent(Exception exception)
    {
        LastException = exception;
        Events?.Error?.Invoke(this, exception);
        Alerts.TorrentErrorAlert(this, exception);
    }

    internal void FireStateChangedEvent(TorrentState newState)
    {
        if (newState == _previousState)
        {
            return;
        }

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
        if (FileTransferInternal == null)
        {
            return;
        }

        long downloadSpeed = 0;
        long uploadSpeed = 0;

        foreach (var peer in PeersInternal?.GetConnectedPeersInternal() ?? [])
        {
            downloadSpeed += peer.DownloadSpeed;
            uploadSpeed += peer.UploadSpeed;
        }

        if (downloadSpeed == _lastReportedDownloadSpeed && uploadSpeed == _lastReportedUploadSpeed)
        {
            return;
        }

        Volatile.Write(ref _lastReportedDownloadSpeed, downloadSpeed);
        Volatile.Write(ref _lastReportedUploadSpeed, uploadSpeed);

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
        if (downloaded <= 0)
        {
            return uploaded > 0 ? double.PositiveInfinity : 0.0;
        }

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
        // Second gate, and the one that catches magnets. ApplyResumeData runs before a magnet knows
        // its geometry, so it has nothing to check the bitfield against and lets it through; this
        // runs from Initialize, which is also what the metadata rebuild calls once the geometry is
        // known. Without it, resume data that would have been rejected outright for a .torrent is
        // silently trusted for the same content fetched by magnet.
        if (HasMetadata && !IsResumeDataUsable(LocalState, out string? rejection))
        {
            _logger.LogWarning(
                "Discarding resume state for {TorrentName} now that its metadata is known: {Reason}.",
                Name,
                rejection);

            // Keep only what makes no claim about piece contents. The download path is where the
            // files already are, and when the torrent was added is not a statement about them.
            LocalState = new TorrentStateData
            {
                DownloadPath = LocalState.DownloadPath,
                AddedTime = LocalState.AddedTime
            };
        }

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
            FileTransferInternal = new FileTransfer(this, Services.TimeProvider, Services.LoggerFactory);
            _fileSelectionManager.SetBytesProvider(FileTransferInternal);
        }
    }

    private void EnsureFilesInitialized()
    {
        if (FilesInternal == null || FilesInternal.IsDisposed)
        {
            string? downloadPath = !string.IsNullOrEmpty(LocalState.DownloadPath) ? LocalState.DownloadPath : null;
            FilesInternal = PieceWriter.Files.Create(this, Services.FileHandleCache, Services.LoggerFactory, downloadPath);
        }
    }

    private void FireAndForgetLsdAnnounce()
    {
        var lsd = Network.Lsd;
        if (lsd == null)
        {
            return;
        }

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
        if (Pieces == null || fileIndex < 0 || fileIndex >= InfoFile.Info.Files.Count)
        {
            return 0;
        }

        var file = InfoFile.Info.Files[fileIndex];
        var (firstPiece, lastPiece) = InfoFile.Info.GetPieceRangeForFile(fileIndex);
        if (firstPiece == -1)
        {
            return 0;
        }

        long downloaded = 0;
        uint pieceSize = InfoFile.Info.PieceSize;
        long fullSize = InfoFile.Info.FullSize;

        for (int i = firstPiece; i <= lastPiece; i++)
        {
            if (Pieces.HasPiece(i))
            {
                long pieceStart = i * pieceSize;
                long pieceEnd = pieceStart + pieceSize;
                if (pieceEnd > fullSize)
                {
                    pieceEnd = fullSize;
                }

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

        FilesInternal = PieceWriter.Files.Create(this, Services.FileHandleCache, Services.LoggerFactory, downloadPath);
        Streaming = new StreamingController(this, Services.TimeProvider, Services.LoggerFactory);

        PeersInternal = new PeerManager(this, Services.GeoIp, Services.PeerFactory, Services.TimeProvider, Services.ConnectionGovernor, Services.LoggerFactory.CreateLogger<PeerManager>());
        TrackerManager = new TrackerManager(this, Services.TrackerFactory, Services.TimeProvider, Services.LoggerFactory.CreateLogger<TrackerManager>());

        int piecesCount = 0;
        if (InfoFile.Info.PieceSize > 0)
        {
            piecesCount = (int)((InfoFile.Info.FullSize + InfoFile.Info.PieceSize - 1) / InfoFile.Info.PieceSize);
        }

        Pieces = new PiecesProgress(piecesCount);
        FileTransferInternal = new FileTransfer(this, Services.TimeProvider, Services.LoggerFactory);
        _fileSelectionManager.SetBytesProvider(FileTransferInternal);
        SuperSeedManager = new SuperSeedManager(this, Services.LoggerFactory.CreateLogger<SuperSeedManager>())
        {
            Enabled = Configuration.SuperSeeding
        };

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
                TrackerManager.AddTrackerFromMetadata(InfoFile.Announce);
            }

            foreach (var t in InfoFile.AnnounceList)
            {
                TrackerManager.AddTrackerFromMetadata(t);
            }
        }

        if (InfoFile.InfoBytes?.Length > 0)
        {
            MetadataDownloadInternal = new MetadataDownload(this, Services.LoggerFactory);
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
        if (_lastReportedProgress < 0)
        {
            return true;
        }

        return (currentProgress - _lastReportedProgress) >= 0.01f;
    }

    private void StartSeedingTimerIfNeeded()
    {
        if (Started && Finished)
        {
            _seedStartedAt ??= Services.TimeProvider.GetUtcNow();
        }
    }

    private Task StopInternalAsync(bool disposing, CancellationToken ct = default)
        => StopInternalAsync(disposing, sendStoppedAnnounce: true, peerManagerAlreadyStopped: false, ct);

    private async Task StopInternalAsync(
        bool disposing,
        bool sendStoppedAnnounce,
        bool peerManagerAlreadyStopped = false,
        CancellationToken ct = default)
    {
        if (!disposing)
        {
            _disposal.ThrowIfDisposed(this);
        }

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool recoveringFailedStart = Interlocked.CompareExchange(ref _rollbackIncomplete, 0, 0) == 1;
            if (Interlocked.CompareExchange(ref _started, 0, 0) == 0 && !recoveringFailedStart && !disposing)
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
                if (PeersInternal != null && !peerManagerAlreadyStopped)
                {
                    await PeersInternal.StopAsync().ConfigureAwait(false);
                }
                if (TrackerManager != null)
                {
                    await TrackerManager.StopAsync(sendStoppedAnnounce).ConfigureAwait(false);
                }
                // Deliberately uncancellable, like the rest of this block: once the stop has
                // begun, aborting it midway would leave some transports running while the
                // torrent already reports itself stopped.
                await StopPeerTransportsAsync(
                    disposing,
                    throwOnFailure: recoveringFailedStart && !disposing,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                if (WebSeedManager != null)
                {
                    await WebSeedManager.DisposeAsync().ConfigureAwait(false);
                }
                if (FileTransferInternal != null)
                {
                    await FileTransferInternal.DisposeAsync().ConfigureAwait(false);
                }
                if (FilesInternal != null)
                {
                    await FilesInternal.StopAsync().ConfigureAwait(false);
                }

                Interlocked.Exchange(ref _rollbackIncomplete, 0);
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
