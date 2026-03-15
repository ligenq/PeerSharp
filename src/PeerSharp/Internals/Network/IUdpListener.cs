using System.Net;

namespace PeerSharp.Internals.Network;

internal interface IUdpListener : IAsyncDisposable
{
    int Port { get; }

    void RegisterReceiver(IUdpReceiver receiver);

    Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct);

    Task StartAsync(CancellationToken cancellationToken = default);

    void Stop();

    Task StopAsync();
}
