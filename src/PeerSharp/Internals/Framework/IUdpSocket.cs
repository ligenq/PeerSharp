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
