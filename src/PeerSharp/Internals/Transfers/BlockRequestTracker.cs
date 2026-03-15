using System.Collections.Concurrent;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals;

internal sealed class PeerRequestCollection
{
    private readonly ConcurrentDictionary<(int Piece, int Offset), BlockRequest> _requests = new();
    private int _count;

    public int Count => Interlocked.CompareExchange(ref _count, 0, 0);
    public bool IsEmpty => Count == 0;

    public ICollection<(int Piece, int Offset)> Keys => _requests.Keys;

    public ICollection<BlockRequest> Values => _requests.Values;

    public BlockRequest this[(int Piece, int Offset) key]
    {
        set
        {
            if (_requests.TryAdd(key, value))
            {
                Interlocked.Increment(ref _count);
            }
            else
            {
                _requests[key] = value;
            }
        }
    }

    public ConcurrentDictionary<(int Piece, int Offset), BlockRequest> AsEnumerable()
        => _requests;

    public bool TryGetValue((int Piece, int Offset) key, out BlockRequest value)
    {
        return _requests.TryGetValue(key, out value!);
    }

    public bool TryRemove((int Piece, int Offset) key, out BlockRequest value)
    {
        if (_requests.TryRemove(key, out value!))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }
        return false;
    }
}

internal sealed class BlockRequestTracker
{
    private readonly ConcurrentDictionary<(int Piece, int Offset), ConcurrentDictionary<PeerCommunication, BlockRequest>> _blockRequestIndex = new();
    private readonly ConcurrentDictionary<PeerCommunication, PeerRequestCollection> _peerRequests = new();
    private int _blockRequestIndexCount;

    public int BlockRequestIndexCount => Interlocked.CompareExchange(ref _blockRequestIndexCount, 0, 0);

    public PeerRequestCollection GetOrAddPeerRequests(PeerCommunication peer)
    {
        return _peerRequests.GetOrAdd(peer, _ => new PeerRequestCollection());
    }

    public ConcurrentDictionary<PeerCommunication, PeerRequestCollection> EnumeratePeerRequests()
        => _peerRequests;

    public bool TryGetPeerRequests(PeerCommunication peer, out PeerRequestCollection requests)
    {
        return _peerRequests.TryGetValue(peer, out requests!);
    }

    public bool TryRemovePeerRequest(PeerCommunication peer, (int Piece, int Offset) key, out BlockRequest request)
    {
        if (_peerRequests.TryGetValue(peer, out var list))
        {
            var result = list.TryRemove(key, out request);
            if (list.IsEmpty)
            {
                _peerRequests.TryRemove(peer, out _);
            }
            return result;
        }
        request = default!;
        return false;
    }

    public void RemovePeer(PeerCommunication peer)
    {
        if (_peerRequests.TryRemove(peer, out var list))
        {
            foreach (var req in list.Keys)
            {
                RemoveBlockRequest(req.Piece, req.Offset, peer);
            }
        }
    }

    public bool TryGetBlockPeers((int Piece, int Offset) key, out ConcurrentDictionary<PeerCommunication, BlockRequest> list)
    {
        return _blockRequestIndex.TryGetValue(key, out list!);
    }

    public void AddBlockRequest(int piece, int offset, PeerCommunication peer, BlockRequest request)
    {
        // Ensure peer request collection is updated
        var peerReqs = GetOrAddPeerRequests(peer);
        peerReqs[(piece, offset)] = request;

        var key = (piece, offset);
        while (true)
        {
            var list = _blockRequestIndex.GetOrAdd(key, _ =>
            {
                Interlocked.Increment(ref _blockRequestIndexCount);
                return new ConcurrentDictionary<PeerCommunication, BlockRequest>();
            });

            list[peer] = request;

            if (_blockRequestIndex.TryGetValue(key, out var currentList) && currentList == list)
            {
                return;
            }
        }
    }

    public void RemoveBlockRequest(int piece, int offset, PeerCommunication peer)
    {
        var key = (piece, offset);
        if (_blockRequestIndex.TryGetValue(key, out var list))
        {
            list.TryRemove(peer, out _);
            if (list.IsEmpty && _blockRequestIndex.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _blockRequestIndexCount);
            }
        }
    }

    public (int AgeMs, PeerCommunication Peer)? GetOldestPendingRequest(int piece, int offset, DateTimeOffset now)
    {
        var key = (piece, offset);
        if (_blockRequestIndex.TryGetValue(key, out var list) && !list.IsEmpty)
        {
            BlockRequest? oldest = null;
            PeerCommunication? oldestPeer = null;

            foreach (var kv in list.ToArray())
            {
                if (oldest == null || kv.Value.Timestamp < oldest.Timestamp)
                {
                    oldest = kv.Value;
                    oldestPeer = kv.Key;
                }
            }

            if (oldest != null)
            {
                int ageMs = (int)(now - oldest.Timestamp).TotalMilliseconds;
                return (ageMs, oldestPeer!);
            }
        }
        return null;
    }

    public bool HasPendingRequestFromPeer(int piece, int offset, PeerCommunication peer)
    {
        var key = (piece, offset);
        if (_blockRequestIndex.TryGetValue(key, out var list))
        {
            return list.ContainsKey(peer);
        }
        return false;
    }
}
