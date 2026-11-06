using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Trackers;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// The rules that keep a torrent's URLs from reaching services on this device or its network.
/// </summary>
public class SsrfGuardTests
{
    private static readonly SsrfPolicy TrackerOnThisDevice = new(SsrfMitigation.ThisDevice, HttpRequestKind.Tracker);
    private static readonly SsrfPolicy WebSeedOnThisDevice = new(SsrfMitigation.ThisDevice, HttpRequestKind.WebSeed);

    [Theory]
    [InlineData("127.0.0.1", SsrfMitigation.ThisDevice, true)]
    [InlineData("127.45.6.7", SsrfMitigation.ThisDevice, true)]
    [InlineData("::1", SsrfMitigation.ThisDevice, true)]
    [InlineData("::ffff:127.0.0.1", SsrfMitigation.ThisDevice, true)]
    [InlineData("0.0.0.0", SsrfMitigation.ThisDevice, true)]
    [InlineData("::", SsrfMitigation.ThisDevice, true)]
    [InlineData("192.168.1.1", SsrfMitigation.ThisDevice, false)]
    [InlineData("8.8.8.8", SsrfMitigation.ThisDevice, false)]
    [InlineData("192.168.1.1", SsrfMitigation.LocalNetworks, true)]
    [InlineData("10.0.0.5", SsrfMitigation.LocalNetworks, true)]
    [InlineData("172.16.0.1", SsrfMitigation.LocalNetworks, true)]
    [InlineData("169.254.169.254", SsrfMitigation.LocalNetworks, true)]
    [InlineData("fe80::1", SsrfMitigation.LocalNetworks, true)]
    [InlineData("fd00::1", SsrfMitigation.LocalNetworks, true)]
    [InlineData("8.8.8.8", SsrfMitigation.LocalNetworks, false)]
    [InlineData("2001:4860:4860::8888", SsrfMitigation.LocalNetworks, false)]
    [InlineData("127.0.0.1", SsrfMitigation.None, false)]
    public void IsGuarded_CoversWhatTheModeSays(string address, SsrfMitigation mode, bool guarded)
    {
        Assert.Equal(guarded, SsrfGuard.IsGuarded(IPAddress.Parse(address), mode));
    }

    [Theory]
    [InlineData("127.0.0.1:8080", true)]
    [InlineData("[::1]:8080", true)]
    [InlineData("localhost", true)]
    [InlineData("LocalHost:6969", true)]
    [InlineData("tracker.localhost", true)]
    [InlineData("tracker.example", false)]
    [InlineData("localhost.example", false)]
    [InlineData("8.8.8.8:80", false)]
    public void IsExplicitlyGuarded_RecognisesAHostThatNamesThisDevice(string host, bool expected)
    {
        Assert.Equal(expected, SsrfGuard.IsExplicitlyGuarded(host, SsrfMitigation.ThisDevice));
    }

