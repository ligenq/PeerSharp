using Microsoft.Extensions.Logging;
using PeerSharp.BEncoding;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace PeerSharp.Internals.Dht;

/// <summary>
/// Client side of BEP 44: reading and publishing DHT items.
///
/// These are iterative lookups rather than fire-and-forget queries, so unlike the rest of the DHT
/// surface they are awaitable. A get walks toward the target keeping the closest nodes seen so
/// far, and a put reuses that walk - it has to, because writing requires a token that only comes
/// back from a get to the same node.
/// </summary>
internal partial class DhtManager
{
    /// <summary>Nodes queried in parallel at each step of a lookup.</summary>
    private const int LookupConcurrency = 3;

    /// <summary>Nodes an item is written to, matching the replication BEP 5 uses for peers.</summary>
    private const int ReplicationCount = 8;

    /// <summary>Upper bound on lookup rounds, so a hostile or broken swarm cannot spin forever.</summary>
    private const int MaxLookupRounds = 8;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<BDict>> _pendingItemQueries = new();

    /// <summary>
    /// Fetches an item from the DHT.
    /// </summary>
    /// <param name="target">The address to read.</param>
    /// <param name="salt">
    /// The salt the target was derived from, for a salted mutable item. A BEP 44 <c>get</c> reply
    /// deliberately does not carry the salt - the requester computed the target, so it already
    /// knows it - but the salt is part of what the signature covers and what the address is
    /// derived from, so it has to be supplied here or a salted item cannot be verified.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The most recent item found, or null when no node returned a verifiable one.</returns>
    public async Task<DhtItem?> GetItemAsync(DhtTarget target, byte[]? salt = null, CancellationToken cancellationToken = default)
    {
        var result = await RunItemLookupAsync(target, salt, cancellationToken).ConfigureAwait(false);
        return result.Item;
    }

    /// <summary>
    /// Publishes an item to the nodes closest to its address.
    /// </summary>
    /// <param name="item">The item to store. Must already be signed if mutable.</param>
    /// <param name="compareAndSwap">
    /// Optional expected sequence number, so a publisher can detect a concurrent update rather
    /// than silently overwriting it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of nodes that accepted the item.</returns>
    public async Task<int> PutItemAsync(DhtItem item, long? compareAndSwap = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var validation = DhtItemCodec.Validate(item);
        if (validation != DhtPutError.None)
        {
            throw new ArgumentException($"The item is not valid for publication: {validation}.", nameof(item));
        }

        // The walk is what produces the write tokens, so a put always begins with a get.
        var lookup = await RunItemLookupAsync(
            item.Target,
            (item as DhtMutableItem)?.Salt,
            cancellationToken).ConfigureAwait(false);
        if (lookup.WriteTargets.Count == 0)
        {
            return 0;
        }

        var accepted = 0;
        foreach (var (endpoint, token) in lookup.WriteTargets.Take(ReplicationCount))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var reply = await SendItemQueryAsync(
                BuildPutQuery(item, token, compareAndSwap, out var transactionId),
                transactionId,
                endpoint,
                cancellationToken).ConfigureAwait(false);

            if (reply?.Get("r") is BDict)
            {
                accepted++;
            }
        }

