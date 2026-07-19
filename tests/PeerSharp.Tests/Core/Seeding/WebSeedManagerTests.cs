using PeerSharp.Internals;
using PeerSharp.Internals.Seeding;
using PeerSharp.Internals.Framework;
using Microsoft.Extensions.Time.Testing;
using System.Net;

namespace PeerSharp.Tests.Core.Seeding;

public class WebSeedManagerTests
{
    private class MockHttpClient : IHttpClient
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public byte[]? ResponseBytes { get; set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.PartialContent;
        public List<HttpRequestMessage> SentRequests { get; } = [];

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseBytes ?? []);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            if (Handler != null)
            {
                return Task.FromResult(Handler(request));
            }
            var response = new HttpResponseMessage(StatusCode);
            if (ResponseBytes != null)
            {
                response.Content = new ByteArrayContent(ResponseBytes);
            }
            return Task.FromResult(response);
        }
    }

    private readonly Torrent _torrent;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly MockHttpClient _mockHttp = new();

    public WebSeedManagerTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
        _torrent.InfoFile.Info.PieceSize = 16384;
        _torrent.InfoFile.Info.FullSize = 32768; // 2 pieces
        _torrent.InfoFile.Info.Files.Add(new Internals.TorrentFileEntry { Path = "test.dat", Size = 32768, Offset = 0 });
        _torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        _torrent.InfoFile.Info.Pieces.Add(new byte[20]);
        _torrent.ReinitializeAfterMetadataAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void GetNeededPieces_ReturnsMissingPieces()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);

        var needed = manager.GetNeededPieces();

        Assert.Equal(2, needed.Count);
        Assert.Equal(0, needed[0]);
        Assert.Equal(1, needed[1]);

        // Complete first piece
        _torrent.Pieces.AddPiece(0);
        needed = manager.GetNeededPieces();
        Assert.Single(needed);
        Assert.Equal(1, needed[0]);
    }

    [Fact(Timeout = 30000)]
    public async Task DownloadSingleFilePieceAsync_SendsCorrectRange()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = new byte[16384];

        var source = new WebSeedManager.WebSeedSource("http://seed.com", false);

        var data = await manager.DownloadSingleFilePieceAsync(source, 0, 16384, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(16384, data.Length);

        var request = _mockHttp.SentRequests[0];
        Assert.Equal("http://seed.com/", request.RequestUri?.ToString());
        Assert.Equal(0, request.Headers.Range?.Ranges.First().From);
        Assert.Equal(16383, request.Headers.Range?.Ranges.First().To);
    }

    [Fact]
    public async Task DownloadSingleFilePieceAsync_OkResponse_SlicesRequestedRangeFromFullContent()
    {
        // A server that ignores the Range header replies 200 with the whole file;
        // like libtorrent, the requested range is sliced out of the full body.
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(_mockHttp);
        _mockHttp.StatusCode = HttpStatusCode.OK;
        var fullContent = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        _mockHttp.ResponseBytes = fullContent;

        var source = new WebSeedManager.WebSeedSource("http://seed.com", false);

        var data = await manager.DownloadSingleFilePieceAsync(source, 8, 16, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(fullContent.AsSpan(8, 16).ToArray(), data);
    }

    [Fact]
    public async Task DownloadSingleFilePieceAsync_OkResponseTooShort_ReturnsNull()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(_mockHttp);
        _mockHttp.StatusCode = HttpStatusCode.OK;
        _mockHttp.ResponseBytes = new byte[8]; // shorter than the requested range

        var source = new WebSeedManager.WebSeedSource("http://seed.com", false);

        var data = await manager.DownloadSingleFilePieceAsync(source, 0, 16, CancellationToken.None);

        Assert.Null(data);
    }

    [Fact]
    public async Task DownloadMultiFilePieceAsync_SpansFiles()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "multi";
        metadata.Info.PieceSize = 10;
        metadata.Info.FullSize = 12;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "a.bin", Size = 6, Offset = 0 });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "b.bin", Size = 6, Offset = 6 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata);

        var fileA = Enumerable.Range(0, 6).Select(i => (byte)(i + 1)).ToArray();
        var fileB = Enumerable.Range(0, 6).Select(i => (byte)(i + 101)).ToArray();

        var handler = new MockHttpClient();
        handler.Handler = request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var range = request.Headers.Range?.Ranges.First();
            long start = range?.From ?? 0;
            long end = range?.To ?? -1;
            byte[] source = url.Contains("a.bin", StringComparison.OrdinalIgnoreCase) ? fileA : fileB;
            int length = (int)(end - start + 1);
            byte[] slice = source.AsSpan((int)start, length).ToArray();
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
        };

        var manager = new WebSeedManager(torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(handler);

        var source = new WebSeedManager.WebSeedSource("http://seed.com", true);
        var data = await manager.DownloadMultiFilePieceAsync(source, 0, 0, 10, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(10, data.Length);
        Assert.Equal(fileA.Concat(fileB.Take(4)).ToArray(), data);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task DownloadMultiFilePieceAsync_EscapesPathSegmentsWithoutEscapingSlashes()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "multi";
        metadata.Info.PieceSize = 4;
        metadata.Info.FullSize = 4;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "dir name/a file.bin", Size = 4, Offset = 0 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var handler = new MockHttpClient
        {
            ResponseBytes = [1, 2, 3, 4]
        };

        var manager = new WebSeedManager(torrent, ["http://seed.com/root"], _timeProvider);
        manager.SetTestClient(handler);

        var source = new WebSeedManager.WebSeedSource("http://seed.com/root", true);
        var data = await manager.DownloadMultiFilePieceAsync(source, 0, 0, 4, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], data);
        Assert.Equal("http://seed.com/root/dir%20name/a%20file.bin", handler.SentRequests.Single().RequestUri?.AbsoluteUri);

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task DownloadMultiFilePieceAsync_DirectoryWebSeed_IncludesTorrentName()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "Big Buck Bunny";
        metadata.Info.PieceSize = 4;
        metadata.Info.FullSize = 4;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "Big Buck Bunny.en.srt", Size = 4, Offset = 0 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var handler = new MockHttpClient
        {
            ResponseBytes = [1, 2, 3, 4]
        };

        var manager = new WebSeedManager(torrent, ["https://webtorrent.io/torrents/"], _timeProvider);
        manager.SetTestClient(handler);

        var source = new WebSeedManager.WebSeedSource("https://webtorrent.io/torrents/", true);
        var data = await manager.DownloadMultiFilePieceAsync(source, 0, 0, 4, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], data);
        Assert.Equal("https://webtorrent.io/torrents/Big%20Buck%20Bunny/Big%20Buck%20Bunny.en.srt", handler.SentRequests.Single().RequestUri?.AbsoluteUri);

        await torrent.DisposeAsync();
    }

    [Fact]
    public void GetStats_ReflectsAvailableSources()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed1.com", "http://seed2.com"], _timeProvider);

        var stats = manager.GetStats();
        Assert.Equal(2, stats.TotalSources);
        Assert.Equal(2, stats.AvailableSources);
        Assert.Equal(0, stats.ActiveDownloads);
    }

    [Fact]
    public async Task DownloadSingleFilePieceAsync_416_ReturnsNull()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(_mockHttp);
        _mockHttp.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable;

        var source = new WebSeedManager.WebSeedSource("http://seed.com", false);
        var data = await manager.DownloadSingleFilePieceAsync(source, 0, 16384, CancellationToken.None);

        Assert.Null(data);
    }

    [Fact]
    public async Task DownloadSingleFilePieceAsync_UnexpectedStatus_ReturnsNull()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(_mockHttp);
        _mockHttp.StatusCode = HttpStatusCode.ServiceUnavailable;

        var source = new WebSeedManager.WebSeedSource("http://seed.com", false);
        var data = await manager.DownloadSingleFilePieceAsync(source, 0, 16384, CancellationToken.None);

        Assert.Null(data);
    }

    [Fact]
    public async Task DownloadMultiFilePieceAsync_SkipsPaddingFile_ZeroFillsBytes()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "multi";
        metadata.Info.PieceSize = 10;
        metadata.Info.FullSize = 10;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "a.bin", Size = 3, Offset = 0 });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = ".pad/3", Size = 3, Offset = 3, IsPadding = true });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "b.bin", Size = 4, Offset = 6 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var aData = new byte[] { 1, 2, 3 };
        var bData = new byte[] { 101, 102, 103, 104 };

        var handler = new MockHttpClient
        {
            Handler = request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                var range = request.Headers.Range?.Ranges.First();
                long start = range?.From ?? 0;
                long end = range?.To ?? -1;
                byte[] src = url.Contains("a.bin", StringComparison.OrdinalIgnoreCase) ? aData : bData;
                int length = (int)(end - start + 1);
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(src.AsSpan((int)start, length).ToArray())
                };
            }
        };

        var manager = new WebSeedManager(torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(handler);

        var source = new WebSeedManager.WebSeedSource("http://seed.com", false);
        var data = await manager.DownloadMultiFilePieceAsync(source, 0, 0, 10, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(10, data.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 101, 102, 103, 104 }, data);
        // Only real files get HTTP requests — padding is zero-filled without a request
        Assert.Equal(2, handler.SentRequests.Count);
        Assert.DoesNotContain(".pad", handler.SentRequests.Select(r => r.RequestUri?.ToString() ?? ""));

        await torrent.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeAsync_CompletesSuccessfully()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.Start();

        await manager.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeAsync_IsIdempotent()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.Start();

        await manager.DisposeAsync();
        await manager.DisposeAsync(); // Second call must not throw
    }
}





