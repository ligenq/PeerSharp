using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Framework;

/// <summary>
/// Abstraction for UdpClient to facilitate unit testing.
/// </summary>
internal interface IUdpSocket : IDisposable
{
    Socket Client { get; }

    void Close();

    void JoinMulticastGroup(IPAddress multicastAddr);

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

    public void JoinMulticastGroup(IPAddress multicastAddr)
    {
        _client.JoinMulticastGroup(multicastAddr);
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
