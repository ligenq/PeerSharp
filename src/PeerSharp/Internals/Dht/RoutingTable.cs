using System.Net;
using System.Numerics;

namespace PeerSharp.Internals.Dht;

internal class NodeInfo
{
    public NodeInfo(byte[] id, IPEndPoint ep)
    {
        Id = id;
        EndPoint = ep;
    }

    public NodeInfo(ReadOnlySpan<byte> id, IPEndPoint ep)
    {
        Id = id.ToArray();
        EndPoint = ep;
    }

    public IPEndPoint EndPoint { get; set; }
    public byte[] Id { get; set; }
}

internal class RoutingTable
{
    private const int MaxBucketCacheSize = 8;

    private const int MaxBucketNodesCount = 8;

    private const int MaxFailuresBeforeInactive = 2;

    private static readonly TimeSpan MaxBucketFreshNodeInactiveTime = TimeSpan.Zero;

    private static readonly TimeSpan MaxBucketNodeInactiveTime = TimeSpan.FromMinutes(15);

    private readonly Bucket[] _buckets = new Bucket[160];

    private readonly byte[] _localId;

    private readonly Lock _lock = new();

    private readonly TimeProvider _timeProvider;

    public RoutingTable(byte[] localId, TimeProvider timeProvider)
    {
        _localId = localId;
        _timeProvider = timeProvider;
        for (int i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new Bucket();
        }
    }

    public static int GetDistance(ReadOnlySpan<byte> n1, ReadOnlySpan<byte> n2)
    {
        if (n1.Length < 20 || n2.Length < 20)
        {
            throw new ArgumentException("DHT node IDs must be 20 bytes.");
        }

        // 160 - leading zeros of XOR
        for (int i = 0; i < 20; i++)
        {
            byte xor = (byte)(n1[i] ^ n2[i]);
            if (xor != 0)
            {
                // BitOperations.LeadingZeroCount returns count for 32-bit int
                // For a byte, we want bits 0-7. LeadingZeroCount of 0x80 is 24.
                // 31 - 24 = 7 (the bit index)
                int leadingZeros = BitOperations.LeadingZeroCount(xor);
                int highestBit = 31 - leadingZeros;
                return ((19 - i) * 8) + highestBit;
            }
        }
        return 0;
    }

    public void AddNode(ReadOnlySpan<byte> id, IPEndPoint ep)
    {
        if (id.Length != 20)
        {
            return;
        }

        NodeResponded(new NodeInfo(id, ep), IdMatchesAddress(id, ep));
    }

    /// <summary>
    /// Remembers a referral in an empty slot. It stays inactive until direct contact confirms it,
    /// and any responding node can take its slot.
    /// </summary>
    public void AddReferral(ReadOnlySpan<byte> id, IPEndPoint ep)
    {
        if (id.Length != 20)
        {
            return;
        }

        int bucketId = GetBucketId(id);
        lock (_lock)
        {
            var bucket = _buckets[bucketId];
            if (bucket.Nodes.Count >= MaxBucketNodesCount || bucket.Find(id) != null || bucket.FindCache(id) != null)
            {
                return;
            }
            bucket.Nodes.Add(new BucketNode(new NodeInfo(id, ep), _timeProvider) { Active = false });
        }
    }

    /// <summary>
    /// Records a query to <paramref name="ep"/> that went unanswered. A node that fails
    /// <see cref="MaxFailuresBeforeInactive"/> times running is no longer handed out, and the next
    /// node to respond in its bucket may take its place.
    /// </summary>
    public void EndpointNotResponded(IPEndPoint ep)
    {
        lock (_lock)
        {
            foreach (var bucket in _buckets)
            {
                foreach (var n in bucket.Nodes)
                {
                    if (n.Info.EndPoint.Equals(ep))
                    {
                        if (++n.Failures >= MaxFailuresBeforeInactive)
                        {
                            n.Active = false;
                        }
                        return;
                    }
                }
            }
        }
    }

    private static bool IdMatchesAddress(ReadOnlySpan<byte> id, IPEndPoint ep)
    {
        // BEP 42: Validate node ID against IP address. Local/private addresses are not validated
        // and count as secure.
        return !DhtSecurity.ShouldValidate(ep.Address) || DhtSecurity.ValidateNodeId(id, ep.Address);
    }

