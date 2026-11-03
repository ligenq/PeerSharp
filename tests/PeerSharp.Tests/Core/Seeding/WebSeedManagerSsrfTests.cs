using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Seeding;

namespace PeerSharp.Tests.Core.Seeding;

/// <summary>
/// A web seed that names this device outright is judged once, when it is added: one that could only
/// ever be refused is skipped rather than retried for the whole download.
/// </summary>
public class WebSeedManagerSsrfTests
{
    [Fact]
    public async Task ASeedOnThisDeviceWithAQueryString_IsSkipped()
    {
        await using var manager = new WebSeedManager(
            CreateTorrent(SsrfMitigation.ThisDevice),
            ["http://127.0.0.1:8080/api/v2/torrents/delete?hashes=all", "http://127.0.0.1:8080/files/movie.bin", "http://seed.example/movie.bin?token=1"],
            TimeProvider.System);

        Assert.Equal(["http://127.0.0.1:8080/files/movie.bin", "http://seed.example/movie.bin?token=1"], manager.GetSourceUrls());
        Assert.False(manager.AddSource("http://localhost:8080/api?action=shutdown"));
        Assert.True(manager.AddSource("http://localhost:8080/movie.bin"));
    }

    [Fact]
    public async Task WithoutTheGuard_EverySeedIsKept()
    {
        await using var manager = new WebSeedManager(
            CreateTorrent(SsrfMitigation.None),
            ["http://127.0.0.1:8080/seed?token=1"],
            TimeProvider.System);

        Assert.Equal(["http://127.0.0.1:8080/seed?token=1"], manager.GetSourceUrls());
    }

    private static Torrent CreateTorrent(SsrfMitigation mode)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "movie.bin", Size = 16384, Offset = 0 });
        metadata.Info.Pieces.Add(new byte[20]);

        var settings = new Settings();
        settings.Files.DefaultDownloadPath = Path.GetTempPath();
        settings.Connection.SsrfMitigation = mode;

        return Torrent.Create(
            metadata,
            settings,
            new TorrentTestUtility.MockBandwidthManager(),
            new TorrentTestUtility.MockAlertsManager(),
            new NotFinishedFileSelectionManager(),
            new TorrentTestUtility.MockPeerCommunicationFactory(),
            new TorrentTestUtility.MockTrackerFactory(),
            new TorrentTestUtility.MockGeoIpService(),
            new TorrentTestUtility.MockFileHandleCache(),
            new TorrentTestUtility.MockConnectionGovernor(),
            new HttpClientFactory(),
            TimeProvider.System);
    }

    private sealed class NotFinishedFileSelectionManager : IFileSelectionManager
    {
        public bool IsSelectionFinished => false;
        public int TotalSelectedPieces => 1;
        public int ReceivedSelectedPieces => 0;
        public ulong CalculateFinishedSelectedBytes() => 0;
        public float CalculateSelectionProgress() => 0;
        public void SetObserver(IFileSelectionObserver observer) { }
        public FileSelection GetFileSelection(int fileIndex) => new();
        public IReadOnlyList<FileSelection> GetAllFileSelections() => [];
        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPieceVerified(int pieceIndex) { }
        public void Initialize(List<FileSelection>? savedSelection, PiecesProgress pieces) { }
        public void SetBytesProvider(IUnfinishedBytesProvider provider) { }
    }
}
