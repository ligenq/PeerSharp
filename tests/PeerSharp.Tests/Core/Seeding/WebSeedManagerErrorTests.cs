using PeerSharp.Internals;
using PeerSharp.Internals.Seeding;
using PeerSharp.Internals.Framework;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core.Seeding;

public class WebSeedManagerErrorTests
{
    private class MockHttpClient : IHttpClient
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<string> RequestedUrls { get; } = new();

        public Task<byte[]> GetByteArrayAsync(string requestUri, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            if (Responses.TryDequeue(out var response))
            {
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void Dispose() {}
    }

    /// <summary>
    /// A file selection manager that reports selection as NOT finished,
    /// so the WebSeedManager worker loop will attempt downloads.
    /// </summary>
    private class NotFinishedFileSelectionManager : IFileSelectionManager
    {
        public bool IsSelectionFinished => false;
        public int TotalSelectedPieces => 1;
        public int ReceivedSelectedPieces => 0;
        public ulong CalculateFinishedSelectedBytes() => 0;
        public float CalculateSelectionProgress() => 0;
        public void SetObserver(IFileSelectionObserver observer) { }
        public FileSelection GetFileSelection(int fileIndex) => new();
        public IReadOnlyList<FileSelection> GetAllFileSelections() => new List<FileSelection>();
        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPieceVerified(int pieceIndex) { }
        public void Initialize(List<FileSelection>? savedSelection, PiecesProgress pieces) { }
        public void SetBytesProvider(IUnfinishedBytesProvider provider) { }
    }

    private static Torrent CreateTorrentWithPieces()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384; // 1 piece
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "test.dat", Size = 16384, Offset = 0 });
        metadata.Info.Pieces.Add(new byte[20]);

        var settings = new Settings();
        settings.Files.DefaultDownloadPath = "C:\\Downloads";

        var torrent = Torrent.Create(
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
            TimeProvider.System
        );

        torrent.ReinitializeAfterMetadataAsync().GetAwaiter().GetResult();
        return torrent;
    }

    /// <summary>
    /// Advances the FakeTimeProvider in small steps while waiting for requests.
    /// This is needed because the worker loop has multiple Task.Delay calls
    /// that all use the FakeTimeProvider.
    /// </summary>
    private static async Task WaitForRequestCount(MockHttpClient mockHttp, int count, FakeTimeProvider timeProvider)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (mockHttp.RequestedUrls.Count < count && Environment.TickCount64 < deadline)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task DownloadPieceAsync_404_RecordsFailureAndRetries()
    {
        var timeProvider = new FakeTimeProvider();
        var torrent = CreateTorrentWithPieces();
        var manager = new WebSeedManager(torrent, new[] { "http://seed.com/file.bin" }, timeProvider);
        var mockHttp = new MockHttpClient();
        manager.SetTestClient(mockHttp);

        // Fail 3 times
        mockHttp.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        mockHttp.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        mockHttp.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        manager.Start();

        // Wait for all 3 requests, advancing time to resolve delays
        await WaitForRequestCount(mockHttp, 3, timeProvider);

        // Allow tasks to settle
        await Task.Delay(100);

        Assert.True(mockHttp.RequestedUrls.Count >= 3, $"Expected 3 requests but got {mockHttp.RequestedUrls.Count}");

        // After 3 failures, FailureCount == MaxRetries so IsAvailable checks time.
        // It failed just now, so backoff time hasn't passed.
        var stats = manager.GetStats();
        Assert.Equal(0, stats.AvailableSources);

        // Advance time so pending FakeTimeProvider delays resolve before stopping
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await manager.StopAsync();
    }

    [Fact]
    public async Task DownloadPieceAsync_503_RecordsFailure()
    {
        var timeProvider = new FakeTimeProvider();
        var torrent = CreateTorrentWithPieces();
        var manager = new WebSeedManager(torrent, new[] { "http://seed.com/file.bin" }, timeProvider);
        var mockHttp = new MockHttpClient();
        manager.SetTestClient(mockHttp);

        mockHttp.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        manager.Start();

        // Wait for the request, advancing time to resolve delays
        await WaitForRequestCount(mockHttp, 1, timeProvider);
        Assert.True(mockHttp.RequestedUrls.Count >= 1, $"Expected at least 1 request but got {mockHttp.RequestedUrls.Count}");

        // Allow the download task and ContinueWith to complete
        await Task.Delay(100);

        var stats = manager.GetStats();
        Assert.Equal(1, stats.TotalSources);

        // Advance time so pending FakeTimeProvider delays resolve before stopping
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await manager.StopAsync();
    }
}
