using System.Net;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// Callback interface for DHT results.
/// </summary>
internal interface IDhtCallback
{
    void OnPeersFound(InfoHash infoHash, List<IPEndPoint> peers);

    void OnScrapeResult(InfoHash infoHash, int estimatedSeeds, int estimatedPeers);
}
