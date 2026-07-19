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

    internal class MockBandwidthManager : IBandwidthManager
    {
        private readonly Dictionary<string, BandwidthChannel> _channels = [];

        public void SetGlobalLimits(int downloadLimit, int uploadLimit) { }
        public void SetGlobalDiskLimits(int readLimit, int writeLimit) { }
        public void SetTorrentLimits(ITorrent torrent, int downloadLimit, int uploadLimit) { }
        public void SetTorrentDiskLimits(ITorrent torrent, int readLimit, int writeLimit) { }
        public (int DownloadLimit, int UploadLimit) GetTorrentLimits(ITorrent torrent)
        {
            return (0, 0);
        }
        public (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent)
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
        public void TransferStatsAlert(ITorrent torrent, long downloaded, long uploaded, int downloadSpeed, int uploadSpeed, int connectedPeers) { }
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

    public static Torrent CreateMinimal(TorrentFileMetadata? metadata = null, string? downloadPath = null)
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
            new MockAlertsManager(),
            new MockFileSelectionManager(),
            new MockPeerCommunicationFactory(),
            new MockTrackerFactory(),
            new MockGeoIpService(),
            new MockFileHandleCache(),
            new MockConnectionGovernor(),
            TimeProvider.System
        );
    }
}






