using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Extensions;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;

namespace PeerSharp.Tests;

internal static class TorrentTestUtility
{
    /// <summary>
    /// Polls until <paramref name="condition"/> becomes true, failing the test after
    /// <paramref name="timeoutMs"/> so a broken condition can never hang the test run.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out after {timeoutMs}ms waiting for condition{(because == null ? "" : $": {because}")}");
    }

    /// <summary>
    /// Steps a <see cref="FakeTimeProvider"/> forward until <paramref name="condition"/> holds.
    ///
    /// <para>
    /// Advancing a fake clock once and then sleeping a fixed amount of real time loses two ways, and
    /// both have failed in CI. If the code under test has not yet reached its <c>Task.Delay</c>, the
    /// advance moves the clock past a deadline that does not exist yet and the tick is lost entirely -
    /// no subsequent wait can recover it. And if it has, the continuation still has to be picked up off
    /// the thread pool, which on a loaded runner takes longer than a short sleep allows.
    /// </para>
    ///
    /// <para>
    /// Advancing repeatedly and re-checking covers both: a lost tick is caught by the next advance, a
    /// late continuation by the next poll. Use this instead of <c>Advance</c> followed by
    /// <c>Task.Delay</c> whenever the thing being waited for is driven by the fake clock.
    /// </para>
    /// </summary>
    /// <param name="timeProvider">The clock to step.</param>
    /// <param name="condition">What is being waited for.</param>
    /// <param name="step">How far to move the clock each round.</param>
    /// <param name="because">Described in the failure message.</param>
    /// <param name="timeoutMs">Real-time budget before giving up.</param>
    public static async Task AdvanceUntilAsync(
        FakeTimeProvider timeProvider,
        Func<bool> condition,
        TimeSpan step,
        string? because = null,
        int timeoutMs = 20000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            timeProvider.Advance(step);

            // Yield first. When the thread pool is healthy the continuation the advance released is
            // ready immediately and this costs microseconds, where a timed sleep cannot cost less than
            // the system timer tick - about 16ms on Windows however small the number passed to it.
            for (int i = 0; i < 4 && !condition(); i++)
            {
                await Task.Yield();
            }

            // Then sleep, for a runner too busy to have run it yet. Kept short deliberately: this is
            // paid on every round that does not finish, so it sets how much of the clock the real-time
            // budget can afford to cover. Twenty sleeps a round cost 300ms each, which bought only
            // sixty rounds - enough locally, and not enough on CI, where these tests took six and
            // twenty seconds against the one second they take here.
            for (int i = 0; i < 4 && !condition(); i++)
            {
                await Task.Delay(1);
            }
        }

        Assert.Fail($"Timed out after {timeoutMs}ms advancing the clock{(because == null ? "" : $" waiting for {because}")}.");
    }

    /// <summary>
    /// Steps a <see cref="FakeTimeProvider"/> forward until <paramref name="task"/> completes, for the
    /// common case where the thing being waited for is a task rather than a predicate.
    /// </summary>
    public static async Task AdvanceUntilCompleteAsync(
        FakeTimeProvider timeProvider,
        Task task,
        TimeSpan step,
        string? because = null,
        int timeoutMs = 20000)
    {
        await AdvanceUntilAsync(timeProvider, () => task.IsCompleted, step, because, timeoutMs);
        await task;
    }

    internal class MockBandwidthManager : IBandwidthManager
    {
        private readonly Dictionary<string, BandwidthChannel> _channels = [];

        public void SetGlobalLimits(long downloadLimit, long uploadLimit) { }
        public void SetGlobalDiskLimits(long readLimit, long writeLimit) { }
        public void SetTorrentLimits(ITorrent torrent, long downloadLimit, long uploadLimit) { }
        public void SetTorrentDiskLimits(ITorrent torrent, long readLimit, long writeLimit) { }
        public (long DownloadLimit, long UploadLimit) GetTorrentLimits(ITorrent torrent)
        {
            return (0, 0);
        }
        public (long ReadLimit, long WriteLimit) GetTorrentDiskLimits(ITorrent torrent)
        {
            return (0, 0);
        }

        public void Configure(int updateIntervalMs) { }
        public void Start() { }
        public Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default)
        {
            return Task.FromResult(amount);
        }

        public BandwidthChannel GetChannel(string name)
        {
            if (_channels.TryGetValue(name, out var channel))
            {
                return channel;
            }

            channel = new BandwidthChannel(TimeProvider.System);
            _channels[name] = channel;
            return channel;
        }

        public void ReturnBandwidth(int amount, string[] channelNames) { }
        public void RemoveTorrentChannels(ITorrent torrent) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal class MockAlertsManager : IAlertsManager
    {
        public void PieceHashFailedAlert(ITorrent torrent, int pieceIndex, int failures, System.Net.IPEndPoint? suspectedPeer) { }

        public void PeerBlockedAlert(ITorrent torrent, System.Net.IPEndPoint endpoint, PeerBlockReason reason) { }

        public void ListenPortChangedAlert(int requestedPort, int actualPort, ListenTransport transport) { }
        public void MetadataAlert(AlertId id, ITorrent torrent) { }
        public void MetadataProgressAlert(ITorrent torrent, float progress, int receivedPieces, int totalPieces) { }
        public static void AddAlert(Alert alert) { }
        public void TorrentAlert(AlertId id, ITorrent torrent) { }
        public static void PeerAlert(AlertId id, ITorrent torrent, IPeerCommunication peer) { }
        public static void TrackerAlert(AlertId id, ITorrent torrent, string trackerUrl, string? message = null) { }
        public static void ErrorAlert(AlertId id, ITorrent torrent, Exception ex) { }
        public void PostAlert(Alert alert) { }
        public void ConfigAlert(AlertId id, string configType) { }
        public void PieceCompletedAlert(ITorrent torrent, int pieceIndex, int completedPieces, int totalPieces) { }
        public void ProgressChangedAlert(ITorrent torrent, float progress, float selectionProgress, ulong finishedBytes, ulong totalBytes, int completedPieces, int totalPieces) { }
        public void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, long downloadSpeed, long uploadSpeed, int connectedPeers) { }
        public void StateChangedAlert(ITorrent torrent, TorrentState previousState, TorrentState newState) { }
        public void TorrentErrorAlert(ITorrent torrent, Exception exception) { }
        public void RegisterAlerts(uint categories) { }
        public List<Alert> PopAlerts()
        {
            return [];
        }

        public async IAsyncEnumerable<Alert> GetAlertsAsync(TimeSpan? timeout = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    internal class MockFileSelectionManager : IFileSelectionManager
    {
        public bool IsSelectionFinished { get; set; } = true;
        public int TotalSelectedPieces => 0;
        public int ReceivedSelectedPieces => 0;
        public ulong CalculateFinishedSelectedBytes()
        {
            return 0;
        }

        public float CalculateSelectionProgress()
        {
            return 0;
        }

        public void SetObserver(IFileSelectionObserver observer) { }
        public FileSelection GetFileSelection(int fileIndex)
        {
            return new FileSelection();
        }

        public IReadOnlyList<FileSelection> GetAllFileSelections()
        {
            return new List<FileSelection>();
        }

        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void OnPieceVerified(int pieceIndex) { }
        public void Initialize(List<FileSelection>? savedSelection, PiecesProgress pieces) { }
        public void SetBytesProvider(IUnfinishedBytesProvider provider) { }
    }

    internal class MockPeerCommunicationFactory : IPeerCommunicationFactory
    {
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            return null!;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? remoteEndPoint)
        {
            return null!;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client)
        {
            return null!;
        }
    }

    internal class MockTrackerFactory : ITrackerFactory
    {
        public static ITracker Create(string url, Torrent torrent)
        {
            return null!;
        }

        public ITracker CreateTracker(string url, TimeProvider timeProvider)
        {
            return null!;
        }
    }

    internal class MockGeoIpService : IGeoIpService
    {
        public bool Enabled { get; set; }
        public string GetCountry(IPAddress ip)
        {
            return "US";
        }

        public void Load(Stream stream) { Enabled = true; }
        public Task LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            Enabled = true;
            return Task.CompletedTask;
        }
        public void Clear() { Enabled = false; }
    }

    private class MockFileHandleLease : IFileHandleLease
    {
        public SafeFileHandle Handle { get; }
        public string Path { get; }
        private readonly FileStream _stream;

        public MockFileHandleLease(string path, bool write)
        {
            Path = path;
            var access = write ? FileAccess.ReadWrite : FileAccess.Read;
            var mode = write ? FileMode.OpenOrCreate : FileMode.Open;
            _stream = new FileStream(path, mode, access, FileShare.ReadWrite);
            Handle = _stream.SafeFileHandle;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }

    internal class MockFileHandleCache : IFileHandleCache
    {
        public void Dispose() { }
        public ValueTask<IFileHandleLease> GetHandleAsync(string path, bool write, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IFileHandleLease>(new MockFileHandleLease(path, write));
        }

        public void CloseTorrentHandles(string rootPath) { }
    }

    internal class MockConnectionGovernor : IConnectionGovernor
    {
        public int ActiveConnections => 0;
        public int PendingConnections => 0;
        public static bool CanAcceptConnection()
        {
            return true;
        }

        public static bool CanInitiateConnection()
        {
            return true;
        }

        public bool TryAcquireConnectionSlot()
        {
            return true;
        }

        public bool TryAcquirePendingSlot()
        {
            return true;
        }

        public void ReleaseConnectionSlot() { }
        public void ReleasePendingSlot() { }
    }

    /// <param name="trackerFactory">
    /// Overrides the default factory, which returns null for every URL - fine for a torrent whose
    /// trackers are beside the point, but it means the tracker manager registers nothing at all. Pass
    /// one that returns a tracker when the test is about which trackers a torrent ends up with.
    /// </param>
    /// <param name="resumeData">
    /// Applied before the torrent initializes, exactly as the session manager does on startup. Pass
    /// one to exercise what the torrent accepts or rejects from a resume file.
    /// </param>
    public static Torrent CreateMinimal(
        TorrentFileMetadata? metadata = null,
        string? downloadPath = null,
        ITrackerFactory? trackerFactory = null,
        TimeProvider? timeProvider = null,
        IAlertsManager? alerts = null,
        TorrentResumeData? resumeData = null)
    {
        metadata ??= new TorrentFileMetadata();
        if (metadata.Info.PieceSize == 0)
        {
            metadata.Info.PieceSize = 16384;
        }

        var settings = new Settings();
        settings.Files.DefaultDownloadPath = string.IsNullOrWhiteSpace(downloadPath)
            ? "C:\\Downloads"
            : downloadPath;

        return Torrent.Create(
            metadata,
            settings,
            new MockBandwidthManager(),
            alerts ?? new MockAlertsManager(),
            new MockFileSelectionManager(),
            new MockPeerCommunicationFactory(),
            trackerFactory ?? new MockTrackerFactory(),
            new MockGeoIpService(),
            new MockFileHandleCache(),
            new MockConnectionGovernor(),
            timeProvider ?? TimeProvider.System,
            resumeData: resumeData
        );
    }
}




