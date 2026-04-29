using System.Collections.Concurrent;

namespace PeerSharp.Internals;

internal sealed class MerkleHashRequestCoordinator
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _outstandingHashRequests = new();
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
        if (_outstandingHashRequests.TryGetValue(requestKey, out var lastRequested)
            && now - lastRequested < _retryInterval)
        {
            return MerkleHashRequestSelection<TPeer>.Throttled(requestKey);
        }

        foreach (var peer in peers)
        {
            if (canRequest(peer))
            {
                _outstandingHashRequests[requestKey] = now;
                return MerkleHashRequestSelection<TPeer>.Selected(peer, requestKey);
            }
        }

        return MerkleHashRequestSelection<TPeer>.NoPeer;
    }

    public void CompleteFailedV2Request(string requestKey)
    {
        _outstandingHashRequests.TryRemove(requestKey, out _);
    }

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
