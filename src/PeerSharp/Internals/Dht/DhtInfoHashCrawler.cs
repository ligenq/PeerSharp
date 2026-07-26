using Microsoft.Extensions.Logging;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// Walks the DHT asking nodes for samples of the info-hashes they hold (BEP 51), and streams the
/// distinct ones back.
///
/// <para>
/// The walk is a frontier of known nodes rather than a lookup toward a fixed target: there is no
/// destination, the goal is coverage. Each reply supplies both hashes and more nodes, so one query
/// per node both harvests and expands - which is exactly what BEP 51 was designed to make possible.
/// Every node is put on a cooldown afterwards, taken from the interval it asked for, and a node with
/// nothing to add is simply not asked again until then.
/// </para>
///
/// <para>
/// Results stream out as they arrive rather than accumulating: a crawl has no natural end, so the
/// consumer sets the pace and decides when it has seen enough. Cancelling throws
/// <see cref="OperationCanceledException"/> as an enumerator is expected to; breaking out of the loop
/// stops it without one. The only self-terminating case is a frontier where nothing answers.
/// </para>
/// </summary>
internal sealed class DhtInfoHashCrawler
{
    /// <summary>
    /// How long to wait for the routing table to offer a starting node. A crawl cannot begin from an
    /// empty table, and bootstrap runs in the background, so a caller who starts the engine and
    /// immediately crawls has to be allowed to wait.
    /// </summary>
    private static readonly TimeSpan RoutingTableWarmupTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan RoutingTablePollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to sleep when every known node is in cooldown. Short enough to pick up an expiring
    /// cooldown promptly, long enough not to spin.
    /// </summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);

    private readonly DhtManager _dht;
    private readonly DhtIndexerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DhtInfoHashCrawler> _logger;

    public DhtInfoHashCrawler(
        DhtManager dht,
        DhtIndexerOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        _dht = dht;
        _options = options;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<DhtInfoHashCrawler>();
    }

    public async IAsyncEnumerable<DiscoveredInfoHash> CrawlAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<InfoHash>();
        var known = new HashSet<IPEndPoint>();

        // Ready to ask now, in insertion order, so the crawl spreads outward instead of re-asking
        // whichever node last answered.
        var pending = new Queue<IPEndPoint>();

        // Asked recently; the value is when it may be asked again.
        var cooldown = new Dictionary<IPEndPoint, DateTimeOffset>();

