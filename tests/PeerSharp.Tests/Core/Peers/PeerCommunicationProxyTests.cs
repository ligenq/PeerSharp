using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class PeerCommunicationProxyTests
{
    [Fact]
    public void CanUseUtpWithProxy_NoProxy_ReturnsTrue()
    {
        var settings = new Settings
        {
            Proxy = new ProxySettings
            {
                Type = ProxyType.None,
                ProxyPeers = true,
                Host = "127.0.0.1",
                Port = 1080
            }
        };

        Assert.True(PeerCommunication.CanUseUtpWithProxy(settings));
    }

    [Fact]
    public void CanUseUtpWithProxy_Socks5Proxy_ReturnsTrue()
    {
        var settings = new Settings
        {
            Proxy = new ProxySettings
            {
                Type = ProxyType.Socks5,
                ProxyPeers = true,
                Host = "127.0.0.1",
                Port = 1080
            }
        };

        Assert.True(PeerCommunication.CanUseUtpWithProxy(settings));
    }

    [Fact]
    public void CanUseUtpWithProxy_HttpProxy_ReturnsFalse()
    {
        var settings = new Settings
        {
            Proxy = new ProxySettings
            {
                Type = ProxyType.Http,
                ProxyPeers = true,
                Host = "127.0.0.1",
                Port = 8080
            }
        };

        Assert.False(PeerCommunication.CanUseUtpWithProxy(settings));
    }

    [Fact]
    public void CanUseUtpWithProxy_ProxyPeersDisabled_ReturnsTrue()
    {
        var settings = new Settings
        {
            Proxy = new ProxySettings
            {
                Type = ProxyType.Http,
                ProxyPeers = false,
                Host = "127.0.0.1",
                Port = 8080
            }
        };

        Assert.True(PeerCommunication.CanUseUtpWithProxy(settings));
    }

    [Fact]
    public void CanUseUtpWithProxy_ProxyHostMissing_ReturnsTrue()
    {
        var settings = new Settings
        {
            Proxy = new ProxySettings
            {
                Type = ProxyType.Http,
                ProxyPeers = true,
                Host = string.Empty,
                Port = 8080
            }
        };

        Assert.True(PeerCommunication.CanUseUtpWithProxy(settings));
    }
}





