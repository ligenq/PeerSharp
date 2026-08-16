using PeerSharp.Internals;
using PeerSharp.Internals.Trackers;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.BEncoding;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace PeerSharp.Tests.Core.Trackers;

public class HttpTrackerTests
{
    private sealed class TestPortListener(int port) : IPortListener
    {
        public int Port { get; } = port;
        public void Start(int port) { }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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

    private sealed class FamilyHttpClientFactory(
        Func<AddressFamily?, HttpResponseMessage> responseFactory) : IHttpClientFactory
    {
        public ConcurrentQueue<AddressFamily?> RequestedFamilies { get; } = [];

        public HttpClient CreateClient(
            ProxySettings proxy,
            bool isTracker,
            IPAddress? bindAddress = null,
            AddressFamily? addressFamily = null)
        {
            RequestedFamilies.Enqueue(addressFamily);
            return new HttpClient(new FamilyHandler(() => responseFactory(addressFamily)));
        }
    }

    private sealed class FamilyHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(responseFactory());
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
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

    [Fact]
    public async Task AnnounceAsync_UnboundDirectTracker_AnnouncesBothFamiliesAndMergesResponses()
    {
        byte[] ipv4Response = BuildAnnounceResponse(
            interval: 1800,
            peersKey: "peers",
            peerBytes: [1, 2, 3, 4, 0x1A, 0xE1]);
        byte[] ipv6Peer = [.. IPAddress.IPv6Loopback.GetAddressBytes(), 0x1A, 0xE2];
        byte[] ipv6Response = BuildAnnounceResponse(900, "peers6", ipv6Peer);
        var factory = new FamilyHttpClientFactory(family => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(family == AddressFamily.InterNetwork ? ipv4Response : ipv6Response)
        });
        var tracker = new HttpTracker(NullLoggerFactory.Instance, factory);
        tracker.Init("http://tracker.example/announce", _torrent, _callback);

        await tracker.AnnounceAsync(TrackerEvent.None, TestContext.Current.CancellationToken);

        Assert.True(_callback.Success);
        Assert.Equal(
            [AddressFamily.InterNetwork, AddressFamily.InterNetworkV6],
            factory.RequestedFamilies.OrderBy(family => family).ToArray());
        Assert.Equal(900u, _callback.AnnounceResponse?.Interval);
        Assert.Equal(2, _callback.AnnounceResponse?.Peers.Count);
        Assert.Contains(_callback.AnnounceResponse!.Peers, peer => peer.Address.Equals(IPAddress.Parse("1.2.3.4")));
        Assert.Contains(_callback.AnnounceResponse.Peers, peer => peer.Address.Equals(IPAddress.IPv6Loopback));
    }

    [Fact]
    public async Task AnnounceAsync_WhenIPv6Fails_KeepsSuccessfulIPv4Announce()
    {
        byte[] response = BuildAnnounceResponse(1800, "peers", [1, 2, 3, 4, 0x1A, 0xE1]);
        var factory = new FamilyHttpClientFactory(family =>
        {
            if (family == AddressFamily.InterNetworkV6)
            {
                throw new HttpRequestException("IPv6 unavailable");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
        });
        var tracker = new HttpTracker(NullLoggerFactory.Instance, factory);
        tracker.Init("http://tracker.example/announce", _torrent, _callback);

        await tracker.AnnounceAsync(TrackerEvent.None, TestContext.Current.CancellationToken);

        Assert.True(_callback.Success);
        Assert.Equal(2, factory.RequestedFamilies.Count);
        Assert.Single(_callback.AnnounceResponse!.Peers);
    }

    [Fact]
    public async Task AnnounceAsync_WithExplicitBind_UsesOnlyBoundFamily()
    {
        _torrent.Settings.Connection.BindAddress = IPAddress.Loopback;
        byte[] response = BuildAnnounceResponse(1800, "peers", []);
        var factory = new FamilyHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        });
        var tracker = new HttpTracker(NullLoggerFactory.Instance, factory);
        tracker.Init("http://tracker.example/announce", _torrent, _callback);

        await tracker.AnnounceAsync(TrackerEvent.None, TestContext.Current.CancellationToken);