    public List<NodeInfo> FindClosest(ReadOnlySpan<byte> target, int count)
    {
        int startId = GetBucketId(target);
        var outNodes = new List<NodeInfo>();

        lock (_lock)
        {
            int v6Counter = 0;
            int v4Counter = 0;

            // Check current and lower buckets
            for (int i = startId; i >= 0 && outNodes.Count < count; i--)
            {
                AddNodesFromBucket(_buckets[i], outNodes, count, ref v6Counter, ref v4Counter);
            }

            // Check higher buckets
            for (int i = startId + 1; i < 160 && outNodes.Count < count; i++)
            {
                AddNodesFromBucket(_buckets[i], outNodes, count, ref v6Counter, ref v4Counter);
            }
        }

        return outNodes;
    }

    public void NodeNotResponded(NodeInfo node)
    {
        int bucketId = GetBucketId(node.Id);
        NodeNotResponded(bucketId, node);
    }

    public void NodeResponded(NodeInfo node, bool isSecure = false)
    {
        int bucketId = GetBucketId(node.Id);
        NodeResponded(bucketId, node, isSecure);
    }

    private static void AddNodesFromBucket(Bucket bucket, List<NodeInfo> outNodes, int count, ref int v6Counter, ref int v4Counter)
    {
        foreach (var n in bucket.Nodes)
        {
            if (n.Active && outNodes.Count < count)
            {
                // Simplified IP version check: limit to 8 nodes per family per find_node response (standard heuristic)
                if (n.Info.EndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && v6Counter < 8)
                {
                    outNodes.Add(n.Info);
                    v6Counter++;
                }
                else if (n.Info.EndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && v4Counter < 8)
                {
                    outNodes.Add(n.Info);
                    v4Counter++;
                }
            }
        }
    }

    private int GetBucketId(ReadOnlySpan<byte> id)
    {
        return GetDistance(id, _localId);
    }

    private void NodeNotResponded(int bucketId, NodeInfo node)
    {
        lock (_lock)
        {
            var bucket = _buckets[bucketId];
            var now = _timeProvider.GetUtcNow();

            for (int i = 0; i < bucket.Nodes.Count; i++)
            {
                var n = bucket.Nodes[i];
                if (node.Id.AsSpan().SequenceEqual(n.Info.Id))
                {
                    if (n.Active)
                    {
                        n.Active = false;
                        n.LastUpdate = now;
                    }

                    var maxInactiveTime = i > 5 ? MaxBucketFreshNodeInactiveTime : MaxBucketNodeInactiveTime;
                    if (now - bucket.LastUpdate > maxInactiveTime)
                    {
                        bucket.Nodes.RemoveAt(i);
                        if (bucket.Cache.Count > 0)
                        {
                            var cached = bucket.Cache[0];
                            bucket.Cache.RemoveAt(0);
                            cached.Active = true;
                            cached.LastUpdate = now;
                            bucket.Nodes.Add(cached);
                        }
                    }
                    break;
                }
            }
        }
    }

