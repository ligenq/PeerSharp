using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Framework;
using PeerSharp.PieceWriter;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Bandwidth;

public class PerTorrentLimitsTests
{
    private sealed class TrackingBandwidthManager : IBandwidthManager
    {
        private readonly Dictionary<string, BandwidthChannel> _channels = [];
        public int DownloadLimit { get; private set; }
        public int DiskReadLimit { get; private set; }
        public int DiskWriteLimit { get; private set; }
        public int UploadLimit { get; private set; }
        public int Calls { get; private set; }
        public int DiskCalls { get; private set; }

        public void SetGlobalLimits(int downloadLimit, int uploadLimit) { }
        public void SetGlobalDiskLimits(int readLimit, int writeLimit) { }

        public void SetTorrentLimits(ITorrent torrent, int downloadLimit, int uploadLimit)
        {
            DownloadLimit = downloadLimit;
            UploadLimit = uploadLimit;
            Calls++;
        }

        public void SetTorrentDiskLimits(ITorrent torrent, int readLimit, int writeLimit)
        {
            DiskReadLimit = readLimit;
            DiskWriteLimit = writeLimit;
            DiskCalls++;
        }

        public (int DownloadLimit, int UploadLimit) GetTorrentLimits(ITorrent torrent)
        {
            return (DownloadLimit, UploadLimit);
        }

        public (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent)
        {
            return (DiskReadLimit, DiskWriteLimit);
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

    private sealed class MockAlertsManager : IAlertsManager
    {
        public void MetadataAlert(AlertId id, ITorrent torrent) { }
        public void MetadataProgressAlert(ITorrent torrent, float progress, int received, int total) { }
        public static void AddAlert(Alert alert) { }
        public void TorrentAlert(AlertId id, ITorrent torrent) { }
        public static void PeerAlert(AlertId id, ITorrent torrent, IPeerCommunication peer) { }
        public static void TrackerAlert(AlertId id, ITorrent torrent, string trackerUrl, string? message = null) { }
        public static void ErrorAlert(AlertId id, ITorrent torrent, Exception ex) { }
        public void PostAlert(Alert alert) { }
        public void ConfigAlert(AlertId id, string message) { }
        public void PieceCompletedAlert(ITorrent torrent, int pieceIndex, int totalPieces, int receivedPieces) { }
        public void ProgressChangedAlert(ITorrent torrent, float progress, float selectionProgress, ulong downloaded, ulong selectedDownloaded, int downloadSpeed, int uploadSpeed) { }
        public void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, int downloadSpeed, int uploadSpeed, int peerCount) { }
        public void StateChangedAlert(ITorrent torrent, TorrentState oldState, TorrentState newState) { }
        public void TorrentErrorAlert(ITorrent torrent, Exception ex) { }
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

    private sealed class MockFileSelectionManager : IFileSelectionManager
    {
        public bool IsSelectionFinished => true;
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
            return new();
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

    private sealed class MockPeerCommunicationFactory : IPeerCommunicationFactory
    {
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
        {
            return null!;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, System.Net.IPEndPoint? remoteEndPoint)
        {
            return null!;
        }

        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client)
        {
            return null!;
        }
    }

    private sealed class MockTrackerFactory : ITrackerFactory
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

    private sealed class MockFileHandleLease : IFileHandleLease
    {
        public Microsoft.Win32.SafeHandles.SafeFileHandle Handle { get; }
        public string Path { get; }

        public MockFileHandleLease(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
        {
            Handle = handle;
            Path = path;
        }

        public void Dispose() { }
    }

    private sealed class MockFileHandleCache : IFileHandleCache
    {
        public void Dispose() { }
        public ValueTask<IFileHandleLease> GetHandleAsync(string path, bool write, CancellationToken cancellationToken = default)
        {
            return new ValueTask<IFileHandleLease>(new MockFileHandleLease(new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false), path));
        }

        public void CloseTorrentHandles(string rootPath) { }
    }

    private sealed class MockConnectionGovernor : IConnectionGovernor
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

    [Fact]
    public void DownloadUploadLimits_SetTorrentLimitsOnManager()
    {
        var bandwidth = new TrackingBandwidthManager();
        var torrent = CreateMinimalTorrent(bandwidth);

        torrent.DownloadLimitBytesPerSecond = 1234;
        torrent.UploadLimitBytesPerSecond = 4321;

        Assert.Equal(4321, bandwidth.UploadLimit);
        Assert.Equal(1234, bandwidth.DownloadLimit);
        Assert.True(bandwidth.Calls >= 2);
    }

    private static Torrent CreateMinimalTorrent(IBandwidthManager bandwidth)
    {
        var metadata = new TorrentFileMetadata();
        if (metadata.Info.PieceSize == 0)
        {
            metadata.Info.PieceSize = 16384;
        }

        var settings = new Settings
        {
            Files = { DefaultDownloadPath = "C:\\Downloads" }
        };

        return Torrent.Create(
            metadata,
            settings,
            bandwidth,
            new MockAlertsManager(),
            new MockFileSelectionManager(),
            new MockPeerCommunicationFactory(),
            new MockTrackerFactory(),
            new TorrentTestUtility.MockGeoIpService(),
            new MockFileHandleCache(),
            new MockConnectionGovernor(),
            TimeProvider.System);
    }
}






