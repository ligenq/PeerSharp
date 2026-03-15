using PeerSharp.Internals.Network;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Network;

public class HttpClientFactoryTests
{
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
