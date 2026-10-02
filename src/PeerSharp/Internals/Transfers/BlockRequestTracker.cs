using System.Collections.Concurrent;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Internals.Transfers;

internal sealed class PeerRequestCollection
{
    private readonly ConcurrentDictionary<(int Piece, int Offset), BlockRequest> _requests = new();

    public int Count => _requests.Count;
    public bool IsEmpty => Count == 0;

    public ICollection<(int Piece, int Offset)> Keys => _requests.Keys;

    public ICollection<BlockRequest> Values => _requests.Values;

    public BlockRequest this[(int Piece, int Offset) key]
    {
        set
        {
            _requests[key] = value;
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
        return _requests.TryRemove(key, out value!);
    }

    public bool TryRemove((int Piece, int Offset) key, BlockRequest expected)
        => _requests.TryRemove(new KeyValuePair<(int Piece, int Offset), BlockRequest>(key, expected));
}

internal sealed class BlockRequestTracker
{
    private readonly ConcurrentDictionary<(int Piece, int Offset), ConcurrentDictionary<PeerCommunication, BlockRequest>> _blockRequestIndex = new();
    private readonly ConcurrentDictionary<PeerCommunication, PeerRequestCollection> _peerRequests = new();
    private readonly Lock _mutationLock = new();

    public int BlockRequestIndexCount => _blockRequestIndex.Count;

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

    public bool TryRemovePeerRequest(PeerCommunication peer, (int Piece, int Offset) key, out BlockRequest request, BlockRequest? expected = null)
    {
        lock (_mutationLock)
        {
            request = default!;
            if (!_peerRequests.TryGetValue(peer, out var list) ||
                !list.TryGetValue(key, out request) ||
                (expected != null && !ReferenceEquals(request, expected)) || !list.TryRemove(key, request))
            {
                return false;
            }
            RemoveIndexEntry(key, peer, request);
            // Retain the collection until disconnect: schedulers may already hold it.
            return true;
        }
    }

    public void RemovePeer(PeerCommunication peer)
    {
        lock (_mutationLock)
        {
            if (_peerRequests.TryRemove(peer, out var list))
            {
                foreach (var entry in list.AsEnumerable().ToArray())
                {
                    list.TryRemove(entry.Key, entry.Value);
                    RemoveIndexEntry(entry.Key, peer, entry.Value);
                }
            }
        }
    }

    public bool TryGetBlockPeers((int Piece, int Offset) key, out ConcurrentDictionary<PeerCommunication, BlockRequest> list)
    {
        return _blockRequestIndex.TryGetValue(key, out list!);
    }

    public void AddBlockRequest(int piece, int offset, PeerCommunication peer, BlockRequest request)
    {
        lock (_mutationLock)
        {
            GetOrAddPeerRequests(peer)[(piece, offset)] = request;
            _blockRequestIndex.GetOrAdd((piece, offset), _ => new())[peer] = request;
        }
    }

    public void RemoveBlockRequest(int piece, int offset, PeerCommunication peer)
    {
        TryRemovePeerRequest(peer, (piece, offset), out _);
    }

    private void RemoveIndexEntry((int Piece, int Offset) key, PeerCommunication peer, BlockRequest expected)
    {
        if (_blockRequestIndex.TryGetValue(key, out var list))
        {
            list.TryRemove(new KeyValuePair<PeerCommunication, BlockRequest>(peer, expected));
            if (list.IsEmpty)
            {
                _blockRequestIndex.TryRemove(key, out _);
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

    /// <summary>
    /// How many peers currently owe us this block.
    ///
    /// <para>
    /// Duplicating a stalled request is worth doing, but only a bounded number of times: every extra
    /// copy is a block we will most likely receive twice, because the cancel we send when the first one
    /// lands races the data already on the wire. Callers use this to cap the fan-out.
    /// </para>
    /// </summary>
    public int GetPendingRequestCount(int piece, int offset)
    {
        return _blockRequestIndex.TryGetValue((piece, offset), out var list) ? list.Count : 0;
    }
}
