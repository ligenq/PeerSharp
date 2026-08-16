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
