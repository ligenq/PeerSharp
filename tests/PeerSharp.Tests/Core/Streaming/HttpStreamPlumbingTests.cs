using PeerSharp.Streaming;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Streaming;

/// <summary>
/// The server itself: its listener, the URL it advertises, and its shutdown. What it serves is
/// covered by <see cref="HttpStreamRequestHandlerTests"/>.
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
    public void ANullLoggerFactoryIsRefusedRatherThanFailingLater()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0, null!));
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
    public void DisposingWithoutStartingIsSafe()
    {
        // A caller that decides against streaming after constructing the server still disposes it.
        new HttpStreamServer(TorrentTestUtility.CreateMinimal(), 0).Dispose();
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
        Assert.False(HttpRangeParser.Parse(null, 0).IsValid);
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

public class HttpStreamMimeTypesTests
{
    [Theory]
    [InlineData("movie.mp4", "video/mp4")]
    [InlineData("movie.mkv", "video/x-matroska")]
    [InlineData("movie.avi", "video/x-msvideo")]
    [InlineData("movie.mov", "video/quicktime")]
    [InlineData("movie.wmv", "video/x-ms-wmv")]
    [InlineData("movie.webm", "video/webm")]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("song.flac", "audio/flac")]
    [InlineData("song.ogg", "audio/ogg")]
    [InlineData("song.wav", "audio/wav")]
    public void EachKnownExtensionGetsItsOwnType(string path, string expected)
    {
        Assert.Equal(expected, HttpStreamMimeTypes.GetMimeType(path));
    }

    [Theory]
    [InlineData("MOVIE.MP4")]
    [InlineData("Movie.Mp4")]
    public void TheExtensionIsMatchedRegardlessOfCase(string path)
    {
        // Torrent file names come from whoever packed them, so the case is not ours to assume.
        Assert.Equal("video/mp4", HttpStreamMimeTypes.GetMimeType(path));
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData("trailing.")]
    public void AnythingElseFallsBackToTheGenericBinaryType(string path)
    {
        Assert.Equal("application/octet-stream", HttpStreamMimeTypes.GetMimeType(path));
    }

    [Fact]
    public void AFullPathIsMatchedOnItsExtensionRatherThanItsDirectories()
    {
        Assert.Equal("video/mp4", HttpStreamMimeTypes.GetMimeType("/some.mkv/dir/movie.mp4"));
    }
}

/// <summary>
/// The two adapters that sit between <see cref="HttpListener"/> and the handler. They are thin, but
/// what they are thin over cannot be constructed directly, so the only way to exercise them is to
/// serve a real request on the loopback interface.
/// </summary>
public class HttpListenerStreamRequestTests
{
    [Fact]
    public async Task TheRequestExposesTheMethodPathAndRangeHeaderTheHandlerReadsFrom()
    {
        using var host = new LoopbackListener();

        // Read inside the handler: HttpListenerRequest is disposed with its response, which is the
        // same window the real handler works in.
        (string Method, string Path, string? Range) seen = default;

        var serving = host.ServeOnceAsync(context =>
        {
            var request = new HttpListenerStreamRequest(context.Request);
            seen = (request.Method, request.Path, request.RangeHeader);
            context.Response.StatusCode = 200;
        });

        using var client = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Get, host.Url + "stream");
        message.Headers.Add("Range", "bytes=10-20");
        using var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        await serving;

        Assert.Equal("GET", seen.Method);
        Assert.Equal("/stream", seen.Path);
        Assert.Equal("bytes=10-20", seen.Range);
    }

    [Fact]
    public async Task AMissingRangeHeaderReadsAsNullRatherThanEmpty()
    {
        // The parser distinguishes them: null means serve the whole file, and an empty string is a
        // header the client actually sent.
        using var host = new LoopbackListener();
        string? range = "not read";

        var serving = host.ServeOnceAsync(context =>
        {
            range = new HttpListenerStreamRequest(context.Request).RangeHeader;
            context.Response.StatusCode = 200;
        });

        using var client = new HttpClient();
        using var response = await client.GetAsync(host.Url + "stream", TestContext.Current.CancellationToken);
        await serving;

        Assert.Null(range);
    }

    [Fact]
    public async Task AHeadRequestReportsItsOwnMethod()
    {
        using var host = new LoopbackListener();
        string method = string.Empty;

        var serving = host.ServeOnceAsync(context =>
        {
            method = new HttpListenerStreamRequest(context.Request).Method;
            context.Response.StatusCode = 200;
        });

        using var client = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Head, host.Url + "stream");
        using var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        await serving;

        Assert.Equal("HEAD", method);
    }
}

public class HttpListenerStreamResponseTests
{
    [Fact]
    public async Task EverythingTheHandlerSetsReachesTheWire()
    {
        // The handler writes status, content type, length, protocol version and headers through this
        // adapter and never touches HttpListenerResponse itself, so a property wired to the wrong
        // underlying one would produce a response no player understands.
        using var host = new LoopbackListener();

        var serving = host.ServeOnceAsync(context =>
        {
            var adapter = new HttpListenerStreamResponse(context.Response);

            adapter.StatusCode = (int)HttpStatusCode.PartialContent;
            adapter.ContentType = "video/mp4";
            adapter.ContentLength = 4;
            adapter.ProtocolVersion = new Version(1, 1);
            adapter.AddHeader("Accept-Ranges", "bytes");
            adapter.AddHeader("Content-Range", "bytes 0-3/100");

            // Read back through the adapter: the getters are what the handler's own logic consults.
            Assert.Equal((int)HttpStatusCode.PartialContent, adapter.StatusCode);
            Assert.Equal("video/mp4", adapter.ContentType);
            Assert.Equal(4, adapter.ContentLength);
            Assert.Equal(new Version(1, 1), adapter.ProtocolVersion);

            adapter.Body.Write("data"u8);
        });

        using var client = new HttpClient();
        using var response = await client.GetAsync(host.Url + "stream", TestContext.Current.CancellationToken);
        await serving;

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal("data", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnsetContentTypeReadsAsEmptyRatherThanNull()
    {
        // The adapter's getter substitutes empty for null so the handler can compare it without a
        // null check on a path that runs per request.
        using var host = new LoopbackListener();
        string? contentType = null;

        var serving = host.ServeOnceAsync(context =>
        {
            var adapter = new HttpListenerStreamResponse(context.Response);
            contentType = adapter.ContentType;
            adapter.StatusCode = 200;
            adapter.ContentLength = 0;
        });

        using var client = new HttpClient();
        using var response = await client.GetAsync(host.Url + "stream", TestContext.Current.CancellationToken);
        await serving;

        Assert.Equal(string.Empty, contentType);
    }
}

/// <summary>
/// An HttpListener bound to a free loopback port that answers exactly one request.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly HttpListener _listener = new();

    public LoopbackListener()
    {
        // A port the OS just handed out, so parallel test classes cannot collide. localhost prefixes
        // do not need the URL ACL that a wildcard prefix would.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
    }

    public string Url { get; }

    public async Task ServeOnceAsync(Action<HttpListenerContext> handle)
    {
        var context = await _listener.GetContextAsync();
        try
        {
            handle(context);
        }
        finally
        {
            context.Response.Close();
        }
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();
}
