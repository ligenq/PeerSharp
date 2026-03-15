using PeerSharp.Internals;
using PeerSharp.Internals.Trackers;
using PeerSharp.Internals.Framework;
using PeerSharp.BEncoding;
using System.Net;
using System.Text;

namespace PeerSharp.Tests.Core.Trackers;

public class HttpTrackerTests
{
    private class MockHttpClient : IHttpClient
    {
        public byte[]? ResponseBytes { get; set; }
        public Exception? Exception { get; set; }
        public string? LastUrl { get; private set; }

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken)
        {
            LastUrl = url;
            if (Exception != null)
            {
                throw Exception;
            }

            return Task.FromResult(ResponseBytes ?? Array.Empty<byte>());
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            if (Exception != null)
            {
                throw Exception;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (ResponseBytes != null)
            {
                response.Content = new ByteArrayContent(ResponseBytes);
            }

            return Task.FromResult(response);
        }
    }

    private class MockCallback : ITrackerCallback
    {
        public bool Success { get; private set; }
        public AnnounceResponse? AnnounceResponse { get; private set; }
        public ScrapeResponse? ScrapeResponse { get; private set; }
        public string? ErrorMessage { get; private set; }

        public void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage = null)
        {
            Success = success;
            AnnounceResponse = response;
            ErrorMessage = errorMessage;
        }

        public void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker)
        {
            throw new NotImplementedException();
        }

        public void OnScrapeResult(bool success, ScrapeResponse response, ITracker tracker)
        {
            Success = success;
            ScrapeResponse = response;
        }
    }

    private readonly Torrent _torrent;
    private readonly MockCallback _callback = new();
    private readonly MockHttpClient _mockHttp = new();

    public HttpTrackerTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_SuccessfulResponse_ParsesPeers()
    {
        // Arrange
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        // Bencoded response: d8:intervali1800e5:peers6:AAAAAAe
        // peers = "AAAAAA" (6 bytes) -> 65.65.65.65:16705
        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["peers"] = new BString(new byte[] { 65, 65, 65, 65, 65, 65 });
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        // Act
        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        // Assert
        Assert.True(_callback.Success);
        Assert.NotNull(_callback.AnnounceResponse);
        Assert.Equal(1800u, _callback.AnnounceResponse.Interval);
        Assert.Single(_callback.AnnounceResponse.Peers);
        Assert.Equal("65.65.65.65", _callback.AnnounceResponse.Peers[0].Address.ToString());
        Assert.Equal(16705, _callback.AnnounceResponse.Peers[0].Port);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_HttpError_RaisesFailure()
    {
        // Arrange
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.Exception = new HttpRequestException("404 Not Found");

        // Act
        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        // Assert
        Assert.False(_callback.Success);
        Assert.Contains("404", _callback.ErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task ScrapeAsync_ValidResponse_ParsesStats()
    {
        // Arrange
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        // Response: d5:filesd20:INFO_HASH_HERE_20B_d8:completei10e10:downloadedi50e10:incompletei2eeee
        var infoDict = new BDict();
        infoDict.Dict["complete"] = new BNumber(10);
        infoDict.Dict["incomplete"] = new BNumber(2);
        infoDict.Dict["downloaded"] = new BNumber(50);

        var filesDict = new BDict();
        filesDict.Dict["somehash"] = infoDict;

        var root = new BDict();
        root.Dict["files"] = filesDict;
        _mockHttp.ResponseBytes = BencodeWriter.Write(root);

        // Act
        await tracker.ScrapeAsync(CancellationToken.None);

        // Assert
        Assert.True(_callback.Success);
        Assert.NotNull(_callback.ScrapeResponse);
        Assert.Equal(10u, _callback.ScrapeResponse.SeedCount);
        Assert.Equal(2u, _callback.ScrapeResponse.LeechCount);
        Assert.Equal(50u, _callback.ScrapeResponse.Downloaded);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_InvalidResponse_RaisesFailure()
    {
        var callback = new MockCallback();
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = Encoding.UTF8.GetBytes("not-bencode");

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.False(callback.Success);
        Assert.NotNull(callback.ErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task ScrapeAsync_InvalidResponse_RaisesFailure()
    {
        var callback = new MockCallback();
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = Encoding.UTF8.GetBytes("invalid");

        await tracker.ScrapeAsync(CancellationToken.None);

        Assert.False(callback.Success);
    }
}





