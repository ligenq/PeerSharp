using PeerSharp.Config;

namespace PeerSharp.Internals.Network;

/// <summary>
/// How UDP may leave this machine given the configured proxy.
/// </summary>
/// <remarks>
/// <para>
/// The UDP socket carries the DHT and uTP, so where it sends from is where the swarm and every DHT
/// node see this client. Only SOCKS5 can tunnel UDP; an HTTP proxy cannot carry it at all. Binding a
/// direct socket in that case leaves the real address exposed to the DHT while the tracker and peer
/// traffic the user configured the proxy for goes through it, which is the shape of leak a proxy is
/// bought to prevent.
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
    /// <summary>What the UDP listener should do with the configured proxy.</summary>
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
    /// Decides how UDP may be sent.
    /// </summary>
    /// <param name="proxy">The configured proxy, which may be none.</param>
    public static Decision Decide(ProxySettings proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);

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
