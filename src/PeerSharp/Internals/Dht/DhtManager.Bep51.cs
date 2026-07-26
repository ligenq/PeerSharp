using Microsoft.Extensions.Logging;
using PeerSharp.BEncoding;
using System.Net;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// Client side of BEP 51: asking a node which info-hashes it holds.
///
/// One query does double duty - it returns a sample of the node's storage <em>and</em> the nodes
/// closest to a target - which is what makes indexing the whole keyspace affordable at one RPC per
/// node. Everything a node says here is untrusted input, so every field is bounds-checked before it
/// reaches the crawler.
/// </summary>
internal partial class DhtManager
{
    /// <summary>
    /// Asks one node for a sample of its stored info-hashes.
    /// </summary>
    /// <param name="endpoint">The node to ask.</param>
    /// <param name="target">
    /// The keyspace position whose neighbours we want back. Unrelated to which hashes are sampled -
    /// the sample is drawn from the node's whole store - so a crawl varies this to fan out.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The parsed reply, or null when the node did not answer, answered with an error (including
    /// <c>204 Method Unknown</c> from anything that predates BEP 51), or sent something malformed.
    /// </returns>
    internal async Task<DhtInfoHashSampleReply?> SampleInfoHashesAsync(
        IPEndPoint endpoint,
        DhtTarget target,
        CancellationToken cancellationToken = default)
    {
        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        a.Dict["target"] = new BString(target.Span.ToArray());

        var reply = await SendCorrelatedQueryAsync(
            BuildQuery("sample_infohashes", a, out var transactionId),
            transactionId,
            endpoint,
            cancellationToken).ConfigureAwait(false);

        if (reply?.Get("r") is not BDict r)
        {
            return null;
        }

        return ParseSampleReply(r);
    }

    /// <summary>
    /// Nodes the routing table currently knows, as somewhere for a crawl to start.
    /// </summary>
    internal IReadOnlyList<IPEndPoint> GetKnownNodeEndpoints(int maxNodes)
    {
        return [.. _table.GetAllNodes(maxNodes).Select(static node => node.EndPoint)];
    }

    /// <summary>
    /// Asks one node for its neighbours and waits for the answer.
    ///
    /// <para>
    /// BEP 51 support is patchy - the bootstrap routers a fresh node starts from do not implement it
    /// at all - so a crawl that could only learn about new nodes from <c>sample_infohashes</c> replies
    /// would strand itself among the first few nodes it happened to know. <c>find_node</c> is
    /// universally supported, which makes it the reliable way to keep the frontier growing through
    /// nodes that have no samples to give.
    /// </para>
    ///
    /// <para>
    /// Unlike <see cref="SendFindNode(IPEndPoint, InfoHash, int)"/> this is awaited rather than
    /// fire-and-forget, because a crawl needs the neighbours in hand to decide where to go next.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<IPEndPoint>> FindNodeAsync(
        IPEndPoint endpoint,
        DhtTarget target,
        CancellationToken cancellationToken = default)
    {
        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        a.Dict["target"] = new BString(target.Span.ToArray());

        var reply = await SendCorrelatedQueryAsync(
            BuildQuery("find_node", a, out var transactionId),
            transactionId,
            endpoint,
            cancellationToken).ConfigureAwait(false);

        return reply?.Get("r") is BDict r ? [.. ReadNodeEndpoints(r)] : [];
    }

    private static DhtInfoHashSampleReply ParseSampleReply(BDict r)
    {
        var samples = new List<InfoHash>();
        if (r.GetBytes("samples") is { } raw)
        {
            var span = raw.Span;

            // A trailing partial hash means a broken or truncated peer. Take the whole ones and
            // ignore the remainder rather than discarding an otherwise good reply.
            int whole = span.Length / DhtTarget.Length;
            for (int i = 0; i < whole; i++)
            {
                samples.Add(new InfoHash(span.Slice(i * DhtTarget.Length, DhtTarget.Length)));
            }
        }

        // BEP 51 puts the interval in 0..21600. Clamping keeps a node from parking a crawler on it
        // forever with an absurd value, and covers negatives, which are meaningless here.
        long rawInterval = r.GetLong("interval") ?? 0;
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            rawInterval,
            0,
            (long)DhtInfoHashSampler.MaxRefreshInterval.TotalSeconds));

        // "num" is how many keys the node holds. It is advisory - used only to report how much of a
        // node's storage is still unseen - so a nonsense value is floored rather than rejected.
        int total = (int)Math.Clamp(r.GetLong("num") ?? samples.Count, 0, int.MaxValue);

        return new DhtInfoHashSampleReply(samples, total, interval, [.. ReadNodeEndpoints(r)]);
    }

    /// <summary>
    /// Builds a crawler over this DHT instance.
    /// </summary>
    internal DhtInfoHashCrawler CreateInfoHashCrawler(DhtIndexerOptions options, ILoggerFactory loggerFactory)
    {
        return new DhtInfoHashCrawler(this, options, _timeProvider, loggerFactory);
    }
}

/// <summary>
/// A parsed <c>sample_infohashes</c> reply.
/// </summary>
/// <param name="Samples">Info-hashes the node returned. May be empty.</param>
/// <param name="TotalInfoHashes">
/// The node's <c>num</c>: how many hashes it holds in total. Greater than
/// <paramref name="Samples"/> means more can be collected once the interval elapses.
/// </param>
/// <param name="Interval">How long until this node's sample may change.</param>
/// <param name="Nodes">Nodes it offered as close to the requested target.</param>
internal readonly record struct DhtInfoHashSampleReply(
    IReadOnlyList<InfoHash> Samples,
    int TotalInfoHashes,
    TimeSpan Interval,
    IReadOnlyList<IPEndPoint> Nodes);
