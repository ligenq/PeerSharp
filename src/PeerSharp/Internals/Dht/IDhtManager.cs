using System.Net;

namespace PeerSharp.Internals.Dht;

internal interface IDhtManager : IAsyncDisposable
{
    InfoHash NodeId { get; }

    void Announce(InfoHash infoHash, int port);

    void FindPeers(InfoHash infoHash);

    void Ping(IPEndPoint ep);

    void ScrapeInfoHash(InfoHash infoHash);

    void SetCallback(IDhtCallback callback);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    DhtState? ConsumeStateSnapshot();
}
