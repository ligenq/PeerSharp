using PeerSharp.Core;
using PeerSharp.Internals.Dht;
using System.Runtime.CompilerServices;

namespace PeerSharp.Internals;

/// <summary>
/// BEP 51 surface on the engine: enumerating the info-hashes the DHT knows about.
///
/// <para>
/// This is discovery, not transfer - it answers "what exists out there", which the rest of the engine
/// has no way to ask. Pair it with <see cref="ClientEngine.GetMagnetMetadataAsync"/> to turn a
/// discovered hash into a name and file list.
/// </para>
/// </summary>
internal sealed partial class ClientEngine
{
    /// <inheritdoc />
    public async IAsyncEnumerable<DiscoveredInfoHash> DiscoverInfoHashesAsync(
        DhtIndexerOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effective = options ?? new DhtIndexerOptions();
        effective.Validate(nameof(options));

        if (Dht is not DhtManager dht)
        {
            throw new InvalidOperationException(
                "BEP 51 crawling requires the DHT. Enable it via Settings.Dht and start the engine first.");
        }

        var crawler = dht.CreateInfoHashCrawler(effective, _loggerFactory);

        await foreach (var discovered in crawler.CrawlAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return discovered;
        }
    }
}
