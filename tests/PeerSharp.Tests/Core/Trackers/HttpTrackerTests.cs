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

            return Task.FromResult(ResponseBytes ?? []);
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
        public MultiScrapeResponse? MultiScrapeResponse { get; private set; }
        public string? ErrorMessage { get; private set; }

        public void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage = null)
        {
            Success = success;
            AnnounceResponse = response;
            ErrorMessage = errorMessage;
        }

        public void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker)
        {
            Success = success;
            MultiScrapeResponse = response;
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
        dict.Dict["peers"] = new BString([65, 65, 65, 65, 65, 65]);
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
        _torrent.InfoFile.Info.Hash = InfoHash.CreateRandom();
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        // Response: d5:filesd20:INFO_HASH_HERE_20B_d8:completei10e10:downloadedi50e10:incompletei2eeee
        var infoDict = new BDict();
        infoDict.Dict["complete"] = new BNumber(10);
        infoDict.Dict["incomplete"] = new BNumber(2);
        infoDict.Dict["downloaded"] = new BNumber(50);

        var filesDict = new BDict();
        filesDict.Dict[Encoding.Latin1.GetString(_torrent.InfoFile.Info.GetTrackerInfoHash().Span)] = infoDict;

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
    public async Task ScrapeAsync_StatsKeyedByDifferentHash_RaisesFailure()
    {
        // A response about some other torrent must not be mistaken for ours
        _torrent.InfoFile.Info.Hash = InfoHash.CreateRandom();
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var infoDict = new BDict();
        infoDict.Dict["complete"] = new BNumber(10);
        infoDict.Dict["incomplete"] = new BNumber(2);
        infoDict.Dict["downloaded"] = new BNumber(50);

        var filesDict = new BDict();
        filesDict.Dict[Encoding.Latin1.GetString(InfoHash.CreateRandom().Span)] = infoDict;

        var root = new BDict();
        root.Dict["files"] = filesDict;
        _mockHttp.ResponseBytes = BencodeWriter.Write(root);

        await tracker.ScrapeAsync(CancellationToken.None);

        Assert.False(_callback.Success);
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

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_FailureReasonResponse_SurfacesErrorMessage()
    {
        // BEP 3: a tracker may reply with {'failure reason': '...'} and nothing else.
        // This used to be silently parsed as Success=true with 0 peers.
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["failure reason"] = new BString(Encoding.UTF8.GetBytes("torrent not registered"));
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.ErrorMessage);
        Assert.Contains("torrent not registered", _callback.ErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_Ipv6Peers_ParsesPeers6Field()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        // peers6 entry: 16 bytes IP + 2 bytes port
        byte[] ipv6 = new byte[16];
        ipv6[0] = 0x20; ipv6[1] = 0x01; ipv6[2] = 0x0d; ipv6[3] = 0xb8;
        ipv6[15] = 0x01;
        byte[] peers6 = new byte[18];
        ipv6.CopyTo(peers6, 0);
        peers6[16] = 0x1a; peers6[17] = 0xe1; // port 6881

        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["peers6"] = new BString(peers6);
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.NotNull(_callback.AnnounceResponse);
        Assert.Single(_callback.AnnounceResponse.Peers);
        Assert.Equal("2001:db8::1", _callback.AnnounceResponse.Peers[0].Address.ToString());
        Assert.Equal(6881, _callback.AnnounceResponse.Peers[0].Port);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_MinInterval_IsParsed()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["min interval"] = new BNumber(300);
        dict.Dict["peers"] = new BString([]);
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.Equal(300u, _callback.AnnounceResponse?.MinInterval);
    }

    [Fact(Timeout = 30000)]
    public async Task ScrapeAsync_FailureReasonResponse_RaisesFailure()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["failure reason"] = new BString(Encoding.UTF8.GetBytes("scrape not supported"));
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.ScrapeAsync(CancellationToken.None);

        Assert.False(_callback.Success);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_ValidResponse_ParsesAllHashStats()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var firstHashBytes = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 1)).ToArray();
        var secondHashBytes = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(255 - i)).ToArray();
        var firstStats = CreateScrapeStats(12, 3, 44);
        var secondStats = CreateScrapeStats(7, 1, 9);
        var files = new BDict();
        files.Dict[Encoding.Latin1.GetString(firstHashBytes)] = firstStats;
        files.Dict[Encoding.Latin1.GetString(secondHashBytes)] = secondStats;
        var root = new BDict();
        root.Dict["files"] = files;
        _mockHttp.ResponseBytes = BencodeWriter.Write(root);

        await tracker.MultiScrapeAsync([new InfoHash(firstHashBytes), new InfoHash(secondHashBytes)], CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        Assert.Equal(2, _callback.MultiScrapeResponse.Results.Count);
        var first = _callback.MultiScrapeResponse.Results[Convert.ToHexString(firstHashBytes)];
        Assert.Equal(12u, first.SeedCount);
        Assert.Equal(3u, first.LeechCount);
        Assert.Equal(44u, first.Downloaded);
        var second = _callback.MultiScrapeResponse.Results[Convert.ToHexString(secondHashBytes)];
        Assert.Equal(7u, second.SeedCount);
        Assert.Equal(1u, second.LeechCount);
        Assert.Equal(9u, second.Downloaded);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_SkipsUnsupportedV2HashesInRequestUrl()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = BencodeWriter.Write(new BDict { Dict = { ["files"] = new BDict() } });
        var v1Hash = new InfoHash(Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 1)).ToArray());
        var v2Hash = new InfoHash(Enumerable.Range(0, InfoHash.V2Length).Select(i => (byte)(i + 1)).ToArray());

        await tracker.MultiScrapeAsync([v1Hash, v2Hash], CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.NotNull(_mockHttp.LastUrl);
        Assert.Equal(1, CountOccurrences(_mockHttp.LastUrl, "info_hash="));
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_EmptyList_DoesNotRaiseResult()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        await tracker.MultiScrapeAsync(Array.Empty<InfoHash>(), CancellationToken.None);

        // Early-return branch — no callback should have been fired
        Assert.Null(_callback.MultiScrapeResponse);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_NonAnnounceUrl_DoesNotRaiseResult()
    {
        var tracker = new HttpTracker();
        // URL without "announce" path segment → scrape URL cannot be derived
        tracker.Init("http://tracker.com/peers", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var hash = new InfoHash(Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 1)).ToArray());
        await tracker.MultiScrapeAsync([hash], CancellationToken.None);

        Assert.Null(_callback.MultiScrapeResponse);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_HttpError_RaisesFailure()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.Exception = new HttpRequestException("503 Service Unavailable");

        var hash = new InfoHash(Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 1)).ToArray());
        await tracker.MultiScrapeAsync([hash], CancellationToken.None);

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        Assert.Empty(_callback.MultiScrapeResponse.Results);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_PartialResponse_ReturnsOnlyAvailableStats()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var hashA = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 1)).ToArray();
        var hashB = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 50)).ToArray();
        var hashC = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(100 + i)).ToArray();

        // Tracker only replies for hashA and hashB, omits hashC
        var files = new BDict();
        files.Dict[Encoding.Latin1.GetString(hashA)] = CreateScrapeStats(5, 2, 10);
        files.Dict[Encoding.Latin1.GetString(hashB)] = CreateScrapeStats(3, 1, 7);
        var root = new BDict();
        root.Dict["files"] = files;
        _mockHttp.ResponseBytes = BencodeWriter.Write(root);

        await tracker.MultiScrapeAsync(
            [new InfoHash(hashA), new InfoHash(hashB), new InfoHash(hashC)],
            CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        Assert.Equal(2, _callback.MultiScrapeResponse.Results.Count);
        Assert.True(_callback.MultiScrapeResponse.Results.ContainsKey(Convert.ToHexString(hashA)));
        Assert.True(_callback.MultiScrapeResponse.Results.ContainsKey(Convert.ToHexString(hashB)));
        Assert.False(_callback.MultiScrapeResponse.Results.ContainsKey(Convert.ToHexString(hashC)));
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_UnknownHashInResponse_IncludedInResults()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var requested = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 1)).ToArray();
        var extra = Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(200 - i)).ToArray();

        // Tracker returns stats for the requested hash plus one unrequested hash
        var files = new BDict();
        files.Dict[Encoding.Latin1.GetString(requested)] = CreateScrapeStats(8, 4, 20);
        files.Dict[Encoding.Latin1.GetString(extra)] = CreateScrapeStats(1, 0, 2);
        var root = new BDict();
        root.Dict["files"] = files;
        _mockHttp.ResponseBytes = BencodeWriter.Write(root);

        await tracker.MultiScrapeAsync([new InfoHash(requested)], CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        // ParseMultiScrapeResponse passes through everything in the files dict
        Assert.Equal(2, _callback.MultiScrapeResponse.Results.Count);
        Assert.True(_callback.MultiScrapeResponse.Results.ContainsKey(Convert.ToHexString(requested)));
        Assert.True(_callback.MultiScrapeResponse.Results.ContainsKey(Convert.ToHexString(extra)));
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_FailureReasonResponse_RaisesFailure()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var root = new BDict();
        root.Dict["failure reason"] = new BString(Encoding.UTF8.GetBytes("rate limit exceeded"));
        _mockHttp.ResponseBytes = BencodeWriter.Write(root);

        var hash = new InfoHash(Enumerable.Range(0, InfoHash.V1Length).Select(i => (byte)(i + 1)).ToArray());
        await tracker.MultiScrapeAsync([hash], CancellationToken.None);

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        Assert.Empty(_callback.MultiScrapeResponse.Results);
    }

    private static BDict CreateScrapeStats(long complete, long incomplete, long downloaded)
    {
        var stats = new BDict();
        stats.Dict["complete"] = new BNumber(complete);
        stats.Dict["incomplete"] = new BNumber(incomplete);
        stats.Dict["downloaded"] = new BNumber(downloaded);
        return stats;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}





