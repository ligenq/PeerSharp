using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Utp;
using System.Net;

namespace PeerSharp.Internals.Peers;

internal interface IPeerCommunicationFactory
{
    PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider);

    PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? endpoint = null);

    PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client);
}

internal class PeerCommunicationFactory : IPeerCommunicationFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public PeerCommunicationFactory()
        : this(NullLoggerFactory.Instance)
    {
    }

    public PeerCommunicationFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
    {
        return new PeerCommunication(torrent, listener, timeProvider, _loggerFactory);
    }

    public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? endpoint = null)
    {
        var peer = new PeerCommunication(torrent, listener, timeProvider, _loggerFactory)
        {
            Stream = stream,
            RemoteEndPoint = endpoint,
            Connected = 1
        };
        if (stream is UtpStream us)
        {
            peer.UtpStream = us;
        }
        return peer;
    }

    public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, System.Net.Sockets.TcpClient client)
    {
        PeerCommunication.ConfigureTcpClient(client, torrent.Settings, _loggerFactory.CreateLogger<PeerCommunicationFactory>());
        var peer = new PeerCommunication(torrent, listener, timeProvider, _loggerFactory)
        {
            Client = client,
            Stream = client.GetStream(),
            RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint,
            Connected = 1
        };
        return peer;
    }
}