    private void NodeResponded(int bucketId, NodeInfo node, bool isSecure = false)
    {
        lock (_lock)
        {
            var bucket = _buckets[bucketId];
            var now = _timeProvider.GetUtcNow();

            var n = bucket.Find(node.Id);
            if (n == null)
            {
                n = bucket.FindCache(node.Id);
                if (n == null)
                {
                    var bnode = new BucketNode(node, _timeProvider, isSecure)
                    {
                        LastUpdate = now
                    };

                    if (bucket.Nodes.Count < MaxBucketNodesCount)
                    {
                        bucket.Nodes.Add(bnode);
                        bucket.LastUpdate = now;
                    }
                    else
                    {
                        // A node that has stopped answering is the first to go, then (BEP 42) an
                        // insecure one.
                        bool replaced = false;
                        for (int i = 0; i < bucket.Nodes.Count; i++)
                        {
                            if (!bucket.Nodes[i].Active)
                            {
                                bucket.Nodes[i] = bnode;
                                bucket.LastUpdate = now;
                                replaced = true;
                                break;
                            }
                        }

                        if (!replaced && isSecure)
                        {
                            for (int i = 0; i < bucket.Nodes.Count; i++)
                            {
                                if (!bucket.Nodes[i].IsSecure)
                                {
                                    // Replace insecure node with secure one
                                    var oldNode = bucket.Nodes[i];
                                    bucket.Nodes[i] = bnode;
                                    bucket.LastUpdate = now;
                                    // Move old node to cache
                                    oldNode.Active = false;
                                    if (bucket.Cache.Count < MaxBucketCacheSize)
                                    {
                                        bucket.Cache.Add(oldNode);
                                    }
                                    replaced = true;
                                    break;
                                }
                            }
                        }

                        if (!replaced)
                        {
                            bnode.Active = false;
                            if (bucket.Cache.Count < MaxBucketCacheSize)
                            {
                                bucket.Cache.Add(bnode);
                            }
                            else
                            {
                                bucket.Cache.RemoveAt(0);
                                bucket.Cache.Add(bnode);
                            }
                            bucket.LastCacheUpdate = now;
                        }
                    }
                }
                else
                {
                    if (!n.Info.EndPoint.Equals(node.EndPoint))
                    {
                        // Someone else claiming a known ID; keep the endpoint we already have.
                        return;
                    }
                    n.LastUpdate = now;
                    n.Info = node;
                    n.IsSecure = isSecure; // Update security status
                    bucket.LastCacheUpdate = now;
                }
            }
            else
            {
                if (n.Active && !n.Info.EndPoint.Equals(node.EndPoint))
                {
                    // A live node keeps its endpoint: otherwise anyone could claim its ID and take
                    // its place. Once it stops answering, the new endpoint may have it.
                    return;
                }
                n.LastUpdate = now;
                n.Active = true;
                n.Failures = 0;
                n.Info = node; // Update IP/Port
                n.IsSecure = isSecure; // Update security status
                bucket.LastUpdate = now;
            }
        }
    }

    public List<NodeInfo> GetAllNodes(int maxNodes = 500)
    {
        var nodes = new List<NodeInfo>();

        // The loop below adds a node and then tests the limit, which is right for any positive
        // limit and returns one node for a limit of zero. No caller asks for none today - the only
        // one there is refuses that itself - but a limit is the method's own promise to keep.
        if (maxNodes <= 0)
        {
            return nodes;
        }

        lock (_lock)
        {
            foreach (var bucket in _buckets)
            {
                foreach (var node in bucket.Nodes)
                {
                    if (!node.Active)
                    {
                        continue;
                    }

                    nodes.Add(node.Info);
                    if (nodes.Count >= maxNodes)
                    {
                        return nodes;
                    }
                }
            }
        }

        return nodes;
    }

    private sealed class Bucket
    {
        public List<BucketNode> Cache { get; } = [];
        public DateTimeOffset LastCacheUpdate { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastUpdate { get; set; } = DateTimeOffset.MinValue;
        public List<BucketNode> Nodes { get; } = [];

        public BucketNode? Find(ReadOnlySpan<byte> id)
        {
            foreach (var n in Nodes)
            {
                if (id.SequenceEqual(n.Info.Id))
                {
                    return n;
                }
            }
            return null;
        }

        public BucketNode? FindCache(ReadOnlySpan<byte> id)
        {
            foreach (var n in Cache)
            {
                if (id.SequenceEqual(n.Info.Id))
                {
                    return n;
                }
            }
            return null;
        }
    }

    private sealed class BucketNode
    {
        public BucketNode(NodeInfo info, TimeProvider timeProvider, bool isSecure = false)
        {
            Info = info;
            LastUpdate = timeProvider.GetUtcNow();
            Active = true;
            IsSecure = isSecure;
        }

        public bool Active { get; set; }

        /// <summary>Consecutive queries this node has left unanswered.</summary>
        public int Failures { get; set; }

        public NodeInfo Info { get; set; }

        /// <summary>
        /// BEP 42: Whether this node's ID has been validated against its IP.
        /// </summary>
        public bool IsSecure { get; set; }

        public DateTimeOffset LastUpdate { get; set; }
    }
}
