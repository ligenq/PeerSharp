using PeerSharp.Config;

namespace PeerSharp.Internals.Network;

/// <summary>
/// How UDP may leave this machine given the configured proxy and the traffic-specific proxy setting.
/// </summary>
/// <remarks>
/// <para>
/// Only SOCKS5 can tunnel UDP; an HTTP proxy cannot carry it at all. Binding a direct socket when
/// the relevant traffic is configured to use that proxy exposes the real address instead. Traffic
/// for which proxying is explicitly disabled may still use a direct socket.
/// </para>
/// <para>
/// libtorrent refuses the send rather than falling back:
/// <c>if (active_socks5()) wrap(...); else ec = permission_denied;</c> in <c>udp_socket::send</c>,
/// reached for DHT traffic because it is flagged as neither peer nor tracker. This is the same
/// decision made once at bind time instead of per packet, so the failure is loud and diagnosable
/// rather than silent.
/// </para>
/// </remarks>
internal static class UdpProxyPolicy
{
    /// <summary>What a UDP transport should do with the configured proxy.</summary>
    internal enum Decision
    {
        /// <summary>No usable proxy is configured, so bind a socket normally.</summary>
        BindDirectly,

        /// <summary>Open a SOCKS5 UDP association and send through it.</summary>
        TunnelThroughSocks5,

        /// <summary>A proxy is configured that cannot carry UDP. Send nothing.</summary>
        Refuse
    }

    /// <summary>
    /// Decides how UDP may be sent for one kind of traffic.
    /// </summary>
    /// <param name="proxy">The configured proxy, which may be none.</param>
    /// <param name="proxyTraffic">Whether this kind of traffic is configured to use the proxy.</param>
    public static Decision Decide(ProxySettings proxy, bool proxyTraffic)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        if (!proxyTraffic)
        {
            return Decision.BindDirectly;
        }

        // A type without a host is not a usable proxy, and is treated as none everywhere else that
        // asks this question.
        bool configured = proxy.Type != ProxyType.None && !string.IsNullOrEmpty(proxy.Host);
        if (!configured)
        {
            return Decision.BindDirectly;
        }

        return proxy.Type == ProxyType.Socks5 ? Decision.TunnelThroughSocks5 : Decision.Refuse;
    }
}
