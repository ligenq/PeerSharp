using PeerSharp.Internals.Network;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace PeerSharp.Tests.Core.Network;

public class HttpClientFactoryTests
{
    [Fact]
    public async Task CreateClient_WithBindAddress_UsesItAsTheTcpSourceAddress()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
            var factory = new HttpClientFactory();
            using var client = factory.CreateClient(
                new ProxySettings { Type = ProxyType.None },
                isTracker: true,
                IPAddress.Parse("127.0.0.2"));

            var request = client.GetAsync(
                $"http://127.0.0.1:{port}/",
                HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken);
            using var connection = await accepted;
            var remote = Assert.IsType<IPEndPoint>(connection.Client.RemoteEndPoint);

            Assert.Equal(IPAddress.Parse("127.0.0.2"), remote.Address);

            await using var stream = connection.GetStream();
            byte[] buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
            await stream.WriteAsync(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                TestContext.Current.CancellationToken);
            using var response = await request;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CreateClient_WithIPv4Family_ConnectsOnlyToIPv4Endpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
            var factory = new HttpClientFactory();
            using var client = factory.CreateClient(
                new ProxySettings { Type = ProxyType.None },
                isTracker: true,
                addressFamily: AddressFamily.InterNetwork);

            var request = client.GetAsync(
                $"http://localhost:{port}/",
                HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken);
            using var connection = await accepted;
            Assert.Equal(AddressFamily.InterNetwork, connection.Client.RemoteEndPoint?.AddressFamily);

            await using var stream = connection.GetStream();
            byte[] buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
            await stream.WriteAsync(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                TestContext.Current.CancellationToken);
            using var response = await request;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Selecting the address family means resolving and connecting by hand, which quietly gives up
    /// what <see cref="SocketsHttpHandler"/> did for free: it hands the whole resolved set to the
    /// socket and tries each in turn. Trackers behind DNS round-robin publish several A records
    /// precisely so that one being down does not matter, and this path is now on every announce.
    /// </summary>
    [Fact]
    public async Task Connect_WhenAnEarlierAddressRefuses_TriesTheRest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();

            // Nothing is listening on 127.0.0.9, so it refuses at once - the same answer a dead
            // round-robin member gives, without the wait.
            await using var stream = await HttpClientFactory.ConnectForTestingAsync(
                new DnsEndPoint("tracker.invalid", port),
                bindAddress: null,
                AddressFamily.InterNetwork,
                (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("127.0.0.9"), IPAddress.Loopback]),
                TestContext.Current.CancellationToken);

            using var connection = await accepted;
            Assert.NotNull(stream);
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)connection.Client.RemoteEndPoint!).Address);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Connect_WhenEveryAddressFails_ReportsTheFailure()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await Assert.ThrowsAnyAsync<SocketException>(() => HttpClientFactory.ConnectForTestingAsync(
            new DnsEndPoint("tracker.invalid", deadPort),
            bindAddress: null,
            AddressFamily.InterNetwork,
            (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("127.0.0.9"), IPAddress.Loopback]),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Connect_WhenNoAddressMatchesTheFamily_ReportsHostNotFound()
    {
        var error = await Assert.ThrowsAsync<SocketException>(() => HttpClientFactory.ConnectForTestingAsync(
            new DnsEndPoint("tracker.invalid", 80),
            bindAddress: null,
            AddressFamily.InterNetworkV6,
            (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(SocketError.HostNotFound, error.SocketErrorCode);
    }

    [Fact]
    public void CreateClient_BindAndRequestedFamilyMismatch_IsRejected()
    {
        var factory = new HttpClientFactory();

        Assert.Throws<ArgumentException>(() => factory.CreateClient(
            new ProxySettings { Type = ProxyType.None },
            isTracker: true,
            bindAddress: IPAddress.Loopback,
            addressFamily: AddressFamily.InterNetworkV6));
    }

    [Fact]
    public void CreateClient_NoProxy_HasNullProxy()
    {
        var factory = new HttpClientFactory();
        var proxySettings = new ProxySettings { Type = ProxyType.None };

        var client = factory.CreateClient(proxySettings, false);
        var handler = GetHandler(client);

        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CreateClient_Socks5_ConfiguresProxy()
    {
        var factory = new HttpClientFactory();
        var proxySettings = new ProxySettings
        {
            Type = ProxyType.Socks5,
            Host = "127.0.0.1",
            Port = 1080
        };

        var client = factory.CreateClient(proxySettings, false);
        var handler = GetHandler(client);

        Assert.NotNull(handler.Proxy);
        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("socks5://127.0.0.1:1080"), webProxy.Address);
    }

    [Fact]
    public void CreateClient_Http_ConfiguresProxyWithCredentials()
    {
        var factory = new HttpClientFactory();
        var proxySettings = new ProxySettings
        {
            Type = ProxyType.Http,
            Host = "proxy.example.com",
            Port = 8080,
            Username = "user",
            Password = "pass"
        };

        var client = factory.CreateClient(proxySettings, true);
        var handler = GetHandler(client);

        Assert.NotNull(handler.Proxy);
        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://proxy.example.com:8080"), webProxy.Address);
        Assert.NotNull(webProxy.Credentials);

        var creds = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("user", creds.UserName);
        Assert.Equal("pass", creds.Password);
    }

    [Fact]
    public void CreateClient_HttpProxyPasswordChanges_DoesNotReuseCachedCredentials()
    {
        var factory = new HttpClientFactory();
        var firstProxySettings = new ProxySettings
        {
            Type = ProxyType.Http,
            Host = "cache-key-test.example.com",
            Port = 8080,
            Username = "user",
            Password = "first"
        };
        var secondProxySettings = new ProxySettings
        {
            Type = ProxyType.Http,
            Host = "cache-key-test.example.com",
            Port = 8080,
            Username = "user",
            Password = "second"
        };

        _ = factory.CreateClient(firstProxySettings, true);
        var secondClient = factory.CreateClient(secondProxySettings, true);
        var handler = GetHandler(secondClient);

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        var creds = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("second", creds.Password);
    }

    [Fact]
    public void CreateClient_Tracker_SetsShorterTimeout()
    {
        var factory = new HttpClientFactory();
        var proxySettings = new ProxySettings();

        var client = factory.CreateClient(proxySettings, true);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }

    [Fact]
    public void CreateClient_WebSeed_SetsLongerTimeout()
    {
        var factory = new HttpClientFactory();
        var proxySettings = new ProxySettings();

        var client = factory.CreateClient(proxySettings, false);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    private static SocketsHttpHandler GetHandler(HttpClient client)
    {
        // HttpClient -> HttpHttpMessageHandler -> SocketsHttpHandler
        // or HttpClient._handler
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = field!.GetValue(client);
        return Assert.IsType<SocketsHttpHandler>(handler);
    }
}
