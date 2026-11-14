using System.Runtime.CompilerServices;

namespace PeerSharp.Internals;

internal sealed class MerkleHashRequestCoordinator
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, OutstandingRequest> _outstandingHashRequests = [];
    private readonly ConditionalWeakTable<object, PeerOrder> _peerOrder = new();
    private readonly TimeSpan _retryInterval;
    private long _nextPeerOrder;

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
        lock (_lock)
        {
            long previousOrder = 0;
            if (_outstandingHashRequests.TryGetValue(requestKey, out var outstanding))
            {
                if (now - outstanding.RequestedAt < _retryInterval)
                {
                    return MerkleHashRequestSelection<TPeer>.Throttled(requestKey);
                }
                previousOrder = outstanding.PeerOrder;
            }

            // Keep the order across enumeration changes and peer departures. Wrap only after
            // reaching the end, so every eligible peer gets a turn.
            (TPeer Peer, long Order)? first = null;
            (TPeer Peer, long Order)? next = null;
            foreach (var peer in peers)
            {
                if (!canRequest(peer))
                {
                    continue;
                }

                long order = _peerOrder.GetValue(peer, _ => new PeerOrder(++_nextPeerOrder)).Value;
                if (first == null || order < first.Value.Order)
                {
                    first = (peer, order);
                }
                if (order > previousOrder && (next == null || order < next.Value.Order))
                {
                    next = (peer, order);
                }
            }

            if ((next ?? first) is not { } selected)
            {
                return MerkleHashRequestSelection<TPeer>.NoPeer;
            }

            _outstandingHashRequests[requestKey] = new OutstandingRequest(now, selected.Order);
            return MerkleHashRequestSelection<TPeer>.Selected(selected.Peer, requestKey);
        }
    }

    public void CompleteFailedV2Request(string requestKey)
    {
        lock (_lock)
        {
            if (_outstandingHashRequests.TryGetValue(requestKey, out var outstanding))
            {
                _outstandingHashRequests[requestKey] = outstanding with { RequestedAt = DateTimeOffset.MinValue };
            }
        }
    }

    private sealed record PeerOrder(long Value);

    private readonly record struct OutstandingRequest(DateTimeOffset RequestedAt, long PeerOrder);

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
