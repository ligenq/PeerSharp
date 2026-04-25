using System.Net;
using PeerSharp.Streaming;

namespace PeerSharp.Tests.Core.Streaming;

public class HttpStreamServerTests
{
    [Fact(Timeout = 30000)]
    public async Task Get_Stream_ReturnsFileBytesAndMimeType()
    {
        var data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", data), 0);
        using var client = new HttpClient();
        server.Start();

        using var response = await client.GetAsync(server.Url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal(data, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Get_WithRange_ReturnsPartialContent()
    {
        var data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        using var server = new HttpStreamServer(new FakeTorrent("clip.webm", data), 0);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        server.Start();

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 2-5/16", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Head_Stream_ReturnsHeadersWithoutBody()
    {
        using var server = new HttpStreamServer(new FakeTorrent("audio.flac", new byte[] { 1, 2, 3, 4 }), 0);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, server.Url);
        server.Start();

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Get_InvalidPath_ReturnsNotFound()
    {
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", new byte[] { 1 }), 0);
        using var client = new HttpClient();
        server.Start();

        using var response = await client.GetAsync(server.Url.Replace("/stream", "/missing"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_InvalidFileIndex_ReturnsNotFound()
    {
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", new byte[] { 1 }), 1);
        using var client = new HttpClient();
        server.Start();

        using var response = await client.GetAsync(server.Url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_InvalidRange_ReturnsRangeNotSatisfiable()
    {
        using var server = new HttpStreamServer(new FakeTorrent("movie.bin", new byte[] { 1, 2, 3, 4 }), 0);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(4, 9);
        server.Start();

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */4", response.Content.Headers.ContentRange?.ToString());
    }

    private sealed class FakeTorrent : ITorrent
    {
        private readonly byte[] _data;
        private readonly IReadOnlyList<TorrentFileInfo> _files;

        public FakeTorrent(string path, byte[] data)
        {
            _data = data;
            _files = new[] { new TorrentFileInfo(path, data.Length, 0, data.Length) };
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
        public IReadOnlyList<int> StreamableFileIndices => new[] { 0 };
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
        public byte[] GetPieceBitfield() => new byte[] { 0x80 };
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
