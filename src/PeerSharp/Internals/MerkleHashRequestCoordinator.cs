using System.Collections.Concurrent;

namespace PeerSharp.Internals;

internal sealed class MerkleHashRequestCoordinator
{
    private readonly ConcurrentDictionary<string, OutstandingRequest> _outstandingHashRequests = new();
    private readonly TimeSpan _retryInterval;

    public MerkleHashRequestCoordinator(TimeSpan retryInterval)
    {
        _retryInterval = retryInterval;
    }

    public static MerkleHashRequestSelection<TPeer> SelectBep30Peer<TPeer>(
        IEnumerable<TPeer> peers,
        Func<TPeer, bool> canRequest)
        where TPeer : class
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(canRequest);

        foreach (var peer in peers)
        {
            if (canRequest(peer))
            {
                return MerkleHashRequestSelection<TPeer>.Selected(peer, null);
            }
        }

        return MerkleHashRequestSelection<TPeer>.NoPeer;
    }

    public MerkleHashRequestSelection<TPeer> SelectV2Peer<TPeer>(
        V2HashRequest? request,
        IEnumerable<TPeer> peers,
        Func<TPeer, bool> canRequest,
        DateTimeOffset now)
        where TPeer : class
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(canRequest);

        if (request == null)
        {
            return MerkleHashRequestSelection<TPeer>.NoRequest;
        }

        string requestKey = CreateRequestKey(request);
        object? previousPeer = null;
        if (_outstandingHashRequests.TryGetValue(requestKey, out var outstanding))
        {
            if (now - outstanding.RequestedAt < _retryInterval)
            {
                return MerkleHashRequestSelection<TPeer>.Throttled(requestKey);
            }

            outstanding.Peer?.TryGetTarget(out previousPeer);
        }

        // A retry means the last peer asked did not deliver - it may have rejected the request or
        // ignored it. Asking it again would stall the piece forever, so another peer goes first.
        TPeer? fallback = null;
        foreach (var peer in peers)
        {
            if (!canRequest(peer))
            {
                continue;
            }

            if (ReferenceEquals(peer, previousPeer))
            {
                fallback = peer;
                continue;
            }

            return Select(peer, requestKey, now);
        }

        return fallback != null
            ? Select(fallback, requestKey, now)
            : MerkleHashRequestSelection<TPeer>.NoPeer;
    }

    public void CompleteFailedV2Request(string requestKey)
    {
        // Keep the peer so the next attempt prefers someone else, but allow it immediately.
        _outstandingHashRequests.AddOrUpdate(
            requestKey,
            static _ => new OutstandingRequest(DateTimeOffset.MinValue, null),
            static (_, existing) => existing with { RequestedAt = DateTimeOffset.MinValue });
    }

    private MerkleHashRequestSelection<TPeer> Select<TPeer>(TPeer peer, string requestKey, DateTimeOffset now)
        where TPeer : class
    {
        _outstandingHashRequests[requestKey] = new OutstandingRequest(now, new WeakReference<object>(peer));
        return MerkleHashRequestSelection<TPeer>.Selected(peer, requestKey);
    }

    private readonly record struct OutstandingRequest(DateTimeOffset RequestedAt, WeakReference<object>? Peer);

    private static string CreateRequestKey(V2HashRequest request)
    {
        return $"{Convert.ToHexString(request.PiecesRoot)}|{request.Index}";
    }
}

internal enum MerkleHashRequestSelectionStatus
{
    Selected,
    NoRequest,
    Throttled,
    NoPeer
}

internal readonly record struct MerkleHashRequestSelection<TPeer>(
    MerkleHashRequestSelectionStatus Status,
    TPeer? Peer,
    string? RequestKey)
    where TPeer : class
{
    public static MerkleHashRequestSelection<TPeer> NoRequest { get; } =
        new(MerkleHashRequestSelectionStatus.NoRequest, null, null);

    public static MerkleHashRequestSelection<TPeer> NoPeer { get; } =
        new(MerkleHashRequestSelectionStatus.NoPeer, null, null);

    public static MerkleHashRequestSelection<TPeer> Selected(TPeer peer, string? requestKey) =>
        new(MerkleHashRequestSelectionStatus.Selected, peer, requestKey);

    public static MerkleHashRequestSelection<TPeer> Throttled(string requestKey) =>
        new(MerkleHashRequestSelectionStatus.Throttled, null, requestKey);
}
