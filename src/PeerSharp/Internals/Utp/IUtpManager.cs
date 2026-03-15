using PeerSharp.Internals.Network;
using System.Net;

namespace PeerSharp.Internals.Utp;

internal interface IUtpManager : IAsyncDisposable
{
    Action<UtpStream>? OnNewConnection { get; set; }

    void CloseStream(UtpStream stream);

    UtpStream CreateStream(IPEndPoint remote);

    Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct);

    void Start(IUdpListener listener);

    void Stop();
}