        Assert.True(_callback.Success);
        Assert.Equal([AddressFamily.InterNetwork], factory.RequestedFamilies);
    }

    private static byte[] BuildAnnounceResponse(uint interval, string peersKey, byte[] peerBytes)
    {
        var response = new BDict();
        response.Dict["interval"] = new BNumber(interval);
        response.Dict[peersKey] = new BString(peerBytes);
        return BencodeWriter.Write(response);
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
    public async Task AnnounceAsync_DoesNotDiscloseLocalIpAddresses()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = BencodeWriter.Write(new BDict());

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var url = _mockHttp.LastUrl!;
        Assert.DoesNotContain("ipv6=", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ipv4=", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_UsesTheActuallyBoundListenPort()
    {
        _torrent.Settings.Connection.TcpPort = 0;
        _torrent.PortListener = new TestPortListener(23456);
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = BencodeWriter.Write(new BDict());

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.Contains("port=23456", _mockHttp.LastUrl);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_EchoesTrackerIdOnTheNextAnnounce()
    {
        // BEP 3: a tracker that issues a session token expects to see it again. One that never gets it
        // back has no way to tie our announces together and may treat each as a new session.
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["tracker id"] = new BString(System.Text.Encoding.UTF8.GetBytes("s/42=x"));
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.Equal("s/42=x", _callback.AnnounceResponse?.TrackerId);
        Assert.DoesNotContain("trackerid=", _mockHttp.LastUrl);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        // Percent-encoded, because the value is opaque and may contain reserved characters.
        // Percent-encoded: the value is opaque and may contain characters that would otherwise
        // be read as query syntax.
        Assert.Contains("trackerid=s%2F42%3Dx", _mockHttp.LastUrl);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_ResponseWithoutTrackerId_DoesNotForgetTheOldOne()
    {
        // A response that simply omits the key is not the tracker withdrawing it.
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var withId = new BDict();
        withId.Dict["interval"] = new BNumber(1800);
        withId.Dict["tracker id"] = new BString(System.Text.Encoding.UTF8.GetBytes("abc"));
        _mockHttp.ResponseBytes = BencodeWriter.Write(withId);
        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var without = new BDict();
        without.Dict["interval"] = new BNumber(1800);
        _mockHttp.ResponseBytes = BencodeWriter.Write(without);
        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.Contains("trackerid=abc", _mockHttp.LastUrl);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_WarningMessage_IsSurfacedWithoutFailingTheAnnounce()
    {
        // BEP 3 distinguishes a warning from a failure: the response is valid and its peers usable.
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["warning message"] = new BString(System.Text.Encoding.UTF8.GetBytes("unregistered"));
        dict.Dict["peers"] = new BString([65, 65, 65, 65, 65, 65]);
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.Equal("unregistered", _callback.AnnounceResponse?.WarningMessage);
        Assert.Single(_callback.AnnounceResponse!.Peers);
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
    public async Task AnnounceAsync_ResponseOverLimit_RaisesFailure()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.ResponseBytes = new byte[(1024 * 1024) + 1];

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.False(_callback.Success);
        Assert.Contains("exceeds maximum size", _callback.ErrorMessage);
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

    /// <summary>
    /// Announces once and returns the URL that was requested.
    /// </summary>
    private async Task<string> CaptureAnnounceUrlAsync(Torrent torrent, TrackerEvent evt)
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(evt, CancellationToken.None);

        Assert.NotNull(_mockHttp.LastUrl);
        return _mockHttp.LastUrl;
    }

    /// <summary>
    /// A partial seed: everything selected is present, but the torrent as a whole is not complete.
    /// </summary>
    private static Torrent CreatePartialSeed()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 32768; // Nothing finished, so DataLeft > 0.
        metadata.Info.Pieces = [new byte[20], new byte[20]]; // Enough for HasMetadata.
        return TorrentTestUtility.CreateMinimal(metadata);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_AsPartialSeed_SendsEventPaused()
    {
        // BEP 21: a partial seed "MUST send an event=paused parameter in every announce while it is a
        // partial seed", which is how a tracker tells it apart from an ordinary incomplete peer.
        var url = await CaptureAnnounceUrlAsync(CreatePartialSeed(), TrackerEvent.None);

        Assert.Contains("event=paused", url);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_WithoutMetadata_SendsNoEvent()
    {
        // A torrent with no metadata reports DataLeft as 1 to mean "unknown", not "one byte short". A
        // magnet link that has not fetched its metadata is the furthest thing from a partial seed, so
        // the paused state must not be inferred from that sentinel.
        var url = await CaptureAnnounceUrlAsync(_torrent, TrackerEvent.None);

        Assert.DoesNotContain("event=", url);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_AsFullSeed_SendsNoEvent()
    {
        // Everything is present, so this is a plain seed rather than a partial one.
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = 16384;
        metadata.Info.FullSize = 16384;
        metadata.Info.Pieces = [new byte[20]];
        var torrent = TorrentTestUtility.CreateMinimal(metadata);
        torrent.Pieces.SetHaveAll();

        var url = await CaptureAnnounceUrlAsync(torrent, TrackerEvent.None);

        Assert.DoesNotContain("event=", url);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_PartialSeedStarting_KeepsTheRealEvent()
    {
        // Replacing a transition the tracker needs would lose information rather than add it.
        var url = await CaptureAnnounceUrlAsync(CreatePartialSeed(), TrackerEvent.Started);

        Assert.Contains("event=started", url);
        Assert.DoesNotContain("event=paused", url);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_PartialSeedStopping_KeepsTheRealEvent()
    {
        var url = await CaptureAnnounceUrlAsync(CreatePartialSeed(), TrackerEvent.Stopped);

        Assert.Contains("event=stopped", url);
        Assert.DoesNotContain("event=paused", url);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_ExternalIpV4_IsParsed()
    {
        // BEP 24: 4 raw bytes, no port.
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["external ip"] = new BString([203, 0, 113, 7]);
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), _callback.AnnounceResponse!.ExternalIp);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_ExternalIpV6_IsParsed()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var expected = IPAddress.Parse("2001:db8::1");
        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["external ip"] = new BString(expected.GetAddressBytes());
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.Equal(expected, _callback.AnnounceResponse!.ExternalIp);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public async Task AnnounceAsync_ExternalIpWithWrongLength_IsIgnored(int length)
    {
        // An IPAddress cannot be built from anything but 4 or 16 bytes, so a malformed value has to
        // be dropped rather than thrown - the peer list in the same response is still good.
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["interval"] = new BNumber(1800);
        dict.Dict["external ip"] = new BString(new byte[length]);
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.True(_callback.Success);
        Assert.Null(_callback.AnnounceResponse!.ExternalIp);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_FailureWithRetryIn_SurfacesHint()
    {
        // BEP 31: d14:failure reason...8:retry ini30ee
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["failure reason"] = new BString("Down for maintenance"u8.ToArray());
        dict.Dict["retry in"] = new BNumber(30);
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.False(_callback.Success);
        Assert.Contains("Down for maintenance", _callback.ErrorMessage);
        var hint = _callback.AnnounceResponse!.RetryHint;
        Assert.NotNull(hint);
        Assert.False(hint.Value.Never);
        Assert.Equal(TimeSpan.FromMinutes(30), hint.Value.RetryIn);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_FailureWithRetryNever_SurfacesNeverHint()
    {
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);

        var dict = new BDict();
        dict.Dict["failure reason"] = new BString("Not a tracker"u8.ToArray());
        dict.Dict["retry in"] = new BString("never"u8.ToArray());
        _mockHttp.ResponseBytes = BencodeWriter.Write(dict);

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.False(_callback.Success);
        Assert.True(_callback.AnnounceResponse!.RetryHint!.Value.Never);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_TransportFailure_HasNoRetryHint()
    {
        // Only the tracker's own failure response carries a hint. A timeout tells us nothing about
        // when it wants us back, and must leave the manager on its own backoff.
        var tracker = new HttpTracker();
        tracker.Init("http://tracker.com/announce", _torrent, _callback);
        tracker.SetTestClient(_mockHttp);
        _mockHttp.Exception = new HttpRequestException("connection refused");

        await tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        Assert.False(_callback.Success);
        Assert.Null(_callback.AnnounceResponse!.RetryHint);
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

