using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PeerSharp.Internals.Framework;

/// <summary>
/// Abstraction for UdpClient to facilitate unit testing.
/// </summary>
internal interface IUdpSocket : IDisposable
{
    Socket Client { get; }

    void Close();

    /// <summary>
    /// Joins a multicast group, optionally pinning it to one local interface.
    /// </summary>
    /// <param name="multicastAddr">The group to join.</param>
    /// <param name="localInterface">
    /// The local address whose interface should carry the membership and this socket's outgoing
    /// multicast, or <see langword="null"/> to leave both to the host's routing table. Binding the
    /// socket does not settle either one on its own.
    /// </param>
    void JoinMulticastGroup(IPAddress multicastAddr, IPAddress? localInterface = null);

    Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);

    ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct);
}

/// <summary>
/// Factory for creating IUdpSocket instances.
/// </summary>
internal interface IUdpSocketFactory
{
    IUdpSocket Create(int port);

    IUdpSocket Create(AddressFamily family);
}

internal class UdpSocketFactory : IUdpSocketFactory
{
    public IUdpSocket Create(int port)
    {
        return UdpSocketAdapter.FromPort(port);
    }

    public IUdpSocket Create(AddressFamily family)
    {
        return new UdpSocketAdapter(new UdpClient(family), true);
    }
}

internal class UdpSocketAdapter : IUdpSocket
{
    private readonly UdpClient _client;
    private readonly bool _ownsClient;
    private AtomicDisposal _disposal = new();

    public UdpSocketAdapter(UdpClient client, bool ownsClient)
    {
        _client = client;
        _ownsClient = ownsClient;
        SuppressConnectionReset(client);
    }

    /// <summary>
    /// Windows reports a datagram's ICMP port-unreachable on the <em>next receive</em> from the socket,
    /// as <see cref="SocketError.ConnectionReset"/>. On a connectionless socket that answer belongs to
    /// nothing the caller asked about: the DHT sends to thousands of nodes and any one of them being
    /// gone would otherwise abort an unrelated receive, log a stack trace, and count towards the
    /// receive-error backoff. SIO_UDP_CONNRESET turns the behaviour off, which is what every other
    /// platform already does.
    /// </summary>
    private static void SuppressConnectionReset(UdpClient client)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        try
        {
            client.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            // Not fatal: the socket keeps working, receives just keep surfacing resets as before.
        }
    }

    public Socket Client => _client.Client;

    public static UdpSocketAdapter FromPort(int port)
    {
        try
        {
            var client = new UdpClient(AddressFamily.InterNetworkV6);
            client.Client.DualMode = true;
            client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return new UdpSocketAdapter(client, true);
        }
        catch (SocketException)
        {
            // Fallback to IPv4 if IPv6/DualMode is not supported
            return new UdpSocketAdapter(new UdpClient(port), true);
        }
    }

    public void Close()
    {
        _client.Close();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void JoinMulticastGroup(IPAddress multicastAddr, IPAddress? localInterface = null)
    {
        if (localInterface == null)
        {
            _client.JoinMulticastGroup(multicastAddr);
            return;
        }

        // Membership and outbound selection are two separate settings, and binding the socket sets
        // neither. The membership decides which interface's group traffic reaches us and where the
        // IGMP/MLD join itself is sent; the multicast-interface option decides where our own
        // datagrams leave from. A socket bound to enforce one interface needs both pinned, or half
        // its multicast traffic still follows the host's default route.
        if (localInterface.AddressFamily == AddressFamily.InterNetwork)
        {
            _client.JoinMulticastGroup(multicastAddr, localInterface);
            _client.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                localInterface.GetAddressBytes());
            return;
        }

        int interfaceIndex = GetInterfaceIndex(localInterface);
        _client.JoinMulticastGroup(interfaceIndex, multicastAddr);
        _client.Client.SetSocketOption(
            SocketOptionLevel.IPv6,
            SocketOptionName.MulticastInterface,
            interfaceIndex);
    }

    /// <summary>
    /// Finds the interface index owning an IPv6 address. IPv6 multicast is joined by index rather
    /// than by address, and a scope id only carries one for link-local addresses - the global address
    /// a tunnel hands out has none. Failing here is deliberate: the caller asked for traffic confined
    /// to this address, and the alternative is index 0, which is the default route.
    /// </summary>
    private static int GetInterfaceIndex(IPAddress localInterface)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            if (!properties.UnicastAddresses.Any(unicast => unicast.Address.Equals(localInterface)))
            {
                continue;
            }

            return properties.GetIPv6Properties().Index;
        }

        if (localInterface.ScopeId is > 0 and <= int.MaxValue)
        {
            return (int)localInterface.ScopeId;
        }

        throw new SocketException((int)SocketError.AddressNotAvailable);
    }

    public Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        return _client.ReceiveAsync(cancellationToken).AsTask();
    }

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
    {
        return _client.SendAsync(datagram, endPoint, ct);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing && _ownsClient)
        {
            _client.Dispose();
        }
    }
}