        _logger.LogDebug("BEP 44 put of {Target} accepted by {Accepted} node(s)", item.Target, accepted);
        return accepted;
    }

    /// <summary>
    /// Walks the DHT toward <paramref name="target"/>, collecting the newest item any node holds
    /// and the write tokens needed to publish there afterwards.
    /// </summary>
    private async Task<ItemLookupResult> RunItemLookupAsync(DhtTarget target, byte[]? salt, CancellationToken cancellationToken)
    {
        var queried = new HashSet<IPEndPoint>();
        var writeTargets = new List<(IPEndPoint Endpoint, byte[] Token)>();
        DhtItem? best = null;

        var candidates = _table.FindClosest(target.Span, ReplicationCount)
            .Select(node => node.EndPoint)
            .ToList();

        for (int round = 0; round < MaxLookupRounds && candidates.Count > 0; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = candidates.Where(queried.Add).Take(LookupConcurrency).ToArray();
            if (batch.Length == 0)
            {
                break;
            }

            var replies = await Task.WhenAll(batch.Select(endpoint => SendItemQueryAsync(
                BuildGetQuery(target, out var transactionId),
                transactionId,
                endpoint,
                cancellationToken))).ConfigureAwait(false);

            var discovered = new List<IPEndPoint>();

            for (int i = 0; i < replies.Length; i++)
            {
                if (replies[i]?.Get("r") is not BDict reply)
                {
                    continue;
                }

                var token = reply.GetBytes("token");
                if (token is not null)
                {
                    writeTargets.Add((batch[i], token.Value.ToArray()));
                }

                var candidate = TryReadItemFromReply(reply, target, salt);
                if (candidate is not null && IsNewer(candidate, best))
                {
                    best = candidate;
                }

                discovered.AddRange(ReadNodeEndpoints(reply));
            }

            candidates = discovered.Where(endpoint => !queried.Contains(endpoint)).Distinct().ToList();
        }

        return new ItemLookupResult(best, writeTargets);
    }

    /// <summary>
    /// Reads an item out of a get reply, verifying it before trusting it. A node is free to
    /// return anything at all, so a mutable item is only accepted when its signature checks out
    /// <em>and</em> the key and salt actually hash to the address we asked about - otherwise a
    /// node could answer one query with a validly signed record belonging elsewhere.
    /// </summary>
    private DhtItem? TryReadItemFromReply(BDict reply, DhtTarget target, byte[]? salt)
    {
        var value = reply.Get("v");
        if (value is null)
        {
            return null;
        }

        var publicKey = reply.GetBytes("k");
        if (publicKey is null)
        {
            var immutable = new DhtImmutableItem { Value = value };
            return immutable.Target == target ? immutable : null;
        }

        var signature = reply.GetBytes("sig");
        if (signature is null || reply.Get("seq") is not BNumber sequence)
        {
            return null;
        }

        var mutable = new DhtMutableItem
        {
            Value = value,
            PublicKey = publicKey.Value.ToArray(),
            SequenceNumber = sequence.Value,
            Signature = signature.Value.ToArray(),
            Salt = salt is { Length: > 0 } ? salt : null,
        };

        if (mutable.Target != target)
        {
            _logger.LogDebug("Discarded a BEP 44 reply whose key and salt address {Actual}, not {Requested}", mutable.Target, target);
            return null;
        }

        if (!mutable.VerifySignature())
        {
            _logger.LogDebug("Discarded a BEP 44 reply for {Target} with an invalid signature", target);
            return null;
        }

        return mutable;
    }

    private static bool IsNewer(DhtItem candidate, DhtItem? incumbent)
    {
        if (incumbent is null)
        {
            return true;
        }

        return candidate is DhtMutableItem newer &&
               incumbent is DhtMutableItem current &&
               newer.SequenceNumber > current.SequenceNumber;
    }

    private static IEnumerable<IPEndPoint> ReadNodeEndpoints(BDict reply)
    {
        var results = new List<IPEndPoint>();

        if (reply.GetBytes("nodes") is { } nodes)
        {
            results.AddRange(DhtCompactNodeCodec.Parse(nodes.Span, ipv6: false).Select(node => node.EndPoint));
        }

        if (reply.GetBytes("nodes6") is { } nodes6)
        {
            results.AddRange(DhtCompactNodeCodec.Parse(nodes6.Span, ipv6: true).Select(node => node.EndPoint));
        }

        return results;
    }

    private BDict BuildGetQuery(DhtTarget target, out string transactionId)
    {
        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        a.Dict["target"] = new BString(target.Span.ToArray());
        return BuildQuery("get", a, out transactionId);
    }

    private BDict BuildPutQuery(DhtItem item, byte[] token, long? compareAndSwap, out string transactionId)
    {
        var a = new BDict();
        a.Dict["id"] = new BString(NodeId.ToArray());
        a.Dict["token"] = new BString(token);
        a.Dict["v"] = item.Value;

        if (item is DhtMutableItem mutable)
        {
            a.Dict["k"] = new BString(mutable.PublicKey);
            a.Dict["sig"] = new BString(mutable.Signature);
            a.Dict["seq"] = new BNumber(mutable.SequenceNumber);

            if (mutable.Salt is { Length: > 0 })
            {
                a.Dict["salt"] = new BString(mutable.Salt);
            }

            if (compareAndSwap is not null)
            {
                a.Dict["cas"] = new BNumber(compareAndSwap.Value);
            }
        }

        return BuildQuery("put", a, out transactionId);
    }

    private static BDict BuildQuery(string queryName, BDict arguments, out string transactionId)
    {
        Span<byte> tid = stackalloc byte[4];
        GenerateTransactionId(tid);
        transactionId = Encoding.Latin1.GetString(tid);

        var dict = new BDict();
        dict.Dict["t"] = new BString(tid.ToArray());
        dict.Dict["y"] = new BString("q"u8.ToArray());
        dict.Dict["q"] = new BString(Encoding.ASCII.GetBytes(queryName));
        dict.Dict["a"] = arguments;
        return dict;
    }

    /// <summary>
    /// Sends a query and waits for the matching reply. Returns null on timeout or cancellation;
    /// an unresponsive node is ordinary in a DHT and not worth an exception.
    /// </summary>
    private async Task<BDict?> SendItemQueryAsync(BDict query, string transactionId, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<BDict>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingItemQueries.TryAdd(transactionId, completion))
        {
            return null;
        }

        try
        {
            SendPacket(query, endpoint, cancellationToken);
            return await completion.Task.WaitAsync(QueryTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingItemQueries.TryRemove(transactionId, out _);
        }
    }

    private sealed record ItemLookupResult(DhtItem? Item, IReadOnlyList<(IPEndPoint Endpoint, byte[] Token)> WriteTargets);
}
