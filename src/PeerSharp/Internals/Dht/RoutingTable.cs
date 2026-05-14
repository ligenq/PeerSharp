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
        // BEP 42: Validate node ID against IP address
        bool isSecure = false;
        if (DhtSecurity.ShouldValidate(ep.Address))
        {
            isSecure = DhtSecurity.ValidateNodeId(id, ep.Address);
        }
        else
        {
            // Don't validate local/private addresses - consider them secure
            isSecure = true;
        }

        NodeResponded(new NodeInfo(id, ep), isSecure);
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
                        // BEP 42: If new node is secure and bucket is full,
                        // try to replace an insecure node
                        bool replaced = false;
                        if (isSecure)
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
                    n.LastUpdate = now;
                    n.Info = node; // Update IP/Port
                    n.IsSecure = isSecure; // Update security status
                    bucket.LastCacheUpdate = now;
                }
            }
            else
            {
                n.LastUpdate = now;
                n.Active = true;
                n.Info = node; // Update IP/Port
                n.IsSecure = isSecure; // Update security status
                bucket.LastUpdate = now;
            }
        }
    }

    public List<NodeInfo> GetAllNodes(int maxNodes = 500)
    {
        var nodes = new List<NodeInfo>();
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
        public NodeInfo Info { get; set; }

        /// <summary>
        /// BEP 42: Whether this node's ID has been validated against its IP.
        /// </summary>
        public bool IsSecure { get; set; }

        public DateTimeOffset LastUpdate { get; set; }
    }
}
