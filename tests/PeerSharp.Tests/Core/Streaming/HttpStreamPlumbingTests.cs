using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Streaming;
using static PeerSharp.Tests.Core.Streaming.HttpStreamRequestHandlerTests;

namespace PeerSharp.Tests.Core.Streaming;

/// <summary>
/// The server itself: its listener, the URL it advertises, and its shutdown. What it serves is
/// covered by <see cref="HttpStreamRequestHandlerTests"/>, and how it speaks HTTP on a connection by
/// <see cref="HttpStreamConnectionTests"/>.
/// </summary>
public class HttpStreamServerTests
{
    [Fact]
    public void TheUrlIsAvailableBeforeStartSoAPlayerCanBeHandedItImmediately()
    {
        // A caller starts the server and hands the URL to a player in the same breath. Requiring
        // Start first would make that ordering a trap rather than a choice.
        using var server = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);

        Assert.StartsWith("http://127.0.0.1:", server.Url, StringComparison.Ordinal);
        Assert.EndsWith("/stream", server.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void EachServerBindsItsOwnPortSoTwoCanRunAtOnce()
    {
        // Streaming two files from one torrent means two servers, and a fixed port would make the
        // second one fail on a machine that is already streaming.
        using var first = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);
        using var second = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);

        Assert.NotEqual(first.Url, second.Url);
    }

    [Fact]
    public async Task StartBindsTheListenerSoTheUrlAnswers()
    {
        using var server = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);
        server.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(server.Url, TestContext.Current.CancellationToken);

        // What it answers is the handler's business; that it answers at all is the server's.
        Assert.NotEqual(0, (int)response.StatusCode);
    }

    [Fact]
    public async Task AFileIsServedOverARealConnection()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", data), 0);
        server.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(data[100..200], await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARangeRequestAfterAnotherReusesTheConnection()
    {
        // Seeking is a new range request, and a player seeking often should not pay for a new
        // connection each time.
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", data), 0);
        server.Start();
        var uri = new Uri(server.Url);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(uri.Host, uri.Port, TestContext.Current.CancellationToken);
        await using var connection = new NetworkStream(socket);

        foreach (var (start, end) in new[] { (0, 9), (500, 509) })
        {
            var ask = $"GET {uri.AbsolutePath} HTTP/1.1\r\nHost: {uri.Authority}\r\nRange: bytes={start}-{end}\r\n\r\n";
            await connection.WriteAsync(Encoding.ASCII.GetBytes(ask), TestContext.Current.CancellationToken);

            var (head, body) = await ReadResponseAsync(connection, 10);
            Assert.StartsWith("HTTP/1.1 206", head, StringComparison.Ordinal);
            Assert.Contains("Connection: keep-alive", head, StringComparison.Ordinal);
            Assert.Equal(data[start..(end + 1)], body);
        }
    }

    [Fact]
    public void ALoopbackServerNeedsNoTokenUnlessGivenOne()
    {
        using var plain = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);
        using var tokened = new HttpStreamServer(
            TorrentTestUtility.CreateMinimal(),
            0,
            new HttpStreamServerOptions { AccessToken = "abc-123_XYZ" });

        Assert.Equal("/stream", new Uri(plain.Url).AbsolutePath);
        Assert.Equal("/abc-123_XYZ/stream", new Uri(tokened.Url).AbsolutePath);
    }

    [Fact]
    public void AServerOnTheNetwork_AlwaysRequiresAToken()
    {
        // Anything that can reach a network address can ask it for the file, so a server bound to
        // one is never left open. The token is generated, so the caller cannot forget it.
        var address = NetworkAddressOfThisMachine();
        Assert.SkipWhen(address is null, "This machine has no network address other than loopback.");

        using var first = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, new HttpStreamServerOptions { BindAddress = address! });
        using var second = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, new HttpStreamServerOptions { BindAddress = address! });

        var path = new Uri(first.Url).AbsolutePath;
        Assert.Matches("^/[A-Za-z0-9_-]{22}/stream$", path);
        Assert.NotEqual(path, new Uri(second.Url).AbsolutePath);
        Assert.Equal(address!.ToString(), new Uri(first.Url).Host);
    }

    [Fact]
    public async Task AServerOnTheNetwork_RefusesARequestWithoutTheToken()
    {
        var address = NetworkAddressOfThisMachine();
        Assert.SkipWhen(address is null, "This machine has no network address other than loopback.");

        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", [1, 2, 3]), 0, new HttpStreamServerOptions { BindAddress = address! });
        server.Start();
        var withoutToken = new UriBuilder(server.Url) { Path = "/stream" }.Uri;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var refused = await client.GetAsync(withoutToken, TestContext.Current.CancellationToken);
        using var served = await client.GetAsync(server.Url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void AWildcardAddressIsRefused(string address)
    {
        // The server advertises one URL, and a URL naming every interface names none of them.
        var options = new HttpStreamServerOptions { BindAddress = IPAddress.Parse(address) };

        Assert.Throws<ArgumentException>(() => new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/inside")]
    [InlineData("query?")]
    public void ATokenThatIsNotUrlSafeIsRefused(string token)
    {
        var options = new HttpStreamServerOptions { AccessToken = token };

        Assert.Throws<ArgumentException>(() => new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, options));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void APortOutOfRangeIsRefused(int port)
    {
        var options = new HttpStreamServerOptions { Port = port };

        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, options));
    }

    [Fact]
    public void ThePortAsked_ForIsThePortUsed()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var server = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, new HttpStreamServerOptions { Port = port });

        Assert.Equal(port, new Uri(server.Url).Port);
    }

    [Fact]
    public void AnIPv6AddressIsWrittenInBrackets()
    {
        using var server = new HttpStreamServer(
            TorrentTestUtility.CreateMinimal(),
            0,
            new HttpStreamServerOptions { BindAddress = IPAddress.IPv6Loopback });

        Assert.StartsWith("http://[::1]:", server.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContentTypeIsWhatTheFileIsServedAs()
    {
        // A cast sender puts this in its load request, so it has to agree with what is served.
        using var server = new HttpStreamServer(new FakeTorrent("movie.m4v", [1]), 0);

        Assert.Equal("video/mp4", server.ContentType);
    }

    [Fact]
    public void TheContentTypeIsGenericUntilTheMetadataNamesTheFile()
    {
        using var server = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);

        Assert.Equal("application/octet-stream", server.ContentType);
    }

    [Fact]
    public void ANullLoggerFactoryIsRefusedRatherThanFailingLater()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, (Microsoft.Extensions.Logging.ILoggerFactory)null!));
    }

    [Fact]
    public void NullOptionsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, (HttpStreamServerOptions)null!));
    }

    [Fact]
    public void StartingTwiceIsRefused()
    {
        using var server = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);
        server.Start();

        Assert.Throws<InvalidOperationException>(server.Start);
    }

    [Fact]
    public void StartingAfterDisposeIsRefused()
    {
        var server = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);
        server.Dispose();

        Assert.Throws<ObjectDisposedException>(server.Start);
    }

    [Fact]
    public void DisposeStopsTheListenerAndIsRepeatable()
    {
        var server = new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0);
        server.Start();

        server.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task DisposeEndsAConnectionThatIsStillBeingServed()
    {
        // A read waiting on the swarm must not outlive the server, or the stream it holds keeps the
        // torrent prioritising pieces for a player that has gone.
        var stalled = new TaskCompletionSource();
        var torrent = new FakeTorrent("movie.mp4", new byte[1000], () => new NeverDeliveringStream(1000, stalled));
        var server = new HttpStreamServer(torrent, 0);
        server.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var fetching = client.GetAsync(server.Url, TestContext.Current.CancellationToken);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        server.Dispose();

        // The response was never started, so it ends as a closed connection rather than a status.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => fetching);
    }

    [Fact]
    public void DisposingWithoutStartingIsSafe()
    {
        // A caller that decides against streaming after constructing the server still disposes it.
        new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0).Dispose();
    }

    private static IPAddress? NetworkAddressOfThisMachine() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    /// <summary>Reads one response with a Content-Length body from a raw connection.</summary>
    private static async Task<(string Head, byte[] Body)> ReadResponseAsync(Stream connection, int bodyLength)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
        {
            int read = await connection.ReadAsync(one, TestContext.Current.CancellationToken);
            Assert.NotEqual(0, read);
            bytes.Add(one[0]);
        }

        var body = new byte[bodyLength];
        await connection.ReadExactlyAsync(body, TestContext.Current.CancellationToken);
        return (Encoding.ASCII.GetString(bytes.ToArray()), body);
    }

    /// <summary>A stream whose first read waits for data that never comes.</summary>
    private sealed class NeverDeliveringStream(long length, TaskCompletionSource reading) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => Position = offset;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ASideFile_IsServedOverARealConnection_AtTheUrlItWasGiven()
    {
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", [1]), 0);
        string url = server.AddFile("english.vtt", "text/vtt", () => "WEBVTT\n"u8.ToArray());
        server.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var body = await client.GetStringAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(server.Url[..^"stream".Length] + "english.vtt", url);
        Assert.Equal("WEBVTT\n", body);
    }

    [Fact]
    public void ASideFileOnAServerWithAToken_CarriesTheToken()
    {
        using var server = new HttpStreamServer(
            new FakeTorrent("movie.mp4", [1]), 0, new HttpStreamServerOptions { BindAddress = IPAddress.Loopback, AccessToken = "secret" });

        Assert.EndsWith("/secret/subs.vtt", server.AddFile("subs.vtt", "text/vtt", () => ReadOnlyMemory<byte>.Empty), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stream")]
    [InlineData("..")]
    [InlineData("sub/titles.vtt")]
    [InlineData("subs?.vtt")]
    public void ASideFileName_MustBeSafeAndNotTheStreams(string name)
    {
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", [1]), 0);

        Assert.ThrowsAny<ArgumentException>(() => server.AddFile(name, "text/vtt", () => ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void ASideFile_NeedsATypeAndContent()
    {
        using var server = new HttpStreamServer(new FakeTorrent("movie.mp4", [1]), 0);

        Assert.ThrowsAny<ArgumentException>(() => server.AddFile("subs.vtt", "", () => ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentNullException>(() => server.AddFile("subs.vtt", "text/vtt", null!));
    }
}

public class HttpRangeParserTests
{
    [Fact]
    public void NoRangeHeaderMeansTheWholeFileAsATwoHundred()
    {
        var range = HttpRangeParser.Parse(null, 1000);

        Assert.True(range.IsValid);
        Assert.False(range.IsPartial);
        Assert.Equal(0, range.Start);
        Assert.Equal(999, range.End);
    }

    [Fact]
    public void AUnitOtherThanBytesIsTreatedAsNoRangeAtAll()
    {
        // RFC 7233: a range unit we do not understand must be ignored, not rejected.
        var range = HttpRangeParser.Parse("items=0-10", 1000);

        Assert.True(range.IsValid);
        Assert.False(range.IsPartial);
    }

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=100-199", 100, 199)]
    [InlineData("bytes=500-", 500, 999)]     // open-ended high
    [InlineData("bytes=0-", 0, 999)]
    [InlineData("bytes=999-999", 999, 999)]  // the last byte
    public void AnExplicitRangeIsParsedAsPartialContent(string header, long start, long end)
    {
        var range = HttpRangeParser.Parse(header, 1000);

        Assert.True(range.IsValid);
        Assert.True(range.IsPartial);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    [Theory]
    [InlineData("bytes=-100", 900, 999)]
    [InlineData("bytes=-1", 999, 999)]
    [InlineData("bytes=-5000", 0, 999)]  // a suffix longer than the file is the whole file
    public void ASuffixRangeCountsBackFromTheEnd(string header, long start, long end)
    {
        var range = HttpRangeParser.Parse(header, 1000);

        Assert.True(range.IsValid);
        Assert.True(range.IsPartial);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    [Fact]
    public void AnEndPastTheFileIsTruncatedRatherThanRejected()
    {
        // Players request fixed-size chunks, so the last chunk of every file asks for more than is
        // left - and so does every chunk of a file smaller than one chunk. Answering 416 to those
        // breaks playback at the end of each file.
        var range = HttpRangeParser.Parse("bytes=900-5000", 1000);

        Assert.True(range.IsValid);
        Assert.Equal(900, range.Start);
        Assert.Equal(999, range.End);
    }

    [Theory]
    [InlineData("bytes=1000-1100")]  // starts at the end
    [InlineData("bytes=5000-")]      // starts past the end
    [InlineData("bytes=100-50")]     // end before start
    [InlineData("bytes=abc-def")]    // not numbers
    [InlineData("bytes=0-5,10-15")]  // multi-range, which this server does not serve
    [InlineData("bytes=100")]        // no dash
    [InlineData("bytes=-0")]         // a zero-length suffix
    [InlineData("bytes=-abc")]
    public void AnUnsatisfiableOrMalformedRangeIsRejected(string header)
    {
        // Rejecting rather than silently serving something else: a player that receives the wrong
        // bytes with a 200 has no way to tell, and the file it writes is corrupt.
        Assert.False(HttpRangeParser.Parse(header, 1000).IsValid);
    }

    [Fact]
    public void NothingIsSatisfiableInAnEmptyFile()
    {
        Assert.True(HttpRangeParser.Parse(null, 0).IsValid);
        Assert.False(HttpRangeParser.Parse("bytes=0-10", 0).IsValid);
        Assert.False(HttpRangeParser.Parse("bytes=-10", 0).IsValid);
    }

    [Fact]
    public void AnEmptyRangeHeaderIsTreatedAsNoHeader()
    {
        var range = HttpRangeParser.Parse("", 1000);

        Assert.True(range.IsValid);
        Assert.False(range.IsPartial);
    }
}

public class StreamMediaTypesTests
{
    [Theory]
    [InlineData("movie.mp4", "video/mp4")]
    [InlineData("movie.m4v", "video/mp4")]
    [InlineData("movie.mkv", "video/x-matroska")]
    [InlineData("movie.avi", "video/x-msvideo")]
    [InlineData("movie.mov", "video/quicktime")]
    [InlineData("movie.wmv", "video/x-ms-wmv")]
    [InlineData("movie.webm", "video/webm")]
    [InlineData("movie.flv", "video/x-flv")]
    [InlineData("movie.ts", "video/mp2t")]
    [InlineData("movie.m2ts", "video/mp2t")]
    [InlineData("movie.mpg", "video/mpeg")]
    [InlineData("movie.mpeg", "video/mpeg")]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("song.flac", "audio/flac")]
    [InlineData("song.ogg", "audio/ogg")]
    [InlineData("song.opus", "audio/ogg")]
    [InlineData("song.wav", "audio/wav")]
    [InlineData("song.aac", "audio/aac")]
    [InlineData("song.m4a", "audio/mp4")]
    [InlineData("subtitles.vtt", "text/vtt")]
    [InlineData("subtitles.srt", "application/x-subrip")]
    public void EachKnownExtensionGetsItsOwnType(string path, string expected)
    {
        Assert.Equal(expected, StreamMediaTypes.GetMimeType(path));
    }

    [Theory]
    [InlineData("MOVIE.MP4")]
    [InlineData("Movie.Mp4")]
    public void TheExtensionIsMatchedRegardlessOfCase(string path)
    {
        // Torrent file names come from whoever packed them, so the case is not ours to assume.
        Assert.Equal("video/mp4", StreamMediaTypes.GetMimeType(path));
        Assert.True(StreamMediaTypes.IsStreamable(path));
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData("trailing.")]
    public void AnythingElseFallsBackToTheGenericBinaryType(string path)
    {
        Assert.Equal(StreamMediaTypes.Fallback, StreamMediaTypes.GetMimeType(path));
        Assert.False(StreamMediaTypes.IsStreamable(path));
    }

    [Fact]
    public void AFullPathIsMatchedOnItsExtensionRatherThanItsDirectories()
    {
        Assert.Equal("video/mp4", StreamMediaTypes.GetMimeType("/some.mkv/dir/movie.mp4"));
    }

    [Theory]
    [InlineData("subtitles.vtt")]
    [InlineData("subtitles.srt")]
    public void SubtitlesAreServedButNotOfferedForStreaming(string path)
    {
        Assert.False(StreamMediaTypes.IsStreamable(path));
        Assert.NotEqual(StreamMediaTypes.Fallback, StreamMediaTypes.GetMimeType(path));
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".ts")]
    [InlineData(".m4a")]
    [InlineData(".aac")]
    [InlineData(".opus")]
    public void EverythingOfferedForStreaming_IsServedAsMedia(string extension)
    {
        // The two used to be separate lists, and these five were streamable yet served as a generic
        // binary, which a Chromecast refuses to play.
        Assert.True(StreamMediaTypes.IsStreamable("file" + extension));
        Assert.NotEqual(StreamMediaTypes.Fallback, StreamMediaTypes.GetMimeType("file" + extension));
    }
}

public class HttpRequestHeadTests
{
    [Fact]
    public void AnOrdinaryRequestIsRead()
    {
        var request = Parse("GET /token/stream HTTP/1.1\r\nHost: 192.168.1.5:8080\r\nRange: bytes=0-99");

        Assert.NotNull(request);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/token/stream", request.Path);
        Assert.Equal("bytes=0-99", request.RangeHeader);
        Assert.True(request.IsHttp11);
    }

    [Fact]
    public void TheQueryStringIsNotPartOfThePath()
    {
        Assert.Equal("/stream", Parse("GET /stream?t=12 HTTP/1.1")!.Path);
    }

    [Fact]
    public void AnAbsoluteTargetIsReducedToItsPath()
    {
        Assert.Equal("/stream", Parse("GET http://192.168.1.5:8080/stream HTTP/1.1")!.Path);
    }

    [Fact]
    public void HeaderNamesAreMatchedRegardlessOfCase()
    {
        Assert.Equal("bytes=5-", Parse("GET /stream HTTP/1.1\r\nrange: bytes=5-")!.RangeHeader);
    }

    [Fact]
    public void AMissingRangeReadsAsNull()
    {
        Assert.Null(Parse("GET /stream HTTP/1.1")!.RangeHeader);
    }

    [Theory]
    [InlineData("GET /stream HTTP/1.1", true)]
    [InlineData("GET /stream HTTP/1.1\r\nConnection: close", false)]
    [InlineData("GET /stream HTTP/1.1\r\nConnection: Keep-Alive, Upgrade", true)]
    [InlineData("GET /stream HTTP/1.0", false)]
    [InlineData("GET /stream HTTP/1.0\r\nConnection: keep-alive", true)]
    [InlineData("GET /stream HTTP/1.0\r\nConnection: keep-alive, close", false)]
    public void KeepAliveIsTheVersionDefaultUnlessTheClientSaysOtherwise(string head, bool expected)
    {
        Assert.Equal(expected, Parse(head)!.WantsKeepAlive);
    }

    [Theory]
    [InlineData("GET /stream HTTP/1.1", false)]
    [InlineData("GET /stream HTTP/1.1\r\nContent-Length: 0", false)]
    [InlineData("POST /stream HTTP/1.1\r\nContent-Length: 12", true)]
    [InlineData("POST /stream HTTP/1.1\r\nTransfer-Encoding: chunked", true)]
    public void ABodyIsNoticedSoTheConnectionCanBeClosedAfterIt(string head, bool expected)
    {
        Assert.Equal(expected, Parse(head)!.HasBody);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GET")]
    [InlineData("GET /stream")]
    [InlineData("GET /stream HTTP/2")]
    [InlineData("GET  /stream HTTP/1.1")]
    [InlineData("GET stream HTTP/1.1")]
    [InlineData("GET ftp://host/stream HTTP/1.1")]
    [InlineData("GET /stream HTTP/1.1\r\nNo colon here")]
    [InlineData("GET /stream HTTP/1.1\r\n: no name")]
    [InlineData("GET /stream HTTP/1.1\r\nBad Name: value")]
    [InlineData("GET /stream HTTP/1.1\r\nRange: bytes=0-\r\n  folded")]
    public void AnythingMalformedIsRejectedRatherThanGuessedAt(string head)
    {
        Assert.Null(Parse(head));
    }

    [Fact]
    public void ANonAsciiByteIsRejectedRatherThanDecodedToSomethingElse()
    {
        var bytes = Encoding.ASCII.GetBytes("GET /stream HTTP/1.1\r\nRange: bytes=0-").Append((byte)0xC3).ToArray();

        Assert.Null(HttpRequestHead.TryParse(bytes));
    }

    private static HttpRequestHead? Parse(string head) => HttpRequestHead.TryParse(Encoding.ASCII.GetBytes(head));

    [Theory]
    [InlineData("GET /stream HTTP/1.1\r\nContent-Length: 12\r\nContent-Length: 0")]
    [InlineData("GET /stream HTTP/1.1\r\nContent-Length: +0")]
    [InlineData("GET /stream HTTP/1.1\r\nContent-Length: 0\r\nTransfer-Encoding: chunked")]
    [InlineData("GET /stream HTTP/1.1\r\nRange: bytes=0-1\r\nRange: bytes=2-3")]
    [InlineData("GET /stream HTTP/1.1\r\nBad(Name: value")]
    [InlineData("GET /stream HTTP/1.1\r\nX-Header: value\0tail")]
    [InlineData("GET /stream\0 HTTP/1.1")]
    public void AmbiguousFramingAndControlCharactersAreRejected(string head)
    {
        Assert.Null(Parse(head));
    }

    [Fact]
    public void RepeatedConnectionHeadersPreserveTheCloseDirective()
    {
        var request = Parse("GET /stream HTTP/1.1\r\nConnection: close\r\nConnection: keep-alive");
        Assert.NotNull(request);
        Assert.False(request.WantsKeepAlive);
    }
}

/// <summary>
/// How the server speaks HTTP on one connection, driven through a scripted transport so each case
/// sees exactly the bytes a client sends and exactly the bytes the server writes back.
/// </summary>
public class HttpStreamConnectionTests
{
    private static readonly byte[] Data = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task TwoRequestsOnOneConnectionAreBothAnswered()
    {
        var output = await ServeAsync(
            "GET /stream HTTP/1.1\r\nRange: bytes=0-1\r\n\r\n" +
            "GET /stream HTTP/1.1\r\nRange: bytes=2-3\r\n\r\n");

        Assert.Equal(2, Occurrences(output, "HTTP/1.1 206 Partial Content"));
        Assert.Contains("Content-Range: bytes 0-1/100", output, StringComparison.Ordinal);
        Assert.Contains("Content-Range: bytes 2-3/100", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestSplitAcrossReadsIsReassembled()
    {
        var output = await ServeAsync(["GET /str", "eam HTTP/1.1\r\nRan", "ge: bytes=0-1\r\n", "\r\n"]);

        Assert.StartsWith("HTTP/1.1 206", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestAskingToCloseEndsTheConnectionAfterItsAnswer()
    {
        var output = await ServeAsync(
            "GET /stream HTTP/1.1\r\nConnection: close\r\nRange: bytes=0-1\r\n\r\n" +
            "GET /stream HTTP/1.1\r\nRange: bytes=2-3\r\n\r\n");

        Assert.Equal(1, Occurrences(output, "HTTP/1.1 206"));
        Assert.Contains("Connection: close", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttp10RequestIsAnsweredAndTheConnectionClosed()
    {
        var output = await ServeAsync("GET /stream HTTP/1.0\r\n\r\nGET /stream HTTP/1.0\r\n\r\n");

        Assert.Equal(1, Occurrences(output, "HTTP/1.1 200 OK"));
        Assert.Contains("Connection: close", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestWithABodyIsAnsweredAndTheConnectionClosed()
    {
        // The server reads no bodies, so it cannot find where a following request would begin.
        var output = await ServeAsync(
            "POST /stream HTTP/1.1\r\nContent-Length: 5\r\n\r\nhello" +
            "GET /stream HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 405", output, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(output, "HTTP/1.1 "));
    }

    [Fact]
    public async Task EmptyLinesBeforeARequestAreIgnored()
    {
        var output = await ServeAsync("\r\n\r\nGET /stream HTTP/1.1\r\nRange: bytes=0-1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 206", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeadResponseAnnouncesTheLengthButSendsNoBody()
    {
        var output = await ServeAsync("HEAD /stream HTTP/1.1\r\n\r\n");

        Assert.Contains("Content-Length: 100", output, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoContentResponseCarriesNoContentLength()
    {
        // RFC 9110 8.6 forbids it on a 204.
        var output = await ServeAsync("OPTIONS /stream HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 204 No Content", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedRequestIsAnsweredWithBadRequestAndClosed()
    {
        var output = await ServeAsync("NONSENSE\r\n\r\nGET /stream HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400 Bad Request", output, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(output, "HTTP/1.1 "));
    }

    [Fact]
    public async Task AnOversizedHeadIsRefusedRatherThanBufferedWithoutLimit()
    {
        var output = await ServeAsync("GET /stream HTTP/1.1\r\nX-Padding: " + new string('a', HttpStreamConnection.MaxHeadBytes));

        Assert.StartsWith("HTTP/1.1 431", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStallBeforeTheFirstByteIsAnsweredWithServiceUnavailable()
    {
        // Nothing has been sent, so the client can be told plainly instead of receiving a length the
        // body never reaches.
        var torrent = new FakeTorrent("movie.mp4", Data, () => throw new TimeoutException("the swarm never delivered"));

        var output = await ServeAsync("GET /stream HTTP/1.1\r\n\r\nGET /stream HTTP/1.1\r\n\r\n", torrent);

        Assert.StartsWith("HTTP/1.1 503 Service Unavailable", output, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 0", output, StringComparison.Ordinal);
        Assert.Contains("Connection: close", output, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(output, "HTTP/1.1 "));
    }

    [Fact]
    public async Task AFailureAfterTheBodyHasStartedEscapesSoTheConnectionIsClosed()
    {
        // Content-Length has gone out, so only a closed connection can say the body is incomplete.
        var torrent = new FakeTorrent("movie.mp4", Data, () => new FailingAfterStream(Data, failAfter: 10));

        await Assert.ThrowsAsync<IOException>(() => ServeAsync("GET /stream HTTP/1.1\r\n\r\n", torrent));
    }

    [Fact]
    public async Task AnIdleConnectionIsClosedQuietly()
    {
        var time = new FakeTimeProvider();
        var transport = new ScriptedTransport([], blockWhenDrained: true);
        var connection = new HttpStreamConnection(transport, Handler(), time, NullLogger.Instance);

        var serving = connection.ServeAsync(TestContext.Current.CancellationToken);
        await transport.Drained.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(HttpStreamConnection.IdleTimeout + TimeSpan.FromSeconds(1));

        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(transport.Written);
    }

    private static HttpStreamRequestHandler Handler(FakeTorrent? torrent = null) =>
        new(torrent ?? new FakeTorrent("movie.mp4", Data), 0);

    private static Task<string> ServeAsync(string input, FakeTorrent? torrent = null) => ServeAsync([input], torrent);

    private static async Task<string> ServeAsync(string[] reads, FakeTorrent? torrent = null)
    {
        var transport = new ScriptedTransport(reads.Select(Encoding.ASCII.GetBytes).ToArray(), blockWhenDrained: false);
        var connection = new HttpStreamConnection(transport, Handler(torrent), TimeProvider.System, NullLogger.Instance);

        await connection.ServeAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return Encoding.ASCII.GetString(transport.Written);
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// A connection whose client sends the given chunks, one per read, and then either closes or
    /// goes quiet.
    /// </summary>
    private sealed class ScriptedTransport(byte[][] reads, bool blockWhenDrained) : Stream
    {
        private readonly MemoryStream _written = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _next;
        private int _offset;

        public byte[] Written => _written.ToArray();

        public Task Drained => _drained.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next < reads.Length)
            {
                // Whatever does not fit is left for the next read, as a socket would.
                var chunk = reads[_next];
                int count = Math.Min(chunk.Length - _offset, buffer.Length);
                chunk.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                if (_offset == chunk.Length)
                {
                    _next++;
                    _offset = 0;
                }

                return count;
            }

            _drained.TrySetResult();
            if (blockWhenDrained)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
    }

    /// <summary>Serves <paramref name="failAfter"/> bytes and then fails the way a dropped disk read would.</summary>
    private sealed class FailingAfterStream(byte[] data, int failAfter) : MemoryStream(data, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter)
            {
                throw new IOException("The read failed part way through.");
            }

            int toRead = (int)Math.Min(buffer.Length, failAfter - Position);
            return base.ReadAsync(buffer[..toRead], cancellationToken);
        }
    }
}