    [Theory]
    [InlineData("http://127.0.0.1:6969/announce?info_hash=x")]
    [InlineData("http://127.0.0.1:6969/passkey/announce.php?info_hash=x")]
    [InlineData("http://localhost:6969/scrape")]
    [InlineData("http://tracker.example/api/v2/app/shutdown")]
    public void CheckRequest_LetsATrackerThroughWhenItHasATrackersShape(string url)
    {
        var uri = new Uri(url);

        Assert.Null(SsrfGuard.CheckRequest(TrackerOnThisDevice, uri, uri.Authority, redirectedFrom: null));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/api/v2/app/shutdown")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]:8080/announcements/../reboot")]
    public void CheckRequest_RefusesATrackerOnThisDeviceAtAnyOtherPath(string url)
    {
        var uri = new Uri(url);

        Assert.NotNull(SsrfGuard.CheckRequest(TrackerOnThisDevice, uri, uri.Authority, redirectedFrom: null));
    }

    [Fact]
    public void CheckRequest_RefusesAQueryStringToAWebSeedOnThisDevice()
    {
        var withQuery = new Uri("http://127.0.0.1:8080/files/movie.mkv?action=delete");
        var plain = new Uri("http://127.0.0.1:8080/files/movie.mkv");

        Assert.NotNull(SsrfGuard.CheckRequest(WebSeedOnThisDevice, withQuery, withQuery.Authority, redirectedFrom: null));
        Assert.Null(SsrfGuard.CheckRequest(WebSeedOnThisDevice, plain, plain.Authority, redirectedFrom: null));
    }

    [Fact]
    public void CheckRequest_LeavesAnHttpSeedsQueryAlone_ButNotARedirectIntoThisDevice()
    {
        // BEP 17: the engine writes the query, so the seed's own URL is judged when it is added.
        var policy = new SsrfPolicy(SsrfMitigation.ThisDevice, HttpRequestKind.HttpSeed);
        var piece = new Uri("http://127.0.0.1:8080/seed?info_hash=%01&piece=0&ranges=0-16383");

        Assert.Null(SsrfGuard.CheckRequest(policy, piece, piece.Authority, redirectedFrom: null));
        Assert.NotNull(SsrfGuard.CheckRequest(policy, piece, piece.Authority, new Uri("http://seed.example/seed")));
    }

    [Fact]
    public void CheckRequest_RefusesARedirectIntoThisDeviceFromElsewhere()
    {
        var target = new Uri("http://127.0.0.1:6969/announce");

        Assert.NotNull(SsrfGuard.CheckRequest(TrackerOnThisDevice, target, target.Authority, new Uri("http://tracker.example/announce")));
        Assert.Null(SsrfGuard.CheckRequest(TrackerOnThisDevice, target, target.Authority, new Uri("http://localhost:6969/announce")));
    }

    [Fact]
    public void CheckRequest_JudgesTheHostTheRequestNames()
    {
        // A tracker resolves its own name and sends to the address, naming the host in the Host header.
        var sentTo = new Uri("http://203.0.113.7:6969/announce");

        Assert.Null(SsrfGuard.CheckRequest(TrackerOnThisDevice, sentTo, "tracker.example:6969", redirectedFrom: null));
        Assert.Null(SsrfGuard.CheckRequest(TrackerOnThisDevice, new Uri("http://127.0.0.1:6969/announce"), "localhost:6969", redirectedFrom: null));
        Assert.NotNull(SsrfGuard.CheckRequest(TrackerOnThisDevice, new Uri("http://127.0.0.1:6969/api/v2/app/shutdown"), "localhost:6969", redirectedFrom: null));
    }

    /// <summary>
    /// Whatever its shape: a name that resolves here is refused at connect time, and a request that
    /// reuses a connection has to meet the same answer. Every kind of request, since an HTTP seed's
    /// query is otherwise left alone.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:6969/announce", nameof(HttpRequestKind.Tracker))]
    [InlineData("http://127.0.0.1:6969/api/v2/app/shutdown", nameof(HttpRequestKind.Tracker))]
    [InlineData("http://[::1]:6969/announce", nameof(HttpRequestKind.Tracker))]
    [InlineData("http://127.0.0.1:6969/file", nameof(HttpRequestKind.WebSeed))]
    [InlineData("http://127.0.0.1:6969/seed?info_hash=x", nameof(HttpRequestKind.HttpSeed))]
    public void CheckRequest_RefusesAHostThatResolvedToThisDevice(string sentTo, string kind)
    {
        var policy = new SsrfPolicy(SsrfMitigation.ThisDevice, Enum.Parse<HttpRequestKind>(kind));

        Assert.NotNull(SsrfGuard.CheckRequest(policy, new Uri(sentTo), "rebind.example:6969", redirectedFrom: null));
    }

    [Fact]
    public void CheckRequest_ReachesAsFarAsTheMode_ForAHostThatResolvedToALocalNetwork()
    {
        var sentTo = new Uri("http://192.168.1.1/announce");

        Assert.Null(SsrfGuard.CheckRequest(TrackerOnThisDevice, sentTo, "tracker.corp", redirectedFrom: null));
        Assert.NotNull(SsrfGuard.CheckRequest(
            new SsrfPolicy(SsrfMitigation.LocalNetworks, HttpRequestKind.Tracker), sentTo, "tracker.corp", redirectedFrom: null));
        Assert.Null(SsrfGuard.CheckRequest(default, sentTo, "tracker.corp", redirectedFrom: null));
    }

    [Fact]
    public void CheckRequest_WithNoGuard_RefusesNothing()
    {
        var uri = new Uri("http://127.0.0.1:8080/api/v2/app/shutdown?now=1");

        Assert.Null(SsrfGuard.CheckRequest(new SsrfPolicy(SsrfMitigation.None, HttpRequestKind.Tracker), uri, uri.Authority, new Uri("http://tracker.example/")));
    }

    [Fact]
    public void ScreenAddresses_RefusesANameThatResolvesOnlyToThisDevice()
    {
        // DNS rebinding: a name the torrent gave resolves to loopback.
        Assert.Throws<SsrfRefusedException>(() =>
            SsrfGuard.ScreenAddresses(SsrfMitigation.ThisDevice, "rebind.example:80", [IPAddress.Loopback]));
    }

    [Fact]
    public void ScreenAddresses_KeepsOnlyTheAddressesANameMayUse()
    {
        var screened = SsrfGuard.ScreenAddresses(
            SsrfMitigation.ThisDevice,
            "tracker.example",
            [IPAddress.Loopback, IPAddress.Parse("203.0.113.7")]);

        Assert.Equal([IPAddress.Parse("203.0.113.7")], screened);
    }

    [Fact]
    public void ScreenAddresses_LetsAHostThatNamesThisDeviceReachIt()
    {
        Assert.Equal([IPAddress.Loopback], SsrfGuard.ScreenAddresses(SsrfMitigation.ThisDevice, "localhost:6969", [IPAddress.Loopback]));
        Assert.Equal([IPAddress.Loopback], SsrfGuard.ScreenAddresses(SsrfMitigation.None, "rebind.example", [IPAddress.Loopback]));
    }

    [Fact]
    public void RequestHost_PrefersTheHostHeader()
    {
        using var plain = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:6969/announce");
        using var named = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:6969/announce");
        named.Headers.Host = "tracker.example:6969";

        Assert.Equal("127.0.0.1:6969", SsrfGuard.RequestHost(plain));
        Assert.Equal("tracker.example:6969", SsrfGuard.RequestHost(named));
    }

    [Fact]
    public async Task AGuardedClient_DoesNotConnectWhereANameResolvesToThisDevice()
    {
        using var server = new OneShotHttpServer();
        var hostAddresses = new HostAddressCache(TimeProvider.System, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        using var factory = new HttpClientFactory(hostAddresses);
        var client = factory.CreateClient(new ProxySettings { Type = ProxyType.None }, isTracker: false, ssrf: WebSeedOnThisDevice);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://rebind.example:{server.Port}/file", TestContext.Current.CancellationToken));

        Assert.IsType<SsrfRefusedException>(error.InnerException);
        Assert.False(server.WasConnectedTo);
    }

    [Fact]
    public async Task AGuardedClient_ReachesThisDeviceWhenTheUrlNamesItInTheRightShape()
    {
        using var server = new OneShotHttpServer();
        using var factory = new HttpClientFactory();
        var client = factory.CreateClient(new ProxySettings { Type = ProxyType.None }, isTracker: false, ssrf: WebSeedOnThisDevice);

        var answered = server.AnswerAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", TestContext.Current.CancellationToken);
        using var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/file", TestContext.Current.CancellationToken);
        await answered;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AGuardedClient_RefusesTheWrongShapeWithoutConnecting()
    {
        using var server = new OneShotHttpServer();
        using var factory = new HttpClientFactory();
        var client = factory.CreateClient(new ProxySettings { Type = ProxyType.None }, isTracker: false, ssrf: WebSeedOnThisDevice);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://127.0.0.1:{server.Port}/file?action=delete", TestContext.Current.CancellationToken));

        Assert.IsType<SsrfRefusedException>(error.InnerException);
        Assert.False(server.WasConnectedTo);
    }

    /// <summary>
    /// A tracker sends to the address it resolved and names its host in the Host header, and plain
    /// HTTP connections are pooled by address alone. So a connection opened for a real tracker on this
    /// device answers a name that resolves here, without connecting, and only a check made on every
    /// request can catch it.
    /// </summary>
    [Fact]
    public async Task APooledConnectionToThisDevice_DoesNotServeANameThatResolvesHere()
    {
        using var server = new KeepAliveHttpServer();
        var serving = server.ServeAsync(TestContext.Current.CancellationToken);
        using var factory = new HttpClientFactory();
        var client = factory.CreateClient(
            new ProxySettings { Type = ProxyType.None }, isTracker: true, followRedirects: false, ssrf: TrackerOnThisDevice);
        using (var announce = await client.GetAsync($"http://127.0.0.1:{server.Port}/announce", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, announce.StatusCode);
        }

        using var forged = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/admin?delete=all");
        forged.Headers.Host = $"rebind.example:{server.Port}";
        var error = await Record.ExceptionAsync(() => client.SendAsync(forged, TestContext.Current.CancellationToken));

        Assert.Equal(["/announce"], server.Requests);
        var refused = Assert.IsType<HttpRequestException>(error);
        Assert.IsType<SsrfRefusedException>(refused.InnerException);
        await server.DisposeConnectionAsync(serving);
    }

    /// <summary>
    /// The same thing as a torrent would do it, through two real trackers. The first is a real local
    /// tracker named outright, which leaves a pooled connection. The second is a name the torrent gave
    /// that resolves here, and its announce must not reach that connection.
    /// </summary>
    [Fact]
    public async Task ATrackerThatResolvesToThisDevice_DoesNotAnnounceOverAPooledConnection()
    {
        using var server = new KeepAliveHttpServer();
        var serving = server.ServeAsync(TestContext.Current.CancellationToken);
        var names = new HostAddressCache(TimeProvider.System, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        using var factory = new HttpClientFactory(names);
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.SsrfMitigation = SsrfMitigation.ThisDevice;

        var local = new HttpTracker(NullLoggerFactory.Instance, factory, names);
        local.Init($"http://127.0.0.1:{server.Port}/announce", torrent, new IgnoredTrackerResults());
        await local.AnnounceAsync(TrackerEvent.None, TestContext.Current.CancellationToken);
        Assert.Equal(["/announce"], server.Requests);

        var rebound = new HttpTracker(NullLoggerFactory.Instance, factory, names);
        rebound.Init($"http://rebind.example:{server.Port}/announce", torrent, new IgnoredTrackerResults());
        await rebound.AnnounceAsync(TrackerEvent.None, TestContext.Current.CancellationToken);

        Assert.Equal(["/announce"], server.Requests);
        await server.DisposeConnectionAsync(serving);
    }

    private sealed class IgnoredTrackerResults : ITrackerCallback
    {
        public void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage = null) { }
        public void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker) { }
        public void OnScrapeResult(bool success, ScrapeResponse response, ITracker tracker) { }
    }

    /// <summary>A loopback listener that keeps one connection open and records what it is asked for.</summary>
    private sealed class KeepAliveHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new();
        private readonly CancellationTokenSource _stop = new();

        public KeepAliveHttpServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyCollection<string> Requests => [.. _requests];

        public async Task ServeAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            using var connection = await _listener.AcceptTcpClientAsync(linked.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(linked.Token) is { } line)
            {
                _requests.Enqueue(line.Split(' ')[1].Split('?')[0]);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(linked.Token)))
                {
                    // The rest of the request's headers.
                }

                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), linked.Token);
            }
        }

        public async Task DisposeConnectionAsync(Task serving)
        {
            await _stop.CancelAsync();
            await Record.ExceptionAsync(() => serving);
        }

        public void Dispose()
        {
            _stop.Dispose();
            _listener.Stop();
        }
    }

    /// <summary>A loopback listener that answers at most one request, and says whether anyone came.</summary>
    private sealed class OneShotHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public OneShotHttpServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool WasConnectedTo => _listener.Pending();

        public async Task AnswerAsync(string response, CancellationToken ct)
        {
            using var connection = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = connection.GetStream();
            _ = await stream.ReadAsync(new byte[4096], ct);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), ct);
        }

        public void Dispose() => _listener.Stop();
    }
}

