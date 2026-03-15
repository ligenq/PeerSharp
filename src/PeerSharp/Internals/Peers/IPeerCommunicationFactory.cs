using Microsoft.Extensions.Logging;
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
    private static readonly ILogger _logger = TorrentLoggerFactory.CreateLogger<PeerCommunicationFactory>();

    public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider)
    {
        return new PeerCommunication(torrent, listener, timeProvider);
    }

    public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? endpoint = null)
    {
        var peer = new PeerCommunication(torrent, listener, timeProvider)
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
        PeerCommunication.ConfigureTcpClient(client, torrent.Settings, _logger);
        var peer = new PeerCommunication(torrent, listener, timeProvider)
        {
            Client = client,
            Stream = client.GetStream(),
            RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint,
            Connected = 1
        };
        return peer;
    }
}
