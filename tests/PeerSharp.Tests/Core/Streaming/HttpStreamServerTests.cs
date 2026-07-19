using System.Net;
using PeerSharp.Streaming;

namespace PeerSharp.Tests.Core.Streaming;

public class HttpStreamServerTests
{
    [Fact(Timeout = 30000)]
    public async Task Get_Stream_ReturnsFileBytesAndMimeType()
    {
        var data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var handler = new HttpStreamRequestHandler(new FakeTorrent("movie.mp4", data), 0);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("GET", "/stream"), response);

        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.ContentType);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.Equal(data, response.BodyBytes);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_WithRange_ReturnsPartialContent()
    {
        var data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var handler = new HttpStreamRequestHandler(new FakeTorrent("clip.webm", data), 0);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("GET", "/stream", "bytes=2-5"), response);

        Assert.Equal((int)HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 2-5/16", response.Headers["Content-Range"]);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, response.BodyBytes);
    }

    [Fact(Timeout = 30000)]
    public async Task Head_Stream_ReturnsHeadersWithoutBody()
    {
        var handler = new HttpStreamRequestHandler(new FakeTorrent("audio.flac", [1, 2, 3, 4]), 0);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("HEAD", "/stream"), response);

        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/flac", response.ContentType);
        Assert.Equal(4, response.ContentLength);
        Assert.Empty(response.BodyBytes);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_InvalidPath_ReturnsNotFound()
    {
        var handler = new HttpStreamRequestHandler(new FakeTorrent("movie.mp4", [1]), 0);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("GET", "/missing"), response);

        Assert.Equal((int)HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_InvalidFileIndex_ReturnsNotFound()
    {
        var handler = new HttpStreamRequestHandler(new FakeTorrent("movie.mp4", [1]), 1);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("GET", "/stream"), response);

        Assert.Equal((int)HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_InvalidRange_ReturnsRangeNotSatisfiable()
    {
        var handler = new HttpStreamRequestHandler(new FakeTorrent("movie.bin", [1, 2, 3, 4]), 0);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("GET", "/stream", "bytes=4-9"), response);

        Assert.Equal((int)HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */4", response.Headers["Content-Range"]);
    }

    [Theory]
    [InlineData("movie.mkv", "video/x-matroska")]
    [InlineData("clip.avi", "video/x-msvideo")]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("file.bin", "application/octet-stream")]
    public void GetMimeType_ReturnsExpectedType(string path, string expected)
    {
        Assert.Equal(expected, HttpStreamMimeTypes.GetMimeType(path));
    }

    [Theory]
    // No header (or unknown unit) => serve whole file as 200.
    [InlineData(null, 16, true, false, 0, 15)]
    [InlineData("items=1-2", 16, true, false, 0, 15)]
    // Open-ended high: bytes=N- => N..end.
    [InlineData("bytes=2-", 16, true, true, 2, 15)]
    // RFC 7233 §2.1 suffix-byte-range-spec: "bytes=-N" is the LAST N bytes.
    [InlineData("bytes=-5", 16, true, true, 11, 15)]
    [InlineData("bytes=-1", 16, true, true, 15, 15)]
    // Suffix larger than file clamps to start=0 (i.e. whole file, but as a 206).
    [InlineData("bytes=-100", 16, true, true, 0, 15)]
    // Closed range, valid.
    [InlineData("bytes=15-15", 16, true, true, 15, 15)]
    [InlineData("bytes=0-0", 16, true, true, 0, 0)]
    // Closed range, out of bounds.
    [InlineData("bytes=16-16", 16, false, true, 16, 16)]
    // start > end => 416.
    [InlineData("bytes=8-7", 16, false, true, 8, 7)]
    public void RangeParser_ParsesRanges(string? header, long totalLength, bool valid, bool partial, long start, long end)
    {
        var range = HttpRangeParser.Parse(header, totalLength);

        Assert.Equal(valid, range.IsValid);
        Assert.Equal(partial, range.IsPartial);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    [Theory]
    // Multi-range is not supported; reject rather than serving the first range or the whole file.
    [InlineData("bytes=0-5,10-15")]
    // Non-numeric.
    [InlineData("bytes=foo-bar")]
    [InlineData("bytes=1-bar")]
    // Suffix of zero or negative is meaningless.
    [InlineData("bytes=-0")]
    [InlineData("bytes=--5")]
    // Missing dash.
    [InlineData("bytes=5")]
    // Empty value.
    [InlineData("bytes=")]
    public void RangeParser_RejectsMalformedRanges(string header)
    {
        var range = HttpRangeParser.Parse(header, 16);

        Assert.False(range.IsValid);
        Assert.True(range.IsPartial);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_SuffixRange_ReturnsLastBytes()
    {
        // End-to-end check that the handler honors the corrected suffix-byte-range semantics.
        var data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var handler = new HttpStreamRequestHandler(new FakeTorrent("clip.webm", data), 0);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("GET", "/stream", "bytes=-5"), response);

        Assert.Equal((int)HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 11-15/16", response.Headers["Content-Range"]);
        Assert.Equal(new byte[] { 11, 12, 13, 14, 15 }, response.BodyBytes);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_MultiRange_ReturnsRangeNotSatisfiable()
    {
        // We don't support multipart/byteranges; rather than silently serving the first range
        // we return 416 so callers fall back to a single-range request.
        var data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var handler = new HttpStreamRequestHandler(new FakeTorrent("clip.webm", data), 0);
        var response = new FakeResponse();

        await handler.ProcessAsync(new FakeRequest("GET", "/stream", "bytes=0-3,8-11"), response);

        Assert.Equal((int)HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */16", response.Headers["Content-Range"]);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_Cancelled_StopsStreamingBeforeCompletion()
    {
        // ProcessAsync now takes a CancellationToken so the listener can tear down in-flight
        // requests on Dispose. Verify the token short-circuits the response loop.
        var data = Enumerable.Range(0, 200_000).Select(i => (byte)(i & 0xFF)).ToArray();
        var handler = new HttpStreamRequestHandler(new FakeTorrent("big.bin", data), 0);
        var response = new FakeResponse();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.ProcessAsync(new FakeRequest("GET", "/stream"), response, cts.Token));

        // Headers may have been set before the body loop entered; the body must not be complete.
        Assert.True(response.BodyBytes.Length < data.Length);
    }

    private sealed record FakeRequest(string Method, string Path, string? RangeHeader = null) : IHttpStreamRequest;

    private sealed class FakeResponse : IHttpStreamResponse
    {
        private readonly MemoryStream _body = new();

        public Stream Body => _body;
        public byte[] BodyBytes => _body.ToArray();
        public long ContentLength { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Version ProtocolVersion { get; set; } = new(1, 0);
        public int StatusCode { get; set; }

        public void AddHeader(string name, string value)
        {
            Headers[name] = value;
        }
    }

    private sealed class FakeTorrent : ITorrent
    {
        private readonly byte[] _data;
        private readonly IReadOnlyList<TorrentFileInfo> _files;

        public FakeTorrent(string path, byte[] data)
        {
            _data = data;
            _files = [new TorrentFileInfo(path, data.Length, 0, data.Length)];
        }

        public long DataLeft => 0;
        public int DownloadLimitBytesPerSecond { get; set; }
        public int DiskReadLimitBytesPerSecond { get; set; }
        public int DiskWriteLimitBytesPerSecond { get; set; }
        public DownloadStrategy DownloadStrategy { get; set; }
        public ITorrentEvents? Events => null;
        public int FileCount => _files.Count;
        public IFiles Files => throw new NotImplementedException();
        public IFileTransfer FileTransfer => throw new NotImplementedException();
        public bool Finished => true;
        public ulong FinishedBytes => (ulong)_data.Length;
        public ulong FinishedSelectedBytes => (ulong)_data.Length;
        public InfoHash Hash => InfoHash.Empty;
        public InfoHash HashV2 => InfoHash.EmptyV2;
        public bool HasMetadata => true;
        public bool HasStreamableFiles => true;
        public Exception? LastException => null;
        public IMetadataDownload? MetadataDownload => null;
        public string Name => _files[0].Path;
        public IPeers Peers => throw new NotImplementedException();
        public int PieceCount => 1;
        public uint PieceSize => (uint)_data.Length;
        public int PiecesReceived => 1;
        public float Progress => 1;
        public bool QueueAutoStart { get; set; }
        public int QueuePriority { get; set; }
        public float? RatioLimit { get; set; }
        public TimeSpan? SeedTimeLimit { get; set; }
        public bool SelectionFinished => true;
        public float SelectionProgress => 1;
        public bool Started => true;
        public TorrentState State => TorrentState.Active;
        public DateTimeOffset StateTimestamp => DateTimeOffset.UtcNow;
        public IReadOnlyList<int> StreamableFileIndices => [0];
        public DateTimeOffset TimeAdded => DateTimeOffset.UtcNow;
        public long TotalSize => _data.Length;
        public ITrackers Trackers => throw new NotImplementedException();
        public int UploadLimitBytesPerSecond { get; set; }

        public Task<int> ForceRecheckAsync(IProgress<PieceCheckProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public IReadOnlyList<TorrentFileInfo> GetAllFileInfo() => _files;
        public IReadOnlyList<FileSelection> GetAllFileSelections() => Array.Empty<FileSelection>();
        public TorrentFileInfo GetFileInfo(int fileIndex) => _files[fileIndex];
        public FileSelection GetFileSelection(int fileIndex) => throw new NotImplementedException();
        public byte[] GetPieceBitfield() => [0x80];
        public TorrentResumeData GetResumeData() => throw new NotImplementedException();
        public Task<Stream> OpenStreamAsync(int fileIndex, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_data, writable: false));

        public Task SetAllFilesPriorityAsync(Priority priority, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDownloadPathAsync(string path) => Task.CompletedTask;
        public Task SetFilePriorityAsync(int fileIndex, Priority priority, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFileSelectionAsync(int fileIndex, FileSelection selection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RegisterPeerTransport(IPeerTransport transport) => throw new NotImplementedException();
    }
}
