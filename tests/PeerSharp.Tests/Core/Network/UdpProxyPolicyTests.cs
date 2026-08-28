using PeerSharp.Config;
using PeerSharp.Internals.Network;
using Decision = PeerSharp.Internals.Network.UdpProxyPolicy.Decision;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// Where UDP is allowed to leave from when a proxy is configured.
///
/// <para>
/// The UDP socket carries the DHT and uTP, so its source address is what every DHT node and every
/// uTP peer sees. Only SOCKS5 can tunnel UDP. PeerSharp used to bind a direct socket for any other
/// proxy type, which put the real address in front of the DHT while the tracker and peer traffic the
/// proxy was configured for went through it - the exact leak a proxy is bought to prevent.
/// </para>
///
/// <para>
/// libtorrent refuses instead of falling back, in <c>udp_socket::send</c>: an active SOCKS5
/// association wraps the packet and anything else sets <c>permission_denied</c>. DHT reaches that
/// branch because it is flagged as neither peer nor tracker traffic. This is the same rule, decided
/// once when the socket is opened rather than per packet.
/// </para>
/// </summary>
public class UdpProxyPolicyTests
{
    [Fact]
    public void WithNoProxyUdpIsSentNormally()
    {
        Assert.Equal(Decision.BindDirectly, UdpProxyPolicy.Decide(new ProxySettings(), proxyTraffic: true));
    }

    [Fact]
    public void Socks5CarriesUdpItself()
    {
        var proxy = new ProxySettings { Type = ProxyType.Socks5, Host = "127.0.0.1", Port = 1080 };

        Assert.Equal(Decision.TunnelThroughSocks5, UdpProxyPolicy.Decide(proxy, proxyTraffic: true));
    }

    [Fact]
    public void AnHttpProxyCannotCarryUdpSoNothingIsSent()
    {
        // The leak this exists for: an HTTP proxy has no UDP at all, and the old code answered that
        // by binding a normal socket.
        var proxy = new ProxySettings { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 };

        Assert.Equal(Decision.Refuse, UdpProxyPolicy.Decide(proxy, proxyTraffic: true));
    }

    [Fact]
    public void ATypeWithoutAHostIsNotAProxy()
    {
        // Every other component treats this as unconfigured, and disagreeing here would refuse to
        // start for a setting that has never meant anything.
        var proxy = new ProxySettings { Type = ProxyType.Http, Host = "" };

        Assert.Equal(Decision.BindDirectly, UdpProxyPolicy.Decide(proxy, proxyTraffic: true));
    }

    [Fact]
    public void TrafficExcludedFromProxyingMayUseUdpDirectly()
    {
        var proxy = new ProxySettings { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 };

        Assert.Equal(Decision.BindDirectly, UdpProxyPolicy.Decide(proxy, proxyTraffic: false));
    }
}
