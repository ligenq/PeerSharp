using System.Net;

namespace PeerSharp.Internals.Dht;

internal interface IDhtManager : IAsyncDisposable
{
    InfoHash NodeId { get; }

    void Announce(InfoHash infoHash, int port);

    void FindPeers(InfoHash infoHash);

    void Ping(IPEndPoint ep);

    /// <summary>
    /// Reports an external address observed outside the DHT - today a BEP 24 tracker response - as
    /// one vote towards the BEP 42 secure node ID. Trackers are a separate trust domain from DHT
    /// nodes, so a report here corroborates what the DHT told us rather than replacing it.
    /// </summary>
    void ReportExternalIp(IPAddress address);

    void ScrapeInfoHash(InfoHash infoHash);

    void SetCallback(IDhtCallback callback);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    DhtState? ConsumeStateSnapshot();
}