/// <summary>
/// Redirects followed by a guarded client, each of which is checked before it is sent.
/// </summary>
public class SsrfGuardHandlerTests
{
    private static readonly SsrfPolicy Tracker = new(SsrfMitigation.ThisDevice, HttpRequestKind.Tracker);

    [Fact]
    public async Task ARedirectFromElsewhereIntoThisDevice_IsRefused()
    {
        var inner = new ScriptedHandler(_ => Redirect("http://127.0.0.1:6969/announce"));
        using var client = new HttpClient(new SsrfGuardHandler(inner, Tracker, followRedirects: true));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://tracker.example/announce", TestContext.Current.CancellationToken));

        Assert.IsType<SsrfRefusedException>(error.InnerException);
        Assert.Equal(["http://tracker.example/announce"], inner.Sent);
    }

    [Fact]
    public async Task ARedirectThatStaysOnThisDevice_IsFollowed()
    {
        var inner = new ScriptedHandler(uri => uri.AbsolutePath == "/announce"
            ? Redirect("/v2/announce")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new SsrfGuardHandler(inner, Tracker, followRedirects: true));

        using var response = await client.GetAsync("http://localhost:6969/announce", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["http://localhost:6969/announce", "http://localhost:6969/v2/announce"], inner.Sent);
    }

    [Fact]
    public async Task ARedirectFromHttpsToHttp_IsHandedBackUnfollowed()
    {
        var inner = new ScriptedHandler(_ => Redirect("http://tracker.example/announce"));
        using var client = new HttpClient(new SsrfGuardHandler(inner, Tracker, followRedirects: true));

        using var response = await client.GetAsync("https://tracker.example/announce", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(inner.Sent);
    }

    [Fact]
    public async Task WithoutFollowingRedirects_TheRedirectComesBack_AndTheCallersOriginIsHonoured()
    {
        var inner = new ScriptedHandler(_ => Redirect("http://127.0.0.1:6969/announce"));
        using var client = new HttpClient(new SsrfGuardHandler(inner, Tracker, followRedirects: false));

        using (var response = await client.GetAsync("http://tracker.example/announce", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        // A caller following the redirect itself says where the chain began.
        using var hop = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:6969/announce");
        hop.Options.Set(SsrfGuard.RedirectedFrom, new Uri("http://tracker.example/announce"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(hop, TestContext.Current.CancellationToken));
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request.RequestUri));
        }
    }
}
