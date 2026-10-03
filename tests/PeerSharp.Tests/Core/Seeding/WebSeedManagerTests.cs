using PeerSharp.Internals;
using PeerSharp.Internals.Seeding;
using PeerSharp.Internals.Framework;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using PeerSharp.BEncoding;

namespace PeerSharp.Tests.Core.Seeding;

public class WebSeedManagerTests
{
    [Fact]
    public async Task DownloadSpeed_CountsHttpBytesAndReturnsToZeroWhenIdle()
    {
        await using var manager = new WebSeedManager(_torrent, ["http://seed.com/content"], _timeProvider);
        manager.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = new byte[16384];
        var source = new WebSeedManager.WebSeedSource("http://seed.com/content", false);
        Assert.NotNull(await manager.DownloadSingleFilePieceAsync(source, 0, 16384, TestContext.Current.CancellationToken));
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(16384, manager.SampleDownloadSpeed());
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, manager.SampleDownloadSpeed());
    }
    private class MockHttpClient : IHttpClient
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public byte[]? ResponseBytes { get; set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.PartialContent;
        public List<HttpRequestMessage> SentRequests { get; } = [];
        public bool OmitContentRange { get; set; }

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseBytes ?? []);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            if (Handler != null)
            {
                return Task.FromResult(AddRange(Handler(request), request));
            }
            var response = new HttpResponseMessage(StatusCode);
            if (ResponseBytes != null)
            {
                response.Content = new ByteArrayContent(ResponseBytes);
            }
            return Task.FromResult(AddRange(response, request));
        }

        private HttpResponseMessage AddRange(HttpResponseMessage response, HttpRequestMessage request)
        {
            if (!OmitContentRange && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange == null)
            {
                var range = request.Headers.Range!.Ranges.Single();
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(range.From!.Value, range.To!.Value);
            }
            return response;
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
    public async Task DownloadSingleFilePieceAsync_PartialContentTooLong_ReturnsNull()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(_mockHttp);
        _mockHttp.StatusCode = HttpStatusCode.PartialContent;
        _mockHttp.ResponseBytes = new byte[17];

        var source = new WebSeedManager.WebSeedSource("http://seed.com", false);

        var data = await manager.DownloadSingleFilePieceAsync(source, 0, 16, CancellationToken.None);

        Assert.Null(data);
    }

    /// <summary>
    /// A multi-file seed missing one file still holds the others, so it keeps being asked for them.
    /// </summary>
    /// <remarks>
    /// libtorrent records this per file rather than per server - a non-OK status clears that file's
    /// bit in the web seed's <c>have_files</c> - and only a seed left holding nothing the torrent
    /// wants stops being asked. Retiring the whole source on one missing file would throw away
    /// everything else it could serve.
    /// </remarks>
    [Fact]
    public async Task DownloadMultiFilePieceAsync_OneMissingFileDoesNotRetireTheWholeSource()
    {
        var (torrent, handler, manager) = MultiFileSeed(missing: ["a.bin"]);
        var source = new WebSeedManager.WebSeedSource("http://seed.com", true);

        Assert.Null(await manager.DownloadMultiFilePieceAsync(source, 0, 0, 10, CancellationToken.None));

        Assert.False(source.IsRetired);
        Assert.Contains("a.bin", source.MissingFiles);
        Assert.True(source.IsAvailable(_timeProvider));

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task DownloadMultiFilePieceAsync_ASourceMissingEveryFileIsRetired()
    {
        var (torrent, handler, manager) = MultiFileSeed(missing: ["a.bin", "b.bin"]);
        var source = new WebSeedManager.WebSeedSource("http://seed.com", true);

        // Piece assembly stops at the first file it cannot get, so a source learns its missing files
        // one piece at a time. These two start in different files, which is what it takes to hear
        // about both.
        Assert.Null(await manager.DownloadMultiFilePieceAsync(source, 0, 0, 6, CancellationToken.None));
        Assert.False(source.IsRetired);

        Assert.Null(await manager.DownloadMultiFilePieceAsync(source, 1, 6, 6, CancellationToken.None));

        Assert.True(source.IsRetired);
        Assert.False(source.IsAvailable(_timeProvider));

        await torrent.DisposeAsync();
    }

    [Fact]
    public async Task DownloadMultiFilePieceAsync_AFileKnownMissingIsNotAskedForAgain()
    {
        var (torrent, handler, manager) = MultiFileSeed(missing: ["a.bin"]);
        var source = new WebSeedManager.WebSeedSource("http://seed.com", true);

        Assert.Null(await manager.DownloadMultiFilePieceAsync(source, 0, 0, 10, CancellationToken.None));
        int afterFirst = handler.SentRequests.Count;

        Assert.Null(await manager.DownloadMultiFilePieceAsync(source, 0, 0, 10, CancellationToken.None));

        Assert.Equal(afterFirst, handler.SentRequests.Count);

        await torrent.DisposeAsync();
    }

    /// <summary>A two-file seed that answers 404 for the named files and serves the rest.</summary>
    private (Torrent Torrent, MockHttpClient Handler, WebSeedManager Manager) MultiFileSeed(string[] missing)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "multi";
        metadata.Info.PieceSize = 10;
        metadata.Info.FullSize = 12;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "a.bin", Size = 6, Offset = 0 });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "b.bin", Size = 6, Offset = 6 });

        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var handler = new MockHttpClient
        {
            Handler = request =>
            {
                string url = request.RequestUri?.ToString() ?? string.Empty;
                if (missing.Any(name => url.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var range = request.Headers.Range?.Ranges.First();
                long start = range?.From ?? 0;
                long end = range?.To ?? -1;
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(new byte[end - start + 1])
                };
            }
        };

        var manager = new WebSeedManager(torrent, ["http://seed.com"], _timeProvider);
        manager.SetTestClient(handler);
        return (torrent, handler, manager);
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
    public void RuntimeSources_DistinguishAFileUrlFromADirectoryUrl()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com/content"], _timeProvider);

        Assert.True(manager.AddSource("http://seed.com/content/"));
        Assert.Equal(2, manager.GetSourceUrls().Count);
        Assert.True(manager.RemoveSource("http://seed.com/content/"));
        Assert.Equal(["http://seed.com/content"], manager.GetSourceUrls());
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

    [Theory]
    [InlineData(null)]
    [InlineData("bytes 0-15/32")]
    [InlineData("bytes 8-22/32")]
    [InlineData("items 8-23/32")]
    public async Task PartialContentMustDescribeTheRequestedRange(string? contentRange)
    {
        await using var manager = new WebSeedManager(_torrent, ["http://seed.com/file"], _timeProvider);
        var client = new MockHttpClient { OmitContentRange = true };
        client.Handler = _ =>
        {
            var content = new ByteArrayContent(new byte[16]);
            if (contentRange != null) content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        };
        manager.SetTestClient(client);
        Assert.Null(await manager.DownloadSingleFilePieceAsync(new("http://seed.com/file", false), 8, 16, CancellationToken.None));
    }

    [Fact]
    public async Task PartialContentCannotUseAClaimedLengthToHideExtraBodyBytes()
    {
        await using var manager = new WebSeedManager(_torrent, [], _timeProvider);
        var client = new MockHttpClient
        {
            Handler = _ =>
        {
            var content = new StreamContent(new MemoryStream(new byte[17]));
            content.Headers.ContentLength = 16;
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
        };
        manager.SetTestClient(client);
        Assert.Null(await manager.DownloadSingleFilePieceAsync(new("http://seed.com/file", false), 0, 16, CancellationToken.None));
    }

    [Fact]
    public async Task SingleFileDirectoryUrlAppendsTheEscapedFileNameAndPreservesTheQuery()
    {
        _torrent.InfoFile.Info.Name = "movie name.bin";
        await using var manager = new WebSeedManager(_torrent, [], _timeProvider);
        var client = new MockHttpClient { ResponseBytes = new byte[16] };
        manager.SetTestClient(client);
        Assert.NotNull(await manager.DownloadSingleFilePieceAsync(new("http://seed.com/root/?token=a%2Fb", false), 0, 16, CancellationToken.None));
        Assert.Equal("http://seed.com/root/movie%20name.bin?token=a%2Fb", client.SentRequests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task MissingFilesDoNotPreventDownloadingOtherPiecesFromASource()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 4;
        metadata.Info.FullSize = 8;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "a.bin", Offset = 0, Size = 4 });
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "b.bin", Offset = 4, Size = 4 });
        metadata.Info.Pieces = [new byte[20], new byte[20]];
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        await using var manager = new WebSeedManager(torrent, ["http://seed.com/root"], _timeProvider);
        manager.SetTestClient(new MockHttpClient { StatusCode = HttpStatusCode.NotFound });
        var source = (WebSeedManager.WebSeedSource)((System.Collections.IList)typeof(WebSeedManager).GetField("_sources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(manager)!)[0]!;
        Assert.Null(await manager.DownloadMultiFilePieceAsync(source, 0, 0, 4, CancellationToken.None));
        Assert.Equal(new[] { 1 }, manager.GetNeededPieces(maxPieces: 1));
    }

    [Fact]
    public void SourcesRespectCaseSensitivePathsAndRejectUnsupportedFtpUrls()
    {
        var manager = new WebSeedManager(_torrent, ["http://seed.com/FILE"], _timeProvider);
        Assert.True(manager.AddSource("http://seed.com/file"));
        Assert.False(manager.AddSource("ftp://seed.com/file"));
        Assert.True(_torrent.WebSeeds.Add("http://seed.com/FILE"));
        Assert.True(_torrent.WebSeeds.Add("http://seed.com/file"));
        Assert.False(_torrent.WebSeeds.Add("ftp://seed.com/file"));
    }

    [Fact(Timeout = 10000)]
    public async Task AStalledResponseBodyIsCancelledByTheRequestDeadline()
    {
        await using var manager = new WebSeedManager(_torrent, [], _timeProvider);
        using var body = new StalledBody();
        var client = new MockHttpClient
        {
            Handler = _ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = 16;
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
        };
        manager.SetTestClient(client);
        var downloading = manager.DownloadSingleFilePieceAsync(new("http://seed.com/file", false), 0, 16, CancellationToken.None);
        await body.Reading.Task;
        _timeProvider.Advance(WebSeedManager.RequestTimeout + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloading);
    }

    [Fact(Timeout = 10000)]
    public async Task WebSeedReadsRespectLimitsAndCanMakeProgressForLongerThanTheIdleDeadline()
    {
        await using var bandwidth = new PeerSharp.Internals.Bandwidth.BandwidthManager(10, _timeProvider);
        bandwidth.SetGlobalLimits(1, 0);
        await using var torrent = TorrentTestUtility.CreateMinimal(_torrent.InfoFile, timeProvider: _timeProvider, bandwidth: bandwidth);
        await using var manager = new WebSeedManager(torrent, [], _timeProvider);
        manager.SetTestClient(new MockHttpClient { ResponseBytes = new byte[64] });
        var downloading = manager.DownloadSingleFilePieceAsync(new("http://seed.com/file", false), 0, 64, TestContext.Current.CancellationToken);
        Assert.False(downloading.IsCompleted);
        var started = _timeProvider.GetUtcNow();
        await TorrentTestUtility.AdvanceUntilAsync(_timeProvider, () =>
        {
            bandwidth.Update(null);
            return downloading.IsCompleted;
        }, TimeSpan.FromSeconds(1));
        Assert.Equal(new byte[64], await downloading);
        Assert.True(_timeProvider.GetUtcNow() - started >= TimeSpan.FromSeconds(64));
    }

    private sealed class StalledBody : MemoryStream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task AMultiFileTorrentWithOneFileUsesItsFilePath()
    {
        var info = new BDict();
        info.Dict["name"] = new BString("folder"u8.ToArray());
        info.Dict["piece length"] = new BNumber(16384);
        info.Dict["pieces"] = new BString(System.Security.Cryptography.SHA1.HashData(new byte[4]));
        var file = new BDict();
        file.Dict["length"] = new BNumber(4);
        file.Dict["path"] = new BList { List = { new BString("file.bin"u8.ToArray()) } };
        info.Dict["files"] = new BList { List = { file } };
        var root = new BDict { Dict = { ["info"] = info } };
        var parsed = TorrentFile.Parse(BencodeWriter.Write(root));
        await using var torrent = TorrentTestUtility.CreateMinimal(parsed.Metadata);
        await using var manager = new WebSeedManager(torrent, ["http://seed.test/root/"], _timeProvider);
        var client = new MockHttpClient { ResponseBytes = new byte[4] };
        manager.SetTestClient(client);
        var source = (WebSeedManager.WebSeedSource)((System.Collections.IList)typeof(WebSeedManager).GetField("_sources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(manager)!)[0]!;
        Assert.True(source.IsMultiFile);
        Assert.NotNull(await manager.DownloadMultiFilePieceAsync(source, 0, 0, 4, CancellationToken.None));
        Assert.Equal("http://seed.test/root/folder/file.bin", client.SentRequests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact(Timeout = 10000)]
    public async Task AddingTheFirstWebSeedToARunningTorrentCreatesItsWorker()
    {
        await _torrent.StartAsync();
        Assert.Null(_torrent.WebSeedManager);
        Assert.True(_torrent.WebSeeds.Add("http://seed.invalid/file"));
        Assert.NotNull(_torrent.WebSeedManager);
        Assert.Equal(new[] { "http://seed.invalid/file" }, _torrent.WebSeedManager.GetSourceUrls());
        Assert.True(_torrent.WebSeeds.Remove("http://seed.invalid/file"));
        Assert.Empty(_torrent.WebSeedManager.GetSourceUrls());
        await _torrent.StopAsync();
        Assert.Null(_torrent.WebSeedManager);
    }
}





