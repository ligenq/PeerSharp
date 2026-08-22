using System.Net;

namespace PeerSharp.Internals.Dht;

internal interface IDhtManager : IAsyncDisposable
{
    InfoHash NodeId { get; }

    /// <summary>
    /// This node's own public address, once enough sources agree, otherwise null.
    /// </summary>
    IPAddress? ExternalIp { get; }


    void Announce(InfoHash infoHash, int port);

    /// <summary>
    /// Asks the nodes nearest this hash who is in the swarm.
    /// </summary>
    /// <returns>
    /// How many nodes were queried. Zero means the routing table had nothing to ask - which is the
    /// normal state for the first seconds after start - and the caller should try again rather than
    /// treat the lookup as done.
    /// </returns>
    int FindPeers(InfoHash infoHash);

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