        foreach (var endpoint in await WaitForStartingNodesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (known.Add(endpoint))
            {
                pending.Enqueue(endpoint);
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PromoteExpiredCooldowns(pending, cooldown);

            if (pending.Count == 0)
            {
                if (cooldown.Count == 0)
                {
                    // Nothing left to ask and nothing coming back: the frontier is genuinely spent.
                    _logger.LogDebug("BEP 51 crawl ran out of nodes after {Seen} info-hash(es)", seen.Count);
                    yield break;
                }

                // Every known node is inside the interval it asked for. Waiting is the correct
                // behaviour - the alternative is ignoring the interval - so the crawl idles here
                // until one comes due or the caller stops it.
                await Task.Delay(IdlePollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var batch = TakeBatch(pending);
            var results = await Task.WhenAll(batch.Select(endpoint => QueryAsync(endpoint, cancellationToken)))
                .ConfigureAwait(false);

            var now = _timeProvider.GetUtcNow();

            for (int i = 0; i < batch.Length; i++)
            {
                // Nodes without BEP 51 support are still worth their neighbours, so a result carries
                // nodes either way and only the sample may be absent.
                foreach (var discovered in results[i].Nodes)
                {
                    if (known.Count < _options.MaxTrackedNodes && known.Add(discovered))
                    {
                        pending.Enqueue(discovered);
                    }
                }

                if (results[i].Sample is not { } reply)
                {
                    // Nothing to sample now or later: drop it rather than cooling it down. Its
                    // neighbours, if it gave any, are already queued above.
                    //
                    // Dropped rather than remembered-as-dead deliberately. Remembering would avoid the
                    // occasional re-query when another node names it again, at the cost of letting
                    // dead entries accumulate against MaxTrackedNodes until the frontier could no
                    // longer grow at all. A wasted packet now and then is the cheaper failure.
                    known.Remove(batch[i]);
                    continue;
                }

                cooldown[batch[i]] = now + ClampRequeryInterval(reply.Interval);

                foreach (var hash in reply.Samples)
                {
                    // Checked per hash, not per round: a consumer who cancels part way through a
                    // batch must not receive the rest of it first.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!seen.Add(hash))
                    {
                        continue;
                    }

                    yield return new DiscoveredInfoHash(hash, batch[i], now, reply.TotalInfoHashes);

                    if (_options.MaxInfoHashes is { } limit && seen.Count >= limit)
                    {
                        _logger.LogDebug("BEP 51 crawl reached its limit of {Limit} info-hash(es)", limit);
                        yield break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Asks one node for a sample, falling back to <c>find_node</c> for its neighbours when it has no
    /// BEP 51 support.
    ///
    /// <para>
    /// The target is random and fresh each time, so successive queries to the same node return
    /// neighbours from a different part of the keyspace and the frontier keeps widening rather than
    /// converging on one region.
    /// </para>
    /// </summary>
    private async Task<NodeResult> QueryAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var target = RandomTarget();

        try
        {
            var sample = await _dht.SampleInfoHashesAsync(endpoint, target, cancellationToken).ConfigureAwait(false);
            if (sample is { } reply)
            {
                return new NodeResult(reply, reply.Nodes);
            }

            // No sample: either the node is gone or it predates BEP 51. Either way its neighbours are
            // what a crawl needs from it, and find_node is supported by everything - including the
            // bootstrap routers a fresh crawl necessarily starts from.
            var nodes = await _dht.FindNodeAsync(endpoint, target, cancellationToken).ConfigureAwait(false);
            return new NodeResult(null, nodes);
        }
        catch (OperationCanceledException)
        {
            return NodeResult.Empty;
        }
        catch (Exception ex)
        {
            // One unreachable or misbehaving node must not end a crawl.
            _logger.LogTrace(ex, "BEP 51 sample query to {Endpoint} failed", endpoint);
            return NodeResult.Empty;
        }
    }

    /// <summary>
    /// What one node yielded: a sample when it supports BEP 51, and whatever neighbours it named.
    /// </summary>
    private readonly record struct NodeResult(DhtInfoHashSampleReply? Sample, IReadOnlyList<IPEndPoint> Nodes)
    {
        public static NodeResult Empty { get; } = new(null, []);
    }

    /// <summary>
    /// Moves every node whose cooldown has expired back into the ready queue.
    /// </summary>
    private void PromoteExpiredCooldowns(Queue<IPEndPoint> pending, Dictionary<IPEndPoint, DateTimeOffset> cooldown)
    {
        if (cooldown.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        List<IPEndPoint>? ready = null;

        foreach (var (endpoint, readyAt) in cooldown)
        {
            if (readyAt <= now)
            {
                (ready ??= []).Add(endpoint);
            }
        }

        if (ready == null)
        {
            return;
        }

        foreach (var endpoint in ready)
        {
            cooldown.Remove(endpoint);
            pending.Enqueue(endpoint);
        }
    }

    private IPEndPoint[] TakeBatch(Queue<IPEndPoint> pending)
    {
        int size = Math.Min(_options.MaxConcurrency, pending.Count);
        var batch = new IPEndPoint[size];
        for (int i = 0; i < size; i++)
        {
            batch[i] = pending.Dequeue();
        }

        return batch;
    }

    /// <summary>
    /// Honours the node's requested interval, but never dips below the configured floor - a node
    /// asking for zero does not get to be queried in a tight loop.
    /// </summary>
    private TimeSpan ClampRequeryInterval(TimeSpan requested)
    {
        return requested > _options.MinNodeRequeryInterval ? requested : _options.MinNodeRequeryInterval;
    }

    private static DhtTarget RandomTarget()
    {
        Span<byte> target = stackalloc byte[DhtTarget.Length];
        RandomNumberGenerator.Fill(target);
        return new DhtTarget(target);
    }

    /// <summary>
    /// Waits until the routing table can offer at least one node to start from.
    /// </summary>
    /// <exception cref="TimeoutException">The routing table stayed empty.</exception>
    private async Task<IReadOnlyList<IPEndPoint>> WaitForStartingNodesAsync(CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + RoutingTableWarmupTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nodes = _dht.GetKnownNodeEndpoints(_options.MaxTrackedNodes);
            if (nodes.Count > 0)
            {
                return nodes;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"The DHT routing table was still empty after {RoutingTableWarmupTimeout}, so a BEP 51 crawl has nowhere to start. " +
                    "Check that the DHT is enabled and has reachable bootstrap nodes.");
            }

            await Task.Delay(RoutingTablePollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
