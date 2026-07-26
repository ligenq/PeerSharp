using System.Net;

namespace PeerSharp.Core;

/// <summary>
/// An info-hash learned from the DHT by BEP 51 sampling.
///
/// <para>
/// This is a claim by one node that it holds peers for this hash, not a promise that the torrent
/// exists, is reachable, or is anything in particular. To find out what it actually is, fetch its
/// metadata over BEP 9:
/// <c>engine.GetMagnetMetadataAsync(MagnetLink.Parse($"magnet:?xt=urn:btih:{hash}"))</c>.
/// </para>
/// </summary>
/// <param name="InfoHash">The discovered info-hash. Always a v1 (20 byte) hash.</param>
/// <param name="Source">The node that reported it.</param>
/// <param name="DiscoveredAt">When it was received.</param>
/// <param name="SourceTotalInfoHashes">
/// How many info-hashes the reporting node claims to hold in total. Larger than the sample it sent
/// means that node has more to give on a later pass.
/// </param>
public sealed record DiscoveredInfoHash(
    InfoHash InfoHash,
    IPEndPoint Source,
    DateTimeOffset DiscoveredAt,
    int SourceTotalInfoHashes);
