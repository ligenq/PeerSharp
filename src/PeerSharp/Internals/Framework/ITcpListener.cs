using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Framework;

internal interface ITcpListener : IDisposable
{
    EndPoint? LocalEndpoint { get; }

    Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken);

    void Start();

    void Stop();
}

internal interface ITcpListenerFactory
{
    ITcpListener Create(IPAddress address, int port);
}

internal class TcpListenerFactory : ITcpListenerFactory
{
    public ITcpListener Create(IPAddress address, int port)
    {
        // If the caller requests IPv4 Any, prefer dual-stack on IPv6 Any to accept IPv4+IPv6.
        if (address.Equals(IPAddress.Any))
        {
            if (Socket.OSSupportsIPv6)
            {
                return new TcpListenerAdapter(IPAddress.IPv6Any, port, dualMode: true);
            }

            return new TcpListenerAdapter(IPAddress.Any, port, dualMode: false);
        }

        return new TcpListenerAdapter(address, port, dualMode: false);
    }
}

internal class TcpListenerAdapter : ITcpListener
{
    private readonly TcpListener _listener;
    private AtomicDisposal _disposal = new();

    public TcpListenerAdapter(IPAddress address, int port, bool dualMode)
    {
        _listener = new TcpListener(address, port);
        if (dualMode && Socket.OSSupportsIPv6 && _listener.Server.AddressFamily == AddressFamily.InterNetworkV6)
        {
            _listener.Server.DualMode = true;
        }
    }

    public EndPoint? LocalEndpoint => _listener.LocalEndpoint;

    public Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
    {
        return _listener.AcceptTcpClientAsync(cancellationToken).AsTask();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void Start()
    {
        _listener.Start();
    }

    public void Stop()
    {
        _listener.Stop();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            _listener.Stop();
        }
    }
}
